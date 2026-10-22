using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Client;

/// <summary>
/// Identifies the running client platform.
/// </summary>
public enum ClientPlatform
{
    Web,
    Desktop,
    Mobile
}

/// <summary>
/// Haptic vibration intensity for native mobile devices.
/// </summary>
public enum HapticStyle
{
    Light,
    Medium,
    Heavy
}

/// <summary>
/// Unified client bridge interface for Blazor components to communicate
/// with native client shells (Electron on Desktop, Capacitor on Mobile, or Web).
/// </summary>
public interface ISpokesClientBridge
{
    /// <summary>
    /// Gets the detected client platform.
    /// </summary>
    ValueTask<ClientPlatform> GetPlatformAsync();

    /// <summary>
    /// Returns true if running inside the official Spokes Desktop (Electron) shell.
    /// </summary>
    ValueTask<bool> IsDesktopAsync();

    /// <summary>
    /// Returns true if running inside the official Spokes Mobile (Capacitor) wrapper.
    /// </summary>
    ValueTask<bool> IsMobileAsync();

    /// <summary>
    /// Returns true if running inside either native app shell (Desktop or Mobile).
    /// </summary>
    ValueTask<bool> IsNativeAppAsync();

    /// <summary>
    /// Gets the stable hardware or platform device identifier.
    /// </summary>
    ValueTask<string> GetDeviceIdAsync();

    /// <summary>
    /// Safely copies text to the system clipboard across platforms.
    /// </summary>
    ValueTask CopyTextAsync(string text);

    /// <summary>
    /// Opens an external URL in the user's default OS browser.
    /// </summary>
    ValueTask OpenExternalAsync(string url);

    /// <summary>
    /// Updates the application badge count on the taskbar, dock, or app icon.
    /// </summary>
    ValueTask SetBadgeCountAsync(int count);

    /// <summary>
    /// Triggers tactile haptic feedback on mobile devices or silent fallback on desktop.
    /// </summary>
    ValueTask TriggerHapticAsync(HapticStyle style = HapticStyle.Light);

    /// <summary>
    /// Adjusts native status bar and titlebar themes between dark and light modes.
    /// </summary>
    ValueTask SetStatusBarStyleAsync(bool isDarkMode);
}
