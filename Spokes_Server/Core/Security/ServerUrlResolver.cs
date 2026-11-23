using System;
using Microsoft.AspNetCore.Http;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Centralizes safe server base URL resolution and same-origin request validation
/// to prevent Host header injection and cross-site request forgery (CSRF).
/// </summary>
public static class ServerUrlResolver
{
    /// <summary>
    /// Resolves a validated scheme://host[:port] base URL for the current server.
    /// Prefers a valid configured public URL when provided; otherwise validates Request.Host.
    /// </summary>
    public static string GetSafeServerBaseUrl(HttpContext? context, string? configuredPublicUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPublicUrl) &&
            Uri.TryCreate(configuredPublicUrl.Trim(), UriKind.Absolute, out var publicUri) &&
            (publicUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             publicUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            Uri.CheckHostName(publicUri.Host) != UriHostNameType.Unknown)
        {
            return publicUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        if (context?.Request == null)
        {
            return "http://localhost";
        }

        var scheme = string.Equals(context.Request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : Uri.UriSchemeHttp;

        var host = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(host) ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            ContainsUnsafeHostChars(host))
        {
            return $"{scheme}://localhost";
        }

        var formattedHost = Uri.CheckHostName(host) == UriHostNameType.IPv6 && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

        var port = context.Request.Host.Port;
        if (port.HasValue && port.Value > 0 && port.Value <= 65535)
        {
            bool isDefaultPort = (scheme == Uri.UriSchemeHttp && port.Value == 80) ||
                                 (scheme == Uri.UriSchemeHttps && port.Value == 443);
            if (!isDefaultPort)
            {
                return $"{scheme}://{formattedHost}:{port.Value}";
            }
        }

        return $"{scheme}://{formattedHost}";
    }

