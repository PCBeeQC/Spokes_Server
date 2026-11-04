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
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class ChatService : IChatService
{
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly TeamRepository _teams;
    private readonly ChatReadStateRepository _readStates;
    private readonly ProjectRepository _projects;

    private readonly IChatRealtimeGateway _realtimeGateway;
    private readonly ChatStateService _chatState;
    private readonly INotificationRoutingService _notificationRouting;
    private readonly PresenceStateService _presenceState;
    private readonly ILogger<ChatService> _logger;
    private readonly IFileService _fileService;
    private readonly ICryptoService _crypto;
    private readonly IContentModerationService _moderationService;
    private readonly AlbumService _albumService;
    private readonly MarkdownSanitizerService _markdownSanitizer;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IUserPrivacyService? _privacyService;
    private readonly FileTokenService? _fileTokenService;

    [ActivatorUtilitiesConstructor]
    public ChatService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        ProjectsContext projectsContext,
        IChatRealtimeGateway realtimeGateway,
        ChatStateService chatState,
        INotificationRoutingService notificationRouting,
        PresenceStateService presenceState,
        IFileService fileService,
        IContentModerationService moderationService,
        AlbumService albumService,
        MarkdownSanitizerService markdownSanitizer,
        ILogger<ChatService> logger,
        IChatAuthorizationService? chatAuth = null,
        ICryptoService? crypto = null,
        IUserPrivacyService? privacyService = null,
        FileTokenService? fileTokenService = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(projectsContext);

        _employees = hrContext.Employees;
        _channels = communicationContext.ChatChannels;
        _messages = communicationContext.ChatMessages;
        _teams = hrContext.Teams;
        _readStates = communicationContext.ChatReadStates;
        _projects = projectsContext.Projects;

        _realtimeGateway = realtimeGateway;
        _chatState = chatState;
        _notificationRouting = notificationRouting;
        _presenceState = presenceState;
        _fileService = fileService;
        _crypto = crypto ?? new CryptoService();
        _moderationService = moderationService;
        _albumService = albumService;
        _markdownSanitizer = markdownSanitizer;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _privacyService = privacyService;
        _fileTokenService = fileTokenService;
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

            if (!channel.EncryptedChannelKeys.TryGetValue(employee.Id, out var cipherChannelKey))
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

                // Check if it's base64 (starts with "data:")
                if (att.FilePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (DataUriHelper.TryParse(att.FilePath, out var bytes, out _))
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
        SynchronizeMentionedAlbums(message, channel, previousAlbumIds: Array.Empty<string>(), privateKey);

        // --- ENCRYPTION (Data-at-Rest) ---
        if (channel.IsEncrypted)
        {
            if (plainChannelKey != null)
            {
                try
                {
                    message.Content = _crypto.EncryptAes(message.Content, plainChannelKey);
                    message.IsEncrypted = true;
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

        // 2. Broadcast to external clients via SignalR immediately
        await _realtimeGateway.SendToChannelAsync(channelId, "ReceiveMessage", new
        {
            Id = message.Id,
            MessageId = message.Id,
            message.ChannelId,
            message.SenderId,
            UserId = message.SenderId,
            SenderName = employee.FullName,
            message.Content,
            message.SentAt,
            message.ReplyToId,
            Attachments = message.Attachments.Select(a => new { a.Id, a.FileName, a.FilePath, a.ContentType, a.FileSizeBytes })
        }, cancellationToken);

        // 3. Delegate routing rules to the central notification routing service
        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteChatNotificationAsync(message, channel, employee), "chat message");

        // 4. Synchronize sender previous read state (offloaded after real-time message delivery)
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

    public async Task MarkMessagesAsReadAsync(string userId, string channelId, IReadOnlyCollection<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId) || messageIds == null || messageIds.Count == 0)
            return;

        var channel = _channels.GetById(channelId);
        var employee = _employees.GetById(userId);
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned || !_chatAuth.CanUserAccessChannel(channel, employee))
            return;

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

        if (updatedMessages.Count == 0) return;

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
            // Notify local subscribers
            _chatState.NotifyMessageRead(channelId, message.Id, message.ReadBy);

            // SignalR
            await _realtimeGateway.SendToChannelAsync(channelId, "MessageRead", new
            {
                ChannelId = channelId,
                MessageId = message.Id,
                ReadBy = message.ReadBy
            }, cancellationToken);
        }
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

            await _realtimeGateway.SendToChannelAsync(channelId, "ChannelMessagesRead", new
            {
                ChannelId = channelId,
                UserId = userId
            }, cancellationToken);
        }
    }


    public async Task StartTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return;

        var channel = _channels.GetById(channelId);
        if (channel != null && (!_chatAuth.CanUserAccessChannel(channel, employee) || !_chatAuth.CanUserPostToChannel(channel, employee)))
            return;

        // Local
        _chatState.NotifyUserTyping(channelId, employee.Id, employee.FullName);
        _presenceState.PingUserActive(employee.Id);

        // SignalR
        await _realtimeGateway.SendToChannelAsync(channelId, "UserTyping", new
        {
            ChannelId = channelId,
            UserId = employee.Id,
            SenderId = employee.Id,
            UserName = employee.FullName
        }, cancellationToken);
    }

    public async Task StopTypingAsync(string userId, string channelId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return;

        var channel = _channels.GetById(channelId);
        if (channel != null && !_chatAuth.CanUserAccessChannel(channel, employee))
            return;

        // Local
        _chatState.NotifyUserStoppedTyping(channelId, userId);

        // SignalR
        await _realtimeGateway.SendToChannelAsync(channelId, "UserStoppedTyping", new
        {
            ChannelId = channelId,
            UserId = userId,
            SenderId = userId
        }, cancellationToken);
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
            if (channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherChannelKey))
            {
                try
                {
                    var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, requestingUserPrivateKey);
                    newContent = _crypto.EncryptAes(newContent!, plainChannelKey);
                    message.IsEncrypted = true;
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
            _logger.LogError(ex, "Failed to save message {MessageId} during edit", message.Id);
            return SpokesResult.Failure("Failed to persist edited message.", "SAVE_FAILED");
        }

        // Local
        _chatState.NotifyMessageEdited(message);

        // SignalR
        await _realtimeGateway.SendToChannelAsync(message.ChannelId, "MessageEdited", new
        {
            Id = message.Id,
            MessageId = message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt
        }, cancellationToken);

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
        if (!string.Equals(message.SenderId, userId, StringComparison.OrdinalIgnoreCase) ||
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

        string? plainChannelKey = null;
        string plainContent = message.Content ?? "";

        if (channel.IsEncrypted)
        {
            if (string.IsNullOrEmpty(privateKey))
            {
                return SpokesResult<ChatMessage>.Failure("Vault is locked. Cannot convert encrypted message attachments.", "VAULT_LOCKED");
            }

            if (!channel.EncryptedChannelKeys.TryGetValue(userId, out var cipherChannelKey))
            {
                return SpokesResult<ChatMessage>.Failure("Channel encryption key unavailable for user.", "KEY_NOT_FOUND");
            }

            try
            {
                plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, privateKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConvertMessageImagesToAlbum failed: RSA decryption of channel key failed for User {UserId}", userId);
                return SpokesResult<ChatMessage>.Failure($"Failed to decrypt channel key: {ex.Message}", "CRYPTO_FAILED");
            }

            if (message.IsEncrypted && !string.IsNullOrEmpty(message.Content))
            {
                try
                {
                    plainContent = _crypto.DecryptAes(message.Content, plainChannelKey);
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
            plainChannelKey,
            deleteSourceFiles: true);

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
                finalContent = _crypto.EncryptAes(plainContent, plainChannelKey!);
                message.IsEncrypted = true;
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

        var tokens = ExtractMessageTokens(plainContent);
        ApplyTokensAndResolveMentions(message, tokens);
        var syncResult = SynchronizeMentionedAlbums(message, channel, previousAlbumIds, privateKey);
        if (!syncResult.IsSuccess)
        {
            return SpokesResult<ChatMessage>.Failure(syncResult.ErrorMessage ?? "Failed to synchronize album share.", syncResult.Code);
        }
        RouteEditMentionNotifications(message, channel, previousUserIds, previousMentionsTeam, previousMentionsEveryone);

        try
        {
            _messages.Save(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId} after converting images to album", message.Id);
            return SpokesResult<ChatMessage>.Failure("Failed to persist updated message.", "SAVE_FAILED");
        }

        _chatState.NotifyMessageEdited(message);

        await _realtimeGateway.SendToChannelAsync(message.ChannelId, "MessageEdited", new
        {
            Id = message.Id,
            MessageId = message.Id,
            message.ChannelId,
            message.Content,
            message.EditedAt,
            MentionedAlbumIds = message.MentionedAlbumIds,
            Attachments = message.Attachments.Select(a => new { a.Id, a.FileName, a.FilePath, a.ContentType, a.FileSizeBytes })
        }, cancellationToken);

        return SpokesResult<ChatMessage>.Success(message);
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
        foreach (var albumId in newAlbumIds)
        {
            try
            {
                var shareResult = _albumService.ShareAlbumWithChannel(albumId, message.SenderId, channel.Id, privateKey);
                if (!shareResult.IsSuccess && shareResult.Code != "NOT_FOUND" && shareResult.Code != "FORBIDDEN")
                {
                    _logger.LogWarning("Failed to share album {AlbumId} with channel {ChannelId}: {Error}", albumId, channel.Id, shareResult.ErrorMessage);
                    return shareResult;
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
            (!actor.IsAdmin && !actor.HasPermission(AppPermissions.Chat.Moderator)))
        {
            _logger.LogWarning("AdminDeleteMessageAsync rejected: user {UserId} lacks moderation privileges", requestingUserId);
            return SpokesResult.Failure("User is not authorized to perform moderation message deletion.", "ACCESS_DENIED");
        }

        var message = _messages.GetById(messageId);
        if (message == null) return SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND");

        var channel = _channels.GetById(message.ChannelId);
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

        if (channel != null && previousAlbumIds.Count > 0)
        {
            foreach (var albumId in previousAlbumIds)
            {
                if (!HasOtherAlbumMentionsInChannel(channel.Id, message.Id, albumId))
                {
                    try
                    {
                        _albumService.UnshareAlbumWithChannel(albumId, message.SenderId, channel.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ScrubAndDeleteMessageCoreAsync: Failed to unshare album {AlbumId} from channel {ChannelId}", albumId, channel.Id);
                    }
                }
            }
        }

        // Local
        _chatState.NotifyMessageDeleted(message.ChannelId, message.Id);

        // SignalR
        await _realtimeGateway.SendToChannelAsync(message.ChannelId, "MessageDeleted", new
        {
            Id = message.Id,
            MessageId = message.Id,
            message.ChannelId
        }, cancellationToken);
    }

    public virtual async Task<SpokesResult> ToggleReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND");
        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN");
        }

        var message = _messages.GetById(messageId);
        if (message == null) return SpokesResult.Failure($"Message '{messageId}' not found.", "NOT_FOUND");

        var channel = _channels.GetById(message.ChannelId);
        if (channel == null || !_chatAuth.CanUserAccessChannel(channel, employee))
        {
            _logger.LogWarning("ToggleReaction failed: User {UserId} denied access to Channel {ChannelId}", employee.Id, message.ChannelId);
            return SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN");
        }

        if (_privacyService != null && !_privacyService.CanReactInChannel(channel, employee))
        {
            return SpokesResult.Failure("Reactions are disabled in this channel.", "FORBIDDEN");
        }
        else if (_privacyService == null && !_chatAuth.CanUserPostToChannel(channel, employee))
        {
            _logger.LogWarning("ToggleReaction failed: User {UserId} denied post access to Channel {ChannelId}", employee.Id, message.ChannelId);
            return SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN");
        }

        var reactionKey = $"{emoji}:{userId}";

        if (message.Reactions.Contains(reactionKey))
        {
            // Remove the reaction
            message.Reactions.Remove(reactionKey);
            _messages.Save(message);

            // Local
            _chatState.NotifyReactionRemoved(message.ChannelId, message.Id, emoji, employee.Id);

            // SignalR
            await _realtimeGateway.SendToChannelAsync(message.ChannelId, "ReactionRemoved", new
            {
                Id = message.Id,
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id,
                SenderId = employee.Id
            }, cancellationToken);
        }
        else
        {
            // Add the reaction
            message.Reactions.Add(reactionKey);
            _messages.Save(message);

            // Local
            _chatState.NotifyReactionAdded(message.ChannelId, message.Id, emoji, employee.Id, employee.FullName);

            // SignalR
            await _realtimeGateway.SendToChannelAsync(message.ChannelId, "ReactionAdded", new
            {
                Id = message.Id,
                MessageId = message.Id,
                message.ChannelId,
                Emoji = emoji,
                UserId = employee.Id,
                SenderId = employee.Id,
                UserName = employee.FullName
            }, cancellationToken);

            // Push Notifications
            if (channel != null)
            {
                _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteReactionNotificationAsync(message, channel, emoji, employee), "chat reaction");
            }
        }

        return SpokesResult.Success();
    }

    public virtual Task<SpokesResult> AddReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return Task.FromResult(SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND"));
        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        var message = _messages.GetById(messageId);
        if (message != null && message.Reactions.Contains($"{emoji}:{userId}"))
        {
            return Task.FromResult(SpokesResult.Success());
        }
        return ToggleReactionAsync(userId, messageId, emoji, cancellationToken);
    }

    public virtual Task<SpokesResult> RemoveReactionAsync(string userId, string messageId, string emoji, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(userId);
        if (employee == null) return Task.FromResult(SpokesResult.Failure($"User '{userId}' not found.", "NOT_FOUND"));
        if (!employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return Task.FromResult(SpokesResult.Failure("User is not authorized to react in this channel.", "FORBIDDEN"));
        }

        var message = _messages.GetById(messageId);
        if (message != null && !message.Reactions.Contains($"{emoji}:{userId}"))
        {
            return Task.FromResult(SpokesResult.Success());
        }
        return ToggleReactionAsync(userId, messageId, emoji, cancellationToken);
    }

    private void EnsureActorCanCreateChannel(string? actorUserId)
    {
        if (string.IsNullOrWhiteSpace(actorUserId))
            return;

        var actor = _employees.GetById(actorUserId);
        if (actor != null && (!actor.IsActive || actor.IsSuspended || actor.IsBanned))
        {
            throw new UnauthorizedAccessException("Inactive, suspended, or banned users cannot create channels.");
        }
    }

    public ChatChannel CreateDirectMessageChannel(string initiatorUserId, string targetUserId)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");

        EnsureActorCanCreateChannel(initiatorUserId);
        var channel = _channels.GetOrCreateDirectChannel(initiatorUserId, targetUserId, false, createdById: initiatorUserId);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public string GetChannelDisplayName(ChatChannel channel, string? currentUserId, string youSuffix = ChatChannelFormatter.DefaultYouSuffix)
    {
        return ChatChannelFormatter.GetDisplayName(channel, currentUserId, id => _employees.GetById(id), youSuffix);
    }

    public ChatChannel CreateGroupDirectMessageChannel(string initiatorUserId, List<string> targetUserIds)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");

        EnsureActorCanCreateChannel(initiatorUserId);
        var allParticipantIds = new List<string> { initiatorUserId };
        allParticipantIds.AddRange(targetUserIds);
        allParticipantIds = allParticipantIds.Distinct().ToList();

        var channel = _channels.CreateGroupChannel(allParticipantIds, "", false, createdById: initiatorUserId);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public ChatChannel CreateTeamChannel(string initiatorUserId, List<string> teamIds)
    {
        if (string.IsNullOrWhiteSpace(initiatorUserId))
            throw new UnauthorizedAccessException("Initiator user ID is required.");

        EnsureActorCanCreateChannel(initiatorUserId);
        var teamNames = teamIds
            .Select(id => _teams.GetById(id))
            .Where(t => t != null)
            .Select(t => t!.Name)
            .ToList();
        var defaultName = string.Join(", ", teamNames);

        var channel = new ChatChannel
        {
            Name = defaultName,
            ChannelType = ChatChannelType.Team,
            AllowedTeamIds = teamIds,
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
        EnsureActorCanCreateChannel(channel.CreatedById);
        channel.Normalize();
        channel.IsEncrypted = false;
        _channels.Save(channel);
        _chatState.NotifyChannelCreated(channel);
        return channel;
    }

    public SpokesResult UpdateChannel(ChatChannel channel, string requestingUserId, bool isSessionUnlocked = true)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("UpdateChannel rejected: user {UserId} is missing, inactive, suspended, or banned", requestingUserId);
            return SpokesResult.Failure("User is not authorized to modify this channel.", "ACCESS_DENIED");
        }

        var existing = !string.IsNullOrEmpty(channel.Id) ? _channels.GetById(channel.Id) : null;
        var authTarget = existing ?? channel;

        if ((authTarget.IsEncrypted || channel.IsEncrypted) && !isSessionUnlocked)
        {
            _logger.LogWarning("UpdateChannel rejected: session vault is locked for encrypted channel {ChannelId}", channel.Id);
            return SpokesResult.Failure("Session must have secure chat unlocked to modify an encrypted channel.", "VAULT_LOCKED");
        }

        if (existing != null && !string.Equals(existing.ChannelType, channel.ChannelType, StringComparison.Ordinal) &&
            (existing.ChannelType == ChatChannelType.Direct || existing.ChannelType == ChatChannelType.Group || channel.ChannelType == ChatChannelType.Direct))
        {
            _logger.LogWarning("UpdateChannel rejected: attempted ChannelType mutation on private channel {ChannelId}", channel.Id);
            return SpokesResult.Failure("Channel type cannot be altered on direct or group channels.", "ACCESS_DENIED");
        }

        if (!_chatAuth.CanUserManageChannel(authTarget, employee, isSessionUnlocked))
        {
            _logger.LogWarning("UpdateChannel rejected: user {UserId} not authorized to manage channel {ChannelId}", requestingUserId, channel.Id);
            return SpokesResult.Failure("User is not authorized to modify this channel.", "ACCESS_DENIED");
        }

        channel.Normalize();

        // 1. Save to DB
        _channels.Save(channel);

        // 2. Notify local subscribers - Reusing ChannelCreated for now as it forces reload in UI
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    public SpokesResult DeleteChannel(string channelId, string requestingUserId, bool isSessionUnlocked = true)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return SpokesResult.Failure("Channel ID is required.", "INVALID_INPUT");
        }

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return SpokesResult.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("DeleteChannel rejected: user {UserId} is missing, inactive, suspended, or banned", requestingUserId);
            return SpokesResult.Failure("User is not authorized to delete this channel.", "ACCESS_DENIED");
        }

        if (channel.IsEncrypted && !isSessionUnlocked)
        {
            _logger.LogWarning("DeleteChannel rejected: session vault is locked for encrypted channel {ChannelId}", channelId);
            return SpokesResult.Failure("Session must have secure chat unlocked to delete an encrypted channel.", "VAULT_LOCKED");
        }

        if (!_chatAuth.CanUserDeleteChannel(channel, employee, isSessionUnlocked))
        {
            _logger.LogWarning("DeleteChannel rejected: user {UserId} not authorized to delete channel {ChannelId}", requestingUserId, channelId);
            return SpokesResult.Failure("User is not authorized to delete this channel.", "ACCESS_DENIED");
        }

        // Delete channel
        _channels.Delete(channelId);

        // Delete associated messages
        var msgs = _messages.GetAll().Where(m => m.ChannelId == channelId).ToList();
        foreach (var m in msgs)
        {
            _messages.Delete(m.Id);
        }

        // Delete associated read states
        var readStates = _readStates.GetAll().Where(rs => rs.ChannelId == channelId).ToList();
        foreach (var rs in readStates)
        {
            _readStates.Delete(rs.Id);
        }

        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    public SpokesResult ArchiveChannel(string channelId, bool archive, string requestingUserId, bool isSessionUnlocked = true)
    {
        if (string.IsNullOrEmpty(channelId))
        {
            return SpokesResult.Failure("Channel ID is required.", "INVALID_INPUT");
        }

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            return SpokesResult.Failure("Requesting user ID is required.", "ACCESS_DENIED");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return SpokesResult.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        var employee = _employees.GetById(requestingUserId);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            _logger.LogWarning("ArchiveChannel rejected: user {UserId} is missing, inactive, suspended, or banned", requestingUserId);
            return SpokesResult.Failure("User is not authorized to archive this channel.", "ACCESS_DENIED");
        }

        if (channel.IsEncrypted && !isSessionUnlocked)
        {
            _logger.LogWarning("ArchiveChannel rejected: session vault is locked for encrypted channel {ChannelId}", channelId);
            return SpokesResult.Failure("Session must have secure chat unlocked to archive an encrypted channel.", "VAULT_LOCKED");
        }

        if (!_chatAuth.CanUserManageChannel(channel, employee, isSessionUnlocked))
        {
            _logger.LogWarning("ArchiveChannel rejected: user {UserId} not authorized to archive channel {ChannelId}", requestingUserId, channelId);
            return SpokesResult.Failure("User is not authorized to archive this channel.", "ACCESS_DENIED");
        }

        channel.IsArchived = archive;
        channel.Normalize();
        _channels.Save(channel);
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
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

        channel.ParticipantIds?.RemoveAll(id => string.Equals(id, requestingUserId, StringComparison.Ordinal));
        channel.EncryptedChannelKeys?.Remove(requestingUserId);
        channel.AllowedPostUserIds?.RemoveAll(id => string.Equals(id, requestingUserId, StringComparison.Ordinal));

        if (string.Equals(channel.CreatedById, requestingUserId, StringComparison.Ordinal))
        {
            channel.CreatedById = channel.ParticipantIds != null && channel.ParticipantIds.Count > 0
                ? channel.ParticipantIds[0]
                : string.Empty;
        }

        _channels.Save(channel);
        _fileTokenService?.InvalidateUserEncryptedTokens(requestingUserId);
        _chatState.NotifyChannelCreated(channel);

        return SpokesResult.Success();
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation)
    {
        return Task.Run(async () =>
        {
            try
            {
                await routeAction();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch {Operation} notification in background", operation);
            }
        });
    }
}
