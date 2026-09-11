using System;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Models.Core
{
    public class ServerConfig : IDataEntity
    {
        public string Id { get; set; } = "Global";

    
        public string ServerMasterRsaPublicKey { get; set; } = string.Empty;
        public string ServerMasterEncryptedPrivateKey { get; set; } = string.Empty;
        // Optionally store if the escrow is enabled
        public bool IsEscrowEnabled => !string.IsNullOrEmpty(ServerMasterRsaPublicKey);
        public string DatabaseCreationVersion { get; set; } = string.Empty;
        public string DatabaseCreationSignature { get; set; } = string.Empty;
        public string DatabaseCreationId { get; set; } = string.Empty;
        public string DatabaseCreationIdSignature { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}
