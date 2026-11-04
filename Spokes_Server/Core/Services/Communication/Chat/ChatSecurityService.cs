using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Domain service managing E2EE key distribution, Server Escrow synchronization, and channel encryption toggling.
/// Extracted from ChatService to maintain single responsibility.
/// </summary>
public class ChatSecurityService
{
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly ServerEscrowService _escrowService;
    private readonly ICryptoService _crypto;
    private readonly AlbumService _albumService;
    private readonly IChatRealtimeGateway _realtimeGateway;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IFileService _fileService;
    private readonly ILogger<ChatSecurityService> _logger;

    public ChatSecurityService(
        EmployeeRepository employees,
        ChatChannelRepository channels,
        ChatMessageRepository messages,
        ServerEscrowService escrowService,
        ICryptoService crypto,
        AlbumService albumService,
        IChatRealtimeGateway realtimeGateway,
        IChatAuthorizationService chatAuth,
        IFileService fileService,
        ILogger<ChatSecurityService> logger)
    {
        _employees = employees;
        _channels = channels;
        _messages = messages;
        _escrowService = escrowService;
        _crypto = crypto;
        _albumService = albumService;
        _realtimeGateway = realtimeGateway;
        _chatAuth = chatAuth;
        _fileService = fileService;
        _logger = logger;
    }

    public virtual async Task ToggleChannelEncryptionAsync(
        string channelId,
        bool enableEncryption,
        string requestingUserId,
        string? privateKeyContext = null,
        CancellationToken cancellationToken = default)
    {
        var channel = _channels.GetById(channelId);
        if (channel == null) return;

        if (channel.IsEncrypted == enableEncryption)
        {
            if (!enableEncryption) return;

            // Re-sync: if already encrypted and keys exist, perform an idempotent attachment sweep
            if (channel.EncryptedChannelKeys.Count > 0)
            {
                await Task.Run(async () =>
                {
                    await ProcessChannelMediaSyncAsync(channel, requestingUserId, privateKeyContext, cancellationToken);
                }, cancellationToken);
                return;
            }
        }

        var isDMRestricted = channel.ChannelType == ChatChannelType.Direct ||
                             channel.ChannelType == ChatChannelType.Group;

        string? existingCipherKeyForDisable = null;

        if (enableEncryption)
        {
            ValidateEncryptionPrerequisites(channel, isDMRestricted, privateKeyContext);
        }
        else
        {
            existingCipherKeyForDisable = ValidateDecryptionPrerequisites(channel, requestingUserId, privateKeyContext);
        }

        channel.IsEncrypted = enableEncryption;
        if (!enableEncryption) channel.EncryptedChannelKeys.Clear();
        _channels.Save(channel);

        await Task.Run(async () =>
        {
            await ProcessChannelEncryptionToggleAsync(
                channel,
                enableEncryption,
                isDMRestricted,
                requestingUserId,
                privateKeyContext,
                existingCipherKeyForDisable,
                cancellationToken);
        }, cancellationToken);
    }

