namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Strongly-typed payload sent to the Spokes Relay for GIF searches.
/// </summary>
public class GifRelayPayload
{
    public string Query { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 20;
    public string ServerFingerprint { get; set; } = string.Empty;
    public string? ServerId { get; set; }
    public string? LicensePayload { get; set; }
}
