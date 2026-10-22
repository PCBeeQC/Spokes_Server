namespace Spokes_Server.Core.Configuration;

public class PushConfig
{
    public string VapidPublicKey { get; set; } = string.Empty;
    public string VapidPrivateKey { get; set; } = string.Empty;
    public string VapidSubject { get; set; } = "mailto:admin@yourcompany.com";
}
