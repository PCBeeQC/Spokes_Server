using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Spokes_Server.Core.ReverseProxy;

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddSpokesReverseProxy(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
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

    public static IServiceCollection AddSpokesReverseProxy(this IServiceCollection services, SystemConfig systemConfig)
        => services.AddSpokesReverseProxy();
}
