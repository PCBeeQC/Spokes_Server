using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Spokes_Server.Core.Extensions;

/// <summary>
/// Single source of truth for static asset content-hash fingerprinting, ES module import-map generation,
/// and cache-busting across collocated Blazor scripts (.razor.js), wwwroot ES modules, and JsModuleBase services.
/// Automatically computes SHA-256 content hashes from physical files on disk (cached by LastWriteTimeUtc + Length)
/// and staticwebassets.endpoints.json so editing any .js file immediately invalidates client caches without manual ?v= strings.
/// </summary>
public static class AssetExtensions
{
    /// <summary>
    /// The application version used as a fallback cache-busting query parameter when an asset path
    /// is synthetic (e.g., unit test mocks not present on disk or in staticwebassets.endpoints.json).
    /// </summary>
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "1.0";

    private static readonly ConcurrentDictionary<string, (long Ticks, long Length, string Hash)> FileHashCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, (long Ticks, long Length, bool Eligible)> ImportMapEligibilityCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex EsModuleOrWorkletRegex = new(
        @"(?m)^\s*(?:export|import)\s|registerProcessor\s*\(",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ExplicitImportMapLibPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "lib/noise-suppression/rnnoise-processor.js",
        "lib/noise-suppression/deepfilternet-processor.js"
    };

    private static readonly Lazy<string?> ServerRootLazy = new(ResolveServerRootDirectory);

    private static readonly Lazy<IReadOnlyDictionary<string, (string FingerprintedRoute, string Fingerprint)>> StaticAssetEndpointsLazy =
        new(LoadStaticWebAssetEndpoints);

    /// <summary>
    /// Strongly-typed overload for Blazor .NET 9 ResourceAssetCollection.
    /// </summary>
    public static string Bypassed(this ResourceAssetCollection assets, string path)
    {
        var url = assets != null ? assets[path] : null;
        var effectiveUrl = string.IsNullOrEmpty(url) ? path : url;
        var version = GetAssetVersion(path);
        return $"{effectiveUrl}?v={version}";
    }