    public virtual void ValidateChannelEncryptionPrerequisites(ChatChannel channel, string? privateKeyContext)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var isDMRestricted = channel.ChannelType == ChatChannelType.Direct ||
                             channel.ChannelType == ChatChannelType.Group ||
                             channel.ParticipantIds.Any();
        ValidateEncryptionPrerequisites(channel, isDMRestricted, privateKeyContext);
    }

    private void ValidateEncryptionPrerequisites(ChatChannel channel, bool isDMRestricted, string? privateKeyContext)
    {
        if (string.IsNullOrEmpty(privateKeyContext))
        {
            throw new DomainException("Vault must be unlocked before encrypting a channel.", "VAULT_LOCKED");
        }

        if (!isDMRestricted) return;

        foreach (var uid in channel.ParticipantIds)
        {
            var u = _employees.GetById(uid);
            if (u == null || !u.HasVaultPassword)
            {
                var name = u?.FullName ?? "Unknown User";
                throw new DomainException($"Cannot encrypt chat: User '{name}' has not set up their Vault Password yet.", "VAULT_PASSWORD_REQUIRED");
            }
        }
    }

    private string ValidateDecryptionPrerequisites(ChatChannel channel, string requestingUserId, string? privateKeyContext)
    {
        if (string.IsNullOrEmpty(privateKeyContext))
        {
            throw new DomainException("Private Key is required to disable encryption.", "PRIVATE_KEY_REQUIRED");
        }

        if (!channel.EncryptedChannelKeys.TryGetValue(requestingUserId, out var existingCipherKey))
        {
            throw new DomainException("Could not find your decryption key to disable encryption. Your session may be out of date.", "DECRYPTION_KEY_NOT_FOUND");
        }

        return existingCipherKey;
    }

    private async Task ProcessChannelEncryptionToggleAsync(
        ChatChannel channel,
        bool enableEncryption,
        bool isDMRestricted,
        string requestingUserId,
        string? privateKeyContext,
        string? existingCipherKeyForDisable,
        CancellationToken cancellationToken)
    {
        try
        {
            var allMessages = LoadChannelMessages(channel.Id);

            if (enableEncryption)
            {
                await EncryptChannelAsync(channel, allMessages, isDMRestricted, requestingUserId, privateKeyContext, cancellationToken);
            }
            else
            {
                await DecryptChannelAsync(channel, allMessages, privateKeyContext, existingCipherKeyForDisable, cancellationToken);
            }

            _channels.Save(channel);
            await _realtimeGateway.SendToChannelAsync(channel.Id, "ChannelUpdated", channel, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling channel encryption for {ChannelId}", channel.Id);
        }
    }

    private async Task ProcessChannelMediaSyncAsync(
        ChatChannel channel,
        string requestingUserId,
        string? privateKeyContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var plainChannelKey = TryResolveChannelKey(channel, requestingUserId, privateKeyContext);
            if (string.IsNullOrEmpty(plainChannelKey))
            {
                _logger.LogWarning("Cannot sync channel media for {ChannelId}: Decrypted channel key not found or vault locked.", channel.Id);
                return;
            }

            var allMessages = LoadChannelMessages(channel.Id);
            await EncryptChannelAttachmentsAsync(channel, allMessages, plainChannelKey, cancellationToken);
            _logger.LogInformation("Completed media encryption sync for channel {ChannelId}", channel.Id);
            await _realtimeGateway.SendToChannelAsync(channel.Id, "ChannelUpdated", channel, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing channel media encryption for {ChannelId}", channel.Id);
        }
    }

    private List<ChatMessage> LoadChannelMessages(string channelId)
    {
        if (_messages is ChatMessageRepository chatRepo)
        {
            var index = chatRepo.GetIndexForChannel(channelId);
            var result = new List<ChatMessage>(index.Count);
            foreach (var entry in index)
            {
                var msg = chatRepo.GetMessageFromDisk(channelId, entry.Id);
                if (msg != null) result.Add(msg);
            }
            return result;
        }

        return _messages.GetAll().Where(m => m.ChannelId == channelId).ToList();
    }

    private async Task EncryptChannelAsync(
        ChatChannel channel,
        List<ChatMessage> allMessages,
        bool isDMRestricted,
        string requestingUserId,
        string? privateKeyContext,
        CancellationToken cancellationToken)
    {
        var newChannelKey = _crypto.GenerateAesKeyBase64();
        var allowedUserIds = ResolveAllowedUserIds(channel);

        channel.EncryptedChannelKeys.Clear();
        foreach (var uid in allowedUserIds)
        {
            var user = _employees.GetById(uid);
            if (user != null && !string.IsNullOrEmpty(user.PublicKey))
            {
                channel.EncryptedChannelKeys[uid] = _crypto.EncryptRsa(newChannelKey, user.PublicKey);
            }
        }

        if (!isDMRestricted && _escrowService.IsEscrowAvailable)
        {
            channel.EncryptedChannelKeys["ServerMaster"] = _crypto.EncryptRsa(newChannelKey, _escrowService.EscrowPublicKey!);
        }

        foreach (var m in allMessages)
        {
            if (!m.IsEncrypted && !string.IsNullOrEmpty(m.Content))
            {
                m.Content = _crypto.EncryptAes(m.Content, newChannelKey);
                m.IsEncrypted = true;
                _messages.Save(m);
            }
        }

        await EncryptChannelAttachmentsAsync(channel, allMessages, newChannelKey, cancellationToken);

        _albumService.ReEvaluateAlbumEncryptionForChannel(channel.Id, newChannelKey, requestingUserId, privateKeyContext);
    }

    private async Task EncryptChannelAttachmentsAsync(ChatChannel channel, List<ChatMessage> allMessages, string channelAesKey, CancellationToken cancellationToken)
    {
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in allMessages)
        {
            if (m.Attachments == null || m.Attachments.Count == 0) continue;

            foreach (var att in m.Attachments)
            {
                var candidatePaths = ResolveAttachmentPhysicalPaths(channel.Id, att);
                foreach (var path in candidatePaths)
                {
                    if (processedPaths.Add(path) && File.Exists(path))
                    {
                        try
                        {
                            await _fileService.EncryptFileAsync(path, channelAesKey, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to encrypt attachment at {Path} for channel {ChannelId}", path, channel.Id);
                        }
                    }
                }
            }
        }

        // Scan channel upload directory to ensure zero plaintext files remain
        var channelUploadDir = _fileService.GetPhysicalPath("chat", channel.Id, "");
        if (Directory.Exists(channelUploadDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(channelUploadDir))
            {
                if (processedPaths.Add(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        await _fileService.EncryptFileAsync(filePath, channelAesKey, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to encrypt file at {Path} for channel {ChannelId}", filePath, channel.Id);
                    }
                }
            }
        }
    }

    private HashSet<string> ResolveAllowedUserIds(ChatChannel channel)
    {
        var allowedUserIds = new HashSet<string>();

        if (channel.ParticipantIds.Any())
        {
            foreach (var p in channel.ParticipantIds)
            {
                allowedUserIds.Add(p);
            }
        }

        if (channel.ChannelType != ChatChannelType.Direct && channel.ChannelType != ChatChannelType.Group)
        {
            var members = _employees.GetAll()
                .Where(e => e.IsActive && e.HasVaultPassword && _chatAuth.CanUserAccessChannel(channel, e));

            foreach (var m in members)
            {
                allowedUserIds.Add(m.Id);
            }
        }

        return allowedUserIds;
    }

    private async Task DecryptChannelAsync(
        ChatChannel channel,
        List<ChatMessage> allMessages,
        string? privateKeyContext,
        string? existingCipherKeyForDisable,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(existingCipherKeyForDisable) || string.IsNullOrEmpty(privateKeyContext))
        {
            return;
        }

        try
        {
            var plainChannelKey = _crypto.DecryptRsa(existingCipherKeyForDisable, privateKeyContext);

            foreach (var m in allMessages)
            {
                if (!m.IsEncrypted || string.IsNullOrEmpty(m.Content))
                {
                    continue;
                }

                try
                {
                    m.Content = _crypto.DecryptAes(m.Content, plainChannelKey);
                    m.IsEncrypted = false;
                    _messages.Save(m);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during encryption toggle.", m.Id);
                }
            }

            await DecryptChannelAttachmentsAsync(channel, allMessages, plainChannelKey, cancellationToken);
            _albumService.ReEvaluateAlbumDecryptionForChannel(channel.Id, plainChannelKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt channel key during encryption toggle for {ChannelId}", channel.Id);
        }
    }

    private async Task DecryptChannelAttachmentsAsync(ChatChannel channel, List<ChatMessage> allMessages, string channelAesKey, CancellationToken cancellationToken)
    {
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in allMessages)
        {
            if (m.Attachments == null || m.Attachments.Count == 0) continue;

            foreach (var att in m.Attachments)
            {
                var candidatePaths = ResolveAttachmentPhysicalPaths(channel.Id, att);
                foreach (var path in candidatePaths)
                {
                    if (processedPaths.Add(path) && File.Exists(path))
                    {
                        try
                        {
                            await _fileService.DecryptFileAsync(path, channelAesKey, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to decrypt attachment at {Path} for channel {ChannelId}", path, channel.Id);
                        }
                    }
                }
            }
        }

        var channelUploadDir = _fileService.GetPhysicalPath("chat", channel.Id, "");
        if (Directory.Exists(channelUploadDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(channelUploadDir))
            {
                if (processedPaths.Add(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        await _fileService.DecryptFileAsync(filePath, channelAesKey, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to decrypt file at {Path} for channel {ChannelId}", filePath, channel.Id);
                    }
                }
            }
        }
    }

    private List<string> ResolveAttachmentPhysicalPaths(string channelId, ChatAttachment att)
    {
        var paths = new List<string>();
        if (string.IsNullOrWhiteSpace(att.FilePath)) return paths;

        var fileName = Path.GetFileName(att.FilePath.TrimEnd('/'));
        if (!string.IsNullOrEmpty(fileName))
        {
            paths.Add(_fileService.GetPhysicalPath("chat", channelId, fileName));
            paths.Add(_fileService.GetPhysicalPath("chat", channelId, $"{fileName}_thumb.jpg"));

            try
            {
                paths.Add(_fileService.GetAttachmentPhysicalPath(channelId, fileName, false));
                paths.Add(_fileService.GetAttachmentPhysicalPath(channelId, fileName, true));
            }
            catch { }
        }

        return paths;
    }

    public virtual async Task ClaimEscrowedKeysAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_escrowService.IsEscrowAvailable) return;

        var user = _employees.GetById(userId);
        if (user == null || string.IsNullOrEmpty(user.PublicKey)) return;

        _albumService.ClaimEscrowedAlbumsForUser(userId);

        var allChannels = _channels.GetAll().Where(c => c.IsEncrypted).ToList();

        foreach (var channel in allChannels)
        {
            if (channel.EncryptedChannelKeys.ContainsKey(userId)) continue;
            if (!channel.EncryptedChannelKeys.TryGetValue("ServerMaster", out var encryptedEscrowKey)) continue;

            if (!_chatAuth.CanUserAccessChannel(channel, user)) continue;

            try
            {
                var plainChannelKey = _crypto.DecryptRsa(encryptedEscrowKey, _escrowService.DecryptedEscrowPrivateKey!);
                channel.EncryptedChannelKeys[userId] = _crypto.EncryptRsa(plainChannelKey, user.PublicKey);
                _channels.Save(channel);

                await _realtimeGateway.SendToChannelAsync(channel.Id, "ChannelUpdated", channel, cancellationToken);
                _logger.LogInformation("Claimed escrow key for user {UserId} in channel {ChannelId}", userId, channel.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to claim escrow for user {UserId} in channel {ChannelId}", userId, channel.Id);
            }
        }
    }

    public virtual Task ShareKeyOpportunisticallyAsync(string channelId, string plainChannelKey, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var channel = _channels.GetById(channelId);
                if (channel == null || !channel.IsEncrypted) return;

                bool modified = false;

                if (_escrowService.IsEscrowAvailable && !channel.EncryptedChannelKeys.ContainsKey("ServerMaster"))
                {
                    bool isDMRestricted = channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group;
                    if (!isDMRestricted && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
                    {
                        channel.EncryptedChannelKeys["ServerMaster"] = _crypto.EncryptRsa(plainChannelKey, _escrowService.EscrowPublicKey);
                        modified = true;
                        _logger.LogInformation("Opportunistically healed ServerMaster escrow key for channel {ChannelId}", channelId);
                    }
                }

                var allActiveKeyedUsers = _employees.GetAll()
                    .Where(e => e.IsActive && e.HasVaultPassword && !string.IsNullOrEmpty(e.PublicKey))
                    .ToList();

                foreach (var u in allActiveKeyedUsers)
                {
                    if (string.IsNullOrEmpty(u.PublicKey)) continue;

                    if (channel.EncryptedChannelKeys.ContainsKey(u.Id)) continue;

                    if (!_chatAuth.CanUserAccessChannel(channel, u)) continue;

                    try
                    {
                        channel.EncryptedChannelKeys[u.Id] = _crypto.EncryptRsa(plainChannelKey, u.PublicKey);
                        modified = true;
                        _logger.LogInformation("Opportunistically shared key with user {UserId} for channel {ChannelId}", u.Id, channelId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed opportunistic key encryption for user {UserId} in channel {ChannelId}", u.Id, channelId);
                    }
                }

                if (modified)
                {
                    _channels.Save(channel);
                    _ = _realtimeGateway.SendToChannelAsync(channelId, "ChannelUpdated", channel);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in opportunistic key sharing for channel {ChannelId}", channelId);
            }
        }, cancellationToken);
    }

    public virtual Task<List<ChatMessage>> SecureSearchMessagesAsync(
        string? channelId,
        string query,
        string requestingUserId,
        string? requestingUserPrivateKey = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new List<ChatMessage>());
        }

        var filter = ChatSearchQueryParser.Parse(query);
        filter.ChannelId = channelId;

        // Resolve sender & mention IDs from query filters
        var resolvedSenderIds = ResolveSenderIds(filter.SenderIdentifiers, requestingUserId);
        var resolvedMentionIds = ResolveMentionIds(filter.MentionIdentifiers, requestingUserId);

        // Determine target channels
        List<ChatChannel> targetChannels = [];
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var singleChannel = _channels.GetById(channelId);
            if (singleChannel != null && IsChannelAccessible(singleChannel, requestingUserId))
            {
                targetChannels.Add(singleChannel);
            }
        }
        else
        {
            // Default to all accessible channels for the user
            var accessibleChannels = _chatAuth.GetChannelsForUser(requestingUserId);
            if (accessibleChannels != null)
            {
                targetChannels.AddRange(accessibleChannels);
            }
        }

        if (targetChannels.Count == 0)
        {
            return Task.FromResult(new List<ChatMessage>());
        }

        var allResults = new List<ChatMessage>();

        foreach (var channel in targetChannels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? plainChannelKey = null;
            if (channel.IsEncrypted)
            {
                plainChannelKey = TryResolveChannelKey(channel, requestingUserId, requestingUserPrivateKey);
                if (plainChannelKey == null)
                {
                    // Cannot decrypt this channel without a valid key - skip it
                    continue;
                }
            }

            var channelResults = SearchChannelMessages(channel, plainChannelKey, filter, resolvedSenderIds, resolvedMentionIds, cancellationToken);
            allResults.AddRange(channelResults);

            if (allResults.Count >= 100) break;
        }

        var sortedResults = allResults
            .OrderByDescending(m => m.SentAt)
            .Take(100)
            .ToList();

        return Task.FromResult(sortedResults);
    }

    private bool IsChannelAccessible(ChatChannel channel, string requestingUserId)
    {
        if ((channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group) &&
            !channel.ParticipantIds.Contains(requestingUserId))
        {
            return false;
        }
        return true;
    }

    private HashSet<string>? ResolveSenderIds(List<string> senderIdentifiers, string requestingUserId)
    {
        if (senderIdentifiers.Count == 0) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allEmployees = _employees.GetAll();

        foreach (var id in senderIdentifiers)
        {
            if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(requestingUserId);
                continue;
            }

            var empById = allEmployees.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (empById != null)
            {
                set.Add(empById.Id);
                continue;
            }

            bool isAdminQuery = id.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("administrator", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("admins", StringComparison.OrdinalIgnoreCase);

            var matched = allEmployees.Where(e =>
                (isAdminQuery && e.IsAdmin) ||
                e.FullName.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(e.FirstName) && e.FirstName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.LastName) && e.LastName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.Email) && e.Email.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.Position) && e.Position.Contains(id, StringComparison.OrdinalIgnoreCase)));

            foreach (var m in matched)
            {
                set.Add(m.Id);
            }
        }

        return set;
    }

    private HashSet<string>? ResolveMentionIds(List<string> mentionIdentifiers, string requestingUserId)
    {
        if (mentionIdentifiers.Count == 0) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allEmployees = _employees.GetAll();

        foreach (var id in mentionIdentifiers)
        {
            if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(requestingUserId);
                continue;
            }

            var empById = allEmployees.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (empById != null)
            {
                set.Add(empById.Id);
                continue;
            }

            bool isAdminQuery = id.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("administrator", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("admins", StringComparison.OrdinalIgnoreCase);

            var matched = allEmployees.Where(e =>
                (isAdminQuery && e.IsAdmin) ||
                e.FullName.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(e.FirstName) && e.FirstName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.LastName) && e.LastName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.Email) && e.Email.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(e.Position) && e.Position.Contains(id, StringComparison.OrdinalIgnoreCase)));

            foreach (var m in matched)
            {
                set.Add(m.Id);
            }
        }

        return set;
    }

    private string? TryResolveChannelKey(ChatChannel channel, string requestingUserId, string? requestingUserPrivateKey)
    {
        if (!string.IsNullOrEmpty(requestingUserPrivateKey) &&
            channel.EncryptedChannelKeys.TryGetValue(requestingUserId, out var userCipherKey))
        {
            try
            {
                return _crypto.DecryptRsa(userCipherKey, requestingUserPrivateKey);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to decrypt channel key with user private key. Falling back to escrow.");
            }
        }

        if (_escrowService.IsEscrowAvailable &&
            channel.EncryptedChannelKeys.TryGetValue("ServerMaster", out var escrowCipherKey))
        {
            try
            {
                return _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt channel key using escrow key.");
            }
        }

        return null;
    }

    private List<ChatMessage> SearchChannelMessages(
        ChatChannel channel,
        string? plainChannelKey,
        ChatSearchFilter filter,
        HashSet<string>? resolvedSenderIds,
        HashSet<string>? resolvedMentionIds,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatMessage>();
        var index = _messages.GetIndexForChannel(channel.Id);

        foreach (var entry in index.OrderByDescending(e => e.SentAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted) continue;

            // 1. Fast index pruning: Senders
            if (resolvedSenderIds != null && !resolvedSenderIds.Contains(entry.SenderId))
                continue;

            // 2. Fast index pruning: Dates
            if (filter.Before.HasValue && entry.SentAt > filter.Before.Value)
                continue;
            if (filter.After.HasValue && entry.SentAt < filter.After.Value)
                continue;

            // 3. Fast index pruning: Media & file flags
            if (filter.HasImages == true && !entry.HasImages)
                continue;
            if (filter.HasImages == false && entry.HasImages)
                continue;
            if (filter.HasFiles == true && !entry.HasFiles)
                continue;
            if (filter.HasFiles == false && entry.HasFiles)
                continue;
            if (filter.HasLinks == true && !entry.HasLinks)
                continue;
            if (filter.HasLinks == false && entry.HasLinks)
                continue;

            // 4. Fast index pruning: Unencrypted text terms
            if (!entry.IsEncrypted && !channel.IsEncrypted && filter.TextTerms.Count > 0)
            {
                if (!entry.HasFiles && !entry.HasImages && entry.SearchText != null)
                {
                    if (!filter.TextTerms.All(t => entry.SearchText.Contains(t.ToLowerInvariant())))
                        continue;
                }
            }

            // Candidate passed index-level checks -> load and verify message
            if (!entry.IsEncrypted && !channel.IsEncrypted)
            {
                var msg = _messages.GetMessageFromDisk(channel.Id, entry.Id);
                if (msg != null && MatchesMessage(msg, filter, resolvedMentionIds))
                {
                    results.Add(msg);
                }
            }
            else if (channel.IsEncrypted && entry.IsEncrypted && plainChannelKey != null)
            {
                var match = TryDecryptAndMatchMessage(channel.Id, entry.Id, plainChannelKey, filter, resolvedMentionIds);
                if (match != null) results.Add(match);
            }

            if (results.Count >= 100) break;
        }

        return results;
    }

    private static bool MatchesMessage(ChatMessage msg, ChatSearchFilter filter, HashSet<string>? resolvedMentionIds)
    {
        // Text terms (matches content or attachment file names)
        if (filter.TextTerms.Count > 0)
        {
            var lowerContent = msg.Content?.ToLowerInvariant() ?? string.Empty;
            bool textMatches = filter.TextTerms.All(term =>
            {
                var lowerTerm = term.ToLowerInvariant();
                if (lowerContent.Contains(lowerTerm)) return true;
                if (msg.Attachments != null && msg.Attachments.Any(a => !string.IsNullOrEmpty(a.FileName) && a.FileName.ToLowerInvariant().Contains(lowerTerm))) return true;
                return false;
            });

            if (!textMatches) return false;
        }

        // Mentioned users
        if (resolvedMentionIds != null && resolvedMentionIds.Count > 0)
        {
            bool hasMention = msg.MentionedUserIds != null && msg.MentionedUserIds.Any(m => resolvedMentionIds.Contains(m));
            if (!hasMention && (msg.MentionsEveryone || msg.MentionsTeam))
            {
                hasMention = true;
            }
            if (!hasMention) return false;
        }

        // Reactions
        if (filter.HasReactions == true && (msg.Reactions == null || msg.Reactions.Count == 0))
            return false;

        if (!string.IsNullOrEmpty(filter.ReactionEmoji))
        {
            if (msg.Reactions == null || !msg.Reactions.Any(r => r.StartsWith(filter.ReactionEmoji, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private ChatMessage? TryDecryptAndMatchMessage(
        string channelId,
        string messageId,
        string plainChannelKey,
        ChatSearchFilter filter,
        HashSet<string>? resolvedMentionIds)
    {
        var msg = _messages.GetMessageFromDisk(channelId, messageId);
        if (msg == null) return null;

        string plainContent;
        try
        {
            plainContent = string.IsNullOrEmpty(msg.Content)
                ? string.Empty
                : _crypto.DecryptAes(msg.Content, plainChannelKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during search", msg.Id);
            if (!IsPotentialBase64Cipher(msg.Content))
            {
                plainContent = msg.Content;
            }
            else
            {
                return null;
            }
        }

        var candidateMsg = new ChatMessage
        {
            Id = msg.Id,
            ChannelId = msg.ChannelId,
            SenderId = msg.SenderId,
            SentAt = msg.SentAt,
            EditedAt = msg.EditedAt,
            Content = plainContent,
            IsEncrypted = false,
            MessageType = msg.MessageType,
            CallStatus = msg.CallStatus,
            Attachments = msg.Attachments != null ? new List<ChatAttachment>(msg.Attachments) : [],
            Reactions = msg.Reactions != null ? new List<string>(msg.Reactions) : [],
            MentionedUserIds = msg.MentionedUserIds != null ? new List<string>(msg.MentionedUserIds) : [],
            MentionedAlbumIds = msg.MentionedAlbumIds != null ? new List<string>(msg.MentionedAlbumIds) : [],
            MentionedEventIds = msg.MentionedEventIds != null ? new List<string>(msg.MentionedEventIds) : [],
            MentionedPollIds = msg.MentionedPollIds != null ? new List<string>(msg.MentionedPollIds) : [],
            MentionsEveryone = msg.MentionsEveryone,
            MentionsTeam = msg.MentionsTeam,
            ReplyToId = msg.ReplyToId
        };

        if (MatchesMessage(candidateMsg, filter, resolvedMentionIds))
        {
            return candidateMsg;
        }

        return null;
    }
    public static bool IsPotentialBase64Cipher(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        var trimmed = content.Trim();
        if (trimmed.Length < 44 || trimmed.Length % 4 != 0) return false;
        Span<byte> buffer = stackalloc byte[trimmed.Length];
        if (!Convert.TryFromBase64String(trimmed, buffer, out int bytesWritten)) return false;
        return bytesWritten >= 32 && bytesWritten % 16 == 0;
    }
}
