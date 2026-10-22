namespace Spokes_Server.Core.Configuration;

/// <summary>
/// Configuration for album media storage and compression fidelity.
/// </summary>
public class AlbumConfig
{
    // Mobile Compression Settings (Albums)
    // 0 = Original (No Downscaling / Raw), 3840 = 4K UHD, 2560 = 2K, 1920 = 1080p
    public int MobilePhotoMaxDimension { get; set; } = 3840; // 4K default for albums
    public int MobilePhotoQuality { get; set; } = 90; // 90% default for albums
    public int MobileVideoMaxDimension { get; set; } = 0; // 0 = Original pass-through, or 1080 / 2160
    public int MobileVideoBitrate { get; set; } = 0; // 0 = Original pass-through, or e.g. 15_000_000

    // Desktop Compression Settings (Albums)
    public int DesktopPhotoMaxDimension { get; set; } = 3840; // 4K default (0 = Original / Raw)
    public int DesktopPhotoQuality { get; set; } = 90; // 90% default
}
