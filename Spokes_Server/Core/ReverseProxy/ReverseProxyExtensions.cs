using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Spokes_Server.Core.ReverseProxy;

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddSpokesReverseProxy(this IServiceCollection services)
        => services.AddSpokesReverseProxy((IConfiguration?)null);

    public static IServiceCollection AddSpokesReverseProxy(this IServiceCollection services, IConfiguration? configuration)
    {
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                var effectiveConfig = configuration ?? sp.GetService<IConfiguration>();
                ConfigureForwardedHeaders(options, effectiveConfig);
            });

        services.AddSingleton<SpokesProxyConfigProvider>();
        services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<SpokesProxyConfigProvider>());

        services.AddReverseProxy()
            .AddTransforms(builderContext =>
            {
                if (builderContext.Route.ClusterId == "casdoorCluster")
                {
                    builderContext.AddRequestTransform(context =>
                    {
                        var cookies = context.HttpContext.Request.Cookies;
                        var filteredCookies = cookies
                            .Where(c => !c.Key.StartsWith("Spokes_", StringComparison.OrdinalIgnoreCase) && 
                                        !c.Key.StartsWith(".AspNetCore.", StringComparison.OrdinalIgnoreCase))
                            .Select(c => $"{c.Key}={c.Value}");

                        context.ProxyRequest.Headers.Remove("Cookie");

                        if (filteredCookies.Any())
                        {
                            context.ProxyRequest.Headers.Add("Cookie", string.Join("; ", filteredCookies));
                        }

                        return ValueTask.CompletedTask;
                    });

                    builderContext.AddResponseTransform(context =>
                    {
                        context.HttpContext.Response.Headers["Content-Security-Policy"] = "connect-src 'self' https: wss:;";
                        return ValueTask.CompletedTask;
                    });
                }
            });

        return services;
    }

    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, IConfiguration? configuration)
    {
        bool trustForwardedHost = configuration?.GetValue<bool>("ReverseProxy:TrustForwardedHost") == true;
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        if (trustForwardedHost)
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost;
        }

        int? configuredLimit = configuration?.GetValue<int?>("ReverseProxy:ForwardLimit");
        options.ForwardLimit = configuredLimit is > 0 ? configuredLimit.Value : 1;

        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        // By default, trust ONLY loopback peers for X-Forwarded-For / X-Forwarded-Proto.
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("127.0.0.0"), 8));

        foreach (var entry in EnumerateConfiguredProxyEntries(configuration))
        {
            TryAddTrustedProxyOrNetwork(options, entry);
        }
    }

    private static IEnumerable<string> EnumerateConfiguredProxyEntries(IConfiguration? configuration)
    {
        if (configuration != null)
        {
            foreach (var key in new[] { "ReverseProxy:TrustedProxies", "ReverseProxy:TrustedNetworks", "SPOKES_TRUSTED_PROXIES" })
            {
                var directValue = configuration[key];
                if (!string.IsNullOrWhiteSpace(directValue))
                {
                    foreach (var part in SplitEntries(directValue))
                    {
                        yield return part;
                    }
                }

                foreach (var child in configuration.GetSection(key).GetChildren())
                {
                    if (!string.IsNullOrWhiteSpace(child.Value))
                    {
                        foreach (var part in SplitEntries(child.Value))
                        {
                            yield return part;
                        }
                    }
                }
            }
        }

        var envProxies = Environment.GetEnvironmentVariable("SPOKES_TRUSTED_PROXIES");
        if (!string.IsNullOrWhiteSpace(envProxies))
        {
            foreach (var part in SplitEntries(envProxies))
            {
                yield return part;
            }
        }
    }

    private static IEnumerable<string> SplitEntries(string raw) =>
        raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void TryAddTrustedProxyOrNetwork(ForwardedHeadersOptions options, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;

        var slashIdx = entry.IndexOf('/');
        if (slashIdx > 0)
        {
            var ipPart = entry[..slashIdx].Trim();
            var prefixPart = entry[(slashIdx + 1)..].Trim();
            if (IPAddress.TryParse(ipPart, out var netIp) && int.TryParse(prefixPart, out var prefixLength))
            {
                int maxBits = netIp.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                if (prefixLength >= 0 && prefixLength <= maxBits)
                {
                    var maskedIp = MaskNetworkAddress(netIp, prefixLength);
                    options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(maskedIp, prefixLength));
                }
            }
            return;
        }

        if (IPAddress.TryParse(entry.Trim(), out var proxyIp))
        {
            if (!options.KnownProxies.Contains(proxyIp))
            {
                options.KnownProxies.Add(proxyIp);
            }
        }
    }

    private static IPAddress MaskNetworkAddress(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        if (fullBytes < bytes.Length)
        {
            if (remainingBits > 0)
            {
                byte mask = (byte)(0xFF << (8 - remainingBits));
                bytes[fullBytes] &= mask;
                fullBytes++;
            }

            for (int i = fullBytes; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }
        }

        return new IPAddress(bytes);
    }
}
