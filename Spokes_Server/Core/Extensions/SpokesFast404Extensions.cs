using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Spokes_Server.Core.Extensions;

/// <summary>
/// Defensive Fast-404 middleware that intercepts unmapped API, attachment, and static asset requests
/// before they can fall through to the catch-all Blazor server-side rendering pipeline (MapRazorComponents).
/// Prevents thread-pool starvation and high CPU load caused by broken media/asset requests.
/// </summary>
public static class SpokesFast404Extensions
{
    private static readonly HashSet<string> Fast404Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".css", ".js", ".map", ".json", ".xml", ".zip", ".pdf"
    };

    public static IApplicationBuilder UseSpokesFast404Guard(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path))
            {
                bool isApiOrInternal = path.StartsWith("/spokesapi/", StringComparison.OrdinalIgnoreCase)
                                    || path.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase)
                                    || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

                var ext = Path.GetExtension(path);
                bool isAssetExtension = !string.IsNullOrEmpty(ext) && Fast404Extensions.Contains(ext);

                if (isApiOrInternal || isAssetExtension)
                {
                    var endpoint = context.GetEndpoint();
                    if (endpoint == null || IsBlazorFallbackEndpoint(endpoint))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("Not Found");
                        return;
                    }
                }
            }

            await next();
        });
    }

    private static bool IsBlazorFallbackEndpoint(Endpoint endpoint)
    {
        // In ASP.NET Core Blazor Web Apps, MapRazorComponents registers endpoints with ComponentTypeMetadata / RootComponentMetadata
        if (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>() != null
            || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.RootComponentMetadata>() != null)
        {
            return true;
        }

        var displayName = endpoint.DisplayName;
        if (!string.IsNullOrEmpty(displayName) &&
            (displayName.Contains("App (InteractiveServer)") || displayName.Contains("App") || displayName.Contains("RazorComponent")))
        {
            return true;
        }

        return false;
    }
}
