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
                             channel.ChannelType == ChatChannelType.Group ||
                             channel.ParticipantIds.Any();

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
        else
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
        string channelId,
        string query,
        string requestingUserId,
        string? requestingUserPrivateKey = null,
        CancellationToken cancellationToken = default)
    {
        var channel = _channels.GetById(channelId);
        if (channel == null) return Task.FromResult(new List<ChatMessage>());

        if ((channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group) &&
            !channel.ParticipantIds.Contains(requestingUserId))
        {
            return Task.FromResult(new List<ChatMessage>());
        }

        string? plainChannelKey = null;
        if (channel.IsEncrypted)
        {
            plainChannelKey = TryResolveChannelKey(channel, requestingUserId, requestingUserPrivateKey);
            if (plainChannelKey == null)
                return Task.FromResult(new List<ChatMessage>());
        }

        var results = SearchChannelMessages(channel, plainChannelKey, query, cancellationToken);
        return Task.FromResult(results);
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
        string query,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatMessage>();
        var lowerQuery = query.ToLowerInvariant();
        var index = _messages.GetIndexForChannel(channel.Id);

        foreach (var entry in index.OrderByDescending(e => e.SentAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDeleted) continue;

            if (!entry.IsEncrypted && !channel.IsEncrypted)
            {
                if (entry.SearchText != null && entry.SearchText.Contains(lowerQuery))
                {
                    var msg = _messages.GetMessageFromDisk(channel.Id, entry.Id);
                    if (msg != null) results.Add(msg);
                }
            }
            else if (channel.IsEncrypted && entry.IsEncrypted && plainChannelKey != null)
            {
                var match = TryDecryptAndMatchMessage(channel.Id, entry.Id, plainChannelKey, lowerQuery);
                if (match != null) results.Add(match);
            }

            if (results.Count >= 100) break;
        }

        return results;
    }

    private ChatMessage? TryDecryptAndMatchMessage(
        string channelId,
        string messageId,
        string plainChannelKey,
        string lowerQuery)
    {
        var msg = _messages.GetMessageFromDisk(channelId, messageId);
        if (msg == null || string.IsNullOrEmpty(msg.Content)) return null;

        try
        {
            var plainContent = _crypto.DecryptAes(msg.Content, plainChannelKey);
            if (plainContent.ToLowerInvariant().Contains(lowerQuery))
            {
                return new ChatMessage
                {
                    Id = msg.Id,
                    ChannelId = msg.ChannelId,
                    SenderId = msg.SenderId,
                    SentAt = msg.SentAt,
                    Content = plainContent,
                    IsEncrypted = false
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during search", msg.Id);
            if (!IsPotentialBase64Cipher(msg.Content) && msg.Content.ToLowerInvariant().Contains(lowerQuery))
            {
                return new ChatMessage
                {
                    Id = msg.Id,
                    ChannelId = msg.ChannelId,
                    SenderId = msg.SenderId,
                    SentAt = msg.SentAt,
                    Content = msg.Content,
                    IsEncrypted = false
                };
            }
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
