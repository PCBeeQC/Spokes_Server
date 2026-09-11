namespace Spokes_Server.Core.Security;

using System;
using Microsoft.AspNetCore.Http;

public static class RedirectHelper
{
    /// <summary>
    /// Validates a return URL to prevent Open Redirect and malicious scheme (javascript:) attacks.
    /// Ensures the URL is either a strict relative path or an absolute HTTP/HTTPS URL pointing to the expected host.
    /// </summary>
    public static string GetSafeRedirectUrl(string? returnUrl, string expectedHost, string fallbackUrl = "/")
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return fallbackUrl;
        }

        var trimmed = returnUrl.Trim();

        // Reject control characters (CRLF, null bytes) that could cause header splitting or Kestrel header exceptions
        if (trimmed.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            return fallbackUrl;
        }

        // Must be a safe relative path (prevent protocol-relative open redirects like //evil.com)
        if (trimmed.StartsWith("/") && !trimmed.StartsWith("//") && !trimmed.StartsWith("/\\"))
        {
            var pathAndQuery = trimmed.Split('#', 2)[0];
            if (Uri.TryCreate(new Uri("http://localhost"), pathAndQuery, out var canonicalUri))
            {
                if (IsRestrictedAuthPath(canonicalUri.AbsolutePath))
                {
                    return fallbackUrl;
                }
            }
            else if (IsRestrictedAuthPath(pathAndQuery))
            {
                return fallbackUrl;
            }

            if (Uri.IsWellFormedUriString(pathAndQuery, UriKind.Relative))
            {
                return trimmed;
            }
        }

        // If absolute, it must be HTTP/HTTPS and match the expected host
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            var isSafeScheme = string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase) || 
                               string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase);
                               
            var isSameHost = string.Equals(parsed.Host, expectedHost, StringComparison.OrdinalIgnoreCase);

            if (isSafeScheme && isSameHost)
            {
                if (IsRestrictedAuthPath(parsed.AbsolutePath))
                {
                    return fallbackUrl;
                }
                return trimmed;
            }
        }

        return fallbackUrl;
    }

    private static bool IsRestrictedAuthPath(string path)
    {
        return path.StartsWith("/mobile-login", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/sso/login", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/session-expired", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/authentication/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Convenience overload that extracts the expected host from the current HttpContext.
    /// </summary>
    public static string GetSafeRedirectUrl(string? returnUrl, HttpContext httpContext, string fallbackUrl = "/")
    {
        var safe = GetSafeRedirectUrl(returnUrl, httpContext.Request.Host.Host ?? "localhost", fallbackUrl);
        if (Uri.TryCreate(safe, UriKind.Absolute, out var parsed))
        {
            var isStandardPort = (parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && parsed.Port == 443) ||
                                 (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && parsed.Port == 80);
            var isExpectedPort = httpContext.Request.Host.Port.HasValue
                ? parsed.Port == httpContext.Request.Host.Port.Value
                : isStandardPort;

            if (!isExpectedPort)
            {
                return fallbackUrl;
            }
        }
        return safe;
    }
}

