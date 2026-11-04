using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class AlbumService
{
    public const int AlbumSuggestionPhotoThreshold = 4;

    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly ICryptoService _crypto;
    private readonly ServerEscrowService _escrowService;
    private readonly EmployeeRepository _employees;
    private readonly IChatChannelAccessService? _chatAccess;
    private readonly IChatAuthorizationService? _chatAuth;
    private readonly FileTokenService? _fileTokenService;
    private readonly IFileService? _fileService;
    private readonly ILogger<AlbumService>? _logger;
    private readonly ConcurrentDictionary<string, object> _albumLocks = new();

    private object GetAlbumLock(string albumId) => _albumLocks.GetOrAdd(albumId, _ => new object());

    public event Action<string>? OnAlbumUpdated;

    public AlbumService(
        AlbumRepository albums,
        ChatChannelRepository channels,
        ICryptoService crypto,
        ServerEscrowService escrowService,
        EmployeeRepository employees,
        ILogger<AlbumService>? logger = null,
        IChatChannelAccessService? chatAccess = null,
        IChatAuthorizationService? chatAuth = null,
        FileTokenService? fileTokenService = null,
        IFileService? fileService = null)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(crypto);
        ArgumentNullException.ThrowIfNull(escrowService);
        ArgumentNullException.ThrowIfNull(employees);

        _albums = albums;
        _channels = channels;
        _crypto = crypto;
        _escrowService = escrowService;
        _employees = employees;
        _logger = logger;
        _chatAccess = chatAccess;
        _chatAuth = chatAuth;
        _fileTokenService = fileTokenService;
        _fileService = fileService;
    }

    private bool IsCallerWriteBlocked(string? userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return true;
        }

        var employee = _employees.GetById(userId);
        return employee != null && (!employee.IsActive || employee.IsSuspended || employee.IsBanned);
    }

    private bool CanUserReadSharedChannel(ChatChannel channel, string userId, Employee? user)
    {
        if (channel == null || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (_chatAuth != null)
        {
            return user != null
                ? _chatAuth.CanUserAccessChannel(channel, user)
                : _chatAuth.CanUserAccessChannel(channel.Id, userId);
        }

        if (_chatAccess != null)
        {
            return _chatAccess.GetUsersForChannel(channel.Id).Contains(userId);
        }

        if (channel.IsPublicChannel)
        {
            return true;
        }

        if (channel.ParticipantIds != null && channel.ParticipantIds.Contains(userId))
        {
            return true;
        }

        if (user != null)
        {
            if (user.IsAdmin)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(user.TeamId) && channel.AllowedTeamIds != null && channel.AllowedTeamIds.Contains(user.TeamId))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanUserPostToSharedChannel(ChatChannel channel, string userId, Employee? user)
    {
        if (channel == null || channel.IsArchived || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var resolvedUser = user ?? _employees.GetById(userId);
        if (_chatAuth != null)
        {
            return resolvedUser != null
                && _chatAuth.CanUserAccessChannel(channel, resolvedUser)
                && _chatAuth.CanUserPostToChannel(channel, resolvedUser);
        }

        if (!CanUserReadSharedChannel(channel, userId, user))
        {
            return false;
        }

        if (!channel.IsAnnouncementOnly)
        {
            return true;
        }

        return (user?.IsAdmin == true) ||
               (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(userId)) ||
               (user != null && !string.IsNullOrEmpty(user.TeamId) && channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Contains(user.TeamId)) ||
               (channel.ChannelType == ChatChannelType.Group && channel.CreatedById == userId);
    }

    public virtual bool CanUserDecryptAlbum(Album? album, Employee? user)
    {
        if (album == null || user == null || !user.IsActive || user.IsBanned)
        {
            return false;
        }

        if (!CanUserViewAlbum(album, user.Id))
        {
            return false;
        }

        if (!album.IsEncrypted)
        {
            return true;
        }

        if (album.EncryptedAlbumKeys.ContainsKey(user.Id))
        {
            return true;
        }

        bool userHasVault = !string.IsNullOrEmpty(user.PublicKey) || user.HasVaultPassword;
        if (album.SharedWithChannelIds != null)
        {
            foreach (var channelId in album.SharedWithChannelIds)
            {
                if (!album.EncryptedAlbumKeys.ContainsKey($"Channel_{channelId}"))
                {
                    continue;
                }

                var channel = _channels.GetById(channelId);
                if (channel == null || !CanUserReadSharedChannel(channel, user.Id, user))
                {
                    continue;
                }

                if (channel.EncryptedChannelKeys.ContainsKey(user.Id) ||
                    (!userHasVault && channel.EncryptedChannelKeys.ContainsKey("ServerMaster")))
                {
                    return true;
                }
            }
        }

        var owner = !string.IsNullOrEmpty(album.OwnerId) ? _employees.GetById(album.OwnerId) : null;
        bool ownerHasVault = owner != null && (!string.IsNullOrEmpty(owner.PublicKey) || owner.HasVaultPassword);
        bool allowEscrowFallback = !ownerHasVault || HasUnencryptedSharedChannel(album);

        return allowEscrowFallback && album.EncryptedAlbumKeys.ContainsKey("Escrow");
    }

    public virtual bool CanUserViewAlbum(string? albumId, string? userId)
    {
        if (string.IsNullOrEmpty(albumId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var album = _albums.GetById(albumId);
        return CanUserViewAlbum(album, userId);
    }

    public virtual bool CanUserViewAlbum(Album? album, string? userId)
    {
        if (album == null || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        if (string.Equals(album.OwnerId, userId, StringComparison.OrdinalIgnoreCase) ||
            (album.ContributorUserIds != null && album.ContributorUserIds.Contains(userId)))
        {
            return true;
        }

        if (album.SharedWithChannelIds != null)
        {
            foreach (var channelId in album.SharedWithChannelIds)
            {
                if (_chatAccess != null)
                {
                    var channelUsers = _chatAccess.GetUsersForChannel(channelId);
                    if (channelUsers.Contains(userId))
                    {
                        return true;
                    }
                }
                else
                {
                    var channel = _channels.GetById(channelId);
                    if (channel == null) continue;
                    if (channel.IsPublicChannel)
                    {
                        return true;
                    }
                    if (channel.ParticipantIds != null && channel.ParticipantIds.Contains(userId))
                    {
                        return true;
                    }
                    var user = _employees.GetById(userId);
                    if (user != null && !string.IsNullOrEmpty(user.TeamId) && channel.AllowedTeamIds != null && channel.AllowedTeamIds.Contains(user.TeamId))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public SpokesResult<Album> CreateAlbum(Album album, string ownerId, string? ownerPrivateKey)
    {
        if (album == null)
        {
            return SpokesResult<Album>.Failure("Album cannot be null.", "INVALID_ARGUMENT");
        }
        if (string.IsNullOrEmpty(ownerId))
        {
            return SpokesResult<Album>.Failure("OwnerId is required.", "INVALID_ARGUMENT");
        }
        if (IsCallerWriteBlocked(ownerId))
        {
            return SpokesResult<Album>.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        album.OwnerId = ownerId;
        album.IsEncrypted = true;

        var plainAlbumKey = _crypto.GenerateAesKeyBase64();
        SyncAlbumEncryptionKeys(album, plainAlbumKey);

        _albums.Save(album);
        OnAlbumUpdated?.Invoke(album.Id);
        return SpokesResult<Album>.Success(album);
    }

    public bool HasUnencryptedSharedChannel(Album album)
    {
        if (album.SharedWithChannelIds == null || album.SharedWithChannelIds.Count == 0)
        {
            return false;
        }

        foreach (var channelId in album.SharedWithChannelIds)
        {
            var channel = _channels.GetById(channelId);
            if (channel != null && !channel.IsEncrypted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Synchronizes an album's encryption keys with its current ownership, contributors, and channel shares.
    /// Private albums owned by a user with a configured vault are encrypted exclusively with participant/channel keys
    /// and have server Escrow removed immediately. Escrow is only added when the owner has no vault or the album is
    /// shared with at least one unencrypted channel.
    /// </summary>
    public bool SyncAlbumEncryptionKeys(Album album, string? plainAlbumKey = null)
    {
        if (album == null || !album.IsEncrypted)
        {
            return false;
        }

        bool modified = false;

        if (string.IsNullOrEmpty(plainAlbumKey) &&
            _escrowService.IsEscrowAvailable &&
            !string.IsNullOrEmpty(_escrowService.DecryptedEscrowPrivateKey) &&
            album.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
        {
            try
            {
                plainAlbumKey = _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to decrypt escrow key during SyncAlbumEncryptionKeys for album {AlbumId}", album.Id);
            }
        }

        var owner = !string.IsNullOrEmpty(album.OwnerId) ? _employees.GetById(album.OwnerId) : null;
        bool ownerHasPublicKey = owner != null && !string.IsNullOrEmpty(owner.PublicKey);

        if (!string.IsNullOrEmpty(plainAlbumKey))
        {
            if (ownerHasPublicKey && !album.EncryptedAlbumKeys.ContainsKey(album.OwnerId))
            {
                try
                {
                    album.EncryptedAlbumKeys[album.OwnerId] = _crypto.EncryptRsa(plainAlbumKey, owner!.PublicKey!);
                    modified = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to encrypt album key for owner {OwnerId} on album {AlbumId}", album.OwnerId, album.Id);
                }
            }

            foreach (var contributorId in album.ContributorUserIds)
            {
                if (string.IsNullOrEmpty(contributorId) || album.EncryptedAlbumKeys.ContainsKey(contributorId))
                {
                    continue;
                }

                var contributor = _employees.GetById(contributorId);
                if (contributor != null && !string.IsNullOrEmpty(contributor.PublicKey))
                {
                    try
                    {
                        album.EncryptedAlbumKeys[contributorId] = _crypto.EncryptRsa(plainAlbumKey, contributor.PublicKey);
                        modified = true;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to encrypt album key for contributor {ContributorId} on album {AlbumId}", contributorId, album.Id);
                    }
                }
            }
        }

        bool hasUnencryptedChannel = HasUnencryptedSharedChannel(album);
        bool requiresEscrow = !ownerHasPublicKey || hasUnencryptedChannel;

        if (requiresEscrow)
        {
            if (!album.EncryptedAlbumKeys.ContainsKey("Escrow") &&
                !string.IsNullOrEmpty(plainAlbumKey) &&
                _escrowService.IsEscrowAvailable &&
                !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
            {
                try
                {
                    album.EncryptedAlbumKeys["Escrow"] = _crypto.EncryptRsa(plainAlbumKey, _escrowService.EscrowPublicKey);
                    modified = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to encrypt escrow key on album {AlbumId}", album.Id);
                }
            }
        }
        else if (album.EncryptedAlbumKeys.ContainsKey(album.OwnerId))
        {
            if (album.EncryptedAlbumKeys.Remove("Escrow"))
            {
                modified = true;
            }
        }

        return modified;
    }

    public void ClaimEscrowedAlbumsForUser(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        var userAlbums = _albums.GetAll()
            .Where(a => a.IsEncrypted && (a.OwnerId == userId || a.ContributorUserIds.Contains(userId)))
            .ToList();

        foreach (var album in userAlbums)
        {
            lock (GetAlbumLock(album.Id))
            {
                var currentAlbum = _albums.GetById(album.Id) ?? album;
                if (SyncAlbumEncryptionKeys(currentAlbum))
                {
                    _albums.Save(currentAlbum);
                    OnAlbumUpdated?.Invoke(currentAlbum.Id);
                }
            }
        }
    }

    /// <summary>
    /// Single canonical method to check whether an album is locked for a user and resolve its plaintext key if unlocked.
    /// </summary>
    public virtual bool IsAlbumLocked(Album? album, string? currentUserId, string? currentPrivateKey, out string? plainAlbumKey)
    {
        plainAlbumKey = null;
        if (album == null || !album.IsEncrypted)
        {
            return false;
        }

        if (string.IsNullOrEmpty(currentUserId))
        {
            return true;
        }

        plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);
        return string.IsNullOrEmpty(plainAlbumKey);
    }

    public virtual bool IsAlbumLocked(Album? album, string? currentUserId, string? currentPrivateKey)
        => IsAlbumLocked(album, currentUserId, currentPrivateKey, out _);

    public virtual string? GetPlainAlbumKey(Album album, string currentUserId, string? currentPrivateKey)
    {
        if (album == null || !CanUserViewAlbum(album, currentUserId))
        {
            return null;
        }

        // Eagerly migrate any escrowed private album to owner-only encryption as soon as the owner has a public key
        if (!string.IsNullOrEmpty(album.Id) && album.EncryptedAlbumKeys.ContainsKey("Escrow"))
        {
            lock (GetAlbumLock(album.Id))
            {
                var persisted = _albums.GetById(album.Id);
                var target = persisted ?? album;
                if (SyncAlbumEncryptionKeys(target))
                {
                    if (persisted != null)
                    {
                        _albums.Save(persisted);
                    }
                    if (!ReferenceEquals(target, album))
                    {
                        album.EncryptedAlbumKeys = new Dictionary<string, string>(target.EncryptedAlbumKeys);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(currentPrivateKey) && album.EncryptedAlbumKeys.TryGetValue(currentUserId, out var cipherUserKey))
        {
            try
            {
                var plainKey = _crypto.DecryptRsa(cipherUserKey, currentPrivateKey);
                if (!string.IsNullOrEmpty(album.Id) && SyncAlbumEncryptionKeys(album, plainKey))
                {
                    lock (GetAlbumLock(album.Id))
                    {
                        var persisted = _albums.GetById(album.Id);
                        if (persisted != null && SyncAlbumEncryptionKeys(persisted, plainKey))
                        {
                            _albums.Save(persisted);
                        }
                    }
                }
                return plainKey;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to decrypt album key directly for user {UserId} on album {AlbumId}", currentUserId, album.Id);
            }
        }

        if (!string.IsNullOrEmpty(currentPrivateKey))
        {
            foreach (var channelId in album.SharedWithChannelIds)
            {
                if (album.EncryptedAlbumKeys.TryGetValue($"Channel_{channelId}", out var cipherAlbumKey))
                {
                    var channel = _channels.GetById(channelId);
                    if (channel != null && channel.EncryptedChannelKeys.TryGetValue(currentUserId, out var cipherChannelKey))
                    {
                        try
                        {
                            var channelKey = _crypto.DecryptRsa(cipherChannelKey, currentPrivateKey);
                            return _crypto.DecryptAes(cipherAlbumKey, channelKey);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Failed to decrypt album key via channel {ChannelId} for user {UserId} on album {AlbumId}", channelId, currentUserId, album.Id);
                        }
                    }
                }
            }
        }
        else
        {
            var currentUser = !string.IsNullOrEmpty(currentUserId) ? _employees.GetById(currentUserId) : null;
            bool userHasVault = currentUser != null && (!string.IsNullOrEmpty(currentUser.PublicKey) || currentUser.HasVaultPassword);
            if (!userHasVault && _escrowService.IsEscrowAvailable && !string.IsNullOrEmpty(_escrowService.DecryptedEscrowPrivateKey))
            {
                foreach (var channelId in album.SharedWithChannelIds)
                {
                    if (album.EncryptedAlbumKeys.TryGetValue($"Channel_{channelId}", out var cipherAlbumKey))
                    {
                        var channel = _channels.GetById(channelId);
                        if (channel != null && channel.EncryptedChannelKeys.TryGetValue("ServerMaster", out var serverMasterKey))
                        {
                            try
                            {
                                var channelKey = _crypto.DecryptRsa(serverMasterKey, _escrowService.DecryptedEscrowPrivateKey!);
                                return _crypto.DecryptAes(cipherAlbumKey, channelKey);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed to decrypt album key via ServerMaster for channel {ChannelId} on album {AlbumId}", channelId, album.Id);
                            }
                        }
                    }
                }
            }
        }

        var owner = !string.IsNullOrEmpty(album.OwnerId) ? _employees.GetById(album.OwnerId) : null;
        bool ownerHasVault = owner != null && (!string.IsNullOrEmpty(owner.PublicKey) || owner.HasVaultPassword);
        bool allowEscrowFallback = !ownerHasVault || HasUnencryptedSharedChannel(album);

        if (allowEscrowFallback && _escrowService.IsEscrowAvailable && album.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
        {
            try
            {
                return _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to decrypt album key via escrow for album {AlbumId}", album.Id);
            }
        }

        return null;
    }

    public SpokesResult ShareAlbumWithChannel(string albumId, string currentUserId, string channelId, string? currentPrivateKey)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("ShareAlbumWithChannel failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger?.LogWarning("ShareAlbumWithChannel failed: channel {ChannelId} not found", channelId);
            return SpokesResult.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        var callerEmployee = _employees.GetById(currentUserId);

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;

            // Re-mentioning an album in a channel where it is ALREADY shared does not mutate keys or escrow;
            // allow any authorized viewer who has read access to that channel.
            if (album.SharedWithChannelIds.Contains(channelId))
            {
                if (!CanUserViewAlbum(album, currentUserId) || !CanUserReadSharedChannel(channel, currentUserId, callerEmployee))
                {
                    _logger?.LogWarning("ShareAlbumWithChannel failed: user {UserId} cannot access already-shared channel {ChannelId} for album {AlbumId}", currentUserId, channelId, albumId);
                    return SpokesResult.Failure($"User '{currentUserId}' does not have access to channel '{channelId}'.", "FORBIDDEN");
                }

                return SpokesResult.Success();
            }

            // Sharing an album with a NEW channel requires the album owner and posting permission in the target channel.
            if (album.OwnerId != currentUserId)
            {
                _logger?.LogWarning("ShareAlbumWithChannel failed: non-owner {UserId} attempted to share album {AlbumId} with new channel {ChannelId}", currentUserId, albumId, channelId);
                return SpokesResult.Failure($"User '{currentUserId}' is not authorized to share album '{albumId}' with a new channel.", "FORBIDDEN");
            }

            if (!CanUserPostToSharedChannel(channel, currentUserId, callerEmployee))
            {
                _logger?.LogWarning("ShareAlbumWithChannel failed: owner {UserId} is not permitted to post to channel {ChannelId}", currentUserId, channelId);
                return SpokesResult.Failure($"User '{currentUserId}' is not authorized to post to channel '{channelId}'.", "FORBIDDEN");
            }

            var updatedSharedChannels = new HashSet<string>(album.SharedWithChannelIds);
            if (updatedSharedChannels.Add(channelId))
            {
                var plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);
                if (channel.IsEncrypted)
                {
                    if (string.IsNullOrEmpty(plainAlbumKey))
                    {
                        _logger?.LogError("Failed to share album {AlbumId} with encrypted channel {ChannelId}: could not obtain plain album key for user {UserId}", albumId, channelId, currentUserId);
                        return SpokesResult.Failure($"Cannot share album {albumId} with encrypted channel {channelId}: caller cannot decrypt album key.", "ENCRYPTION_ERROR");
                    }

                    if (string.IsNullOrEmpty(currentPrivateKey) || !channel.EncryptedChannelKeys.TryGetValue(currentUserId, out var cipherChannelKey))
                    {
                        _logger?.LogError("Failed to share album {AlbumId} with encrypted channel {ChannelId}: user {UserId} has no channel key or private key is missing", albumId, channelId, currentUserId);
                        return SpokesResult.Failure($"Cannot share album {albumId} with encrypted channel {channelId}: channel key unavailable for user {currentUserId}.", "ENCRYPTION_ERROR");
                    }

                    try
                    {
                        var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, currentPrivateKey);
                        var encryptedKey = _crypto.EncryptAes(plainAlbumKey, plainChannelKey);
                        album.EncryptedAlbumKeys[$"Channel_{channelId}"] = encryptedKey;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to derive or encrypt album key for channel {ChannelId} on album {AlbumId}", channelId, albumId);
                        return SpokesResult.Failure($"Cryptographic operation failed while sharing album {albumId} with channel {channelId}: {ex.Message}", "ENCRYPTION_ERROR");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(plainAlbumKey) && album.EncryptedAlbumKeys.Count > 0)
                    {
                        _logger?.LogError("Failed to share album {AlbumId} with unencrypted channel {ChannelId}: could not obtain plain album key for user {UserId}", albumId, channelId, currentUserId);
                        return SpokesResult.Failure($"Cannot share locked album {albumId} with channel {channelId}: caller cannot decrypt album key.", "ENCRYPTION_ERROR");
                    }

                    if (plainAlbumKey != null && _escrowService.IsEscrowAvailable && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
                    {
                        try
                        {
                            var encryptedEscrowKey = _crypto.EncryptRsa(plainAlbumKey, _escrowService.EscrowPublicKey);
                            album.EncryptedAlbumKeys["Escrow"] = encryptedEscrowKey;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Failed to encrypt escrow key while sharing album {AlbumId} with unencrypted channel {ChannelId}", albumId, channelId);
                            return SpokesResult.Failure($"Cryptographic operation failed while establishing escrow key for album {albumId}: {ex.Message}", "ENCRYPTION_ERROR");
                        }
                    }
                }

                album.SharedWithChannelIds = updatedSharedChannels.ToList();
                SyncAlbumEncryptionKeys(album, plainAlbumKey);
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }

        return SpokesResult.Success();
    }

    public SpokesResult UnshareAlbumWithChannel(string albumId, string currentUserId, string channelId)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("UnshareAlbumWithChannel failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId)
        {
            _logger?.LogWarning("UnshareAlbumWithChannel failed: user {UserId} not authorized to unshare album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to unshare album '{albumId}'.", "FORBIDDEN");
        }

        bool removed = false;
        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var updatedSharedChannels = new HashSet<string>(album.SharedWithChannelIds);
            if (updatedSharedChannels.Remove(channelId))
            {
                removed = true;
                album.EncryptedAlbumKeys.Remove($"Channel_{channelId}");
                album.SharedWithChannelIds = updatedSharedChannels.ToList();

                var owner = _employees.GetById(album.OwnerId);
                bool ownerHasPublicKey = owner != null && !string.IsNullOrEmpty(owner.PublicKey);
                if (!HasUnencryptedSharedChannel(album) && ownerHasPublicKey)
                {
                    SyncAlbumEncryptionKeys(album);
                    if (album.EncryptedAlbumKeys.ContainsKey(album.OwnerId))
                    {
                        album.EncryptedAlbumKeys.Remove("Escrow");
                    }
                }

                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }

        if (removed && _fileTokenService != null)
        {
            _fileTokenService.InvalidateContextTokens("albums", albumId);
            var affectedUsers = _chatAccess?.GetUsersForChannel(channelId);
            if (affectedUsers != null)
            {
                foreach (var userId in affectedUsers)
                {
                    if (!string.Equals(userId, currentUserId, StringComparison.Ordinal))
                    {
                        _fileTokenService.InvalidateUserTokens(userId);
                    }
                }
            }
        }

        return SpokesResult.Success();
    }

    public void ReEvaluateAlbumEncryptionForChannel(string channelId, string newChannelAesKey, string? requestingUserId = null, string? requestingUserPrivateKey = null)
    {
        var albumsInChannel = _albums.GetAll().Where(a => a.SharedWithChannelIds.Contains(channelId)).ToList();

        foreach (var album in albumsInChannel)
        {
            lock (GetAlbumLock(album.Id))
            {
                var currentAlbum = _albums.GetById(album.Id) ?? album;
                try
                {
                    string? plainAlbumKey = null;
                    if (_escrowService.IsEscrowAvailable && currentAlbum.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
                    {
                        plainAlbumKey = _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
                    }
                    else if (!string.IsNullOrEmpty(requestingUserId) && !string.IsNullOrEmpty(requestingUserPrivateKey))
                    {
                        plainAlbumKey = GetPlainAlbumKey(currentAlbum, requestingUserId, requestingUserPrivateKey);
                    }

                    if (!string.IsNullOrEmpty(plainAlbumKey))
                    {
                        currentAlbum.EncryptedAlbumKeys[$"Channel_{channelId}"] = _crypto.EncryptAes(plainAlbumKey, newChannelAesKey);
                        SyncAlbumEncryptionKeys(currentAlbum, plainAlbumKey);
                        _albums.Save(currentAlbum);
                        OnAlbumUpdated?.Invoke(currentAlbum.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to re-evaluate album encryption for channel {ChannelId} on album {AlbumId}", channelId, album.Id);
                }
            }
        }
    }

    public void ReEvaluateAlbumDecryptionForChannel(string channelId, string oldChannelAesKey)
    {
        var albumsInChannel = _albums.GetAll().Where(a => a.SharedWithChannelIds.Contains(channelId)).ToList();

        foreach (var album in albumsInChannel)
        {
            lock (GetAlbumLock(album.Id))
            {
                var currentAlbum = _albums.GetById(album.Id) ?? album;
                try
                {
                    string? plainAlbumKey = null;
                    if (currentAlbum.EncryptedAlbumKeys.TryGetValue($"Channel_{channelId}", out var cipherAlbumKey))
                    {
                        plainAlbumKey = _crypto.DecryptAes(cipherAlbumKey, oldChannelAesKey);
                        currentAlbum.EncryptedAlbumKeys.Remove($"Channel_{channelId}");
                    }

                    if (SyncAlbumEncryptionKeys(currentAlbum, plainAlbumKey) || plainAlbumKey != null)
                    {
                        _albums.Save(currentAlbum);
                        OnAlbumUpdated?.Invoke(currentAlbum.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to re-evaluate album decryption for channel {ChannelId} on album {AlbumId}", channelId, album.Id);
                }
            }
        }
    }

    public SpokesResult<List<AlbumMedia>> UpdateContributors(string albumId, string currentUserId, IEnumerable<string> newContributorIds, string? currentPrivateKey)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult<List<AlbumMedia>>.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("UpdateContributors failed: album {AlbumId} not found", albumId);
            return SpokesResult<List<AlbumMedia>>.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId)
        {
            _logger?.LogWarning("UpdateContributors failed: user {UserId} is not owner of album {AlbumId}", currentUserId, albumId);
            return SpokesResult<List<AlbumMedia>>.Failure($"User '{currentUserId}' is not authorized to update contributors for album '{albumId}'.", "FORBIDDEN");
        }

        List<AlbumMedia> removedMedia = [];
        List<string> removedContributors = [];

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var existingContributors = new HashSet<string>(album.ContributorUserIds);
            var newContributors = new HashSet<string>(newContributorIds);

            foreach (var existing in existingContributors)
            {
                if (!newContributors.Contains(existing))
                    removedContributors.Add(existing);
            }

            var plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);

            if (plainAlbumKey != null)
            {
                foreach (var newId in newContributors)
                {
                    if (!existingContributors.Contains(newId) && !album.EncryptedAlbumKeys.ContainsKey(newId))
                    {
                        var user = _employees.GetById(newId);
                        if (user != null && !string.IsNullOrEmpty(user.PublicKey))
                        {
                            try
                            {
                                album.EncryptedAlbumKeys[newId] = _crypto.EncryptRsa(plainAlbumKey, user.PublicKey);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to encrypt album key for contributor {ContributorId} on album {AlbumId}", newId, albumId);
                            }
                        }
                    }
                }
            }

            foreach (var removedId in removedContributors)
            {
                album.EncryptedAlbumKeys.Remove(removedId);
            }

            album.ContributorUserIds = newContributors.ToList();

            if (removedContributors.Any())
            {
                var updatedMedia = album.Media.ToList();
                removedMedia = updatedMedia.Where(m => removedContributors.Contains(m.AddedByUserId)).ToList();
                foreach (var m in removedMedia) updatedMedia.Remove(m);
                album.Media = updatedMedia;
            }

            SyncAlbumEncryptionKeys(album, plainAlbumKey);
            _albums.Save(album);
            OnAlbumUpdated?.Invoke(albumId);
        }

        if (_fileTokenService != null && removedContributors.Count > 0)
        {
            _fileTokenService.InvalidateContextTokens("albums", albumId);
            foreach (var removedId in removedContributors)
            {
                _fileTokenService.InvalidateUserTokens(removedId);
            }
        }

        return SpokesResult<List<AlbumMedia>>.Success(removedMedia);
    }

    public SpokesResult AddMedia(string albumId, string currentUserId, IEnumerable<AlbumMedia> mediaItems)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("AddMedia failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId))
        {
            _logger?.LogWarning("AddMedia failed: user {UserId} not authorized on album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to add media to album '{albumId}'.", "FORBIDDEN");
        }

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var updatedMedia = album.Media.ToList();
            updatedMedia.AddRange(mediaItems);
            album.Media = updatedMedia;
            _albums.Save(album);
            OnAlbumUpdated?.Invoke(albumId);
        }

        return SpokesResult.Success();
    }

    public SpokesResult RemoveMedia(string albumId, string currentUserId, AlbumMedia mediaItem)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("RemoveMedia failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }

        bool isOwner = album.OwnerId == currentUserId;
        bool isContributor = album.ContributorUserIds.Contains(currentUserId);

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var updatedMedia = album.Media.ToList();
            var itemToRemove = updatedMedia.FirstOrDefault(m => m.Id == mediaItem.Id);
            if (itemToRemove != null)
            {
                if (!isOwner && !(isContributor && itemToRemove.AddedByUserId == currentUserId))
                {
                    _logger?.LogWarning("RemoveMedia failed: user {UserId} not authorized to remove media {MediaId}", currentUserId, mediaItem.Id);
                    return SpokesResult.Failure($"User '{currentUserId}' is not authorized to remove media '{mediaItem.Id}'.", "FORBIDDEN");
                }

                updatedMedia.Remove(itemToRemove);
                album.Media = updatedMedia;
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }

        return SpokesResult.Success();
    }

    public SpokesResult<List<AlbumMedia>> RemoveMediaBulk(string albumId, string currentUserId, IEnumerable<string> mediaIds)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult<List<AlbumMedia>>.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("RemoveMediaBulk failed: album {AlbumId} not found", albumId);
            return SpokesResult<List<AlbumMedia>>.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }

        bool isOwner = album.OwnerId == currentUserId;
        bool isContributor = album.ContributorUserIds.Contains(currentUserId);
        if (!isOwner && !isContributor)
        {
            _logger?.LogWarning("RemoveMediaBulk failed: user {UserId} not authorized on album {AlbumId}", currentUserId, albumId);
            return SpokesResult<List<AlbumMedia>>.Failure($"User '{currentUserId}' is not authorized to remove media from album '{albumId}'.", "FORBIDDEN");
        }

        List<AlbumMedia> removedItems = [];
        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var updatedMedia = album.Media.ToList();
            bool changed = false;

            foreach (var mediaId in mediaIds)
            {
                var itemToRemove = updatedMedia.FirstOrDefault(m => m.Id == mediaId);
                if (itemToRemove != null)
                {
                    if (isOwner || (isContributor && itemToRemove.AddedByUserId == currentUserId))
                    {
                        updatedMedia.Remove(itemToRemove);
                        removedItems.Add(itemToRemove);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                album.Media = updatedMedia;
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }

        return SpokesResult<List<AlbumMedia>>.Success(removedItems);
    }

    public SpokesResult DeleteAlbum(string albumId, string currentUserId)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("DeleteAlbum failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId)
        {
            _logger?.LogWarning("DeleteAlbum failed: user {UserId} is not owner of album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to delete album '{albumId}'.", "FORBIDDEN");
        }

        _albums.Delete(albumId);
        _albumLocks.TryRemove(albumId, out _);
        OnAlbumUpdated?.Invoke(albumId);
        return SpokesResult.Success();
    }

    public SpokesResult RenameAlbum(string albumId, string currentUserId, string newTitle)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        if (string.IsNullOrWhiteSpace(newTitle))
        {
            return SpokesResult.Failure("Album title cannot be empty.", "INVALID_ARGUMENT");
        }

        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("RenameAlbum failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId)
        {
            _logger?.LogWarning("RenameAlbum failed: user {UserId} is not owner of album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to rename album '{albumId}'.", "FORBIDDEN");
        }

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            album.Title = newTitle.Trim();
            _albums.Save(album);
            OnAlbumUpdated?.Invoke(albumId);
        }

        return SpokesResult.Success();
    }

    public SpokesResult TrySelfHealContributorKeys(string albumId, string currentUserId, string? currentPrivateKey)
    {
        if (IsCallerWriteBlocked(currentUserId))
            return SpokesResult.Failure("User account is suspended or inactive.", "FORBIDDEN");

        if (string.IsNullOrEmpty(currentPrivateKey))
            return SpokesResult.Failure("Private key required to self-heal keys.", "UNAUTHORIZED");

        var album = _albums.GetById(albumId);
        if (album == null || !album.IsEncrypted || album.OwnerId != currentUserId)
            return SpokesResult.Success();

        var missingContributors = album.ContributorUserIds
            .Where(cId => !album.EncryptedAlbumKeys.ContainsKey(cId))
            .ToList();

        if (!missingContributors.Any())
            return SpokesResult.Success();

        var plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);
        if (string.IsNullOrEmpty(plainAlbumKey))
            return SpokesResult.Failure("Cannot derive album key.", "ENCRYPTION_ERROR");

        bool modified = false;
        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            foreach (var contributorId in missingContributors)
            {
                if (album.EncryptedAlbumKeys.ContainsKey(contributorId)) continue;
                var contributor = _employees.GetById(contributorId);
                if (contributor != null && !string.IsNullOrEmpty(contributor.PublicKey))
                {
                    try
                    {
                        album.EncryptedAlbumKeys[contributorId] = _crypto.EncryptRsa(plainAlbumKey, contributor.PublicKey);
                        modified = true;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to self-heal album key for contributor {ContributorId} on album {AlbumId}", contributorId, albumId);
                    }
                }
            }

            if (SyncAlbumEncryptionKeys(album, plainAlbumKey))
            {
                modified = true;
            }

            if (modified)
            {
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }

        return SpokesResult.Success();
    }

    public virtual List<Album> GetWritableAlbumsForChannel(string? userId, string? channelId, string? currentPrivateKey = null)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(channelId) || IsCallerWriteBlocked(userId))
        {
            return [];
        }

        return _albums.GetAll()
            .Where(a =>
                (string.Equals(a.OwnerId, userId, StringComparison.OrdinalIgnoreCase) ||
                 (a.ContributorUserIds.Contains(userId) && a.SharedWithChannelIds.Contains(channelId))) &&
                !IsAlbumLocked(a, userId, currentPrivateKey))
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
    }

    public virtual async Task<SpokesResult<Album>> ImportChatAttachmentsToAlbumAsync(
        string currentUserId,
        string channelId,
        IReadOnlyList<ChatAttachment> imageAttachments,
        string? targetAlbumId,
        string? newAlbumTitle,
        string? currentPrivateKey = null,
        string? plainChannelKey = null,
        bool deleteSourceFiles = true)
    {
        if (IsCallerWriteBlocked(currentUserId))
        {
            return SpokesResult<Album>.Failure("User account is suspended or inactive.", "FORBIDDEN");
        }

        if (_fileService == null)
        {
            return SpokesResult<Album>.Failure("File service is not configured on AlbumService.", "INTERNAL_ERROR");
        }

        if (string.IsNullOrWhiteSpace(channelId))
        {
            return SpokesResult<Album>.Failure("ChannelId is required.", "INVALID_ARGUMENT");
        }

        if (imageAttachments == null || imageAttachments.Count == 0)
        {
            return SpokesResult<Album>.Failure("At least one image attachment is required.", "INVALID_ARGUMENT");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            return SpokesResult<Album>.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        var callerEmployee = _employees.GetById(currentUserId);
        if (!CanUserPostToSharedChannel(channel, currentUserId, callerEmployee))
        {
            return SpokesResult<Album>.Failure("User is not authorized to post to this channel.", "FORBIDDEN");
        }

        if (channel.IsEncrypted)
        {
            if (string.IsNullOrEmpty(plainChannelKey))
            {
                if (string.IsNullOrEmpty(currentPrivateKey))
                {
                    return SpokesResult<Album>.Failure("Vault is locked. Cannot decrypt channel attachments.", "VAULT_LOCKED");
                }

                if (!channel.EncryptedChannelKeys.TryGetValue(currentUserId, out var cipherChannelKey))
                {
                    return SpokesResult<Album>.Failure("Channel key not found for current user.", "KEY_NOT_FOUND");
                }

                try
                {
                    plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, currentPrivateKey);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "ImportChatAttachmentsToAlbumAsync failed: RSA decryption of channel key failed for user {UserId} in channel {ChannelId}", currentUserId, channelId);
                    return SpokesResult<Album>.Failure($"Failed to decrypt channel key: {ex.Message}", "CRYPTO_FAILED");
                }
            }
        }

        Album album;
        bool createdNewAlbum = false;

        if (!string.IsNullOrWhiteSpace(targetAlbumId))
        {
            var existing = _albums.GetById(targetAlbumId);
            if (existing == null)
            {
                return SpokesResult<Album>.Failure($"Album '{targetAlbumId}' not found.", "NOT_FOUND");
            }

            if (existing.OwnerId != currentUserId && !existing.ContributorUserIds.Contains(currentUserId))
            {
                return SpokesResult<Album>.Failure($"User '{currentUserId}' is not authorized to add media to album '{targetAlbumId}'.", "FORBIDDEN");
            }

            album = existing;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(newAlbumTitle))
            {
                return SpokesResult<Album>.Failure("Album title is required when creating a new album.", "INVALID_ARGUMENT");
            }

            var createResult = CreateAlbum(new Album { Title = newAlbumTitle.Trim() }, currentUserId, currentPrivateKey);
            if (!createResult.IsSuccess || createResult.Value == null)
            {
                return createResult;
            }

            album = createResult.Value;
            createdNewAlbum = true;
        }

        var shareResult = ShareAlbumWithChannel(album.Id, currentUserId, channelId, currentPrivateKey);
        if (!shareResult.IsSuccess)
        {
            if (createdNewAlbum)
            {
                _albums.Delete(album.Id);
                _albumLocks.TryRemove(album.Id, out _);
            }

            return SpokesResult<Album>.Failure(shareResult.ErrorMessage ?? "Failed to share album with channel.", shareResult.Code ?? "FORBIDDEN");
        }

        album = _albums.GetById(album.Id) ?? album;
        var plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);
        if (string.IsNullOrEmpty(plainAlbumKey))
        {
            if (createdNewAlbum)
            {
                _albums.Delete(album.Id);
                _albumLocks.TryRemove(album.Id, out _);
            }

            return SpokesResult<Album>.Failure("Cannot decrypt album key.", "ENCRYPTION_ERROR");
        }

        var expectedChannelPrefix = $"/spokesapi/files/chat/{channelId}/";
        var importedMedia = new List<AlbumMedia>();
        var migratedSourcePaths = new List<string>();

        foreach (var att in imageAttachments)
        {
            if (string.IsNullOrWhiteSpace(att.FilePath))
            {
                continue;
            }

            var normalizedPath = att.FilePath.StartsWith('/') ? att.FilePath : "/" + att.FilePath;
            var qIdx = normalizedPath.IndexOf('?');
            if (qIdx >= 0) normalizedPath = normalizedPath[..qIdx];

            if (normalizedPath.Contains("..", StringComparison.Ordinal) ||
                !normalizedPath.StartsWith(expectedChannelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Rejected invalid or cross-channel attachment path {FilePath} during album import for channel {ChannelId}", att.FilePath, channelId);
                continue;
            }

            var safeName = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                continue;
            }

            try
            {
                using var sourceStream = _fileService.OpenRead("chat", channelId, safeName, channel.IsEncrypted ? plainChannelKey : null);
                if (sourceStream == null)
                {
                    _logger?.LogWarning("Source chat attachment not found on disk: {FilePath}", normalizedPath);
                    continue;
                }

                var targetFilePath = await _fileService.UploadStreamAsync("albums", album.Id, sourceStream, att.FileName, plainAlbumKey);
                var targetSafeName = Path.GetFileName(targetFilePath);

                bool hasThumb = false;
                if (att.HasServerThumbnail)
                {
                    var sourceThumbName = FileService.GetThumbnailFileName(safeName);
                    using var thumbStream = _fileService.OpenRead("chat", channelId, sourceThumbName, channel.IsEncrypted ? plainChannelKey : null);
                    if (thumbStream != null)
                    {
                        await _fileService.UploadThumbnailAsync("albums", album.Id, thumbStream, targetSafeName, plainAlbumKey);
                        hasThumb = true;
                    }
                }

                importedMedia.Add(new AlbumMedia
                {
                    FilePath = targetFilePath,
                    FileName = att.FileName,
                    ContentType = att.ContentType,
                    FileSizeBytes = att.FileSizeBytes,
                    AddedByUserId = currentUserId,
                    AddedAt = DateTime.UtcNow,
                    HasServerThumbnail = hasThumb,
                    ImageWidth = att.ImageWidth,
                    ImageHeight = att.ImageHeight
                });
                migratedSourcePaths.Add(normalizedPath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to migrate chat attachment {FilePath} to album {AlbumId}", normalizedPath, album.Id);
            }
        }

        if (importedMedia.Count == 0)
        {
            if (createdNewAlbum)
            {
                _albums.Delete(album.Id);
                _albumLocks.TryRemove(album.Id, out _);
            }

            return SpokesResult<Album>.Failure("No valid image attachments could be imported into the album.", "FILE_NOT_FOUND");
        }

        var addResult = AddMedia(album.Id, currentUserId, importedMedia);
        if (!addResult.IsSuccess)
        {
            return SpokesResult<Album>.Failure(addResult.ErrorMessage ?? "Failed to add media to album.", addResult.Code ?? "SAVE_FAILED");
        }

        if (deleteSourceFiles)
        {
            foreach (var sourcePath in migratedSourcePaths)
            {
                try
                {
                    await _fileService.DeleteAsync(sourcePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete migrated chat attachment {FilePath}", sourcePath);
                }
            }
        }

        album = _albums.GetById(album.Id) ?? album;
        return SpokesResult<Album>.Success(album);
    }
}
