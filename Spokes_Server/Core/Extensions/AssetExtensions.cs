using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Spokes_Server.Core.Extensions;

/// <summary>
/// Provides cache-busting helpers for collocated Blazor component JS files (.razor.js).
/// .NET 9's MapStaticAssets() does not fingerprint the URLs of collocated .razor.js files,
/// which causes SRI hash mismatches behind CDNs like Cloudflare and aggressive browser caching.
/// This helper appends a version query string to force fresh fetches after deployments.
/// </summary>
public static class AssetExtensions
{
    /// <summary>
    /// The application version used as a cache-busting query parameter.
    /// Reads InformationalVersion (set by the Dockerfile's /p:InformationalVersion),
    /// falling back to Assembly.Version, then "1.0".
    /// </summary>
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "1.0";

    /// <summary>
    /// Strongly-typed overload for Blazor .NET 9 ResourceAssetCollection.
    /// </summary>
    public static string Bypassed(this ResourceAssetCollection assets, string path)
    {
        var url = assets != null ? assets[path] : null;
        var effectiveUrl = string.IsNullOrEmpty(url) ? path : url;
        return $"{effectiveUrl}?v={AppVersion}";
    }

    /// <summary>
    /// Appends a version query string to a resolved asset URL for cache-busting.
    /// Supports ResourceAssetCollection, typed dictionaries, and object fallbacks without runtime dynamic binding.
    /// </summary>
    public static string Bypassed(this object assets, string path)
    {
        string? url = null;
        if (assets is ResourceAssetCollection resourceAssets)
        {
            url = resourceAssets[path];
        }
        else if (assets is IReadOnlyDictionary<string, string?> readOnlyDict)
        {
            readOnlyDict.TryGetValue(path, out url);
        }
        else if (assets is IDictionary<string, string?> dict)
        {
            dict.TryGetValue(path, out url);
        }
        else if (assets is IDictionary legacyDict && legacyDict.Contains(path))
        {
            url = legacyDict[path] as string;
        }
        else if (assets is IReadOnlyDictionary<string, object?> objectDict && objectDict.TryGetValue(path, out var val))
        {
            url = val?.ToString();
        }

        var effectiveUrl = string.IsNullOrEmpty(url) ? path : url;
        return $"{effectiveUrl}?v={AppVersion}";
    }
}
