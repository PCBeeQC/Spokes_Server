using System.Text.Json.Serialization;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

/// <summary>
/// Strongly typed payload envelope sent to the Spokes Native Push Relay server.
/// Encapsulates all server identity, routing metadata, license payloads, and either
/// encrypted payload tokens or plaintext fields.
/// </summary>
public class RelayPushPayload
{
    public string Token { get; set; } = string.Empty;

    public string? ServerId { get; set; }
    public string? DatabaseCreationVersion { get; set; }
    public string? LicensePayload { get; set; }

    /// <summary>Base64url SHA-256 of the escrow public key; identifies this server for the relay's one-time trial.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerFingerprint { get; set; }

    /// <summary>Trial ticket previously issued by the relay, echoed back verbatim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RelayTrialTicket? TrialTicket { get; set; }

    public string? Category { get; set; }
    public string? Sound { get; set; }
    public string? ThreadId { get; set; }
    public string? ServerName { get; set; }
    public string? ChannelName { get; set; }
    public bool IsGroupChat { get; set; }
    public string? ServerIconUrl { get; set; }
    public int? Badge { get; set; }
    public bool IsSilent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginUrl { get; set; }

    // Encrypted envelope fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsEncrypted { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nonce { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ciphertext { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; set; }

    // Plaintext envelope fields (populated when encryption is not enabled for the subscription)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }
}
