using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Communication.Chat.Formatters;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class ChatService : IChatService
{
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatCategoryRepository? _categories;
    private readonly ChatMessageRepository _messages;
    private readonly TeamRepository _teams;
    private readonly ChatReadStateRepository _readStates;
    private readonly ProjectRepository _projects;
    private readonly ChatPollRepository? _polls;
    private readonly ReportedMessageRepository? _reportedMessages;

    private readonly ChatStateService _chatState;
    private readonly INotificationRoutingService _notificationRouting;
    private readonly PresenceStateService _presenceState;
    private readonly ILogger<ChatService> _logger;
    private readonly IFileService _fileService;
    private readonly ICryptoService _crypto;
    private readonly IContentModerationService _moderationService;
    private readonly AlbumService _albumService;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IUserPrivacyService? _privacyService;
    private readonly FileTokenService? _fileTokenService;
    private readonly ChatSecurityService? _securityService;
    private readonly VoiceChannelService? _voiceService;
    private readonly NotificationQueueService? _notificationQueue;

    [ActivatorUtilitiesConstructor]
    public ChatService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        ProjectsContext projectsContext,
        ChatStateService chatState,
        INotificationRoutingService notificationRouting,
        PresenceStateService presenceState,
        IFileService fileService,
        IContentModerationService moderationService,
        AlbumService albumService,
        ILogger<ChatService> logger,
        IChatAuthorizationService? chatAuth = null,
        ICryptoService? crypto = null,
        IUserPrivacyService? privacyService = null,
        FileTokenService? fileTokenService = null,
        ChatSecurityService? securityService = null,
        VoiceChannelService? voiceService = null,
        NotificationQueueService? notificationQueue = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(projectsContext);

        _employees = hrContext.Employees;
        _channels = communicationContext.ChatChannels;
        _categories = communicationContext.ChatCategories;
        _messages = communicationContext.ChatMessages;
        _teams = hrContext.Teams;
        _readStates = communicationContext.ChatReadStates;
        _projects = projectsContext.Projects;
        _polls = communicationContext.ChatPolls;
        _reportedMessages = communicationContext.ReportedMessages;

        _chatState = chatState;
        _notificationRouting = notificationRouting;
        _presenceState = presenceState;
        _fileService = fileService;
        _crypto = crypto ?? new CryptoService();
        _moderationService = moderationService;
        _albumService = albumService;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _privacyService = privacyService;
        _fileTokenService = fileTokenService;
        _securityService = securityService;
        _voiceService = voiceService;
        _notificationQueue = notificationQueue;
        _chatAuth = chatAuth ?? new ChatAuthorizationService(_channels, _projects, _teams, _employees, privacyService);
    }

    public virtual Task<SpokesResult<ChatMessage>> SendMessageAsync(string senderId, string channelId, string content, string? replyToId = null) =>
        SendMessageAsync(senderId, channelId, content, replyToId, CancellationToken.None);

    public virtual Task<SpokesResult<ChatMessage>> SendMessageAsync(string senderId, string channelId, string content, string? replyToId, CancellationToken cancellationToken)
    {
        return SendMessageAsync(new SendMessageOptions
        {
            SenderId = senderId,
            ChannelId = channelId,
            Content = content,
            ReplyToId = replyToId
        }, cancellationToken);
    }

    /// <summary>
    /// Sends a message and returns a <see cref="SpokesResult{ChatMessage}"/> containing the created message entity
    /// or failure details preserving distinct error reasons and codes (e.g. NOT_FOUND, FORBIDDEN, MODERATION_BLOCKED, SAVE_FAILED).
    /// </summary>
    public virtual async Task<SpokesResult<ChatMessage>> SendMessageAsync(SendMessageOptions options, CancellationToken cancellationToken = default)
    {
        var senderId = options.SenderId;
        var channelId = options.ChannelId;
        var content = options.Content;
        var attachments = options.Attachments;
        var replyToId = options.ReplyToId;
        var privateKey = options.PrivateKey;

        var employee = _employees.GetById(senderId);
        if (employee == null)
        {
            _logger.LogWarning("SendMessage failed: Employee not found: {SenderId}", senderId);
            return SpokesResult<ChatMessage>.Failure($"User '{senderId}' not found.", "NOT_FOUND");
        }

        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("SendMessage failed: Employee {SenderId} is inactive, suspended, or banned", senderId);
            return SpokesResult<ChatMessage>.Failure("User is not authorized to send messages.", "FORBIDDEN");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger.LogWarning("SendMessage failed: Channel not found: {ChannelId}", channelId);
            return SpokesResult<ChatMessage>.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        // Validate Channel Access and Post Access
        if (!_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("SendMessage failed: User {UserId} denied access to Channel {ChannelId}", employee.Id, channelId);
            return SpokesResult<ChatMessage>.Failure("User is not authorized to access or post to this channel.", "FORBIDDEN");
        }

        string? plainChannelKey = null;
        if (channel.IsEncrypted)
        {
            if (string.IsNullOrEmpty(privateKey))
            {
                _logger.LogWarning("SendMessage failed: Vault locked for User {UserId}", employee.Id);
                return SpokesResult<ChatMessage>.Failure("Vault is locked. Cannot send encrypted message.", "VAULT_LOCKED");
            }

            if (!channel.TryGetEncryptedKeyForEpoch(channel.CurrentKeyEpoch, employee.Id, out var cipherChannelKey))
            {
                _logger.LogWarning("SendMessage failed: Channel key not found for User {UserId} in channel {ChannelId}", employee.Id, channelId);
                return SpokesResult<ChatMessage>.Failure("Channel key not found for current user.", "KEY_NOT_FOUND");
            }

            try
            {
                plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, privateKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendMessage failed: RSA Decryption of Channel Key failed for User {UserId}", employee.Id);
                return SpokesResult<ChatMessage>.Failure($"Failed to decrypt channel key: {ex.Message}", "CRYPTO_FAILED");
            }
        }

        // Process Attachments - Save to Disk and Validate Channel Ownership
        var processedAttachments = new List<ChatAttachment>();
        if (attachments != null)
        {
            var expectedChannelPrefix = $"/spokesapi/files/chat/{channelId}/";
            foreach (var att in attachments)
            {
                if (string.IsNullOrEmpty(att.FilePath))
                    continue;

                att.FileName = FileHelper.SanitizeDisplayFileName(att.FileName);

                // Check if it's base64 (starts with "data:")
                if (att.FilePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (DataUriHelper.TryParse(att.FilePath, out var bytes, out _) && bytes.Length > 0)
                        {
                            // Delegate to unified FileService pipeline for secure storage and valid routing
                            using var stream = new MemoryStream(bytes);
                            var url = await _fileService.UploadStreamAsync("chat", channelId, stream, att.FileName, plainChannelKey);

                            // Update attachment to point to valid API URL returned by FileService
                            att.FilePath = url;
                        }
                        else
                        {
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to save attachment {FileName}", att.FileName);
                        continue; // Skip failed attachment
                    }
                }
                else
                {
                    var normalizedPath = att.FilePath.StartsWith('/') ? att.FilePath : "/" + att.FilePath;
                    if (normalizedPath.Contains("..", StringComparison.Ordinal) ||
                        !normalizedPath.StartsWith(expectedChannelPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Rejected invalid or cross-channel attachment path {FilePath} for channel {ChannelId}", att.FilePath, channelId);
                        continue;
                    }
                    att.FilePath = normalizedPath;
                }
                att.KeyEpoch = channel.IsEncrypted ? channel.CurrentKeyEpoch : 0;
                processedAttachments.Add(att);
            }
        }

        var modResult = await _moderationService.EvaluateTextAsync(content ?? "", cancellationToken);
        if (modResult.HasViolation)
        {
            if (modResult.Action == TextModerationAction.Block)
            {
                _logger.LogWarning("SendMessage blocked due to moderation policy for User {UserId}", employee.Id);
                return SpokesResult<ChatMessage>.Failure("Message content violates moderation policy.", "MODERATION_BLOCKED");
            }
            else if (modResult.Action == TextModerationAction.Sanitize)
            {
                content = modResult.SanitizedText ?? content ?? "";
            }
        }

        var message = new ChatMessage
        {
            ChannelId = channelId,
            ChannelType = channel.ChannelType,
            SenderId = employee.Id,
            Content = content ?? "",
            ReplyToId = replyToId,
            SentAt = DateTime.UtcNow,
            Attachments = processedAttachments,
            ReadBy = new List<string> { employee.Id }
        };

        var tokens = ExtractMessageTokens(content);
        ApplyTokensAndResolveMentions(message, tokens);
        var albumSyncResult = SynchronizeMentionedAlbums(message, channel, previousAlbumIds: Array.Empty<string>(), privateKey);
        if (!albumSyncResult.IsSuccess)
        {
            return SpokesResult<ChatMessage>.Failure(
                albumSyncResult.ErrorMessage ?? "Failed to share mentioned album.",
                albumSyncResult.Code);
        }

        // --- ENCRYPTION (Data-at-Rest) ---
        if (channel.IsEncrypted)
        {
            if (plainChannelKey != null)
            {
                try
                {
                    message.Content = _crypto.EncryptAes(message.Content, plainChannelKey);
                    message.IsEncrypted = true;
                    message.KeyEpoch = channel.CurrentKeyEpoch;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to encrypt message content for channel {ChannelId}", channelId);
                    return SpokesResult<ChatMessage>.Failure($"Failed to encrypt message: {ex.Message}", "CRYPTO_FAILED");
                }
            }
            else
            {
                return SpokesResult<ChatMessage>.Failure("Cannot encrypt message: Channel key is unavailable.", "KEY_NOT_FOUND");
            }
        }

        // Save to database
        try
        {
            _messages.Save(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId}", message.Id);
            return SpokesResult<ChatMessage>.Failure($"Failed to persist message to database: {ex.Message}", "SAVE_FAILED");
        }

        // Update channel last activity
        _channels.UpdateLastActivity(channelId);
        _readStates.MarkAsRead(employee.Id, channelId, channel.ChannelType.ToString(), channel.IsVoiceChannel);

        // 1. Notify local subscribers (Server-side Blazor) immediately
        _chatState.NotifyMessageReceived(message);

        // 2. Delegate routing rules to the central notification routing service
        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteChatNotificationAsync(message, channel, employee), "chat message");

        // 3. Synchronize sender previous read state (offloaded after real-time message delivery)
        await SynchronizeSenderPreviousReadStateAsync(channelId, employee.Id, message.Id, cancellationToken);

        return SpokesResult<ChatMessage>.Success(message);
    }

    private async Task SynchronizeSenderPreviousReadStateAsync(string channelId, string senderId, string currentMessageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var recentMessages = await _messages.GetByChannelAsync(channelId, 50, cancellationToken);
            var unreadPrevious = new List<ChatMessage>();
            foreach (var prevMsg in recentMessages)
            {
                if (prevMsg.Id != currentMessageId && !prevMsg.ReadBy.Contains(senderId))
                {
                    prevMsg.ReadBy.Add(senderId);
                    unreadPrevious.Add(prevMsg);
                }
            }

            if (unreadPrevious.Count > 0)
            {
                _messages.SaveBatch(unreadPrevious);
                foreach (var prevMsg in unreadPrevious)
                {
                    _chatState.NotifyMessageRead(channelId, prevMsg.Id, prevMsg.ReadBy);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-mark previous messages as read for sender {SenderId} in channel {ChannelId}", senderId, channelId);
        }
    }

    public Task MarkMessageAsReadAsync(string userId, string channelId, string messageId, CancellationToken cancellationToken = default)
        => MarkMessagesAsReadAsync(userId, channelId, [messageId], cancellationToken);

    public Task MarkMessagesAsReadAsync(string userId, string channelId, IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId) || messageIds == null || messageIds.Count == 0)
            return Task.CompletedTask;

        var channel = _channels.GetById(channelId);
        var employee = _employees.GetById(userId);
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned || !_chatAuth.CanUserAccessChannel(channel, employee))
            return Task.CompletedTask;

        var updatedMessages = new List<ChatMessage>();
        foreach (var messageId in messageIds.Distinct())
        {
            if (string.IsNullOrWhiteSpace(messageId)) continue;
            var message = _messages.GetById(messageId);
            if (message == null || !string.Equals(message.ChannelId, channelId, StringComparison.Ordinal)) continue;

            if (!message.ReadBy.Contains(userId))
            {
                message.ReadBy.Add(userId);
                updatedMessages.Add(message);
            }
        }

        if (updatedMessages.Count == 0) return Task.CompletedTask;

        if (updatedMessages.Count == 1)
        {
            _messages.Save(updatedMessages[0]);
        }
        else
        {
            _messages.SaveBatch(updatedMessages);
        }

        foreach (var message in updatedMessages)
        {
            _chatState.NotifyMessageRead(channelId, message.Id, message.ReadBy);
        }

        return Task.CompletedTask;
    }

    public async Task MarkChannelMessagesAsReadAsync(string userId, string channelId, int count = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        var channel = _channels.GetById(channelId);
        var employee = _employees.GetById(userId);
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned || !_chatAuth.CanUserAccessChannel(channel, employee))
            return;

        var messages = await _messages.GetByChannelAsync(channelId, count, cancellationToken);
        var unreadMessages = new List<ChatMessage>();

        foreach (var msg in messages)
        {
            if (!string.Equals(msg.ChannelId, channelId, StringComparison.Ordinal))
                continue;

            if (msg.SenderId != userId && !msg.ReadBy.Contains(userId))
            {
                msg.ReadBy.Add(userId);
                unreadMessages.Add(msg);
            }
        }

        if (unreadMessages.Count > 0)
        {
            _messages.SaveBatch(unreadMessages);
            foreach (var msg in unreadMessages)
            {
                _chatState.NotifyMessageRead(channelId, msg.Id, msg.ReadBy);
            }
        }
    }


    public Task StartTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return Task.CompletedTask;

        var channel = _channels.GetById(channelId);
        if (channel != null && (!_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)))
            return Task.CompletedTask;

        _chatState.NotifyUserTyping(channelId, employee.Id, employee.FullName);
        _presenceState.PingUserActive(employee.Id);
        return Task.CompletedTask;
    }

    public Task StopTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return Task.CompletedTask;

        var channel = _channels.GetById(channelId);
        if (channel != null && !_chatAuth.CanUserAccessChannel(channel, employee))
            return Task.CompletedTask;

        _chatState.NotifyUserStoppedTyping(channelId, userId);
        return Task.CompletedTask;
    }

    public virtual async Task<SpokesResult> EditMessageAsync(string userId, string messageId, string newContent, string? requestingUserPrivateKey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newContent))
        {
            return SpokesResult.Failure("Message content cannot be empty.", "INVALID_CONTENT");
        }

        var message = _messages.GetById(messageId);
        if (message == null) return SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND");

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null)
        {
            return SpokesResult.Failure($"Channel '{message.ChannelId}' not found.", "NOT_FOUND");
        }

        var employee = _employees.GetById(userId);
        if (!_chatAuth.CanUserModifyMessage(message, channel, employee))
        {
            _logger.LogWarning("EditMessage failed: User {UserId} denied edit access to Message {MessageId}", userId, messageId);
            return SpokesResult.Failure("User is not authorized to edit this message.", "FORBIDDEN");
        }

        (bool HasViolation, string SanitizedText, TextModerationAction Action) modResult;
        try
        {
            modResult = await _moderationService.EvaluateTextAsync(newContent ?? "", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moderation service evaluation failed for message {MessageId}", messageId);
            return SpokesResult.Failure($"Message moderation evaluation failed: {ex.Message}", "MODERATION_ERROR");
        }

        if (modResult.HasViolation)
        {
            if (modResult.Action == TextModerationAction.Block)
            {
                _logger.LogWarning("EditMessage blocked due to moderation policy for User {UserId}", userId);
                return SpokesResult.Failure("Message content violates moderation policy.", "MODERATION_BLOCKED");
            }
            else if (modResult.Action == TextModerationAction.Sanitize && !string.IsNullOrEmpty(modResult.SanitizedText))
            {
                newContent = modResult.SanitizedText;
            }
        }

        message.EditedAt = DateTime.UtcNow;

        // Pass the original plaintext 'newContent' to the parser, 
        // since message.Content might be encrypted below
        var plainContent = newContent;
        
        if (channel.IsEncrypted && message.IsEncrypted)
        {
            if (string.IsNullOrEmpty(requestingUserPrivateKey))
            {
                return SpokesResult.Failure("Private key is required to edit an encrypted message.", "KEY_REQUIRED");
            }
            if (channel.TryGetEncryptedKeyForEpoch(channel.CurrentKeyEpoch, userId, out var cipherChannelKey))
            {
                try
                {
                    var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, requestingUserPrivateKey);
                    newContent = _crypto.EncryptAes(newContent!, plainChannelKey);
                    message.IsEncrypted = true;
                    message.KeyEpoch = channel.CurrentKeyEpoch;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to re-encrypt message content during edit.");
                    return SpokesResult.Failure($"Failed to re-encrypt message: {ex.Message}", "CRYPTO_FAILED");
                }
            }
            else
            {
                _logger.LogWarning("EditMessage failed: Channel encryption key unavailable for user {UserId}", userId);
                return SpokesResult.Failure("Channel encryption key unavailable for user.", "KEY_UNAVAILABLE");
            }
        }

        var previousUserIds = message.MentionedUserIds.ToList();
        var previousAlbumIds = message.MentionedAlbumIds.ToList();
        var previousMentionsTeam = message.MentionsTeam;
        var previousMentionsEveryone = message.MentionsEveryone;

        message.Content = newContent ?? "";

        var saveResult = ApplyEditedMentionsAndSave(
            message,
            channel,
            plainContent,
            previousUserIds,
            previousAlbumIds,
            previousMentionsTeam,
            previousMentionsEveryone,
            requestingUserPrivateKey,
            "during edit",
            "Failed to persist edited message.");
        if (!saveResult.IsSuccess)
        {
            return saveResult;
        }

        _chatState.NotifyMessageEdited(message);

        return SpokesResult.Success();
    }

    public virtual async Task<SpokesResult<ChatMessage>> ConvertMessageImagesToAlbumAsync(
        string userId,
        string messageId,
        string? targetAlbumId,
        string? newAlbumTitle,
        string? privateKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(messageId))
        {
            return SpokesResult<ChatMessage>.Failure("UserId and MessageId are required.", "INVALID_ARGUMENT");
        }

        var message = _messages.GetById(messageId);
        if (message == null || message.IsDeleted)
        {
            return SpokesResult<ChatMessage>.Failure($"Message '{messageId}' not found.", "NOT_FOUND");
        }

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null)
        {
            return SpokesResult<ChatMessage>.Failure($"Channel '{message.ChannelId}' not found.", "NOT_FOUND");
        }

        var employee = _employees.GetById(userId);
        if (employee == null ||
            !employee.HasPermission(AppPermissions.Albums.View) ||
            !string.Equals(message.SenderId, userId, StringComparison.OrdinalIgnoreCase) ||
            !_chatAuth.CanUserModifyMessage(message, channel, employee))
        {
            _logger.LogWarning("ConvertMessageImagesToAlbum failed: User {UserId} denied access to Message {MessageId}", userId, messageId);
            return SpokesResult<ChatMessage>.Failure("User is not authorized to convert this message's photos into an album.", "FORBIDDEN");
        }

        var imageAttachments = message.Attachments
            .Where(a => FileHelper.IsImageFile(a.FileName) || a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (imageAttachments.Count == 0)
        {
            return SpokesResult<ChatMessage>.Failure("Message does not contain any image attachments.", "INVALID_ARGUMENT");
        }

        var remainingAttachments = message.Attachments
            .Where(a => !FileHelper.IsImageFile(a.FileName) && !a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? plainSourceChannelKey = null;
        string? plainCurrentChannelKey = null;
        string plainContent = message.Content ?? "";

        if (channel.IsEncrypted)
        {
            if (string.IsNullOrEmpty(privateKey))
            {
                return SpokesResult<ChatMessage>.Failure("Vault is locked. Cannot convert encrypted message attachments.", "VAULT_LOCKED");
            }

            if (!channel.TryGetEncryptedKeyForEpoch(channel.CurrentKeyEpoch, userId, out var currentCipherKey))
            {
                return SpokesResult<ChatMessage>.Failure("Channel encryption key unavailable for user.", "KEY_NOT_FOUND");
            }

            try
            {
                plainCurrentChannelKey = _crypto.DecryptRsa(currentCipherKey, privateKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConvertMessageImagesToAlbum failed: RSA decryption of current channel key failed for User {UserId}", userId);
                return SpokesResult<ChatMessage>.Failure($"Failed to decrypt channel key: {ex.Message}", "CRYPTO_FAILED");
            }

            if (message.KeyEpoch == channel.CurrentKeyEpoch)
            {
                plainSourceChannelKey = plainCurrentChannelKey;
            }
            else if (channel.TryGetEncryptedKeyForEpoch(message.KeyEpoch, userId, out var msgEpochCipherKey))
            {
                try
                {
                    plainSourceChannelKey = _crypto.DecryptRsa(msgEpochCipherKey, privateKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ConvertMessageImagesToAlbum failed: RSA decryption of message epoch {Epoch} key failed for User {UserId}", message.KeyEpoch, userId);
                    return SpokesResult<ChatMessage>.Failure($"Failed to decrypt channel key: {ex.Message}", "CRYPTO_FAILED");
                }
            }
            else
            {
                plainSourceChannelKey = plainCurrentChannelKey;
            }

            if (message.IsEncrypted && !string.IsNullOrEmpty(message.Content))
            {
                try
                {
                    plainContent = _crypto.DecryptAes(message.Content, plainSourceChannelKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ConvertMessageImagesToAlbum failed: AES decryption of message content failed for Message {MessageId}", messageId);
                    return SpokesResult<ChatMessage>.Failure($"Failed to decrypt message content: {ex.Message}", "CRYPTO_FAILED");
                }
            }
        }

        var importResult = await _albumService.ImportChatAttachmentsToAlbumAsync(
            userId,
            channel.Id,
            imageAttachments,
            targetAlbumId,
            newAlbumTitle,
            privateKey,
            plainSourceChannelKey,
            deleteSourceFiles: true,
            sourceMessageId: message.Id);

        if (!importResult.IsSuccess || importResult.Value == null)
        {
            return SpokesResult<ChatMessage>.Failure(
                importResult.ErrorMessage ?? "Failed to import attachments into album.",
                importResult.Code ?? "ALBUM_IMPORT_FAILED");
        }

        var album = importResult.Value;
        var cleanTitle = album.Title.Replace("[", "(").Replace("]", ")");
        var albumToken = $"#[{cleanTitle}](album:{album.Id})";

        if (!plainContent.Contains($"(album:{album.Id})", StringComparison.OrdinalIgnoreCase))
        {
            plainContent = string.IsNullOrWhiteSpace(plainContent)
                ? albumToken
                : $"{plainContent.TrimEnd()} {albumToken}";
        }

        string finalContent = plainContent;
        if (channel.IsEncrypted)
        {
            try
            {
                finalContent = _crypto.EncryptAes(plainContent, plainCurrentChannelKey!);
                message.IsEncrypted = true;
                message.KeyEpoch = channel.CurrentKeyEpoch;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConvertMessageImagesToAlbum failed: Failed to re-encrypt message {MessageId}", messageId);
                return SpokesResult<ChatMessage>.Failure($"Failed to re-encrypt message: {ex.Message}", "CRYPTO_FAILED");
            }
        }

        var previousUserIds = message.MentionedUserIds.ToList();
        var previousAlbumIds = message.MentionedAlbumIds.ToList();
        var previousMentionsTeam = message.MentionsTeam;
        var previousMentionsEveryone = message.MentionsEveryone;

        message.Content = finalContent;
        message.Attachments = remainingAttachments;
        message.EditedAt = DateTime.UtcNow;

        var saveResult = ApplyEditedMentionsAndSave(
            message,
            channel,
            plainContent,
            previousUserIds,
            previousAlbumIds,
            previousMentionsTeam,
            previousMentionsEveryone,
            privateKey,
            "after converting images to album",
            "Failed to persist updated message.");
        if (!saveResult.IsSuccess)
        {
            return SpokesResult<ChatMessage>.Failure(saveResult.ErrorMessage ?? "Failed to synchronize album share.", saveResult.Code);
        }

        _chatState.NotifyMessageEdited(message);

        return SpokesResult<ChatMessage>.Success(message);
    }

    private SpokesResult ApplyEditedMentionsAndSave(
        ChatMessage message,
        ChatChannel channel,
        string? plainContent,
        List<string> previousUserIds,
        List<string> previousAlbumIds,
        bool previousMentionsTeam,
        bool previousMentionsEveryone,
        string? requestingUserPrivateKey,
        string saveLogContext,
        string saveFailureMessage)
    {
        var tokens = ExtractMessageTokens(plainContent);
        ApplyTokensAndResolveMentions(message, tokens);
        var syncResult = SynchronizeMentionedAlbums(message, channel, previousAlbumIds, requestingUserPrivateKey);
        if (!syncResult.IsSuccess)
        {
            return syncResult;
        }
        RouteEditMentionNotifications(message, channel, previousUserIds, previousMentionsTeam, previousMentionsEveryone);

        try
        {
            _messages.Save(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId} {Context}", message.Id, saveLogContext);
            return SpokesResult.Failure(saveFailureMessage, "SAVE_FAILED");
        }

        return SpokesResult.Success();
    }

    internal sealed class ParsedMessageTokens
    {
        public List<string> MentionedDisplayNames { get; set; } = [];
        public bool MentionsEveryone { get; set; }
        public List<string> MentionedEventIds { get; set; } = [];
        public List<string> MentionedAlbumIds { get; set; } = [];
        public List<string> MentionedPollIds { get; set; } = [];
    }

    internal static ParsedMessageTokens ExtractMessageTokens(string? plainContent)
    {
        var tokens = new ParsedMessageTokens();
        if (string.IsNullOrEmpty(plainContent))
            return tokens;

        var mentionMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"@\[([^\]]+)\]");
        foreach (System.Text.RegularExpressions.Match match in mentionMatches)
        {
            var displayName = match.Groups[1].Value;
            if (!tokens.MentionedDisplayNames.Contains(displayName, StringComparer.OrdinalIgnoreCase))
            {
                tokens.MentionedDisplayNames.Add(displayName);
            }
        }

        tokens.MentionsEveryone = System.Text.RegularExpressions.Regex.IsMatch(plainContent, @"@everyone\b");

        var eventMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"#\[([^\]]+)\]\(event:([^\)]+)\)");
        foreach (System.Text.RegularExpressions.Match match in eventMatches)
        {
            var eventId = match.Groups[2].Value;
            if (!tokens.MentionedEventIds.Contains(eventId))
            {
                tokens.MentionedEventIds.Add(eventId);
            }
        }

        var albumMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"#\[([^\]]+)\]\(album:([^\)]+)\)");
        foreach (System.Text.RegularExpressions.Match match in albumMatches)
        {
            var albumId = match.Groups[2].Value;
            if (!tokens.MentionedAlbumIds.Contains(albumId))
            {
                tokens.MentionedAlbumIds.Add(albumId);
            }
        }

        var pollMatches = System.Text.RegularExpressions.Regex.Matches(plainContent, @"#\[([^\]]+)\]\(poll:([^\)]+)\)");
        foreach (System.Text.RegularExpressions.Match match in pollMatches)
        {
            var pollId = match.Groups[2].Value;
            if (!tokens.MentionedPollIds.Contains(pollId))
            {
                tokens.MentionedPollIds.Add(pollId);
            }
        }

        return tokens;
    }

    private void ApplyTokensAndResolveMentions(ChatMessage message, ParsedMessageTokens tokens)
    {
        message.MentionedUserIds.Clear();
        message.MentionedEventIds.Clear();
        message.MentionedAlbumIds.Clear();
        message.MentionedPollIds.Clear();
        message.MentionsTeam = false;
        message.MentionsEveryone = tokens.MentionsEveryone;

        message.MentionedEventIds.AddRange(tokens.MentionedEventIds);
        message.MentionedAlbumIds.AddRange(tokens.MentionedAlbumIds);
        message.MentionedPollIds.AddRange(tokens.MentionedPollIds);

        if (tokens.MentionedDisplayNames.Count == 0)
            return;

        var employees = _employees.GetAll().ToList();
        var teams = _teams.GetAll().ToList();

        var employeeLookup = employees
            .GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var teamLookup = teams
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var teamMembersLookup = employees
            .Where(e => !string.IsNullOrEmpty(e.TeamId))
            .GroupBy(e => e.TeamId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var displayName in tokens.MentionedDisplayNames)
        {
            if (employeeLookup.TryGetValue(displayName, out var mentionedEmployee))
            {
                if (!message.MentionedUserIds.Contains(mentionedEmployee.Id))
                    message.MentionedUserIds.Add(mentionedEmployee.Id);
                continue;
            }

            if (teamLookup.TryGetValue(displayName, out var team))
            {
                message.MentionsTeam = true;
                if (!string.IsNullOrEmpty(team.LeaderId) && !message.MentionedUserIds.Contains(team.LeaderId))
                {
                    message.MentionedUserIds.Add(team.LeaderId);
                }
                if (teamMembersLookup.TryGetValue(team.Id, out var members))
                {
                    foreach (var member in members)
                    {
                        if (!message.MentionedUserIds.Contains(member.Id))
                            message.MentionedUserIds.Add(member.Id);
                    }
                }
            }
        }
    }

    private SpokesResult SynchronizeMentionedAlbums(ChatMessage message, ChatChannel channel, IReadOnlyCollection<string> previousAlbumIds, string? privateKey)
    {
        var newAlbumIds = message.MentionedAlbumIds.Except(previousAlbumIds).ToList();
        if (newAlbumIds.Count > 0)
        {
            var sender = _employees.GetById(message.SenderId);
            if (sender is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
                !sender.HasPermission(AppPermissions.Albums.View))
            {
                message.MentionedAlbumIds.RemoveAll(id => newAlbumIds.Contains(id));
                newAlbumIds.Clear();
            }
        }

        foreach (var albumId in newAlbumIds)
        {
            try
            {
                var shareResult = _albumService.ShareAlbumWithChannel(albumId, message.SenderId, channel.Id, privateKey);
                if (!shareResult.IsSuccess)
                {
                    if (string.Equals(shareResult.Code, "FORBIDDEN", StringComparison.Ordinal))
                    {
                        message.MentionedAlbumIds.Remove(albumId);
                        continue;
                    }

                    if (!string.Equals(shareResult.Code, "NOT_FOUND", StringComparison.Ordinal))
                    {
                        _logger.LogWarning("Failed to share album {AlbumId} with channel {ChannelId}: {Error}", albumId, channel.Id, shareResult.ErrorMessage);
                        return shareResult;
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Encryption error sharing album {AlbumId} with channel {ChannelId} in message {MessageId}", albumId, channel.Id, message.Id);
                return SpokesResult.Failure($"Cannot share album '{albumId}': {ex.Message}", "ENCRYPTION_ERROR");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to share album {AlbumId} with channel {ChannelId} mentioned in message {MessageId}", albumId, channel.Id, message.Id);
            }
        }

        var removedAlbumIds = previousAlbumIds.Except(message.MentionedAlbumIds).ToList();
        foreach (var albumId in removedAlbumIds)
        {
            if (!HasOtherAlbumMentionsInChannel(channel.Id, message.Id, albumId))
            {
                try
                {
                    var unshareResult = _albumService.UnshareAlbumWithChannel(albumId, message.SenderId, channel.Id);
                    if (!unshareResult.IsSuccess)
                    {
                        _logger.LogWarning("Failed to unshare album {AlbumId} with channel {ChannelId}: {Error}", albumId, channel.Id, unshareResult.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unshare album {AlbumId} with channel {ChannelId}", albumId, channel.Id);
                }
            }
        }

        return SpokesResult.Success();
    }

    private bool HasOtherAlbumMentionsInChannel(string channelId, string excludeMessageId, string albumId)
    {
        var index = _messages.GetIndexForChannel(channelId);
        foreach (var entry in index)
        {
            if (entry.Id == excludeMessageId || entry.IsDeleted)
            {
                continue;
            }

            // 1. Fast-path: Check indexed album metadata (in-memory, 0 disk I/O)
            if (entry.MentionedAlbumIds != null && entry.MentionedAlbumIds.Contains(albumId))
            {
                return true;
            }

            // 2. Query in-memory message cache if available
            var cached = _messages.GetCachedMessage(entry.Id);
            if (cached != null)
            {
                if (cached.MentionedAlbumIds != null && cached.MentionedAlbumIds.Contains(albumId))
                {
                    return true;
                }
                continue;
            }

            // 3. Pre-filter by index metadata: only inspect disk if SearchText explicitly contains the album ID
            if (entry.SearchText != null && entry.SearchText.Contains(albumId, StringComparison.OrdinalIgnoreCase))
            {
                var msg = _messages.GetByIdOrLoad(channelId, entry.Id);
                if (msg != null && msg.MentionedAlbumIds != null && msg.MentionedAlbumIds.Contains(albumId))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void RouteEditMentionNotifications(ChatMessage message, ChatChannel channel, IReadOnlyCollection<string> previousUserIds, bool wasTeamMentioned, bool wasEveryoneMentioned)
    {
        var newlyMentionedUserIds = message.MentionedUserIds.Except(previousUserIds).ToList();
        var newlyMentionedTeam = !wasTeamMentioned && message.MentionsTeam;
        var newlyMentionedEveryone = !wasEveryoneMentioned && message.MentionsEveryone;

        if (!newlyMentionedUserIds.Any() && !newlyMentionedTeam && !newlyMentionedEveryone)
            return;

        var sender = _employees.GetById(message.SenderId);
        if (sender == null)
            return;

        var notificationMessage = new ChatMessage
        {
            Id = message.Id,
            ChannelId = message.ChannelId,
            SenderId = message.SenderId,
            Content = message.Content,
            IsEncrypted = message.IsEncrypted,
            MentionedUserIds = newlyMentionedUserIds,
            MentionsTeam = newlyMentionedTeam,
            MentionsEveryone = newlyMentionedEveryone
        };

        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteChatNotificationAsync(notificationMessage, channel, sender), "chat message edit");
    }

    public async Task<SpokesResult> AdminDeleteMessageAsync(string messageId, string requestingUserId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var actor = _employees.GetById(requestingUserId);
        if (actor == null || !actor.IsActive || actor.IsSuspended || actor.IsBanned ||
            (!actor.IsAdmin && (!actor.HasPermission(AppPermissions.Chat.Moderator) || !actor.HasPermission(AppPermissions.Chat.Use))))
        {
            _logger.LogWarning("AdminDeleteMessageAsync rejected: user {UserId} lacks moderation privileges", requestingUserId);
            return SpokesResult.Failure("User is not authorized to perform moderation message deletion.", "ACCESS_DENIED");
        }

        var message = _messages.GetById(messageId);
        if (message == null) return SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND");

        var channel = _channels.GetById(message.ChannelId);
        if (!actor.IsAdmin)
        {
            if (channel == null ||
                string.Equals(channel.ChannelType, ChatChannelType.Direct, StringComparison.Ordinal) ||
                string.Equals(channel.ChannelType, ChatChannelType.Group, StringComparison.Ordinal) ||
                !_chatAuth.CanUserAccessChannel(channel, actor))
            {
                _logger.LogWarning("AdminDeleteMessageAsync rejected: moderator {UserId} lacks channel access to {ChannelId}", requestingUserId, message.ChannelId);
                return SpokesResult.Failure("User is not authorized to moderate messages in this channel.", "ACCESS_DENIED");
            }

            var targetSender = string.IsNullOrWhiteSpace(message.SenderId) ? null : _employees.GetById(message.SenderId);
            if (!Spokes_Server.Core.Services.Identity.UserService.CanModerateUserTarget(actor, targetSender))
            {
                _logger.LogWarning("AdminDeleteMessageAsync rejected: moderator {UserId} failed target hierarchy check for {TargetUserId}", requestingUserId, message.SenderId);
                return SpokesResult.Failure("User is not authorized to moderate messages from this user.", "ACCESS_DENIED");
            }
        }

        await ScrubAndDeleteMessageCoreAsync(message, channel, cancellationToken);
        return SpokesResult.Success();
    }

    public virtual async Task<SpokesResult> DeleteMessageAsync(string userId, string messageId, CancellationToken cancellationToken = default)
    {
        var message = _messages.GetById(messageId);
        if (message == null) return SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND");

        var channel = _channels.GetById(message.ChannelId);
        var employee = _employees.GetById(userId);

        if (!_chatAuth.CanUserDeleteMessage(message, channel, employee))
        {
            _logger.LogWarning("DeleteMessage failed: User {UserId} denied delete access to Message {MessageId}", userId, messageId);
            return SpokesResult.Failure("User is not authorized to delete this message.", "FORBIDDEN");
        }

        await ScrubAndDeleteMessageCoreAsync(message, channel, cancellationToken);
        return SpokesResult.Success();
    }

    private async Task ScrubAndDeleteMessageCoreAsync(ChatMessage message, ChatChannel? channel, CancellationToken cancellationToken)
    {
        var previousAlbumIds = message.MentionedAlbumIds?.ToList() ?? [];

        if (message.Attachments != null && message.Attachments.Count > 0)
        {
            foreach (var att in message.Attachments)
            {
                if (!string.IsNullOrEmpty(att.FilePath))
                {
                    try
                    {
                        await _fileService.DeleteAsync(att.FilePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ScrubAndDeleteMessageCoreAsync: Failed to delete attachment {FilePath}", att.FilePath);
                    }
                }
            }
            message.Attachments.Clear();
        }

        message.Reactions?.Clear();
        message.MentionedUserIds?.Clear();
        message.MentionedAlbumIds?.Clear();
        message.MentionedPollIds?.Clear();
        message.MentionedEventIds?.Clear();
        message.MentionsTeam = false;
        message.MentionsEveryone = false;
        message.IsDeleted = true;
        message.Content = string.Empty;
        _messages.Save(message);

        if (_reportedMessages != null)
        {
            var reportsForMessage = _reportedMessages.GetAll()
                .Where(r => string.Equals(r.MessageId, message.Id, StringComparison.Ordinal))
                .ToList();
            foreach (var report in reportsForMessage)
            {
                report.SnapshotContent = ReportedMessageResolution.DeletedContentPlaceholder;
                report.Attachments?.Clear();
                _reportedMessages.Save(report);
            }
        }

        if (channel != null && previousAlbumIds.Count > 0)
        {
            foreach (var albumId in previousAlbumIds)
            {
                if (!HasOtherAlbumMentionsInChannel(channel.Id, message.Id, albumId))
                {
                    try
                    {
                        _albumService.RemoveChannelShareForScrubbedMentions(albumId, channel.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ScrubAndDeleteMessageCoreAsync: Failed to unshare album {AlbumId} from channel {ChannelId}", albumId, channel.Id);
                    }
                }
            }
        }

        _chatState.NotifyMessageDeleted(message.ChannelId, message.Id);
        _notificationQueue?.PurgeForChatMessage(message.ChannelId, message.Id);
    }

    public virtual Task<SpokesResult> ToggleReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return Task.FromResult(SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND"));
        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        var message = _messages.GetById(messageId);
        if (message == null) return Task.FromResult(SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND"));

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee))
        {
            _logger.LogWarning("ToggleReaction failed: User {UserId} denied access to Channel {ChannelId}", employee.Id, message.ChannelId);
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        if (_privacyService != null && !_privacyService.CanReactInChannel(channel, employee))
        {
            return Task.FromResult(SpokesResult.Failure("Reactions are disabled in this channel.", "FORBIDDEN"));
        }
        else if (_privacyService == null && !_chatAuth.CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("ToggleReaction failed: User {UserId} denied post access to Channel {ChannelId}", employee.Id, message.ChannelId);
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        var reactionKey = $"{emoji}:{userId}";

        if (message.Reactions.Contains(reactionKey))
        {
            // Remove the reaction
            message.Reactions.Remove(reactionKey);
            _messages.Save(message);

            _chatState.NotifyReactionRemoved(message.ChannelId, message.Id, emoji, employee.Id);
        }
        else
        {
            // Add the reaction
            message.Reactions.Add(reactionKey);
            _messages.Save(message);

            _chatState.NotifyReactionAdded(message.ChannelId, message.Id, emoji, employee.Id, employee.FullName);

            // Push Notifications
            if (channel != null)
            {
                _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteReactionNotificationAsync(message, channel, emoji, employee), "chat reaction");
            }
        }

        return Task.FromResult(SpokesResult.Success());
    }

    public virtual Task<SpokesResult> AddReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default) =>
        SetReactionStateAsync(userId, messageId, emoji, shouldExist: true, cancellationToken);

    public virtual Task<SpokesResult> RemoveReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default) =>
        SetReactionStateAsync(userId, messageId, emoji, shouldExist: false, cancellationToken);

    private Task<SpokesResult> SetReactionStateAsync(string userId, string messageId, string emoji, bool shouldExist, CancellationToken cancellationToken)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return Task.FromResult(SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND"));
        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        var message = _messages.GetById(messageId);
        if (message != null && message.Reactions.Contains($"{emoji}:{userId}") == shouldExist)
        {
            return Task.FromResult(SpokesResult.Success());
        }
        return ToggleReactionAsync(userId, messageId, emoji, cancellationToken);
    }

    private Employee EnsureActorCanCreateChannel(string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
        {
            throw new UnauthorizedAccessException("User ID is required to use chat or create channels.");
        }

        var actor = _employees.GetById(actorUserId);
        if (actor == null || !actor.IsActive || actor.IsSuspended || actor.IsBanned || !actor.HasPermission(AppPermissions.Chat.Use))
        {
            throw new UnauthorizedAccessException("User is not authorized to use chat or create channels.");
        }

        return actor;
    }

    private bool CanAccessLinkedProject(Employee actor, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        var project = _projects.GetById(projectId);
        return project != null && ProjectAccessHelper.CanAccess(actor, project, _chatAuth.GetUserTeamIds(actor));
    }

    public ChatChannel CreateDirectMessageChannel(string initiatorUserId, string targetUserId)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");

        var actor = EnsureActorCanCreateChannel(initiatorUserId);
        if (_privacyService != null)
        {
            var target = _employees.GetById(targetUserId);
            if (target != null && !_privacyService.CanDiscoverUser(actor, target))
            {
                throw new UnauthorizedAccessException("User is not authorized to initiate a direct conversation with this participant.");
            }
        }

        var channel = _channels.GetOrCreateDirectChannel(initiatorUserId, targetUserId, false, createdById: initiatorUserId);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public ChatChannel CreateGroupDirectMessageChannel(string initiatorUserId, List<string> targetUserIds)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");

        var actor = EnsureActorCanCreateChannel(initiatorUserId);
        if (_privacyService != null && targetUserIds != null)
        {
            foreach (var otherId in targetUserIds.Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, actor.Id, StringComparison.Ordinal)))
            {
                var target = _employees.GetById(otherId);
                if (target != null && !_privacyService.CanDiscoverUser(actor, target))
                {
                    throw new UnauthorizedAccessException("User is not authorized to initiate a direct conversation with this participant.");
                }
            }
        }

        var allParticipantIds = new List<string> { initiatorUserId };
        if (targetUserIds != null)
        {
            allParticipantIds.AddRange(targetUserIds);
        }
        allParticipantIds = allParticipantIds.Distinct().ToList();

        var channel = _channels.CreateGroupChannel(allParticipantIds, "", false, createdById: initiatorUserId);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    private void ValidateTeamChannelTargets(Employee actor, IReadOnlyCollection<string> teamIds)
    {
        var distinctTeamIds = teamIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinctTeamIds.Count == 0)
        {
            throw new ArgumentException("At least one team ID is required.", nameof(teamIds));
        }

        if (distinctTeamIds.Any(id => _teams.GetById(id) == null))
        {
            throw new ArgumentException("One or more specified teams were not found.", nameof(teamIds));
        }

        if (actor.IsAdmin)
        {
            return;
        }

        var actorTeamIds = _chatAuth.GetUserTeamIds(actor);
        if (distinctTeamIds.Any(id => !actorTeamIds.Contains(id)))
        {
            throw new UnauthorizedAccessException("User can only create team channels for teams they belong to.");
        }

        if (_privacyService != null)
        {
            var discoverableTeamIds = _privacyService
                .FilterDiscoverableTeams(actor, _teams.GetAll())
                .Select(t => t.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (distinctTeamIds.Any(id => !discoverableTeamIds.Contains(id)))
            {
                throw new UnauthorizedAccessException("User can only target discoverable teams.");
            }
        }
    }

    public ChatChannel CreateTeamChannel(string initiatorUserId, List<string> teamIds)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");
        if (teamIds == null || teamIds.Count == 0)
            throw new ArgumentException("At least one team ID is required.", nameof(teamIds));

        var actor = EnsureActorCanCreateChannel(initiatorUserId);
        if (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.CreateChannels))
        {
            throw new UnauthorizedAccessException("User is not authorized to create channels.");
        }

        var distinctTeamIds = teamIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        ValidateTeamChannelTargets(actor, distinctTeamIds);

        if (distinctTeamIds.Count == 1)
        {
            var existingTeamChannel = _channels.GetTeamChannel(distinctTeamIds[0]);
            if (existingTeamChannel != null)
            {
                return existingTeamChannel;
            }
        }

        var teamNames = distinctTeamIds
            .Select(id => _teams.GetById(id))
            .Where(t => t != null)
            .Select(t => t!.Name)
            .ToList();
        var defaultName = string.Join(", ", teamNames);

        var channel = new ChatChannel
        {
            Name = defaultName,
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = distinctTeamIds.Count == 1 ? distinctTeamIds[0] : null,
            AllowedTeamIds = distinctTeamIds,
            ParticipantIds = new List<string>(),
            CreatedById = initiatorUserId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsEncrypted = false
        };

        _channels.Save(channel);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public ChatChannel CreateChannel(ChatChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var actor = EnsureActorCanCreateChannel(channel.CreatedById);

        if (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group)
        {
            if (channel.ParticipantIds == null || !channel.ParticipantIds.Contains(actor.Id))
            {
                throw new UnauthorizedAccessException("Creator must be a participant in direct or group channels.");
            }

            if (_privacyService != null)
            {
                foreach (var otherId in channel.ParticipantIds.Where(id => !string.Equals(id, actor.Id, StringComparison.Ordinal)))
                {
                    var target = _employees.GetById(otherId);
                    if (target != null && !_privacyService.CanDiscoverUser(actor, target))
                    {
                        throw new UnauthorizedAccessException("User is not authorized to initiate a direct conversation with this participant.");
                    }
                }
            }
        }
        else
        {
            if (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.CreateChannels))
            {
                throw new UnauthorizedAccessException("User is not authorized to create channels.");
            }

            if (channel.ChannelType == ChatChannelType.Project && !CanAccessLinkedProject(actor, channel.LinkedEntityId))
            {
                throw new UnauthorizedAccessException("User is not authorized to create a channel for this project.");
            }

            if (channel.ChannelType == ChatChannelType.Team)
            {
                var targetTeamIds = (channel.AllowedTeamIds ?? []).ToList();
                if (!string.IsNullOrWhiteSpace(channel.LinkedEntityId) && !targetTeamIds.Contains(channel.LinkedEntityId))
                {
                    targetTeamIds.Add(channel.LinkedEntityId);
                }

                ValidateTeamChannelTargets(actor, targetTeamIds);
            }

            if (channel.ChannelType == ChatChannelType.General)
            {
                bool isPublicGeneral = (channel.ParticipantIds?.Count ?? 0) == 0 && (channel.AllowedTeamIds?.Count ?? 0) == 0;
                if (isPublicGeneral && _privacyService != null && !_privacyService.CanCreatePublicChannels(actor))
                {
                    throw new UnauthorizedAccessException("User is not authorized to create public channels.");
                }

                if (!actor.IsAdmin && channel.AllowedTeamIds != null && channel.AllowedTeamIds.Count > 0)
                {
                    var actorTeamIds = _chatAuth.GetUserTeamIds(actor);
                    if (channel.AllowedTeamIds.Any(id => !actorTeamIds.Contains(id)))
                    {
                        throw new UnauthorizedAccessException("User can only create team-scoped channels for teams they belong to.");
                    }
                }
            }
        }

        channel.Normalize();
        channel.IsEncrypted = false;
        _channels.Save(channel);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public SpokesResult UpdateChannel(ChatChannel channel, string requestingUserId, bool isSessionUnlocked = true)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var existingSnapshot = !string.IsNullOrEmpty(channel.Id) ? _channels.GetPersistedSnapshot(channel.Id) : null;
        var existing = existingSnapshot ?? (!string.IsNullOrEmpty(channel.Id) ? _channels.GetById(channel.Id) : null);

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: user {UserId} is missing, inactive, suspended, or banned", requestingUserId);
            return SpokesResult.Failure("User is not authorized to modify this channel.", "ACCESS_DENIED");
        }

        var authTarget = existing ?? channel;

        if ((authTarget.IsEncrypted || channel.IsEncrypted) && !isSessionUnlocked)
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: session vault is locked for encrypted channel {ChannelId}", channel.Id);
            return SpokesResult.Failure("Session must have secure chat unlocked to modify an encrypted channel.", "VAULT_LOCKED");
        }

        if (existing != null && !string.Equals(existing.ChannelType, channel.ChannelType, StringComparison.Ordinal) &&
            (existing.ChannelType == ChatChannelType.Direct ||
             existing.ChannelType == ChatChannelType.Group ||
             existing.ChannelType == ChatChannelType.Project ||
             channel.ChannelType == ChatChannelType.Direct ||
             channel.ChannelType == ChatChannelType.Group ||
             channel.ChannelType == ChatChannelType.Project))
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: attempted ChannelType mutation on restricted channel {ChannelId}", channel.Id);
            return SpokesResult.Failure("Channel type cannot be altered on direct, group, or project channels.", "ACCESS_DENIED");
        }

        if (existing != null &&
            existing.ChannelType == ChatChannelType.Project &&
            !string.Equals(existing.LinkedEntityId, channel.LinkedEntityId, StringComparison.Ordinal))
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: attempted LinkedEntityId mutation on project channel {ChannelId}", channel.Id);
            return SpokesResult.Failure("Linked project cannot be altered on a project channel.", "ACCESS_DENIED");
        }

        if (channel.ChannelType == ChatChannelType.Project && !CanAccessLinkedProject(employee, channel.LinkedEntityId))
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: user {UserId} cannot access linked project {ProjectId} for channel {ChannelId}", requestingUserId, channel.LinkedEntityId, channel.Id);
            return SpokesResult.Failure("User is not authorized to bind or update a channel for this project.", "ACCESS_DENIED");
        }

        if (!_chatAuth.CanUserManageChannel(authTarget, employee, isSessionUnlocked))
        {
            RestoreRejectedChannelMutation(existingSnapshot, channel);
            _logger.LogWarning("UpdateChannel rejected: user {UserId} not authorized to manage channel {ChannelId}", requestingUserId, channel.Id);
            return SpokesResult.Failure("User is not authorized to modify this channel.", "ACCESS_DENIED");
        }

        channel.Normalize();

        EvictRevokedChannelUsers(existing, channel);

        // 1. Save to DB
        _channels.Save(channel);

        // 2. Notify local subscribers
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    public SpokesResult DeleteChannel(string channelId, string requestingUserId, bool isSessionUnlocked = true)
    {
        var auth = AuthorizeChannelLifecycleAction(
            channelId,
            requestingUserId,
            isSessionUnlocked,
            "DeleteChannel",
            "delete",
            (ch, emp, unlocked) => _chatAuth.CanUserDeleteChannel(ch, emp, unlocked));
        if (!auth.IsSuccess)
        {
            return SpokesResult.Failure(auth.ErrorMessage!, auth.Code);
        }

        var channel = auth.Value!;
        var impactedUserIds = CollectAllChannelMemberAndKeyIds(channel);

        PurgeChannelCascade(
            channelId,
            _channels,
            _messages,
            _readStates,
            _polls,
            _reportedMessages,
            _albumService,
            albumsFallback: null,
            _fileTokenService,
            _notificationQueue,
            deleteAttachmentFile: (filePath, messageId) =>
            {
                try
                {
                    _fileService.DeleteAsync(filePath).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "DeleteChannel: failed to delete attachment {FilePath} for message {MessageId}", filePath, messageId);
                }
            });

        EvictUsersFromTransportAndTokens(channelId, impactedUserIds);
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    /// <summary>
    /// Canonical cascading deletion for a chat channel and all of its persisted sub-entities
    /// (messages, physical attachments, channel storage directory, read states, polls, moderation reports,
    /// queued notifications, album shares/channel escrow keys, and file tokens).
    /// Shared by <see cref="DeleteChannel"/> and <see cref="ProjectService.DeleteProject"/>.
    /// </summary>
    internal static void PurgeChannelCascade(
        string channelId,
        ChatChannelRepository? channels,
        ChatMessageRepository? messages,
        ChatReadStateRepository? readStates,
        ChatPollRepository? polls,
        ReportedMessageRepository? reportedMessages,
        AlbumService? albumService,
        AlbumRepository? albumsFallback,
        FileTokenService? fileTokenService,
        NotificationQueueService? notificationQueue,
        Action<string, string> deleteAttachmentFile)
    {
        channels?.Delete(channelId);

        var msgIds = new HashSet<string>(StringComparer.Ordinal);
        if (messages != null)
        {
            foreach (var m in messages.GetAllByChannelIncludingCold(channelId))
            {
                msgIds.Add(m.Id);
                if (m.Attachments != null)
                {
                    foreach (var att in m.Attachments)
                    {
                        if (!string.IsNullOrWhiteSpace(att.FilePath))
                        {
                            deleteAttachmentFile(att.FilePath, m.Id);
                        }
                    }
                }
                messages.Delete(m.Id);
            }
            messages.PurgeChannelDirectory(channelId);
        }

        notificationQueue?.PurgeForChannel(channelId);

        if (readStates != null)
        {
            foreach (var rs in readStates.GetAll().Where(rs => rs.ChannelId == channelId).ToList())
            {
                readStates.Delete(rs.Id);
            }
        }

        if (polls != null)
        {
            foreach (var poll in polls.GetAll().Where(p => string.Equals(p.ChannelId, channelId, StringComparison.Ordinal)).ToList())
            {
                polls.Delete(poll.Id);
            }
        }

        if (reportedMessages != null)
        {
            foreach (var report in reportedMessages.GetAll()
                .Where(r => string.Equals(r.ChannelId, channelId, StringComparison.Ordinal) ||
                            (!string.IsNullOrEmpty(r.MessageId) && msgIds.Contains(r.MessageId)))
                .ToList())
            {
                reportedMessages.Delete(report.Id);
                notificationQueue?.PurgeForModerationReport(report.Id);
            }
        }

        if (albumService != null)
        {
            albumService.RemoveDeletedChannelShares(channelId);
        }
        else if (albumsFallback != null)
        {
            var channelKeyName = $"Channel_{channelId}";
            foreach (var album in albumsFallback.GetAll()
                .Where(a => a.SharedWithChannelIds.Contains(channelId) || a.EncryptedAlbumKeys.ContainsKey(channelKeyName))
                .ToList())
            {
                album.SharedWithChannelIds.RemoveAll(id => string.Equals(id, channelId, StringComparison.Ordinal));
                album.EncryptedAlbumKeys.Remove(channelKeyName);
                albumsFallback.Save(album);
            }
        }

        fileTokenService?.InvalidateContextTokens("chat", channelId);
    }

    public SpokesResult ArchiveChannel(string channelId, bool archive, string requestingUserId, bool isSessionUnlocked = true)
    {
        var auth = AuthorizeChannelLifecycleAction(
            channelId,
            requestingUserId,
            isSessionUnlocked,
            nameof(ArchiveChannel),
            "archive",
            (ch, emp, unlocked) => _chatAuth.CanUserManageChannel(ch, emp, unlocked));
        if (!auth.IsSuccess)
        {
            return SpokesResult.Failure(auth.ErrorMessage!, auth.Code);
        }

        var channel = auth.Value!;
        channel.IsArchived = archive;
        channel.Normalize();
        _channels.Save(channel);

        if (archive)
        {
            var memberIds = CollectAllChannelMemberAndKeyIds(channel);
            EvictUsersFromTransportAndTokens(channelId, memberIds, invalidateTokens: false);
        }

        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    private SpokesResult<ChatChannel> AuthorizeChannelLifecycleAction(
        string channelId,
        string requestingUserId,
        bool isSessionUnlocked,
        string actionName,
        string verb,
        Func<ChatChannel, Employee, bool, bool> authorize)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return SpokesResult<ChatChannel>.Failure("Channel ID is required.", "INVALID_INPUT");
        }

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult<ChatChannel>.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return SpokesResult<ChatChannel>.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("{Action} rejected: user {UserId} is missing, inactive, suspended, or banned", actionName, requestingUserId);
            return SpokesResult<ChatChannel>.Failure($"User is not authorized to {verb} this channel.", "ACCESS_DENIED");
        }

        if (channel.IsEncrypted && !isSessionUnlocked)
        {
            _logger.LogWarning("{Action} rejected: session vault is locked for encrypted channel {ChannelId}", actionName, channelId);
            return SpokesResult<ChatChannel>.Failure($"Session must have secure chat unlocked to {verb} an encrypted channel.", "VAULT_LOCKED");
        }

        if (!authorize(channel, employee, isSessionUnlocked))
        {
            _logger.LogWarning("{Action} rejected: user {UserId} not authorized to {Verb} channel {ChannelId}", actionName, requestingUserId, verb, channelId);
            return SpokesResult<ChatChannel>.Failure($"User is not authorized to {verb} this channel.", "ACCESS_DENIED");
        }

        return SpokesResult<ChatChannel>.Success(channel);
    }

    public SpokesResult UpdateChannelOrders(Dictionary<string, int> channelOrders, string requestingUserId)
    {
        if (channelOrders == null)
        {
            return SpokesResult.Failure("Channel orders are required.", "INVALID_INPUT");
        }

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned ||
            (!employee.IsAdmin && !employee.HasPermission(AppPermissions.Chat.EditAllPublicChannels)))
        {
            _logger.LogWarning("UpdateChannelOrders rejected: user {UserId} lacks channel ordering permissions", requestingUserId);
            return SpokesResult.Failure("User is not authorized to reorder channels.", "ACCESS_DENIED");
        }

        foreach (var kvp in channelOrders)
        {
            var channel = _channels.GetById(kvp.Key);
            if (channel != null &&
                channel.ChannelType != ChatChannelType.Direct &&
                channel.ChannelType != ChatChannelType.Group)
            {
                channel.DisplayOrder = kvp.Value;
                _channels.Save(channel);
                _chatState.NotifyChannelCreated(channel);
            }
        }

        return SpokesResult.Success();
    }

    private bool TryAuthorizeCategoryManager(string requestingUserId, out Employee? actor)
    {
        actor = null;
        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return false;
        }

        var candidate = _employees.GetById(requestingUserId);
        if (candidate is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            !candidate.HasPermission(AppPermissions.Chat.Use) ||
            (!candidate.IsAdmin && !candidate.HasPermission(AppPermissions.Chat.EditAllPublicChannels)))
        {
            return false;
        }

        actor = candidate;
        return true;
    }

    public List<ChatCategory> GetCategories()
    {
        if (_categories == null) return [];
        return _categories.GetAll().OrderBy(c => c.DisplayOrder).ToList();
    }

    public int GetCategoryChannelCount(string categoryId, string requestingUserId)
    {
        if (!TryAuthorizeCategoryManager(requestingUserId, out var actor) || actor == null)
        {
            throw new UnauthorizedAccessException("User is not authorized to manage chat categories.");
        }

        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return 0;
        }

        bool canViewArchive = actor.IsAdmin || actor.HasPermission(AppPermissions.Chat.ViewArchive);
        return _channels.GetAll().Count(c =>
            string.Equals(c.CategoryId, categoryId, StringComparison.Ordinal) &&
            (!c.IsArchived || canViewArchive) &&
            _chatAuth.CanUserAccessChannel(c, actor));
    }

    public SpokesResult<ChatCategory> CreateCategory(string name, string requestingUserId)
    {
        if (!TryAuthorizeCategoryManager(requestingUserId, out _))
        {
            _logger.LogWarning("CreateCategory rejected: user {UserId} lacks category management permissions", requestingUserId);
            return SpokesResult<ChatCategory>.Failure("User is not authorized to manage chat categories.", "ACCESS_DENIED");
        }

        if (_categories == null)
        {
            return SpokesResult<ChatCategory>.Failure("Category repository is not configured.", "UNAVAILABLE");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return SpokesResult<ChatCategory>.Failure("Category name is required.", "INVALID_INPUT");
        }

        var categories = GetCategories();
        var publicCat = categories.FirstOrDefault(c => c.Id == "sys_public");
        var startOrder = publicCat?.DisplayOrder ?? -600;
        var projectsCat = categories.FirstOrDefault(c => c.Id == "sys_projects");
        var endOrder = projectsCat?.DisplayOrder ?? -500;

        var customCatsInSpace = categories
            .Where(c => !c.IsSystem && c.DisplayOrder > startOrder && c.DisplayOrder < endOrder)
            .ToList();
        var newOrder = customCatsInSpace.Count > 0 ? customCatsInSpace.Max(c => c.DisplayOrder) + 1 : startOrder + 1;

        if (newOrder >= endOrder)
        {
            foreach (var c in categories.Where(c => c.Id != "sys_archive" && c.DisplayOrder >= newOrder))
            {
                c.DisplayOrder++;
                _categories.Save(c);
            }
        }

        var newCat = new ChatCategory
        {
            Name = name.Trim(),
            DisplayOrder = newOrder
        };

        _categories.Save(newCat);
        return SpokesResult<ChatCategory>.Success(newCat);
    }

    public SpokesResult UpdateCategoryOrders(Dictionary<string, int> categoryOrders, string requestingUserId)
    {
        if (categoryOrders == null)
        {
            return SpokesResult.Failure("Category orders are required.", "INVALID_INPUT");
        }

        if (!TryAuthorizeCategoryManager(requestingUserId, out _))
        {
            _logger.LogWarning("UpdateCategoryOrders rejected: user {UserId} lacks category management permissions", requestingUserId);
            return SpokesResult.Failure("User is not authorized to reorder chat categories.", "ACCESS_DENIED");
        }

        if (_categories == null)
        {
            return SpokesResult.Failure("Category repository is not configured.", "UNAVAILABLE");
        }

        foreach (var kvp in categoryOrders)
        {
            var cat = _categories.GetById(kvp.Key);
            if (cat == null) continue;

            cat.DisplayOrder = string.Equals(cat.Id, "sys_archive", StringComparison.Ordinal)
                ? 10000
                : kvp.Value;
            _categories.Save(cat);
        }

        return SpokesResult.Success();
    }

    public SpokesResult DeleteCategory(string categoryId, string requestingUserId, bool isSessionUnlocked = true)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return SpokesResult.Failure("Category ID is required.", "INVALID_INPUT");
        }

        if (!TryAuthorizeCategoryManager(requestingUserId, out var actor) || actor == null)
        {
            _logger.LogWarning("DeleteCategory rejected: user {UserId} lacks category management permissions", requestingUserId);
            return SpokesResult.Failure("User is not authorized to delete chat categories.", "ACCESS_DENIED");
        }

        if (_categories == null)
        {
            return SpokesResult.Failure("Category repository is not configured.", "UNAVAILABLE");
        }

        var cat = _categories.GetById(categoryId);
        if (cat == null)
        {
            return SpokesResult.Failure("Category not found.", "NOT_FOUND");
        }

        if (cat.IsSystem)
        {
            return SpokesResult.Failure("System categories cannot be deleted.", "FORBIDDEN");
        }

        var channelsInCat = _channels.GetAll()
            .Where(c => string.Equals(c.CategoryId, categoryId, StringComparison.Ordinal))
            .ToList();

        bool canViewArchive = actor.IsAdmin || actor.HasPermission(AppPermissions.Chat.ViewArchive);
        foreach (var ch in channelsInCat)
        {
            if (ch.IsEncrypted && !isSessionUnlocked)
            {
                return SpokesResult.Failure("Session must have secure chat unlocked to modify encrypted channels in this category.", "VAULT_LOCKED");
            }

            if ((ch.IsArchived && !canViewArchive) ||
                !_chatAuth.CanUserAccessChannel(ch, actor) ||
                !_chatAuth.CanUserManageChannel(ch, actor, isSessionUnlocked))
            {
                _logger.LogWarning("DeleteCategory rejected: user {UserId} lacks authority over channel {ChannelId} in category {CategoryId}", requestingUserId, ch.Id, categoryId);
                return SpokesResult.Failure("Cannot delete a category containing channels you are not authorized to manage.", "ACCESS_DENIED");
            }
        }

        foreach (var ch in channelsInCat)
        {
            ch.CategoryId = null;
            _channels.Save(ch);
            _chatState.NotifyChannelCreated(ch);
        }

        _categories.Delete(cat.Id);
        return SpokesResult.Success();
    }

    public SpokesResult LeaveGroupChannel(string channelId, string requestingUserId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return SpokesResult.Failure("Channel ID is required.", "INVALID_INPUT");
        }

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return SpokesResult.Failure("User is not authorized to modify group membership.", "ACCESS_DENIED");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return SpokesResult.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        if (channel.ChannelType != ChatChannelType.Group)
        {
            return SpokesResult.Failure("Only group channels support leaving.", "INVALID_OPERATION");
        }

        bool isParticipant = (channel.ParticipantIds != null && channel.ParticipantIds.Contains(requestingUserId)) ||
                             string.Equals(channel.CreatedById, requestingUserId, StringComparison.Ordinal);
        if (!isParticipant)
        {
            return SpokesResult.Failure("User is not a participant of this group channel.", "ACCESS_DENIED");
        }

        var previousSnapshot = _channels.GetPersistedSnapshot(channelId) ?? channel.Clone();

        channel.ParticipantIds?.RemoveAll(id => string.Equals(id, requestingUserId, StringComparison.Ordinal));
        channel.AllowedPostUserIds?.RemoveAll(id => string.Equals(id, requestingUserId, StringComparison.Ordinal));

        if (string.Equals(channel.CreatedById, requestingUserId, StringComparison.Ordinal))
        {
            channel.CreatedById = channel.ParticipantIds != null && channel.ParticipantIds.Count > 0
                ? channel.ParticipantIds[0]
                : string.Empty;
        }

        EvictRevokedChannelUsers(previousSnapshot, channel, new[] { requestingUserId });

        _channels.Save(channel);
        _fileTokenService?.InvalidateUserEncryptedTokens(requestingUserId);
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    private void EvictRevokedChannelUsers(
        ChatChannel? previousChannel,
        ChatChannel updatedChannel,
        IEnumerable<string>? explicitEvictedIds = null)
    {
        var activeEmployees = _employees.GetAll()
            .Where(e => e.IsActive && !e.IsBanned)
            .ToList();

        var remainingAuthorized = activeEmployees
            .Where(e => _chatAuth.CanUserAccessChannel(updatedChannel, e))
            .ToList();
        var remainingIds = remainingAuthorized
            .Select(e => e.Id)
            .ToHashSet(StringComparer.Ordinal);

        var evictedIds = new HashSet<string>(StringComparer.Ordinal);
        if (explicitEvictedIds != null)
        {
            foreach (var uid in explicitEvictedIds)
            {
                if (!string.IsNullOrWhiteSpace(uid) && !remainingIds.Contains(uid))
                {
                    evictedIds.Add(uid);
                }
            }
        }

        if (previousChannel != null)
        {
            foreach (var emp in activeEmployees)
            {
                if (!remainingIds.Contains(emp.Id) && _chatAuth.CanUserAccessChannel(previousChannel, emp))
                {
                    evictedIds.Add(emp.Id);
                }
            }
        }

        foreach (var ownerId in updatedChannel.EncryptedChannelKeys.Keys)
        {
            if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal) && !remainingIds.Contains(ownerId))
            {
                evictedIds.Add(ownerId);
            }
        }

        foreach (var epochDict in updatedChannel.EncryptedEpochKeys.Values)
        {
            foreach (var ownerId in epochDict.Keys)
            {
                if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal) && !remainingIds.Contains(ownerId))
                {
                    evictedIds.Add(ownerId);
                }
            }
        }

        if (evictedIds.Count == 0)
        {
            return;
        }

        if (_securityService != null)
        {
            _securityService.RotateChannelKeyOnEviction(updatedChannel, evictedIds, remainingAuthorized);
        }
        else
        {
            foreach (var uid in evictedIds)
            {
                updatedChannel.RemoveUserKeysFromAllEpochs(uid);
            }
        }

        EvictUsersFromTransportAndTokens(updatedChannel.Id, evictedIds);
    }

    private HashSet<string> CollectAllChannelMemberAndKeyIds(ChatChannel channel)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emp in _employees.GetAll())
        {
            if (_chatAuth.CanUserAccessChannel(channel, emp))
            {
                ids.Add(emp.Id);
            }
        }
        foreach (var ownerId in channel.EncryptedChannelKeys.Keys)
        {
            if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal))
            {
                ids.Add(ownerId);
            }
        }
        foreach (var epochDict in channel.EncryptedEpochKeys.Values)
        {
            foreach (var ownerId in epochDict.Keys)
            {
                if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal))
                {
                    ids.Add(ownerId);
                }
            }
        }
        return ids;
    }

    private void EvictUsersFromTransportAndTokens(string channelId, IReadOnlyCollection<string> userIds, bool invalidateTokens = true)
    {
        if (userIds.Count == 0) return;

        foreach (var uid in userIds)
        {
            _notificationQueue?.PurgeForChannel(channelId, uid);
        }

        if (invalidateTokens)
        {
            foreach (var uid in userIds)
            {
                _fileTokenService?.InvalidateUserEncryptedTokens(uid);
            }
        }

        var snapshotIds = userIds.ToArray();

        if (_voiceService != null)
        {
            foreach (var uid in snapshotIds)
            {
                var capturedUid = uid;
                _ = SafeDispatchBackgroundAsync(
                    () => _voiceService.LeaveVoiceAsync(channelId, capturedUid, evictFromLiveKit: true),
                    $"evicting voice session for user {capturedUid} in channel {channelId}");
            }
        }
    }

    private void RestoreRejectedChannelMutation(ChatChannel? snapshot, ChatChannel attemptedChannel)
    {
        if (snapshot == null || string.IsNullOrEmpty(attemptedChannel.Id)) return;
        if (ReferenceEquals(_channels.GetById(attemptedChannel.Id), attemptedChannel))
        {
            _channels.RestoreFromSnapshot(snapshot);
        }
    }

    private async Task SafeDispatchBackgroundAsync(Func<Task> action, string operation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while {Operation}", operation);
        }
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation) =>
        NotificationDispatchHelper.SafeDispatchAsync(
            routeAction,
            operation,
            _logger,
            logMessageTemplate: "Failed to dispatch {Operation} notification in background");
}
