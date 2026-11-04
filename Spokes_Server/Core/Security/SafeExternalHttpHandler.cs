using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Outbound HTTP message handler and URL validator that blocks Server-Side Request Forgery (SSRF)
/// against loopback, link-local, RFC1918 private, CGNAT, cloud metadata, and IPv6 ULA addresses,
/// including DNS-rebinding protection at socket connect time and redirect target validation.
/// </summary>
public sealed class SafeExternalHttpHandler : DelegatingHandler
{
    private readonly int _maxRedirects;
    private readonly bool _requireHttps;

    public SafeExternalHttpHandler(int maxRedirects = 3, bool requireHttps = false, bool allowRedirects = true)
        : base(CreateSocketsHttpHandler())
    {
        _maxRedirects = allowRedirects ? maxRedirects : 0;
        _requireHttps = requireHttps;
    }

    public SafeExternalHttpHandler(HttpMessageHandler innerHandler, int maxRedirects = 3, bool requireHttps = false, bool allowRedirects = true)
        : base(innerHandler)
    {
        _maxRedirects = allowRedirects ? maxRedirects : 0;
        _requireHttps = requireHttps;
    }

    public static SocketsHttpHandler CreateSocketsHttpHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                try
                {
                    var socket = await ConnectSafeExternalTcpSocketAsync(
                        context.DnsEndPoint.Host,
                        context.DnsEndPoint.Port,
                        cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (InvalidOperationException ex)
                {
                    throw new HttpRequestException(ex.Message, ex);
                }
            }
        };
    }

    /// <summary>
    /// Resolves <paramref name="host"/> once, verifies every resolved IP address is publicly routable
    /// (blocking loopback, RFC1918, link-local/metadata, CGNAT, multicast, and IPv6 ULA/mapped ranges),
    /// and opens a TCP <see cref="Socket"/> directly to those validated addresses to prevent DNS-rebinding TOCTOU attacks.
    /// </summary>
    public static async Task<Socket> ConnectSafeExternalTcpSocketAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"Blocked outbound connection to invalid TCP port '{port}'.");
        }

        var addresses = await ResolveSafeExternalAddressesAsync(host, cancellationToken);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(addresses, port, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!(_requireHttps ? IsSafeExternalHttpsUri(request.RequestUri) : IsSafeExternalHttpUri(request.RequestUri)))
        {
            throw new HttpRequestException("Blocked outbound HTTP request to unsafe URI.");
        }

        var currentUri = request.RequestUri!;
        for (int redirectCount = 0; ; redirectCount++)
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (!IsRedirectStatusCode(response.StatusCode) || response.Headers.Location == null)
            {
                return response;
            }

            if (redirectCount >= _maxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("Maximum safe redirect count exceeded.");
            }

            var location = response.Headers.Location;
            if (!location.IsAbsoluteUri)
            {
                if (!Uri.TryCreate(currentUri, location, out var resolvedLocation))
                {
                    response.Dispose();
                    throw new HttpRequestException("Invalid redirect Location URI.");
                }
                location = resolvedLocation;
            }

            if (!IsSafeExternalHttpUri(location))
            {
                response.Dispose();
                throw new HttpRequestException("Blocked redirect to unsafe or internal URI.");
            }

            var nextMethod = request.Method;
            if (response.StatusCode == HttpStatusCode.SeeOther ||
                ((response.StatusCode == HttpStatusCode.MovedPermanently || response.StatusCode == HttpStatusCode.Found) &&
                 request.Method == HttpMethod.Post))
            {
                nextMethod = HttpMethod.Get;
            }

            var redirectedRequest = new HttpRequestMessage(nextMethod, location);
            foreach (var header in request.Headers)
            {
                if (!string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
                {
                    redirectedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            response.Dispose();
            request = redirectedRequest;
            currentUri = location;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    public static bool IsSafeExternalUrl(string? rawUrl, bool requireHttps = false)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return false;
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)) return false;
        return requireHttps ? IsSafeExternalHttpsUri(uri) : IsSafeExternalHttpUri(uri);
    }

    public static bool IsSafeExternalHttpsUri(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        return IsSafeUriHost(uri);
    }

    public static bool IsSafeExternalHttpUri(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSafeUriHost(uri);
    }

    private static bool IsSafeUriHost(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.DnsSafeHost;
        if (IsBlockedHostname(host)) return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            return IsPublicIpAddress(ip);
        }

        return true;
    }

    public static async Task AssertSafeExternalHostAsync(string? host, CancellationToken cancellationToken = default)
    {
        await ResolveSafeExternalAddressesAsync(host, cancellationToken);
    }

    private static async Task<IPAddress[]> ResolveSafeExternalAddressesAsync(
        string? host,
        CancellationToken cancellationToken)
    {
        if (IsBlockedHostname(host))
        {
            throw new InvalidOperationException($"Blocked outbound connection to restricted host '{host}'.");
        }

        var trimmed = host!.Trim();
        IPAddress[] addresses;
        if (IPAddress.TryParse(trimmed, out var literalIp))
        {
            addresses = [literalIp];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(trimmed, cancellationToken);
        }

        if (addresses.Length == 0 || !addresses.All(IsPublicIpAddress))
        {
            throw new InvalidOperationException($"Blocked outbound connection to non-public address for host '{host}'.");
        }

        return addresses;
    }

    public static bool IsBlockedHostname(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return true;

        if (normalized is "localhost" or "ip6-localhost" or "ip6-loopback" or "metadata.google.internal" or "metadata" or "host.docker.internal")
        {
            return true;
        }

        if (normalized.EndsWith(".localhost", StringComparison.Ordinal) ||
            normalized.EndsWith(".local", StringComparison.Ordinal) ||
            normalized.EndsWith(".internal", StringComparison.Ordinal) ||
            normalized.EndsWith(".home.arpa", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool IsPublicIpAddress(IPAddress ip)
    {
        if (ip == null) return false;

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) ||
            ip.Equals(IPAddress.Broadcast) ||
            ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.IPv6Loopback) ||
            ip.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                0 => false, // 0.0.0.0/8
                10 => false, // 10.0.0.0/8
                100 when (bytes[1] & 0xC0) == 64 => false, // 100.64.0.0/10 (CGNAT)
                127 => false, // 127.0.0.0/8 (Loopback)
                169 when bytes[1] == 254 => false, // 169.254.0.0/16 (Link-local / Cloud Metadata)
                172 when bytes[1] >= 16 && bytes[1] <= 31 => false, // 172.16.0.0/12
                192 when bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2) => false, // 192.0.0.0/24, 192.0.2.0/24
                192 when bytes[1] == 168 => false, // 192.168.0.0/16
                198 when bytes[1] == 18 || bytes[1] == 19 => false, // 198.18.0.0/15
                198 when bytes[1] == 51 && bytes[2] == 100 => false, // 198.51.100.0/24
                203 when bytes[1] == 0 && bytes[2] == 113 => false, // 203.0.113.0/24
                >= 224 => false, // Multicast & Reserved (224.0.0.0/4, 240.0.0.0/4)
                _ => true
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6Teredo)
            {
                return false;
            }

            var bytes = ip.GetAddressBytes();
            // fc00::/7 Unique Local Address (ULA)
            if ((bytes[0] & 0xFE) == 0xFC) return false;
            // fe80::/10 Link-Local
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return false;
            // 2001:db8::/32 Documentation
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;

            return true;
        }

        return false;
    }
}