    /// <summary>
    /// Appends a content-hash version query string to a resolved asset URL for cache-busting.
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
        var version = GetAssetVersion(path);
        return $"{effectiveUrl}?v={version}";
    }

    /// <summary>
    /// Resolves a module path (e.g. "./js/voiceInterop.js") to include an automated content-hash version token
    /// covering the module and the project's JS bundle tree. Unknown/synthetic paths in unit tests are returned unchanged.
    /// </summary>
    public static string ResolveModulePath(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return modulePath;
        }

        var cleanPath = StripQueryString(modulePath);
        var normalized = NormalizeRelativeAssetPath(cleanPath);
        var contentHash = TryGetKnownAssetVersion(normalized);
        if (string.IsNullOrEmpty(contentHash))
        {
            return modulePath;
        }

        return $"{cleanPath}?v={contentHash}";
    }

    /// <summary>
    /// Returns the content-based version token for a static asset path if the asset exists on disk or in
    /// staticwebassets.endpoints.json; otherwise falls back to <see cref="AppVersion"/>.
    /// </summary>
    public static string GetAssetVersion(string path)
    {
        var normalized = NormalizeRelativeAssetPath(StripQueryString(path));
        return TryGetKnownAssetVersion(normalized) ?? AppVersion;
    }

    /// <summary>
    /// Returns true when <paramref name="normalizedPath"/> is an ES module or AudioWorklet script that is
    /// resolved via <c>&lt;script type="importmap"&gt;</c>, dynamic <c>import(...)</c>, or
    /// <c>window.spokesResolveAssetUrl(...)</c>. Excludes classic/UMD vendor scripts (<c>quill.js</c>,
    /// <c>cropper.min.js</c>, <c>livekit-client.umd.min.js</c>, <c>spokes-boot.js</c>, etc.) so the
    /// inline importmap in <c>App.razor</c> stays compact.
    /// </summary>
    public static bool IsImportMapEligible(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath) || !normalizedPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalizedPath.StartsWith("_framework/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("_content/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedPath, "service-worker.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalizedPath.StartsWith("Components/", StringComparison.OrdinalIgnoreCase) &&
            normalizedPath.EndsWith(".razor.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExplicitImportMapLibPaths.Contains(normalizedPath))
        {
            return true;
        }

        if (normalizedPath.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!normalizedPath.StartsWith("js/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var physicalPath = ResolvePhysicalAssetPath(normalizedPath);
        if (physicalPath != null)
        {
            try
            {
                var info = new FileInfo(physicalPath);
                if (info.Exists)
                {
                    var ticks = info.LastWriteTimeUtc.Ticks;
                    var length = info.Length;
                    if (ImportMapEligibilityCache.TryGetValue(physicalPath, out var cached) &&
                        cached.Ticks == ticks &&
                        cached.Length == length)
                    {
                        return cached.Eligible;
                    }

                    var content = File.ReadAllText(physicalPath);
                    var eligible = EsModuleOrWorkletRegex.IsMatch(content);
                    ImportMapEligibilityCache[physicalPath] = (ticks, length, eligible);
                    return eligible;
                }
            }
            catch
            {
            }
        }

        // Published container fallback when staticwebassets.endpoints.json lists an asset without loose source on disk
        return normalizedPath.StartsWith("js/voice/", StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith("js/voice", StringComparison.OrdinalIgnoreCase) && !string.Equals(normalizedPath, "js/voice-recorder.js", StringComparison.OrdinalIgnoreCase) && !string.Equals(normalizedPath, "js/voiceSpeakingDom.js", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, "js/customRNNoiseProcessor.js", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, "js/noiseSuppressionManager.js", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, "js/mediaZoomGesture.js", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a scoped WHATWG <c>&lt;script type="importmap"&gt;</c> JSON payload covering project ES modules
    /// (<c>Components/**/*.razor.js</c>, ESM/AudioWorklet files in <c>wwwroot/js/**</c>, and noise-suppression
    /// worklets/processors). Excludes classic/UMD scripts to keep dynamic HTML compact while ensuring both
    /// relative (<c>./voicePip.js</c>) and root-relative (<c>/js/voicePip.js</c>) imports resolve cleanly.
    /// </summary>
    public static string BuildImportMapJson(ResourceAssetCollection? assets = null)
    {
        var imports = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var endpoints = StaticAssetEndpointsLazy.Value;
        var allJsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in endpoints.Keys)
        {
            var normalized = NormalizeRelativeAssetPath(key);
            if (IsImportMapEligible(normalized))
            {
                allJsPaths.Add(normalized);
            }
        }

        var serverRoot = ServerRootLazy.Value;
        if (!string.IsNullOrEmpty(serverRoot))
        {
            var wwwrootDir = Path.Combine(serverRoot, "wwwroot");
            if (Directory.Exists(wwwrootDir))
            {
                foreach (var file in Directory.EnumerateFiles(wwwrootDir, "*.js", SearchOption.AllDirectories))
                {
                    var rel = NormalizeRelativeAssetPath(Path.GetRelativePath(wwwrootDir, file).Replace('\\', '/'));
                    if (IsImportMapEligible(rel))
                    {
                        allJsPaths.Add(rel);
                    }
                }
            }

            var componentsDir = Path.Combine(serverRoot, "Components");
            if (Directory.Exists(componentsDir))
            {
                foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor.js", SearchOption.AllDirectories))
                {
                    var rel = NormalizeRelativeAssetPath(Path.GetRelativePath(serverRoot, file).Replace('\\', '/'));
                    if (IsImportMapEligible(rel))
                    {
                        allJsPaths.Add(rel);
                    }
                }
            }
        }

        foreach (var relPath in allJsPaths)
        {
            if (string.IsNullOrEmpty(relPath)) continue;

            string? resolvedRoute = null;
            if (assets != null)
            {
                try { resolvedRoute = assets[relPath]; } catch { }
            }
            if (string.IsNullOrEmpty(resolvedRoute) && endpoints.TryGetValue(relPath, out var ep))
            {
                resolvedRoute = ep.FingerprintedRoute;
            }

            var effectiveRoute = NormalizeRelativeAssetPath(string.IsNullOrEmpty(resolvedRoute) ? relPath : resolvedRoute);
            var version = GetAssetVersion(relPath);
            var targetWithVersion = $"{effectiveRoute}?v={version}";

            imports[$"./{relPath}"] = $"./{targetWithVersion}";
            imports[$"/{relPath}"] = $"/{targetWithVersion}";
        }

        return JsonSerializer.Serialize(new { imports });
    }

    private static string? TryGetKnownAssetVersion(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return null;
        }

        var physicalPath = ResolvePhysicalAssetPath(normalizedPath);
        string? fileHash = null;
        if (physicalPath != null)
        {
            fileHash = ComputeOrGetCachedFileHash(physicalPath);
        }
        else if (StaticAssetEndpointsLazy.Value.TryGetValue(normalizedPath, out var ep) && !string.IsNullOrEmpty(ep.Fingerprint))
        {
            fileHash = ep.Fingerprint;
        }

        if (string.IsNullOrEmpty(fileHash))
        {
            return null;
        }

        if (normalizedPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            var treeSuffix = ComputeJsTreeHashSuffix();
            if (!string.IsNullOrEmpty(treeSuffix))
            {
                return $"{fileHash}{treeSuffix}";
            }
        }

        return fileHash;
    }

    private static string ComputeJsTreeHashSuffix()
    {
        var serverRoot = ServerRootLazy.Value;
        if (string.IsNullOrEmpty(serverRoot))
        {
            return string.Empty;
        }

        var wwwrootJsDir = Path.Combine(serverRoot, "wwwroot", "js");
        if (!Directory.Exists(wwwrootJsDir))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(wwwrootJsDir, "*.js", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var h = ComputeOrGetCachedFileHash(file);
            sb.Append(h);
        }

        if (sb.Length == 0)
        {
            return string.Empty;
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), hash);
        return Convert.ToHexStringLower(hash[..2]);
    }

    private static string ComputeOrGetCachedFileHash(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return string.Empty;
            }

            var ticks = info.LastWriteTimeUtc.Ticks;
            var length = info.Length;
            if (FileHashCache.TryGetValue(fullPath, out var cached) && cached.Ticks == ticks && cached.Length == length)
            {
                return cached.Hash;
            }

            var bytes = File.ReadAllBytes(fullPath);
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(bytes, digest);
            var hash = Convert.ToHexStringLower(digest[..5]);
            FileHashCache[fullPath] = (ticks, length, hash);
            return hash;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ResolvePhysicalAssetPath(string normalizedPath)
    {
        var relativeOsPath = normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        var serverRoot = ServerRootLazy.Value;
        if (!string.IsNullOrEmpty(serverRoot))
        {
            var inWwwroot = Path.Combine(serverRoot, "wwwroot", relativeOsPath);
            if (File.Exists(inWwwroot))
            {
                return inWwwroot;
            }

            var inServerRoot = Path.Combine(serverRoot, relativeOsPath);
            if (File.Exists(inServerRoot))
            {
                return inServerRoot;
            }
        }

        var inBaseWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot", relativeOsPath);
        if (File.Exists(inBaseWwwroot))
        {
            return inBaseWwwroot;
        }

        return null;
    }

    private static string? ResolveServerRootDirectory()
    {
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(cwd, "Spokes_Server.csproj")) || Directory.Exists(Path.Combine(cwd, "wwwroot", "js")))
            {
                return cwd;
            }

            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Spokes_Server.csproj")) ||
                    Directory.Exists(Path.Combine(current.FullName, "wwwroot", "js")))
                {
                    return current.FullName;
                }

                var childServer = Path.Combine(current.FullName, "Spokes_Server");
                if (File.Exists(Path.Combine(childServer, "Spokes_Server.csproj")) ||
                    Directory.Exists(Path.Combine(childServer, "wwwroot", "js")))
                {
                    return childServer;
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyDictionary<string, (string FingerprintedRoute, string Fingerprint)> LoadStaticWebAssetEndpoints()
    {
        var result = new Dictionary<string, (string FingerprintedRoute, string Fingerprint)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var candidateFiles = new List<string>();
            if (Directory.Exists(AppContext.BaseDirectory))
            {
                candidateFiles.AddRange(Directory.GetFiles(AppContext.BaseDirectory, "*staticwebassets*.endpoints.json"));
            }

            if (candidateFiles.Count == 0 && !string.IsNullOrEmpty(ServerRootLazy.Value))
            {
                candidateFiles.AddRange(Directory.GetFiles(ServerRootLazy.Value, "*staticwebassets*.endpoints.json", SearchOption.AllDirectories));
            }

            var endpointsFile = candidateFiles.FirstOrDefault();
            if (string.IsNullOrEmpty(endpointsFile) || !File.Exists(endpointsFile))
            {
                return result;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(endpointsFile));
            if (!doc.RootElement.TryGetProperty("Endpoints", out var endpointsEl) || endpointsEl.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var ep in endpointsEl.EnumerateArray())
            {
                var route = ep.TryGetProperty("Route", out var rEl) ? rEl.GetString() : null;
                if (string.IsNullOrEmpty(route) || !ep.TryGetProperty("EndpointProperties", out var propsEl) || propsEl.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string? label = null;
                string? fingerprint = null;
                foreach (var prop in propsEl.EnumerateArray())
                {
                    var name = prop.TryGetProperty("Name", out var nEl) ? nEl.GetString() : null;
                    var val = prop.TryGetProperty("Value", out var vEl) ? vEl.GetString() : null;
                    if (string.Equals(name, "label", StringComparison.OrdinalIgnoreCase))
                    {
                        label = val;
                    }
                    else if (string.Equals(name, "fingerprint", StringComparison.OrdinalIgnoreCase))
                    {
                        fingerprint = val;
                    }
                }

                if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(fingerprint))
                {
                    result[NormalizeRelativeAssetPath(label)] = (NormalizeRelativeAssetPath(route), fingerprint);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static string StripQueryString(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var qIdx = path.IndexOf('?');
        return qIdx >= 0 ? path[..qIdx] : path;
    }

    private static string NormalizeRelativeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        while (normalized.StartsWith('/'))
        {
            normalized = normalized[1..];
        }
        return normalized;
    }
}
