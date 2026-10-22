using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Configuration;

public class ServerConfig : IDataEntity
{
    public string Id { get; set; } = "Global";

    public string ServerMasterRsaPublicKey { get; set; } = string.Empty;
    public string ServerMasterEncryptedPrivateKey { get; set; } = string.Empty;
    public string ServerMasterKeySalt { get; set; } = string.Empty;
    // Optionally store if the escrow is enabled
    public bool IsEscrowEnabled => !string.IsNullOrEmpty(ServerMasterRsaPublicKey);
    public string DatabaseCreationVersion { get; set; } = string.Empty;
    public string DatabaseCreationId { get; set; } = string.Empty;
    /// <summary>Native push trial ticket issued by the Spokes Push Relay on first contact. Null until the first relay push.</summary>
    public Spokes_Server.Core.Services.Licensing.RelayTrialTicket? RelayTrialTicket { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
