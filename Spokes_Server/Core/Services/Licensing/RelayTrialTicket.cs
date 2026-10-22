using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Services.Licensing;

/// <summary>
/// Trial ticket issued and signed by the Spokes Push Relay the first time this server sends a native push.
/// The server cannot verify the signature (only the relay holds the key); it stores the ticket verbatim,
/// sends it back with every push, and reads <see cref="ExpiresAt"/> only for display and to stop calling the relay once expired.
/// </summary>
public class RelayTrialTicket
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("issuedAt")]
    public string IssuedAt { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public string ExpiresAt { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}
