using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Identity;

/// <summary>
/// Patches Casdoor v4's compiled frontend asset bundle to fix an issue where same-origin OIDC
/// redirect URIs (e.g. Spokes's /signin-oidc) are erroneously treated as internal logins,
/// causing users to be redirected to Casdoor's /apps dashboard instead of returning to Spokes.
/// </summary>
public static class CasdoorAssetPatcher
{
    private static readonly Regex SelfRedirectRegex = new(
        @"function\s+(?<fn>\w+)\((?<arg>\w+)\)\{if\(!\k<arg>\)return!1;try\{return\s+new\s+URL\(\k<arg>\)\.origin===(?<gh>\w+)\(\)\}catch\{return!1\}\}",
        RegexOptions.Compiled);

    private static readonly Regex FallbackReturnRegex = new(
        @"return\s+new\s+URL\((?<arg>\w+)\)\.origin===(?<gh>\w+)\(\)",
        RegexOptions.Compiled);

    private static readonly Regex LoginPageSelfRedirectRegex = new(
        @"(?<prefix>\.type!==[""']device[""']&&)\w+\(\w+==null\?void 0:\w+\.redirectUri\)(?<suffix>&&\(\w+\.type=[""']login[""']\))",
        RegexOptions.Compiled);

    private static readonly Regex IndexHtmlVersionedScriptTagRegex = new(
        @"<script\s+type=""module""\s+crossorigin\s+src=""(?<src>/assets/index-[^""?]+)\?v=[^""]*""></script>",
        RegexOptions.Compiled);

    public const string CallbackCheckMarker = "_u.pathname===\"/callback\"";
    public const string LoginPageBypassMarker = ".type!==\"device\"&&!1&&(";

    /// <summary>
    /// Patches all index-*.js and LoginPage-*.js asset files in the specified directory,
    /// and ensures index.html references the unsplit ES module entry path.
    /// </summary>
    /// <param name="assetsDirectory">The path to the Casdoor web assets directory (e.g. /app/casdoor/web/build/assets).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>True if any file was modified, false otherwise.</returns>
    public static bool PatchAssets(string assetsDirectory, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(assetsDirectory) || !Directory.Exists(assetsDirectory))
        {
            logger?.LogWarning("[CasdoorAssetPatcher] Assets directory does not exist or is empty: {Path}", assetsDirectory);
            return false;
        }

        var anyPatched = false;

        try
        {
            // 1. Patch index-*.js files (isSelfRedirectUri definition)
            var indexFiles = Directory.GetFiles(assetsDirectory, "index-*.js", SearchOption.TopDirectoryOnly);
            foreach (var file in indexFiles)
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                var patched = PatchScriptContent(content);
                if (!string.Equals(content, patched, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, patched, new UTF8Encoding(false));
                    logger?.LogInformation("[CasdoorAssetPatcher] Successfully patched same-origin redirect handling in {FileName}", Path.GetFileName(file));
                    anyPatched = true;
                }
            }

