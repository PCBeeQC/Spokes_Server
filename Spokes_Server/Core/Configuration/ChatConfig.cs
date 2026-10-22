namespace Spokes_Server.Core.Configuration;

public class ChatConfig
{
    public string DefaultPublicChannelSubscription { get; set; } = "Mentions";
    public string PublicChannelMasterPassword { get; set; } = string.Empty;
    public int MaxChatCacheSizeMb { get; set; } = 500;
    public string GifProvider { get; set; } = "Spokes Default Provider";
    public string GifApiKey { get; set; } = string.Empty;

    // Mobile Compression Settings (Chat)
    public int MobilePhotoMaxDimension { get; set; } = 1920; // 1080p default
    public int MobilePhotoQuality { get; set; } = 80; // 80% default
    public int MobileVideoMaxDimension { get; set; } = 1080; // 1080p default
    public int MobileVideoBitrate { get; set; } = 5_000_000; // 5 Mbps default

    // Desktop Compression Settings (Chat)
    public int DesktopPhotoMaxDimension { get; set; } = 3840; // 4K default
    public int DesktopPhotoQuality { get; set; } = 85; // 85% default
}
