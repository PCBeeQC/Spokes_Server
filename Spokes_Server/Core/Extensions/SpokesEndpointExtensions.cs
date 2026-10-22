using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Spokes_Server.Components;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Hubs;

namespace Spokes_Server.Core.Extensions;

public static class SpokesEndpointExtensions
{
    public static IEndpointRouteBuilder MapSpokesEndpoints(this IEndpointRouteBuilder app)
    {
        // Disallow search engine crawlers from indexing private Spokes instances and demo servers
        app.MapGet("/robots.txt", [AllowAnonymous] () =>
            Results.Text("User-agent: *\nDisallow: /\n", "text/plain"));

        // TLS check handlers for Caddy reverse proxy on-demand certificates
        Delegate tlsAskHandler = [AllowAnonymous] (string? domain, SystemConfigRepository systemConfigs) =>
        {
            if (string.IsNullOrWhiteSpace(domain)) return Results.StatusCode(403);
            
            var config = systemConfigs.Get();
            if (string.IsNullOrWhiteSpace(config.ServerPublicUrl)) return Results.StatusCode(403);

            var expectedDomain = config.ServerPublicUrl.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            var requestedDomain = domain.Replace("https://", "").Replace("http://", "").TrimEnd('/');

            if (expectedDomain.Equals(requestedDomain, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Ok();
            }
            
            return Results.StatusCode(403);
        };

        app.MapGet("/api/tls/ask", tlsAskHandler);
        app.MapGet("/spokesapi/tls/ask", tlsAskHandler);

        // Core App Mappings
        app.MapControllers();
        app.MapReverseProxy().AllowAnonymous();
        app.MapHub<ChatHub>("/hubs/chat");
        app.MapHub<SlideshowHub>("/hubs/slideshow").RequireCors("AllowPublicApi");
        app.MapHub<SlideshowHub>("/slideshowHub").RequireCors("AllowPublicApi");
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        return app;
    }
}
