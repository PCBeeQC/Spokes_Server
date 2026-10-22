namespace Spokes_Server.Core.Configuration;

public class LicensingConfig
{
    public string LicensePayload { get; set; } = string.Empty;
    public bool SendUsageStatistics { get; set; } = true;
}
