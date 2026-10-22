using System;
using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Provides unified device and user-agent heuristics for mobile, desktop, and Capacitor client detection.
/// </summary>
public static class DeviceDetectionHelper
{
    /// <summary>
    /// Extracts device description and Capacitor status from an incoming HTTP request.
    /// </summary>
    public static (string DeviceInfo, bool IsCapacitor) ParseDeviceInfo(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var clientQuery = context.Request.Query["client"].ToString();
        var hasCapacitorHeader = context.Request.Headers.ContainsKey("X-Capacitor");

        return ParseDeviceInfo(userAgent, clientQuery, hasCapacitorHeader);
    }

    /// <summary>
    /// Parses device description and mobile application status from raw client metadata.
    /// </summary>
    public static (string DeviceInfo, bool IsCapacitor) ParseDeviceInfo(
        string? userAgent,
        string? clientQuery = null,
        bool hasCapacitorHeader = false)
    {
        var ua = userAgent ?? string.Empty;
        var deviceInfo = "Unknown Device";

        if (ua.Contains("Windows")) deviceInfo = "Windows";
        else if (ua.Contains("iPhone") || ua.Contains("iPad")) deviceInfo = "iOS";
        else if (ua.Contains("Android")) deviceInfo = "Android";
        else if (ua.Contains("Mac OS")) deviceInfo = "macOS";

        bool isDesktop = IsDesktopClient(ua);
        bool isCapacitor = !isDesktop && IsMobileClient(ua, clientQuery, hasCapacitorHeader);

        if (isCapacitor)
        {
            deviceInfo += " (App)";
        }
        else if (isDesktop)
        {
            deviceInfo += " (Desktop App)";
        }

        return (deviceInfo, isCapacitor);
    }

    /// <summary>
    /// Determines whether the current request originates from an Electron / Spokes Desktop client.
    /// </summary>
    public static bool IsDesktopClient(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        return ua.Contains("SpokesDesktop", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Electron", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether the current request originates from a mobile or Capacitor client.
    /// </summary>
    public static bool IsMobileClient(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var clientQuery = context.Request.Query["client"].ToString();
        var hasCapacitorHeader = context.Request.Headers.ContainsKey("X-Capacitor");

        return IsMobileClient(userAgent, clientQuery, hasCapacitorHeader);
    }

    /// <summary>
    /// Evaluates whether the user agent, client query parameter, or custom headers indicate a mobile client.
    /// </summary>
    public static bool IsMobileClient(
        string? userAgent,
        string? clientQuery = null,
        bool hasCapacitorHeader = false)
    {
        var ua = userAgent ?? string.Empty;
        if (IsDesktopClient(ua)) return false;

        return ua.Contains("Capacitor", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("wv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientQuery, "mobile", StringComparison.OrdinalIgnoreCase)
            || hasCapacitorHeader;
    }

    /// <summary>
    /// Evaluates whether the user agent indicates a mobile device (phone, tablet, or Capacitor app).
    /// </summary>
    public static bool IsMobileDevice(string? userAgent)
    {
        var ua = userAgent ?? string.Empty;
        if (IsDesktopClient(ua)) return false;

        return ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Capacitor", StringComparison.OrdinalIgnoreCase);
    }
}
