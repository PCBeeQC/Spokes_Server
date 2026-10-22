namespace Spokes_Server.Core.Configuration;

using Spokes_Server.Core.Constants;

public class SpokesOptions
{
    public const string SectionName = "Spokes";

    /// <summary>
    /// Push relay server URL. Defaults to the official Spokes relay service.
    /// </summary>
    public string PushRelayUrl { get; set; } = SpokesConstants.PushRelayUrl;

    /// <summary>
    /// GIF relay server URL. Defaults to the official Spokes relay service.
    /// </summary>
    public string GifRelayUrl { get; set; } = SpokesConstants.GifRelayUrl;

    /// <summary>
    /// Telemetry relay server URL. Defaults to the official Spokes relay service.
    /// </summary>
    public string TelemetryRelayUrl { get; set; } = SpokesConstants.TelemetryRelayUrl;

    /// <summary>
    /// Push relay authentication key. Injected via environment variable (SPOKES_RELAY_KEY)
    /// or configuration section in official Docker builds.
    /// </summary>
    public string PushRelayKey { get; set; } = string.Empty;

    /// <summary>
    /// Legacy Klipy GIF search API key. Prefer using the Spokes GIF relay or user-configured provider keys.
    /// </summary>
    [Obsolete("Prefer using the official Spokes GIF relay or user-configured provider keys.")]
    public string DefaultKlipyApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Public key used for license and telemetry payload validation.
    /// </summary>
    public string LicensePublicKeyPem { get; set; } = SpokesConstants.LicensePublicKeyPem;
}