    /// <summary>
    /// Validates that a state-changing request (such as POST /logout or presence beacon) originated from the same origin.
    /// Compares full authority (scheme + host + port) against configuredPublicUrl (when valid) or Request authority,
    /// rejects Sec-Fetch-Site values other than "same-origin" or "none", and requires at least one positive same-origin proof.
    /// </summary>
    public static bool IsSameOriginRequest(HttpContext? context, string? configuredPublicUrl = null)
    {
        if (context?.Request == null)
        {
            return false;
        }

        Uri? publicUri = TryParseHttpAuthorityUri(configuredPublicUrl);
        var requestHost = context.Request.Host.Host;
        if (publicUri == null && !HasValidRequestHost(requestHost))
        {
            return false;
        }

        if (!TryValidateSameOriginHeaders(
                context,
                uri => MatchesExpectedAuthority(uri, context, publicUri),
                out bool hasSameOriginFetchSite,
                out bool hasOriginOrRefererProof))
        {
            return false;
        }

        if (hasOriginOrRefererProof)
        {
            return true;
        }

        if (hasSameOriginFetchSite)
        {
            if (publicUri != null)
            {
                if (string.IsNullOrWhiteSpace(requestHost) ||
                    !string.Equals(requestHost, publicUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(context.Request.Scheme))
                {
                    if (!string.Equals(context.Request.Scheme, publicUri.Scheme, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    int reqPort = context.Request.Host.Port ?? GetDefaultPort(context.Request.Scheme);
                    if (reqPort != GetEffectivePort(publicUri))
                    {
                        return false;
                    }
                }
                else if (context.Request.Host.Port.HasValue && context.Request.Host.Port.Value != GetEffectivePort(publicUri))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates origin metadata for presence/push beacons.
    /// Cookie-only requests (without a verified capability token) enforce strict <see cref="IsSameOriginRequest"/>.
    /// Requests carrying a verified cryptographic capability token still reject cross-site Fetch Metadata and foreign
    /// origins, while tolerating TLS-terminating reverse proxies (https -&gt; http without X-Forwarded-Proto) and
    /// local/LAN access when ServerPublicUrl points to an external hostname.
    /// </summary>
    public static bool IsTrustedPresenceBeaconRequest(
        HttpContext? context,
        string? configuredPublicUrl = null,
        bool hasVerifiedCapabilityToken = false)
    {
        if (!hasVerifiedCapabilityToken)
        {
            return IsSameOriginRequest(context, configuredPublicUrl);
        }

        if (context?.Request == null)
        {
            return false;
        }

        if (IsSameOriginRequest(context, configuredPublicUrl))
        {
            return true;
        }

        Uri? publicUri = TryParseHttpAuthorityUri(configuredPublicUrl);
        bool hasValidRequestHost = HasValidRequestHost(context.Request.Host.Host);
        if (publicUri == null && !hasValidRequestHost)
        {
            return false;
        }

        if (!TryValidateSameOriginHeaders(
                context,
                uri => MatchesBootstrapAuthorityAllowingTlsProxy(uri, context, publicUri) ||
                       MatchesRequestAuthorityAllowingTlsProxy(uri, context),
                out bool hasSameOriginFetchSite,
                out bool hasOriginOrRefererProof))
        {
            return false;
        }

        return hasOriginOrRefererProof || (hasSameOriginFetchSite && hasValidRequestHost);
    }

    private static bool HasValidRequestHost(string? requestHost) =>
        !string.IsNullOrWhiteSpace(requestHost) &&
        Uri.CheckHostName(requestHost) != UriHostNameType.Unknown &&
        !ContainsUnsafeHostChars(requestHost);

    private static bool TryValidateSameOriginHeaders(
        HttpContext context,
        Func<Uri, bool> isAllowedAuthority,
        out bool hasSameOriginFetchSite,
        out bool hasOriginOrRefererProof)
    {
        hasSameOriginFetchSite = false;
        hasOriginOrRefererProof = false;

        var headers = context.Request.Headers;
        if (headers.TryGetValue("Sec-Fetch-Site", out var secFetchSite))
        {
            var siteValue = secFetchSite.ToString().Trim();
            if (!string.IsNullOrEmpty(siteValue))
            {
                if (siteValue.Equals("same-origin", StringComparison.OrdinalIgnoreCase))
                {
                    hasSameOriginFetchSite = true;
                }
                else if (!siteValue.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (headers.TryGetValue("Origin", out var originValues))
        {
            var origin = originValues.ToString().Trim();
            if (!string.IsNullOrEmpty(origin))
            {
                if (origin.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                    !isAllowedAuthority(originUri))
                {
                    return false;
                }

                hasOriginOrRefererProof = true;
            }
        }

        if (headers.TryGetValue("Referer", out var refererValues))
        {
            var referer = refererValues.ToString().Trim();
            if (!string.IsNullOrEmpty(referer))
            {
                if (!Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) ||
                    !isAllowedAuthority(refererUri))
                {
                    return false;
                }

                hasOriginOrRefererProof = true;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a mobile token-bootstrap POST (/sso/login/mobile-bootstrap) against Login CSRF while
    /// tolerating self-hosted TLS-terminating reverse proxies that forward https -> http without X-Forwarded-Proto.
    /// Rejects cross-site requests and foreign hosts/ports; permits Sec-Fetch-Site: same-site only when
    /// accompanied by a positive Origin/Referer matching the exact server host (e.g., https -> http scheme upgrade).
    /// </summary>
    public static bool IsTrustedMobileBootstrapRequest(
        HttpContext? context,
        string? configuredPublicUrl,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (context?.Request == null)
        {
            errorCode = "INVALID_ORIGIN";
            errorMessage = "Invalid origin header.";
            return false;
        }

        var headers = context.Request.Headers;
        Uri? publicUri = TryParseHttpAuthorityUri(configuredPublicUrl);

        bool hasOrigin = headers.TryGetValue("Origin", out var originValues) && !string.IsNullOrWhiteSpace(originValues);
        bool hasReferer = headers.TryGetValue("Referer", out var refererValues) && !string.IsNullOrWhiteSpace(refererValues);

        bool originMatchesServer = false;
        if (hasOrigin)
        {
            var rawOrigin = originValues.ToString().Trim();
            originMatchesServer = !rawOrigin.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                                  Uri.TryCreate(rawOrigin, UriKind.Absolute, out var originUri) &&
                                  MatchesBootstrapAuthorityAllowingTlsProxy(originUri, context, publicUri);
        }

        bool refererMatchesServer = false;
        if (!hasOrigin && hasReferer)
        {
            var rawReferer = refererValues.ToString().Trim();
            refererMatchesServer = Uri.TryCreate(rawReferer, UriKind.Absolute, out var refererUri) &&
                                   MatchesBootstrapAuthorityAllowingTlsProxy(refererUri, context, publicUri);
        }

        if (headers.TryGetValue("Sec-Fetch-Site", out var fetchSiteValues))
        {
            var fetchSite = fetchSiteValues.ToString().Trim();
            if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "INVALID_CROSS_SITE";
                errorMessage = "Invalid cross-site request.";
                return false;
            }

            if (fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase) &&
                !(originMatchesServer || refererMatchesServer))
            {
                errorCode = "INVALID_CROSS_SITE";
                errorMessage = "Invalid cross-site request.";
                return false;
            }
        }

        if (hasOrigin && !originMatchesServer)
        {
            errorCode = "INVALID_ORIGIN";
            errorMessage = "Invalid origin header.";
            return false;
        }

        if (!hasOrigin && hasReferer && !refererMatchesServer)
        {
            errorCode = "INVALID_REFERER";
            errorMessage = "Invalid referer header.";
            return false;
        }

        return true;
    }

    private static bool MatchesBootstrapAuthorityAllowingTlsProxy(Uri candidate, HttpContext context, Uri? publicUri)
    {
        if (MatchesExpectedAuthority(candidate, context, publicUri))
        {
            return true;
        }

        if (!candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Tolerate https://same-host[:port] -> http://same-host[:port] behind a TLS-terminating reverse proxy omitting X-Forwarded-Proto
        if (publicUri != null &&
            string.Equals(candidate.Host, publicUri.Host, StringComparison.OrdinalIgnoreCase) &&
            (GetEffectivePort(candidate) == GetEffectivePort(publicUri) || (candidate.IsDefaultPort && publicUri.IsDefaultPort)))
        {
            return true;
        }

        return MatchesRequestAuthorityAllowingTlsProxy(candidate, context);
    }

    private static bool MatchesRequestAuthorityAllowingTlsProxy(Uri candidate, HttpContext context)
    {
        var reqHost = context.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(reqHost) ||
            Uri.CheckHostName(reqHost) == UriHostNameType.Unknown ||
            ContainsUnsafeHostChars(reqHost))
        {
            return false;
        }

        if (MatchesExpectedAuthority(candidate, context, publicUri: null))
        {
            return true;
        }

        if (!candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, reqHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(context.Request.Scheme) &&
            !context.Request.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !context.Request.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (context.Request.Host.Port.HasValue)
        {
            int reqPort = context.Request.Host.Port.Value;
            return GetEffectivePort(candidate) == reqPort || (candidate.IsDefaultPort && reqPort == 80);
        }

        return candidate.IsDefaultPort;
    }

    private static Uri? TryParseHttpAuthorityUri(string? rawUrl)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl) &&
            Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            Uri.CheckHostName(uri.Host) != UriHostNameType.Unknown &&
            !ContainsUnsafeHostChars(uri.Host))
        {
            return uri;
        }

        return null;
    }

    private static bool MatchesExpectedAuthority(Uri candidate, HttpContext context, Uri? publicUri)
    {
        if (!candidate.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (publicUri != null)
        {
            return string.Equals(candidate.Scheme, publicUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(candidate.Host, publicUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   GetEffectivePort(candidate) == GetEffectivePort(publicUri);
        }

        if (!string.Equals(candidate.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(context.Request.Scheme))
        {
            if (!string.Equals(candidate.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int expectedPort = context.Request.Host.Port ?? GetDefaultPort(context.Request.Scheme);
            return GetEffectivePort(candidate) == expectedPort;
        }

        return context.Request.Host.Port.HasValue
            ? GetEffectivePort(candidate) == context.Request.Host.Port.Value
            : candidate.IsDefaultPort;
    }

    private static int GetEffectivePort(Uri uri) =>
        uri.IsDefaultPort ? GetDefaultPort(uri.Scheme) : uri.Port;

    private static int GetDefaultPort(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static bool ContainsUnsafeHostChars(string host)
    {
        foreach (var ch in host)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '\'' or '"' or '<' or '>' or '\\' or '/' or ';' or '@')
            {
                return true;
            }
        }
        return false;
    }
}
