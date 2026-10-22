using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Storage;

public record FileKeyResolutionResult(bool IsEncrypted, string? EncryptionKey, string? ErrorMessage)
{
    public bool IsSuccess => !IsEncrypted || EncryptionKey != null;
    public static FileKeyResolutionResult NotEncrypted() => new(false, null, null);
    public static FileKeyResolutionResult Success(string key) => new(true, key, null);
    public static FileKeyResolutionResult Failure(string error) => new(true, null, error);
}

public record FileUploadKeyRequest(
    string Category,
    string ContextId,
    Employee User,
    string? TokenEncryptionKey = null,
    bool IsThumbnail = false,
    HttpContext? HttpContext = null);

public interface IFileAuthorizationService
{
    FileKeyResolutionResult ResolveAlbumKey(string albumId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null);
    FileKeyResolutionResult ResolveChannelKey(string channelId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null);

    FileKeyResolutionResult ResolveUploadKey(FileUploadKeyRequest request);
    FileKeyResolutionResult ResolveUploadKey(string category, string contextId, Employee user, string? tokenEncryptionKey = null, bool isThumbnail = false, HttpContext? httpContext = null);
    FileKeyResolutionResult ResolveDownloadKey(string category, string contextId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null);

    bool CanAccess(string category, string contextId, Employee user);
    bool CanUpload(string category, string contextId, Employee user);
    bool IsKnownCategory(string category);
}

public class FileAuthorizationService : IFileAuthorizationService
{
    private readonly IEnumerable<IFileAccessProvider> _providers;
    private readonly IEnumerable<IFileKeyResolver> _keyResolvers;

    public FileAuthorizationService(
        IEnumerable<IFileAccessProvider>? providers = null,
        IEnumerable<IFileKeyResolver>? keyResolvers = null)
    {
        _providers = providers ?? Enumerable.Empty<IFileAccessProvider>();
        _keyResolvers = keyResolvers ?? Enumerable.Empty<IFileKeyResolver>();
    }

    public bool IsKnownCategory(string category)
    {
        return _providers.Any(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
    }

    public bool CanAccess(string category, string contextId, Employee user)
    {
        var provider = _providers.FirstOrDefault(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (provider != null)
        {
            return provider.CanAccess(user, contextId);
        }
        return false;
    }

    public bool CanUpload(string category, string contextId, Employee user)
    {
        var provider = _providers.FirstOrDefault(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (provider != null)
        {
            return provider.CanUpload(user, contextId);
        }
        return false;
    }

    public FileKeyResolutionResult ResolveAlbumKey(string albumId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null)
    {
        var resolver = _keyResolvers.FirstOrDefault(r => r.Category.Equals("albums", StringComparison.OrdinalIgnoreCase));
        return resolver != null
            ? resolver.ResolveDownloadKey(albumId, user, tokenEncryptionKey, httpContext)
            : FileKeyResolutionResult.NotEncrypted();
    }

    public FileKeyResolutionResult ResolveChannelKey(string channelId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null)
    {
        var resolver = _keyResolvers.FirstOrDefault(r => r.Category.Equals("chat", StringComparison.OrdinalIgnoreCase));
        return resolver != null
            ? resolver.ResolveDownloadKey(channelId, user, tokenEncryptionKey, httpContext)
            : FileKeyResolutionResult.NotEncrypted();
    }

    public FileKeyResolutionResult ResolveUploadKey(FileUploadKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolver = _keyResolvers.FirstOrDefault(r => r.Category.Equals(request.Category, StringComparison.OrdinalIgnoreCase));
        if (resolver != null)
        {
            return resolver.ResolveUploadKey(request.ContextId, request.User, request.TokenEncryptionKey, request.IsThumbnail, request.HttpContext);
        }

        return FileKeyResolutionResult.NotEncrypted();
    }

    public FileKeyResolutionResult ResolveUploadKey(string category, string contextId, Employee user, string? tokenEncryptionKey = null, bool isThumbnail = false, HttpContext? httpContext = null) =>
        ResolveUploadKey(new FileUploadKeyRequest(category, contextId, user, tokenEncryptionKey, isThumbnail, httpContext));

    public FileKeyResolutionResult ResolveDownloadKey(string category, string contextId, Employee user, string? tokenEncryptionKey = null, HttpContext? httpContext = null)
    {
        var resolver = _keyResolvers.FirstOrDefault(r => r.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (resolver != null)
        {
            return resolver.ResolveDownloadKey(contextId, user, tokenEncryptionKey, httpContext);
        }

        return FileKeyResolutionResult.NotEncrypted();
    }
}
