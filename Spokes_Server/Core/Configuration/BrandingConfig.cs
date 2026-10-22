namespace Spokes_Server.Core.Configuration;

using System.Text.Json.Serialization;

public class BrandingConfig
{
    public string CompanyName { get; set; } = "My Spokes";
    public string LogoBase64 { get; set; } = string.Empty;
    public string IconBase64 { get; set; } = string.Empty; // Small square logo / favicon
    public int LogoVersion { get; set; } = 1;
    public int IconVersion { get; set; } = 1;
    public string PrimaryColor { get; set; } = "#7e6fff";
    public string SecondaryColor { get; set; } = "#1E88E5";

    [JsonIgnore]
    public bool HasCustomLogo => !string.IsNullOrEmpty(LogoBase64);

    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrEmpty(IconBase64);
}
