namespace Spokes_Server.Core.Services.Storage;

using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Pluggable strategy interface for resolving cryptographic encryption keys for protected file storage categories.
/// Decouples generic file storage authorization from concrete domain subsystems (e.g. Chat, Albums).
/// </summary>
public interface IFileKeyResolver
{
    string Category { get; }

    FileKeyResolutionResult ResolveUploadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        bool isThumbnail = false,
        HttpContext? httpContext = null);

    FileKeyResolutionResult ResolveDownloadKey(
        string contextId,
        Employee user,
        string? tokenEncryptionKey = null,
        HttpContext? httpContext = null);
}
