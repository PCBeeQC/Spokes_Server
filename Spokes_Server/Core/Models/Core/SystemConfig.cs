namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Data;

public enum IdpType
{
    None,
    BuiltInCasdoor,
    External
}

public class SystemConfig : IDataEntity
{
    public string Id { get; set; } = "system_config";

    
    public bool IsSetupComplete { get; set; } = false;
    public bool EulaAccepted { get; set; } = false;
    public bool IsCasdoorSanitized { get; set; } = false;

    public IdpType ProviderType { get; set; } = IdpType.None;

    // Server URLs
    public string ServerPublicUrl { get; set; } = string.Empty;
    public string PublicBrandingToken { get; set; } = Guid.NewGuid().ToString("N");

    // LiveKit Internal
    public string LiveKitApiKey { get; set; } = "spokes_voice_key";
    public string LiveKitApiSecret { get; set; } = Guid.NewGuid().ToString("N");
    public int LiveKitFallbackPort { get; set; } = 7881;
    public int LiveKitUdpStartPort { get; set; } = 30000;
    public int LiveKitUdpEndPort { get; set; } = 30499;

    // OIDC Info
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public bool EnableAutoUpdate { get; set; } = false;
}
