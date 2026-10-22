using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Spokes_Server.Core.Services.Client;

/// <summary>
/// Default implementation of ISpokesClientBridge communicating with window.SpokesBridge via JSInterop.
/// </summary>
public class SpokesClientBridge : ISpokesClientBridge
{
    private readonly IJSRuntime _js;
    private ClientPlatform? _cachedPlatform;

    public SpokesClientBridge(IJSRuntime js)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
    }

    public async ValueTask<ClientPlatform> GetPlatformAsync()
    {
        if (_cachedPlatform.HasValue)
        {
            return _cachedPlatform.Value;
        }

        try
        {
            var raw = await _js.InvokeAsync<string>("eval", "(window.SpokesBridge && window.SpokesBridge.platform) || (window.isCapacitorNative && window.isCapacitorNative() ? 'mobile' : 'web')");
            _cachedPlatform = raw?.ToLowerInvariant() switch
            {
                "desktop" => ClientPlatform.Desktop,
                "mobile" => ClientPlatform.Mobile,
                _ => ClientPlatform.Web
            };
        }
        catch
        {
            _cachedPlatform = ClientPlatform.Web;
        }

        return _cachedPlatform.Value;
    }

    public async ValueTask<bool> IsDesktopAsync() => await GetPlatformAsync() == ClientPlatform.Desktop;

    public async ValueTask<bool> IsMobileAsync() => await GetPlatformAsync() == ClientPlatform.Mobile;

    public async ValueTask<bool> IsNativeAppAsync() => await GetPlatformAsync() != ClientPlatform.Web;

    public async ValueTask<string> GetDeviceIdAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("SpokesBridge.getDeviceId");
        }
        catch
        {
            return string.Empty;
        }
    }

    public async ValueTask CopyTextAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("SpokesBridge.copyText", text);
        }
        catch
        {
            // Suppress errors if circuit is disconnecting
        }
    }

    public async ValueTask OpenExternalAsync(string url)
    {
        try
        {
            await _js.InvokeVoidAsync("SpokesBridge.openExternal", url);
        }
        catch
        {
            // Suppress errors if circuit is disconnecting
        }
    }

    public async ValueTask SetBadgeCountAsync(int count)
    {
        try
        {
            await _js.InvokeVoidAsync("SpokesBridge.setBadgeCount", count);
        }
        catch
        {
            // Suppress errors if circuit is disconnecting
        }
    }

    public async ValueTask TriggerHapticAsync(HapticStyle style = HapticStyle.Light)
    {
        try
        {
            await _js.InvokeVoidAsync("SpokesBridge.triggerHaptic", style.ToString().ToUpperInvariant());
        }
        catch
        {
            // Suppress errors if circuit is disconnecting
        }
    }

    public async ValueTask SetStatusBarStyleAsync(bool isDarkMode)
    {
        try
        {
            await _js.InvokeVoidAsync("SpokesBridge.setStatusBarStyle", isDarkMode);
        }
        catch
        {
            // Suppress errors if circuit is disconnecting
        }
    }
}
