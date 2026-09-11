namespace Spokes_Server.Controllers;

public class MobileRefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
}
