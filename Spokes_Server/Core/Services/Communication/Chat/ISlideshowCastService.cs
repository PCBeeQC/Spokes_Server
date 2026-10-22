using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Payload transmitted to the Google Cast Web Receiver during session initiation.
/// </summary>
public class SlideshowCastInitPayload
{
    public string AppId { get; set; } = "5AF5A068";
    public string ServerId { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? LicensePayload { get; set; }
    public string? ServerFingerprint { get; set; }
    public RelayTrialTicket? TrialTicket { get; set; }
    public string? DatabaseCreationVersion { get; set; }
    public bool IsDemo { get; set; }
}

/// <summary>
/// Service providing server identity, licensing payload, and dynamic URL resolution for Google Cast sessions.
/// </summary>
public interface ISlideshowCastService
{
    SlideshowCastInitPayload GetCastInitPayload(string currentBaseUri);
    string ResolveServerUrl(string currentBaseUri);
}
