using System.Reflection;

namespace Spokes_Server.Extensions
{
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
        /// Appends a version query string to a resolved asset URL for cache-busting.
        /// Call this from Razor files as: Assets.Bypassed("path") 
        /// where Assets is the IResourceAssetCollection.
        /// 
        /// If the resolved URL is null/empty (which happens for collocated .razor.js files
        /// because MapStaticAssets does not register them), the raw path is used instead,
        /// still with the version appended.
        /// </summary>
        public static string Bypassed(this object assets, string path)
        {
            // In Razor context, 'assets' is IResourceAssetCollection. We use its indexer via dynamic
            // to avoid a compile-time dependency on the Razor-only type.
            string? url = null;
            try
            {
                url = ((dynamic)assets)[path] as string;
            }
            catch
            {
                // If indexer fails, fall through to use the raw path
            }

            var effectiveUrl = string.IsNullOrEmpty(url) ? path : url;
            return $"{effectiveUrl}?v={AppVersion}";
        }
    }
}
