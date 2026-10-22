namespace Spokes_Server.Core.Configuration;

public class ContactConfig
{
    public string AddressStreet { get; set; } = string.Empty;
    public string AddressCity { get; set; } = string.Empty;
    public string AddressZip { get; set; } = string.Empty;
    public string AddressState { get; set; } = string.Empty;
    public string AddressCountry { get; set; } = "Canada";
    public string PhoneNumber { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string PrivacyPolicyUrl { get; set; } = "https://spokes.sh/privacy/";
}