            // 2. Patch LoginPage-*.js files (defense in depth for login form response type)
            var loginPageFiles = Directory.GetFiles(assetsDirectory, "LoginPage-*.js", SearchOption.TopDirectoryOnly);
            foreach (var file in loginPageFiles)
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                var patched = PatchLoginPageContent(content);
                if (!string.Equals(content, patched, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, patched, new UTF8Encoding(false));
                    logger?.LogInformation("[CasdoorAssetPatcher] Successfully patched LoginPage OAuth response type in {FileName}", Path.GetFileName(file));
                    anyPatched = true;
                }
            }

            // 3. Ensure index.html module script URL has no query parameter so it matches relative imports in Vite chunks
            var htmlCandidates = new[]
            {
                Path.GetFullPath(Path.Combine(assetsDirectory, "..", "index.html")),
                Path.Combine(assetsDirectory, "index.html")
            };

            foreach (var htmlPath in htmlCandidates)
            {
                if (File.Exists(htmlPath))
                {
                    var htmlContent = File.ReadAllText(htmlPath, Encoding.UTF8);
                    var patchedHtml = PatchIndexHtmlContent(htmlContent);
                    if (!string.Equals(htmlContent, patchedHtml, StringComparison.Ordinal))
                    {
                        File.WriteAllText(htmlPath, patchedHtml, new UTF8Encoding(false));
                        logger?.LogInformation("[CasdoorAssetPatcher] Normalized ES module script URL in {FileName}", Path.GetFileName(htmlPath));
                        anyPatched = true;
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "[CasdoorAssetPatcher] Error inspecting/patching Casdoor assets at {Path}", assetsDirectory);
        }

        return anyPatched;
    }

    /// <summary>
    /// Patches JavaScript content containing Casdoor's isSelfRedirectUri function.
    /// Ensures that only requests targeting Casdoor's internal /callback endpoint are treated as self-redirects.
    /// </summary>
    public static string PatchScriptContent(string scriptContent)
    {
        if (string.IsNullOrEmpty(scriptContent))
        {
            return scriptContent;
        }

        // Already patched check (fast exit)
        if (scriptContent.Contains(CallbackCheckMarker, StringComparison.Ordinal))
        {
            return scriptContent;
        }

        var match = SelfRedirectRegex.Match(scriptContent);
        if (match.Success)
        {
            var fn = match.Groups["fn"].Value;
            var arg = match.Groups["arg"].Value;
            var gh = match.Groups["gh"].Value;

            var replacement = $"function {fn}({arg}){{if(!{arg})return!1;try{{let _u=new URL({arg});return _u.origin==={gh}()&&(_u.pathname===\"/callback\"||_u.pathname.startsWith(\"/callback/\"))}}catch{{return!1}}}}";
            return SelfRedirectRegex.Replace(scriptContent, replacement, 1);
        }

        var fallbackMatch = FallbackReturnRegex.Match(scriptContent);
        if (fallbackMatch.Success)
        {
            var arg = fallbackMatch.Groups["arg"].Value;
            var gh = fallbackMatch.Groups["gh"].Value;

            var replacement = $"let _u=new URL({arg});return _u.origin==={gh}()&&(_u.pathname===\"/callback\"||_u.pathname.startsWith(\"/callback/\"))";
            return FallbackReturnRegex.Replace(scriptContent, replacement, 1);
        }

        return scriptContent;
    }

    /// <summary>
    /// Patches LoginPage JavaScript content to disable forcing type="login" on OAuth requests.
    /// </summary>
    public static string PatchLoginPageContent(string scriptContent)
    {
        if (string.IsNullOrEmpty(scriptContent))
        {
            return scriptContent;
        }

        if (scriptContent.Contains(LoginPageBypassMarker, StringComparison.Ordinal))
        {
            return scriptContent;
        }

        var match = LoginPageSelfRedirectRegex.Match(scriptContent);
        if (match.Success)
        {
            return LoginPageSelfRedirectRegex.Replace(scriptContent, "${prefix}!1${suffix}", 1);
        }

        return scriptContent;
    }

    /// <summary>
    /// Ensures the main ES module script tag in index.html does not carry a ?v=... query string.
    /// Appending a query string to the entry module causes browsers to load a duplicate instance of the
    /// bundle when lazy-loaded Vite chunks (e.g. LoginPage-*.js) import './index-*.js' without the query string,
    /// resulting in two copies of React in memory (Minified React error #321).
    /// </summary>
    public static string PatchIndexHtmlContent(string htmlContent)
    {
        if (string.IsNullOrEmpty(htmlContent))
        {
            return htmlContent;
        }

        return IndexHtmlVersionedScriptTagRegex.Replace(
            htmlContent,
            "<script type=\"module\" crossorigin src=\"${src}\"></script>",
            1);
    }
}
