using System.Collections.Generic;

namespace Spokes_Server.Core.Models.Core
{
    public class UsageStatistics
    {
        public string ServerId { get; set; } = string.Empty;
        public string LicenseId { get; set; } = string.Empty;
        public string OS { get; set; } = string.Empty;
        public int ActiveUsers { get; set; }
        public string ServerEdition { get; set; } = string.Empty;
        public string ServerVersion { get; set; } = string.Empty;
        public Dictionary<string, string> AdditionalData { get; set; } = new();
    }
}
