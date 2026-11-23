using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Payload transmitted to the Google Cast Web Receiver during session initiation.
/// Sensitive server licensing and trial proof remain server-side and are exchanged
/// for a short-lived <see cref="CastTicket"/> before reaching browser/mobile clients.
/// </summary>
public class SlideshowCastInitPayload
{
    public string AppId { get; set; } = "5AF5A068";
    public string ServerId { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? CastTicket { get; set; }
    public string? ServerFingerprint { get; set; }
    public string? DatabaseCreationVersion { get; set; }
    public bool IsDemo { get; set; }
    public string? AuthorizationError { get; set; }
    public string? AuthorizationErrorCode { get; set; }
}

/// <summary>
/// Service providing server identity, short-lived Cast session tickets, and dynamic URL resolution for Google Cast sessions.
/// </summary>
public interface ISlideshowCastService
{
    bool IsDemoMode();
    Task<SlideshowCastInitPayload> GetCastInitPayloadAsync(string currentBaseUri, CancellationToken cancellationToken = default);
    string ResolveServerUrl(string currentBaseUri);
}
