using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class AlbumService
{
    private readonly AlbumRepository _albums;
    private readonly ChatChannelRepository _channels;
    private readonly ICryptoService _crypto;
    private readonly ServerEscrowService _escrowService;
    private readonly EmployeeRepository _employees;

    public event Action<string>? OnAlbumUpdated;

    public AlbumService(AlbumRepository albums, ChatChannelRepository channels, ICryptoService crypto, ServerEscrowService escrowService, EmployeeRepository employees)
    {
        _albums = albums;
        _channels = channels;
        _crypto = crypto;
        _escrowService = escrowService;
        _employees = employees;
    }

    public async Task<Album> CreateAlbumAsync(Album album, string ownerId, string? ownerPrivateKey)
    {
        album.OwnerId = ownerId;
        album.IsEncrypted = true;
        
        var plainAlbumKey = _crypto.GenerateAesKeyBase64();
        
        if (!string.IsNullOrEmpty(ownerPrivateKey))
        {
            var owner = _employees.GetById(ownerId);
            if (owner != null && !string.IsNullOrEmpty(owner.PublicKey))
            {
                album.EncryptedAlbumKeys[ownerId] = _crypto.EncryptRsa(plainAlbumKey, owner.PublicKey);
            }
        }
        else
        {
            if (_escrowService != null && _escrowService.IsEscrowAvailable && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
            {
                album.EncryptedAlbumKeys["Escrow"] = _crypto.EncryptRsa(plainAlbumKey, _escrowService.EscrowPublicKey);
            }
        }
        
        _albums.Save(album);
        OnAlbumUpdated?.Invoke(album.Id);
        return album;
    }

    public string? GetPlainAlbumKey(Album album, string currentUserId, string? currentPrivateKey)
    {
        if (!string.IsNullOrEmpty(currentPrivateKey) && album.EncryptedAlbumKeys.TryGetValue(currentUserId, out var cipherUserKey))
        {
            try { return _crypto.DecryptRsa(cipherUserKey, currentPrivateKey); } catch {}
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
                        } catch {}
                    }
                }
            }
        }
        
        if (_escrowService != null && _escrowService.IsEscrowAvailable && album.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
        {
            try { return _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!); } catch {}
        }
        
        return null;
    }

    public void ShareAlbumWithChannel(string albumId, string currentUserId, string channelId, string? currentPrivateKey)
    {
        var album = _albums.GetById(albumId);
        if (album == null) return;
        if (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId)) return;

        var channel = _channels.GetById(channelId);
        if (channel == null) return;

        lock (album)
        {
            var updatedSharedChannels = new HashSet<string>(album.SharedWithChannelIds);
            if (updatedSharedChannels.Add(channelId))
            {
                var plainAlbumKey = GetPlainAlbumKey(album, currentUserId, currentPrivateKey);
                if (plainAlbumKey != null)
                {
                    if (channel.IsEncrypted)
                    {
                        if (!string.IsNullOrEmpty(currentPrivateKey) && channel.EncryptedChannelKeys.TryGetValue(currentUserId, out var cipherChannelKey))
                        {
                            try 
                            {
                                var plainChannelKey = _crypto.DecryptRsa(cipherChannelKey, currentPrivateKey);
                                album.EncryptedAlbumKeys[$"Channel_{channelId}"] = _crypto.EncryptAes(plainAlbumKey, plainChannelKey);
                            } catch {}
                        }
                    }
                    else
                    {
                        if (_escrowService != null && _escrowService.IsEscrowAvailable && !string.IsNullOrEmpty(_escrowService.EscrowPublicKey))
                        {
                            album.EncryptedAlbumKeys["Escrow"] = _crypto.EncryptRsa(plainAlbumKey, _escrowService.EscrowPublicKey);
                        }
                    }
                }

                album.SharedWithChannelIds = updatedSharedChannels.ToList();
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }
    }

    public void UnshareAlbumWithChannel(string albumId, string currentUserId, string channelId)
    {
        var album = _albums.GetById(albumId);
        if (album == null) return;
        if (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId)) return;

        lock (album)
        {
            var updatedSharedChannels = new HashSet<string>(album.SharedWithChannelIds);
            if (updatedSharedChannels.Remove(channelId))
            {
                album.EncryptedAlbumKeys.Remove($"Channel_{channelId}");

                var owner = _employees.GetById(album.OwnerId);
                bool hasUnencryptedChannels = false;
                foreach (var cId in updatedSharedChannels)
                {
                    var c = _channels.GetById(cId);
                    if (c != null && !c.IsEncrypted) hasUnencryptedChannels = true;
                }

                if (!hasUnencryptedChannels && owner != null && owner.HasChatPassword)
                {
                    album.EncryptedAlbumKeys.Remove("Escrow");
                }

                album.SharedWithChannelIds = updatedSharedChannels.ToList();
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }
    }

    public void ReEvaluateAlbumEncryptionForChannel(string channelId, string newChannelAesKey)
    {
        var albumsInChannel = _albums.GetAll().Where(a => a.SharedWithChannelIds.Contains(channelId)).ToList();
        
        foreach (var album in albumsInChannel)
        {
            lock (album)
            {
                if (_escrowService != null && _escrowService.IsEscrowAvailable && album.EncryptedAlbumKeys.TryGetValue("Escrow", out var escrowCipherKey))
                {
                    try
                    {
                        var plainAlbumKey = _crypto.DecryptRsa(escrowCipherKey, _escrowService.DecryptedEscrowPrivateKey!);
                        album.EncryptedAlbumKeys[$"Channel_{channelId}"] = _crypto.EncryptAes(plainAlbumKey, newChannelAesKey);
                        _albums.Save(album);
                    }
                    catch { }
                }
            }
        }
    }

    public List<AlbumMedia> UpdateContributors(string albumId, string currentUserId, IEnumerable<string> newContributorIds, string? currentPrivateKey)
    {
        var removedMedia = new List<AlbumMedia>();
        var album = _albums.GetById(albumId);
        if (album == null || album.OwnerId != currentUserId) return removedMedia;

        lock (album)
        {
            var existingContributors = new HashSet<string>(album.ContributorUserIds);
            var newContributors = new HashSet<string>(newContributorIds);
            var removedContributors = new List<string>();

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
                            album.EncryptedAlbumKeys[newId] = _crypto.EncryptRsa(plainAlbumKey, user.PublicKey);
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

            _albums.Save(album);
            OnAlbumUpdated?.Invoke(albumId);
        }

        return removedMedia;
    }

    public void AddMedia(string albumId, string currentUserId, IEnumerable<AlbumMedia> mediaItems)
    {
        var album = _albums.GetById(albumId);
        if (album == null || (album.OwnerId != currentUserId && !album.ContributorUserIds.Contains(currentUserId))) return;

        lock (album)
        {
            var updatedMedia = album.Media.ToList();
            updatedMedia.AddRange(mediaItems);
            album.Media = updatedMedia;
            _albums.Save(album);
            OnAlbumUpdated?.Invoke(albumId);
        }
    }

    public void RemoveMedia(string albumId, string currentUserId, AlbumMedia mediaItem)
    {
        var album = _albums.GetById(albumId);
        if (album == null) return;
        
        bool isOwner = album.OwnerId == currentUserId;
        bool isContributor = album.ContributorUserIds.Contains(currentUserId);

        lock (album)
        {
            var updatedMedia = album.Media.ToList();
            var itemToRemove = updatedMedia.FirstOrDefault(m => m.Id == mediaItem.Id);
            if (itemToRemove != null)
            {
                if (!isOwner && !(isContributor && itemToRemove.AddedByUserId == currentUserId)) return;

                updatedMedia.Remove(itemToRemove);
                album.Media = updatedMedia;
                _albums.Save(album);
                OnAlbumUpdated?.Invoke(albumId);
            }
        }
    }

    public List<AlbumMedia> RemoveMediaBulk(string albumId, string currentUserId, IEnumerable<string> mediaIds)
    {
        var removedItems = new List<AlbumMedia>();
        var album = _albums.GetById(albumId);
        if (album == null) return removedItems;
        
        bool isOwner = album.OwnerId == currentUserId;
        bool isContributor = album.ContributorUserIds.Contains(currentUserId);

        lock (album)
        {
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

        return removedItems;
    }

    public void DeleteAlbum(string albumId, string currentUserId)
    {
        var album = _albums.GetById(albumId);
        if (album == null || album.OwnerId != currentUserId) return;

        _albums.Delete(albumId);
        OnAlbumUpdated?.Invoke(albumId);
    }
}
