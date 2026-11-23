using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;
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
    private readonly ChatStateService _chatState;
    private readonly IChatAuthorizationService _chatAuth;
    private readonly IFileService _fileService;
    private readonly ILogger<ChatSecurityService> _logger;
    private readonly IUserPrivacyService? _privacyService;

    public ChatSecurityService(
        EmployeeRepository employees,
        ChatChannelRepository channels,
        ChatMessageRepository messages,
        ServerEscrowService escrowService,
        ICryptoService crypto,
        AlbumService albumService,
        ChatStateService chatState,
        IChatAuthorizationService chatAuth,
        IFileService fileService,
        ILogger<ChatSecurityService> logger,
        IUserPrivacyService? privacyService = null)
    {
        _employees = employees;
        _channels = channels;
        _messages = messages;
        _escrowService = escrowService;
        _crypto = crypto;
        _albumService = albumService;
        _chatState = chatState;
        _chatAuth = chatAuth;
        _fileService = fileService;
        _logger = logger;
        _privacyService = privacyService;
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

        if (string.IsNullOrWhiteSpace(requestingUserId))
        {
            throw new UnauthorizedAccessException("Requesting user ID is required to change channel encryption.");
        }

        var actor = _employees.GetById(requestingUserId);
        if (actor == null || !actor.IsActive || actor.IsSuspended || actor.IsBanned ||
            !actor.HasPermission(AppPermissions.Chat.Use))
        {
            throw new UnauthorizedAccessException("User is not authorized to modify channel encryption.");
        }

        bool isDirectCreator = channel.ChannelType == ChatChannelType.Direct &&
                               string.Equals(channel.CreatedById, actor.Id, StringComparison.Ordinal) &&
                               channel.ParticipantIds.Contains(actor.Id);

        if (!_chatAuth.CanUserManageChannel(channel, actor, isSessionUnlocked: true) && !isDirectCreator)
        {
            throw new UnauthorizedAccessException("User is not authorized to change encryption on this channel.");
        }

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
        Dictionary<int, Dictionary<string, string>>? epochSnapshot = null;
        int previousCurrentEpoch = channel.CurrentKeyEpoch;

        if (enableEncryption)
        {
            ValidateEncryptionPrerequisites(channel, isDMRestricted, privateKeyContext);
        }
        else
        {
            existingCipherKeyForDisable = ValidateDecryptionPrerequisites(channel, requestingUserId, privateKeyContext);
            epochSnapshot = BuildEpochSnapshot(channel);
        }

        channel.IsEncrypted = enableEncryption;
        if (!enableEncryption)
        {
            channel.EncryptedChannelKeys.Clear();
            channel.EncryptedEpochKeys.Clear();
            channel.CurrentKeyEpoch = 0;
        }
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
                epochSnapshot,
                previousCurrentEpoch,
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

    private static string ValidateDecryptionPrerequisites(ChatChannel channel, string requestingUserId, string? privateKeyContext)
    {
        if (string.IsNullOrEmpty(privateKeyContext))
        {
            throw new DomainException("Private Key is required to disable encryption.", "PRIVATE_KEY_REQUIRED");
        }

        if (!channel.TryGetEncryptedKeyForEpoch(channel.CurrentKeyEpoch, requestingUserId, out var existingCipherKey))
        {
            throw new DomainException("Could not find your decryption key to disable encryption. Your session may be out of date.", "DECRYPTION_KEY_NOT_FOUND");
        }

        return existingCipherKey;
    }

    private static Dictionary<int, Dictionary<string, string>> BuildEpochSnapshot(ChatChannel channel)
    {
        var snapshot = new Dictionary<int, Dictionary<string, string>>();
        foreach (var kvp in channel.EncryptedEpochKeys)
        {
            snapshot[kvp.Key] = new Dictionary<string, string>(kvp.Value);
        }

        if (channel.EncryptedChannelKeys.Count > 0 && !snapshot.ContainsKey(channel.CurrentKeyEpoch))
        {
            snapshot[channel.CurrentKeyEpoch] = new Dictionary<string, string>(channel.EncryptedChannelKeys);
        }

        return snapshot;
    }

    private async Task ProcessChannelEncryptionToggleAsync(
        ChatChannel channel,
        bool enableEncryption,
        bool isDMRestricted,
        string requestingUserId,
        string? privateKeyContext,
        string? existingCipherKeyForDisable,
        Dictionary<int, Dictionary<string, string>>? epochSnapshot,
        int previousCurrentEpoch,
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
                await DecryptChannelAsync(
                    channel,
                    allMessages,
                    requestingUserId,
                    privateKeyContext,
                    existingCipherKeyForDisable,
                    epochSnapshot,
                    previousCurrentEpoch,
                    cancellationToken);
            }

            _channels.Save(channel);
            _chatState.NotifyChannelUpdated(channel);
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
            _chatState.NotifyChannelUpdated(channel);
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

        channel.CurrentKeyEpoch = 0;
        channel.EncryptedChannelKeys.Clear();
        channel.EncryptedEpochKeys.Clear();
        foreach (var uid in allowedUserIds)
        {
            var user = _employees.GetById(uid);
            if (user != null && user.IsActive && !user.IsBanned && !string.IsNullOrEmpty(user.PublicKey))
            {
                channel.EncryptedChannelKeys[uid] = _crypto.EncryptRsa(newChannelKey, user.PublicKey);
            }
        }

        if (!isDMRestricted && _escrowService.IsEscrowAvailable)
        {
            channel.EncryptedChannelKeys["ServerMaster"] = _crypto.EncryptRsa(newChannelKey, _escrowService.EscrowPublicKey!);
        }

        channel.EncryptedEpochKeys[0] = new Dictionary<string, string>(channel.EncryptedChannelKeys);

        foreach (var m in allMessages)
        {
            bool changed = false;
            if (!m.IsEncrypted && !string.IsNullOrEmpty(m.Content))
            {
                m.Content = _crypto.EncryptAes(m.Content, newChannelKey);
                m.IsEncrypted = true;
                m.KeyEpoch = 0;
                changed = true;
            }

            if (m.Attachments != null && m.Attachments.Count > 0)
            {
                foreach (var att in m.Attachments)
                {
                    att.KeyEpoch = 0;
                }
                changed = true;
            }

            if (changed)
            {
                _messages.Save(m);
            }
        }

        await EncryptChannelAttachmentsAsync(channel, allMessages, newChannelKey, cancellationToken);

        _albumService.ReEvaluateAlbumEncryptionForChannel(channel.Id, newChannelKey, requestingUserId, privateKeyContext);
    }

    /// <summary>
    /// Strips evicted or unauthorized users from all stored channel key epochs and, for encrypted
    /// channels, archives the sanitized current epoch before rotating to a fresh AES-256 key
    /// wrapped exclusively for remaining active authorized members.
    /// </summary>
    public virtual bool RotateChannelKeyOnEviction(
        ChatChannel channel,
        IEnumerable<string> evictedUserIds,
        IEnumerable<Employee> remainingAuthorizedMembers)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(evictedUserIds);
        ArgumentNullException.ThrowIfNull(remainingAuthorizedMembers);

        var remainingMembers = remainingAuthorizedMembers
            .Where(m => m.IsActive && !m.IsBanned)
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var remainingIds = remainingMembers.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var evictedSet = evictedUserIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        // Include any stale non-escrow key holders no longer authorized so no unauthorized user keeps historical epochs.
        foreach (var ownerId in channel.EncryptedChannelKeys.Keys)
        {
            if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal) && !remainingIds.Contains(ownerId))
            {
                evictedSet.Add(ownerId);
            }
        }
        foreach (var epochDict in channel.EncryptedEpochKeys.Values)
        {
            foreach (var ownerId in epochDict.Keys)
            {
                if (!string.Equals(ownerId, "ServerMaster", StringComparison.Ordinal) && !remainingIds.Contains(ownerId))
                {
                    evictedSet.Add(ownerId);
                }
            }
        }

        if (evictedSet.Count == 0)
        {
            return false;
        }

        bool modified = false;
        foreach (var uid in evictedSet)
        {
            if (channel.RemoveUserKeysFromAllEpochs(uid))
            {
                modified = true;
            }
        }

        if (!channel.IsEncrypted)
        {
            return modified;
        }

        // Preserve sanitized historical keys for remaining members under the outgoing epoch before rotating.
        if (!channel.EncryptedEpochKeys.ContainsKey(channel.CurrentKeyEpoch) && channel.EncryptedChannelKeys.Count > 0)
        {
            channel.EncryptedEpochKeys[channel.CurrentKeyEpoch] = new Dictionary<string, string>(channel.EncryptedChannelKeys);
        }

        channel.CurrentKeyEpoch++;
        var newChannelKey = _crypto.GenerateAesKeyBase64();
        var newEpochKeys = new Dictionary<string, string>();

        foreach (var member in remainingMembers)
        {
            if (!string.IsNullOrEmpty(member.PublicKey))
            {
                newEpochKeys[member.Id] = _crypto.EncryptRsa(newChannelKey, member.PublicKey);
            }
        }

        var isDMRestricted = channel.ChannelType == ChatChannelType.Direct ||
                             channel.ChannelType == ChatChannelType.Group;
        if (!isDMRestricted && _escrowService.IsEscrowAvailable && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
        {
            newEpochKeys["ServerMaster"] = _crypto.EncryptRsa(newChannelKey, _escrowService.EscrowPublicKey);
        }

        channel.EncryptedChannelKeys = new Dictionary<string, string>(newEpochKeys);
        channel.EncryptedEpochKeys[channel.CurrentKeyEpoch] = new Dictionary<string, string>(newEpochKeys);

        _logger.LogInformation(
            "Rotated channel key for {ChannelId} to epoch {Epoch} after evicting {Count} user(s)",
            channel.Id,
            channel.CurrentKeyEpoch,
            evictedSet.Count);

        return true;
    }

    private Task EncryptChannelAttachmentsAsync(ChatChannel channel, List<ChatMessage> allMessages, string channelAesKey, CancellationToken cancellationToken) =>
        TransformChannelAttachmentsAsync(
            channel,
            allMessages,
            channelAesKey,
            (_, _) => channelAesKey,
            (path, key, ct) => _fileService.EncryptFileAsync(path, key, ct),
            "encrypt",
            cancellationToken);

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
                .Where(e => e.IsActive && !e.IsBanned && e.HasVaultPassword && _chatAuth.CanUserAccessChannel(channel, e));

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
        string requestingUserId,
        string? privateKeyContext,
        string? existingCipherKeyForDisable,
        Dictionary<int, Dictionary<string, string>>? epochSnapshot,
        int previousCurrentEpoch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(existingCipherKeyForDisable) || string.IsNullOrEmpty(privateKeyContext))
        {
            return;
        }

        try
        {
            var plainCurrentKey = _crypto.DecryptRsa(existingCipherKeyForDisable, privateKeyContext);
            var resolvedEpochKeys = ResolveDisableEpochKeys(
                epochSnapshot,
                previousCurrentEpoch,
                plainCurrentKey,
                requestingUserId,
                privateKeyContext);

            foreach (var m in allMessages)
            {
                var epochKey = resolvedEpochKeys.TryGetValue(m.KeyEpoch, out var foundKey) ? foundKey : plainCurrentKey;

                if (m.IsEncrypted && !string.IsNullOrEmpty(m.Content))
                {
                    try
                    {
                        m.Content = _crypto.DecryptAes(m.Content, epochKey);
                        m.IsEncrypted = false;
                        m.KeyEpoch = 0;
                        if (m.Attachments != null)
                        {
                            foreach (var att in m.Attachments)
                            {
                                att.KeyEpoch = 0;
                            }
                        }
                        _messages.Save(m);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to decrypt message {MessageId} during encryption toggle.", m.Id);
                    }
                }
                else if (m.KeyEpoch != 0)
                {
                    m.KeyEpoch = 0;
                    _messages.Save(m);
                }
            }

            await DecryptChannelAttachmentsAsync(channel, allMessages, plainCurrentKey, resolvedEpochKeys, cancellationToken);
            _albumService.ReEvaluateAlbumDecryptionForChannel(channel.Id, plainCurrentKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt channel key during encryption toggle for {ChannelId}", channel.Id);
        }
    }

    private Dictionary<int, string> ResolveDisableEpochKeys(
        Dictionary<int, Dictionary<string, string>>? epochSnapshot,
        int previousCurrentEpoch,
        string plainCurrentKey,
        string requestingUserId,
        string privateKeyContext)
    {
        var resolved = new Dictionary<int, string>
        {
            [previousCurrentEpoch] = plainCurrentKey
        };

        if (epochSnapshot == null)
        {
            return resolved;
        }

        foreach (var (epoch, keys) in epochSnapshot)
        {
            if (resolved.ContainsKey(epoch))
            {
                continue;
            }

            if (keys.TryGetValue(requestingUserId, out var userKey))
            {
                try
                {
                    resolved[epoch] = _crypto.DecryptRsa(userKey, privateKeyContext);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to decrypt historical epoch {Epoch} key with user private key.", epoch);
                }
            }

            if (_escrowService.IsEscrowAvailable && keys.TryGetValue("ServerMaster", out var escrowKey))
            {
                try
                {
                    resolved[epoch] = _crypto.DecryptRsa(escrowKey, _escrowService.DecryptedEscrowPrivateKey!);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to decrypt historical epoch {Epoch} key with escrow.", epoch);
                }
            }
        }

        return resolved;
    }

    private Task DecryptChannelAttachmentsAsync(
        ChatChannel channel,
        List<ChatMessage> allMessages,
        string channelAesKey,
        Dictionary<int, string> resolvedEpochKeys,
        CancellationToken cancellationToken) =>
        TransformChannelAttachmentsAsync(
            channel,
            allMessages,
            channelAesKey,
            (m, att) => resolvedEpochKeys.TryGetValue(att.KeyEpoch, out var foundAttKey)
                ? foundAttKey
                : (resolvedEpochKeys.TryGetValue(m.KeyEpoch, out var foundMsgKey) ? foundMsgKey : channelAesKey),
            (path, key, ct) => _fileService.DecryptFileAsync(path, key, ct),
            "decrypt",
            cancellationToken);

    private async Task TransformChannelAttachmentsAsync(
        ChatChannel channel,
        List<ChatMessage> allMessages,
        string fallbackChannelKey,
        Func<ChatMessage, ChatAttachment, string> resolveAttachmentKey,
        Func<string, string, CancellationToken, Task> transformFileAsync,
        string actionVerb,
        CancellationToken cancellationToken)
    {
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in allMessages)
        {
            if (m.Attachments == null || m.Attachments.Count == 0) continue;

            foreach (var att in m.Attachments)
            {
                var attKey = resolveAttachmentKey(m, att);
                foreach (var path in ResolveAttachmentPhysicalPaths(channel.Id, att))
                {
                    if (processedPaths.Add(path) && File.Exists(path))
                    {
                        try
                        {
                            await transformFileAsync(path, attKey, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to {Action} attachment at {Path} for channel {ChannelId}", actionVerb, path, channel.Id);
                        }
                    }
                }
            }
        }

        // Scan channel upload directory to ensure no unprocessed files remain
        var channelUploadDir = _fileService.GetPhysicalPath("chat", channel.Id, "");
        if (Directory.Exists(channelUploadDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(channelUploadDir))
            {
                if (processedPaths.Add(filePath) && File.Exists(filePath))
                {
                    try
                    {
                        await transformFileAsync(filePath, fallbackChannelKey, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to {Action} file at {Path} for channel {ChannelId}", actionVerb, filePath, channel.Id);
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
            paths.Add(_fileService.GetPhysicalPath("chat", channelId, FileService.GetThumbnailFileName(fileName)));
            paths.Add(_fileService.GetPhysicalPath("chat", channelId, FileService.GetThumbnailFileName(fileName, small: true)));

            try
            {
                paths.Add(_fileService.GetAttachmentPhysicalPath(channelId, fileName, false));
                paths.Add(_fileService.GetAttachmentPhysicalPath(channelId, fileName, true));
            }
            catch { }
        }

        return paths;
    }

    public virtual Task ClaimEscrowedKeysAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!_escrowService.IsEscrowAvailable) return Task.CompletedTask;

        var user = _employees.GetById(userId);
        if (user is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            !user.HasPermission(AppPermissions.Chat.Use) ||
            string.IsNullOrEmpty(user.PublicKey))
        {
            return Task.CompletedTask;
        }

        if (user.HasPermission(AppPermissions.Albums.View))
        {
            _albumService.ClaimEscrowedAlbumsForUser(userId);
        }

        var allChannels = _channels.GetAll().Where(c => c.IsEncrypted).ToList();

        foreach (var channel in allChannels)
        {
            if (!_chatAuth.CanUserAccessChannel(channel, user)) continue;
            if (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group) continue;

            bool modified = false;

            // Ensure legacy CurrentKeyEpoch is materialized in EncryptedEpochKeys before healing all epochs.
            if (channel.EncryptedChannelKeys.Count > 0 && !channel.EncryptedEpochKeys.ContainsKey(channel.CurrentKeyEpoch))
            {
                channel.EncryptedEpochKeys[channel.CurrentKeyEpoch] = new Dictionary<string, string>(channel.EncryptedChannelKeys);
                modified = true;
            }

            foreach (var (epoch, epochKeys) in channel.EncryptedEpochKeys)
            {
                if (epochKeys.ContainsKey(userId)) continue;
                if (!epochKeys.TryGetValue("ServerMaster", out var encryptedEscrowKey)) continue;

                try
                {
                    var plainEpochKey = _crypto.DecryptRsa(encryptedEscrowKey, _escrowService.DecryptedEscrowPrivateKey!);
                    var wrapped = _crypto.EncryptRsa(plainEpochKey, user.PublicKey);
                    epochKeys[userId] = wrapped;
                    if (epoch == channel.CurrentKeyEpoch)
                    {
                        channel.EncryptedChannelKeys[userId] = wrapped;
                    }
                    modified = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to claim escrow for user {UserId} in channel {ChannelId} epoch {Epoch}", userId, channel.Id, epoch);
                }
            }

            if (modified)
            {
                _channels.Save(channel);
                _chatState.NotifyChannelUpdated(channel);
                _logger.LogInformation("Claimed escrow key(s) for user {UserId} in channel {ChannelId}", userId, channel.Id);
            }
        }

        return Task.CompletedTask;
    }

    public virtual Task ShareKeyOpportunisticallyAsync(string channelId, string plainChannelKey, CancellationToken cancellationToken = default) =>
        ShareKeyOpportunisticallyAsync(channelId, plainChannelKey, expectedEpoch: null, requestingUserId: null, cancellationToken);

    public virtual Task ShareKeyOpportunisticallyAsync(string channelId, string plainChannelKey, int? expectedEpoch, CancellationToken cancellationToken = default) =>
        ShareKeyOpportunisticallyAsync(channelId, plainChannelKey, expectedEpoch, requestingUserId: null, cancellationToken);

    public virtual Task ShareKeyOpportunisticallyAsync(string channelId, string plainChannelKey, int? expectedEpoch, string? requestingUserId, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                var channel = _channels.GetById(channelId);
                if (channel == null || !channel.IsEncrypted) return;

                if (!string.IsNullOrEmpty(requestingUserId))
                {
                    var caller = _employees.GetById(requestingUserId);
                    if (caller is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
                        !caller.HasPermission(AppPermissions.Chat.Use) ||
                        !_chatAuth.CanUserAccessChannel(channel, caller))
                    {
                        return;
                    }
                }

                var targetEpoch = expectedEpoch ?? channel.CurrentKeyEpoch;
                if (targetEpoch != channel.CurrentKeyEpoch)
                {
                    return;
                }

                bool modified = false;
                if (!channel.EncryptedEpochKeys.TryGetValue(targetEpoch, out var epochKeys))
                {
                    epochKeys = new Dictionary<string, string>(channel.EncryptedChannelKeys);
                    channel.EncryptedEpochKeys[targetEpoch] = epochKeys;
                }

                if (_escrowService.IsEscrowAvailable && !channel.EncryptedChannelKeys.ContainsKey("ServerMaster"))
                {
                    bool isDMRestricted = channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group;
                    if (!isDMRestricted && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
                    {
                        var wrappedEscrow = _crypto.EncryptRsa(plainChannelKey, _escrowService.EscrowPublicKey);
                        channel.EncryptedChannelKeys["ServerMaster"] = wrappedEscrow;
                        epochKeys["ServerMaster"] = wrappedEscrow;
                        modified = true;
                        _logger.LogInformation("Opportunistically healed ServerMaster escrow key for channel {ChannelId}", channelId);
                    }
                }

                var allActiveKeyedUsers = _employees.GetAll()
                    .Where(e => e.IsActive && !e.IsSuspended && !e.IsBanned &&
                                e.HasVaultPassword && !string.IsNullOrEmpty(e.PublicKey) &&
                                e.HasPermission(AppPermissions.Chat.Use))
                    .ToList();

                foreach (var u in allActiveKeyedUsers)
                {
                    if (string.IsNullOrEmpty(u.PublicKey)) continue;

                    if (channel.EncryptedChannelKeys.ContainsKey(u.Id) && epochKeys.ContainsKey(u.Id)) continue;

                    if (!_chatAuth.CanUserAccessChannel(channel, u)) continue;

                    try
                    {
                        var wrappedUserKey = _crypto.EncryptRsa(plainChannelKey, u.PublicKey);
                        channel.EncryptedChannelKeys[u.Id] = wrappedUserKey;
                        epochKeys[u.Id] = wrappedUserKey;
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
                    _chatState.NotifyChannelUpdated(channel);
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

            if (channel.IsEncrypted && !HasAnyDecryptCandidateKey(channel, requestingUserId, requestingUserPrivateKey))
            {
                // Cannot decrypt any epoch of this channel without a user key or escrow - skip it
                continue;
            }

            var channelResults = SearchChannelMessages(channel, requestingUserId, requestingUserPrivateKey, filter, resolvedSenderIds, resolvedMentionIds, cancellationToken);
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
        var user = _employees.GetById(requestingUserId);
        if (user == null || !user.IsActive || user.IsBanned)
        {
            return false;
        }

        return _chatAuth.CanUserAccessChannel(channel, user);
    }

    private bool HasAnyDecryptCandidateKey(ChatChannel channel, string requestingUserId, string? requestingUserPrivateKey)
    {
        if (string.IsNullOrEmpty(requestingUserPrivateKey))
        {
            return false;
        }

        if (channel.HasAnyEpochKey(requestingUserId))
        {
            return true;
        }

        if (string.Equals(channel.ChannelType, ChatChannelType.Direct, StringComparison.Ordinal) ||
            string.Equals(channel.ChannelType, ChatChannelType.Group, StringComparison.Ordinal))
        {
            return false;
        }

        return _escrowService.IsEscrowAvailable && channel.HasAnyEpochKey("ServerMaster");
    }

    private HashSet<string>? ResolveSenderIds(List<string> senderIdentifiers, string requestingUserId) =>
        ResolveFilteredUserIds(senderIdentifiers, requestingUserId);

    private HashSet<string>? ResolveMentionIds(List<string> mentionIdentifiers, string requestingUserId) =>
        ResolveFilteredUserIds(mentionIdentifiers, requestingUserId);

    private HashSet<string>? ResolveFilteredUserIds(List<string> identifiers, string requestingUserId)
    {
        if (identifiers.Count == 0) return null;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requester = string.IsNullOrWhiteSpace(requestingUserId) ? null : _employees.GetById(requestingUserId);
        var basePool = _employees.GetAll()
            .Where(e => e.IsActive && !e.IsBanned && !e.IsSystem && e.Id != AccountDeletionService.SystemDeletedUserId);
        var candidateEmployees = _privacyService != null
            ? _privacyService.FilterDiscoverableEmployees(requester, basePool)
            : basePool.ToList();
        var privacyConfig = _privacyService?.GetPrivacyConfig();

        foreach (var id in identifiers)
        {
            if (id.Equals("me", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(requestingUserId);
                continue;
            }

            var empById = candidateEmployees.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (empById != null)
            {
                set.Add(empById.Id);
                continue;
            }

            bool isAdminQuery = id.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("administrator", StringComparison.OrdinalIgnoreCase) ||
                                id.Equals("admins", StringComparison.OrdinalIgnoreCase);

            var matched = candidateEmployees.Where(e =>
            {
                bool matchesAdminQuery = isAdminQuery && e.IsEffectiveAdmin && (
                    _privacyService == null ||
                    requester?.IsEffectiveAdmin == true ||
                    privacyConfig == null ||
                    privacyConfig.AdminVisibility == AdminVisibilityMode.AllAdmins ||
                    (privacyConfig.AdminVisibility == AdminVisibilityMode.DesignatedAdminsOnly &&
                     privacyConfig.DesignatedAdminIds.Contains(e.Id)));

                bool canSeeEmail = _privacyService != null
                    ? _privacyService.CanViewUserEmail(requester, e)
                    : (e.IsEmailPublic ||
                       string.Equals(e.Id, requestingUserId, StringComparison.Ordinal) ||
                       requester?.IsEffectiveAdmin == true);

                return matchesAdminQuery ||
                       e.FullName.Contains(id, StringComparison.OrdinalIgnoreCase) ||
                       (!string.IsNullOrEmpty(e.FirstName) && e.FirstName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                       (!string.IsNullOrEmpty(e.LastName) && e.LastName.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                       (canSeeEmail && !string.IsNullOrEmpty(e.Email) && e.Email.Contains(id, StringComparison.OrdinalIgnoreCase)) ||
                       (!string.IsNullOrEmpty(e.Position) && e.Position.Contains(id, StringComparison.OrdinalIgnoreCase));
            });

            foreach (var m in matched)
            {
                set.Add(m.Id);
            }
        }

        return set;
    }

    private string? TryResolveChannelKey(ChatChannel channel, string requestingUserId, string? requestingUserPrivateKey, int? epoch = null)
    {
        if (string.IsNullOrEmpty(requestingUserPrivateKey))
        {
            return null;
        }

        var targetEpoch = epoch ?? channel.CurrentKeyEpoch;

        if (channel.TryGetEncryptedKeyForEpoch(targetEpoch, requestingUserId, out var userCipherKey))
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

        if (!string.Equals(channel.ChannelType, ChatChannelType.Direct, StringComparison.Ordinal) &&
            !string.Equals(channel.ChannelType, ChatChannelType.Group, StringComparison.Ordinal) &&
            _escrowService.IsEscrowAvailable &&
            channel.TryGetEncryptedKeyForEpoch(targetEpoch, "ServerMaster", out var escrowCipherKey))
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
        string requestingUserId,
        string? requestingUserPrivateKey,
        ChatSearchFilter filter,
        HashSet<string>? resolvedSenderIds,
        HashSet<string>? resolvedMentionIds,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatMessage>();
        var index = _messages.GetIndexForChannel(channel.Id);
        var epochKeys = new Dictionary<int, string?>();

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
            else if (channel.IsEncrypted && entry.IsEncrypted)
            {
                var match = TryDecryptAndMatchMessage(channel, entry.Id, requestingUserId, requestingUserPrivateKey, epochKeys, filter, resolvedMentionIds);
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
        ChatChannel channel,
        string messageId,
        string requestingUserId,
        string? requestingUserPrivateKey,
        Dictionary<int, string?> epochKeys,
        ChatSearchFilter filter,
        HashSet<string>? resolvedMentionIds)
    {
        var msg = _messages.GetMessageFromDisk(channel.Id, messageId);
        if (msg == null) return null;

        if (!epochKeys.TryGetValue(msg.KeyEpoch, out var plainChannelKey))
        {
            plainChannelKey = TryResolveChannelKey(channel, requestingUserId, requestingUserPrivateKey, msg.KeyEpoch);
            epochKeys[msg.KeyEpoch] = plainChannelKey;
        }

        if (plainChannelKey == null)
        {
            return null;
        }

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

    /// <summary>
    /// Generates an RSA key pair for <paramref name="employee"/>, encrypts the private key with
    /// <paramref name="password"/>, persists the keys on the employee record, and returns both keys.
    /// </summary>
    public (string PlainPrivateKey, string EncryptedPrivateKey) ProvisionUserVaultKeys(Employee employee, string password)
    {
        var keys = _crypto.GenerateRsaKeyPair();
        var kek = _crypto.DeriveKeyFromPassword(password, employee.Id);
        var encryptedPrivate = _crypto.EncryptAes(keys.PrivateKey, kek);

        employee.PublicKey = keys.PublicKey;
        employee.EncryptedPrivateKey = encryptedPrivate;
        _employees.Save(employee);
        return (keys.PrivateKey, encryptedPrivate);
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
