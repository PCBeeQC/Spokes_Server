namespace Spokes_Server.Core.Services.Communication.Chat;

using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

public class AlbumFileKeyResolver : IFileKeyResolver
{
    private readonly AlbumRepository _albums;
    private readonly AlbumService _albumService;
    private readonly ScopedKeystoreService _keystore;
    private readonly ICryptoService _crypto;

    public string Category => "albums";

    public AlbumFileKeyResolver(
        AlbumRepository albums,
        AlbumService albumService,
        ScopedKeystoreService keystore,
        ICryptoService crypto)
    {
        _albums = albums;
        _albumService = albumService;
        _keystore = keystore;
        _crypto = crypto;
    }

    public FileKeyResolutionResult ResolveUploadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        bool isThumbnail = false,
        HttpContext? httpContext = null)
    {
        var res = ResolveAlbumKey(contextId, user, tokenEncryptionKey, httpContext);
        if (!res.IsSuccess)
        {
            if (isThumbnail)
            {
                return FileKeyResolutionResult.Failure("No encryption key.");
            }
            if (!string.IsNullOrEmpty(res.ErrorMessage) && res.ErrorMessage != "You do not have the encryption key to access this album.")
            {
                return FileKeyResolutionResult.Failure(res.ErrorMessage);
            }
            return FileKeyResolutionResult.Failure("You do not have the encryption key to upload to this album.");
        }
        return res;
    }

    public FileKeyResolutionResult ResolveDownloadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        HttpContext? httpContext = null)
    {
        var res = ResolveAlbumKey(contextId, user, tokenEncryptionKey, httpContext);
        if (!res.IsSuccess)
        {
            if (!string.IsNullOrEmpty(res.ErrorMessage) && res.ErrorMessage != "You do not have the encryption key to access this album.")
            {
                return FileKeyResolutionResult.Failure(res.ErrorMessage);
            }
            return FileKeyResolutionResult.Failure("You do not have the encryption key to view this album.");
        }
        return res;
    }

    public FileKeyResolutionResult ResolveAlbumKey(string albumId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null)
    {
        if (!string.IsNullOrEmpty(tokenEncryptionKey))
            return FileKeyResolutionResult.Success(tokenEncryptionKey);

        var album = _albums.GetById(albumId);
        if (album == null || !album.IsEncrypted)
            return FileKeyResolutionResult.NotEncrypted();

        if (!_keystore.IsUnlocked && httpContext != null && !string.IsNullOrEmpty(user.EncryptedPrivateKey))
        {
            _keystore.TryInitializeFromVaultCookie(httpContext, user.Id, user.EncryptedPrivateKey);
        }

        if (_albumService.IsAlbumLocked(album, user.Id, _keystore.GetPrivateKey(), out var plainKey) || string.IsNullOrEmpty(plainKey))
        {
            if (album.EncryptedAlbumKeys.ContainsKey(user.Id) && !_keystore.IsUnlocked)
            {
                return FileKeyResolutionResult.Failure("Your Secure Vault is locked. Unlock your vault to access this album.");
            }
            return FileKeyResolutionResult.Failure("You do not have the encryption key to access this album.");
        }
        return FileKeyResolutionResult.Success(plainKey);
    }
}
