using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class AlbumService
{
    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly ICryptoService _crypto;
    private readonly ServerEscrowService _escrowService;
    private readonly EmployeeRepository _employees;
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
        ILogger<AlbumService>? logger = null)
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
        if (album == null)
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
        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("ShareAlbumWithChannel failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId))
        {
            _logger?.LogWarning("ShareAlbumWithChannel failed: user {UserId} not authorized to share album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to share album '{albumId}'.", "FORBIDDEN");
        }

        var channel = _channels.GetById(channelId);
        if (channel == null)
        {
            _logger?.LogWarning("ShareAlbumWithChannel failed: channel {ChannelId} not found", channelId);
            return SpokesResult.Failure($"Channel '{channelId}' not found.", "NOT_FOUND");
        }

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
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
        var album = _albums.GetById(albumId);
        if (album == null)
        {
            _logger?.LogWarning("UnshareAlbumWithChannel failed: album {AlbumId} not found", albumId);
            return SpokesResult.Failure($"Album '{albumId}' not found.", "NOT_FOUND");
        }
        if (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId))
        {
            _logger?.LogWarning("UnshareAlbumWithChannel failed: user {UserId} not authorized to unshare album {AlbumId}", currentUserId, albumId);
            return SpokesResult.Failure($"User '{currentUserId}' is not authorized to unshare album '{albumId}'.", "FORBIDDEN");
        }

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var updatedSharedChannels = new HashSet<string>(album.SharedWithChannelIds);
            if (updatedSharedChannels.Remove(channelId))
            {
                album.EncryptedAlbumKeys.Remove($"Channel_{channelId}");
                album.SharedWithChannelIds = updatedSharedChannels.ToList();

                var owner = _employees.GetById(album.OwnerId);
                bool ownerHasVault = owner != null && (!string.IsNullOrEmpty(owner.PublicKey) || owner.HasVaultPassword);
                if (!HasUnencryptedSharedChannel(album) && ownerHasVault)
                {
                    SyncAlbumEncryptionKeys(album);
                    album.EncryptedAlbumKeys.Remove("Escrow");
                }

                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
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

        lock (GetAlbumLock(albumId))
        {
            album = _albums.GetById(albumId) ?? album;
            var existingContributors = new HashSet<string>(album.ContributorUserIds);
            var newContributors = new HashSet<string>(newContributorIds);
            List<string> removedContributors = [];

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

        return SpokesResult<List<AlbumMedia>>.Success(removedMedia);
    }

    public SpokesResult AddMedia(string albumId, string currentUserId, IEnumerable<AlbumMedia> mediaItems)
    {
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
}
