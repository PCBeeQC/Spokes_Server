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
    public static IApplicationBuilder UseSpokesFast404Guard(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (SpokesRequestClassifier.IsFast404Candidate(context.Request.Path))
            {
                var endpoint = context.GetEndpoint();
                if (endpoint == null || SpokesRequestClassifier.IsBlazorComponentEndpoint(endpoint))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentType = "text/plain";
                    await context.Response.WriteAsync("Not Found");
                    return;
                }
            }

            await next();
        });
    }
}
