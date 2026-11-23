using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Core.Extensions;

/// <summary>
/// Centralized HTTP request classification primitives shared across Spokes middleware
/// (setup gating, fast-404 protection, response caching/security headers, diagnostics, and session revocation).
/// </summary>
public static class SpokesRequestClassifier
{
    /// <summary>
    /// File extensions treated as static or binary asset requests rather than Blazor HTML page routes.
    /// </summary>
    public static readonly HashSet<string> StaticAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".avif",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".css", ".js", ".mjs", ".map", ".wasm",
        ".wav", ".mp3", ".ogg",
        ".json", ".webmanifest", ".xml", ".zip", ".pdf",
        ".gz", ".br"
    };

    private static readonly PathString[] StaticAssetPathPrefixes =
    [
        new("/_framework"),
        new("/_content"),
        new("/css"),
        new("/js"),
        new("/lib"),
        new("/fonts"),
        new("/icons"),
        new("/sounds"),
        new("/data"),
        new("/Klipy")
    ];

    private static readonly PathString[] ApiOrHubPathPrefixes =
    [
        new("/spokesapi"),
        new("/api"),
        new("/internal"),
        new("/hubs"),
        new("/media"),
        new("/setup/api")
    ];

    private static readonly PathString[] Fast404ApiPrefixes =
    [
        new("/spokesapi"),
        new("/api"),
        new("/internal")
    ];

    private static readonly PathString[] SetupAllowedPathPrefixes =
    [
        new("/setup"),
        new("/branding"),
        new("/spokesapi/branding"),
        new("/spokesapi/Media/Icon"),
        new("/spokesapi/Media/Logo"),
        new("/spokesapi/Manifest"),
        new("/spokesapi/culture"),
        new("/robots.txt"),
        new("/api/tls/ask"),
        new("/spokesapi/tls/ask")
    ];

    private static readonly PathString[] StaticLoginPathPrefixes =
    [
        new("/authentication/login"),
        new("/mobile-login"),
        new("/access-pending"),
        new("/banned")
    ];

    /// <summary>
    /// Returns <c>true</c> for Blazor Server SignalR circuit transport and framework bootstrap endpoints
    /// (<c>/_blazor</c> WebSockets, <c>POST /_blazor/negotiate</c>, LongPolling <c>POST /_blazor</c> chunks
    /// used by <c>IBrowserFile</c> uploads, and <c>/_framework/*</c>) across all HTTP verbs.
    /// </summary>
    public static bool IsBlazorTransportRequest(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        return path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="path"/> ends with a known static/binary asset file extension.
    /// </summary>
    public static bool HasStaticAssetExtension(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        var ext = Path.GetExtension(path.Value);
        return !string.IsNullOrEmpty(ext) && StaticAssetExtensions.Contains(ext);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="path"/> targets a static web asset (e.g. <c>/lib/*</c>,
    /// <c>/js/*</c>, <c>/css/*</c>, <c>/_content/*</c>, <c>/service-worker.js</c>, or collocated <c>*.razor.js</c>),
    /// excluding API, internal attachment, media, and SignalR hub routes.
    /// </summary>
    public static bool IsStaticAssetPath(PathString path)
    {
        if (!path.HasValue || IsApiOrHubPathPrefix(path))
        {
            return false;
        }

        return MatchesAnySegmentPrefix(path, StaticAssetPathPrefixes) || HasStaticAssetExtension(path);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="path"/> starts with a Spokes API, internal, media, or SignalR hub prefix.
    /// </summary>
    public static bool IsApiOrHubPathPrefix(PathString path)
    {
        return path.HasValue && MatchesAnySegmentPrefix(path, ApiOrHubPathPrefixes);
    }

    /// <summary>
    /// Returns <c>true</c> if an unmapped request to <paramref name="path"/> should short-circuit with HTTP 404
    /// rather than falling through to the Blazor SSR catch-all endpoint.
    /// </summary>
    public static bool IsFast404Candidate(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        return MatchesAnySegmentPrefix(path, Fast404ApiPrefixes) || HasStaticAssetExtension(path);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="endpoint"/> represents a Blazor Razor Component / SSR fallback endpoint.
    /// </summary>
    public static bool IsBlazorComponentEndpoint(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() != null
            || endpoint.Metadata.GetMetadata<RootComponentMetadata>() != null)
        {
            return true;
        }

        var displayName = endpoint.DisplayName;
        return !string.IsNullOrEmpty(displayName)
            && (displayName.Contains("App (InteractiveServer)", StringComparison.Ordinal)
                || displayName.Contains("App", StringComparison.Ordinal)
                || displayName.Contains("RazorComponent", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns <c>true</c> when the request targets a JSON API, file/media controller, SignalR hub, or XHR endpoint
    /// that must never receive an HTML page redirect.
    /// </summary>
    public static bool IsApiOrNonHtmlRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path;
        if (IsBlazorTransportRequest(path) || IsStaticAssetPath(path))
        {
            return false;
        }

        if (IsApiOrHubPathPrefix(path))
        {
            return true;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint != null
            && (endpoint.Metadata.GetMetadata<IApiBehaviorMetadata>() != null
                || endpoint.Metadata.GetMetadata<HubMetadata>() != null))
        {
            return true;
        }

        var accept = context.Request.Headers.Accept.ToString();
        bool acceptsHtml = accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
        bool acceptsJson = accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        if (acceptsJson && !acceptsHtml)
        {
            return true;
        }

        if (string.Equals(context.Request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var method = context.Request.Method;
        bool isGetOrHead = string.IsNullOrEmpty(method) || HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        return !isGetOrHead && !acceptsHtml;
    }

    /// <summary>
    /// Returns <c>true</c> when the request represents a top-level browser HTML document navigation.
    /// </summary>
    public static bool IsHtmlPageNavigation(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path;
        if (IsBlazorTransportRequest(path) || IsStaticAssetPath(path) || IsApiOrNonHtmlRequest(context))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Path.GetExtension(path.Value)))
        {
            return false;
        }

        var method = context.Request.Method;
        return string.IsNullOrEmpty(method) || HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="context"/> is permitted to execute before initial server setup is complete.
    /// </summary>
    public static bool IsAllowedDuringSetup(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path;
        if (IsBlazorTransportRequest(path) || IsStaticAssetPath(path))
        {
            return true;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowDuringSetupMetadata>() != null)
        {
            return true;
        }

        return path.HasValue && MatchesAnySegmentPrefix(path, SetupAllowedPathPrefixes);
    }

    /// <summary>
    /// Returns <c>true</c> for public authentication and account-status interstitial routes that render
    /// via Static SSR without starting an InteractiveServer Blazor circuit.
    /// </summary>
    public static bool IsStaticLoginRoute(PathString path)
    {
        return path.HasValue && MatchesAnySegmentPrefix(path, StaticLoginPathPrefixes);
    }

    private static bool MatchesAnySegmentPrefix(PathString path, ReadOnlySpan<PathString> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
