using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// Canonical scoped helper for signing stored Spokes file URLs (<c>/spokesapi/files/...</c>)
/// with user+session+file-scoped access tokens across Blazor components, dialogs, and headless
/// document/PDF views. Resolves identity from explicit overrides, <see cref="UserCircuitContext"/>,
/// <see cref="IHttpContextAccessor"/>, or completed <see cref="AuthenticationStateProvider"/> state.
/// </summary>
public partial class FileUrlSigner
{
    private readonly FileTokenService _fileTokens;
    private readonly UserCircuitContext? _circuitContext;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly AuthenticationStateProvider? _authStateProvider;
    private readonly UserService? _userService;

    // Matches src="..." or href="..." attributes targeting same-origin Spokes file routes inside sanitized HTML.
    [GeneratedRegex(@"(?<prefix>\b(?:src|href)\s*=\s*"")(?<url>/(?:spokesapi/files|spokesapi/attachments|internal/attachments)/[^""]+)(?<suffix>"")", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlFileUrlAttributeRegex();

    public FileUrlSigner(
        FileTokenService fileTokens,
        UserCircuitContext? circuitContext = null,
        IHttpContextAccessor? httpContextAccessor = null,
        AuthenticationStateProvider? authStateProvider = null,
        UserService? userService = null)
    {
        _fileTokens = fileTokens ?? throw new ArgumentNullException(nameof(fileTokens));
        _circuitContext = circuitContext;
        _httpContextAccessor = httpContextAccessor;
        _authStateProvider = authStateProvider;
        _userService = userService;
    }

    public string SignUrl(
        string? rawUrl,
        string? encryptionKeyBase64 = null,
        bool isThumbnail = false,
        string? displayFileName = null,
        bool smallThumbnail = false,
        string? userId = null,
        string? sessionId = null)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return string.Empty;
        }

        var (resolvedUserId, resolvedSessionId) = ResolveIdentity(userId, sessionId);
        return _fileTokens.SignFileUrl(
            rawUrl,
            resolvedUserId,
            resolvedSessionId,
            encryptionKeyBase64,
            isThumbnail,
            displayFileName,
            smallThumbnail);
    }

    /// <summary>
    /// Rewrites same-origin Spokes file URLs embedded in sanitized HTML (<c>src="..."</c> / <c>href="..."</c>)
    /// to carry a user+session+file-scoped access token. External URLs and non-file routes are untouched.
    /// </summary>
    public string SignHtmlFileUrls(string? html, string? userId = null, string? sessionId = null)
    {
        if (string.IsNullOrEmpty(html) ||
            (!html.Contains("/spokesapi/files/", StringComparison.OrdinalIgnoreCase) &&
             !html.Contains("/spokesapi/attachments/", StringComparison.OrdinalIgnoreCase) &&
             !html.Contains("/internal/attachments/", StringComparison.OrdinalIgnoreCase)))
        {
            return html ?? string.Empty;
        }

        var (resolvedUserId, resolvedSessionId) = ResolveIdentity(userId, sessionId);
        if (string.IsNullOrWhiteSpace(resolvedUserId) || string.IsNullOrWhiteSpace(resolvedSessionId))
        {
            return html;
        }

        return HtmlFileUrlAttributeRegex().Replace(html, match =>
        {
            var decodedUrl = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var signedUrl = _fileTokens.SignFileUrl(decodedUrl, resolvedUserId, resolvedSessionId);
            if (string.Equals(signedUrl, decodedUrl, StringComparison.Ordinal))
            {
                return match.Value;
            }

            return $"{match.Groups["prefix"].Value}{WebUtility.HtmlEncode(signedUrl)}{match.Groups["suffix"].Value}";
        });
    }

    private (string? UserId, string? SessionId) ResolveIdentity(string? explicitUserId, string? explicitSessionId)
    {
        string? userId = !string.IsNullOrWhiteSpace(explicitUserId) ? explicitUserId : _circuitContext?.UserId;
        string? sessionId = !string.IsNullOrWhiteSpace(explicitSessionId) ? explicitSessionId : _circuitContext?.SessionId;

        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            return (userId, sessionId);
        }

        var httpPrincipal = _httpContextAccessor?.HttpContext?.User;
        if (httpPrincipal?.Identity?.IsAuthenticated == true)
        {
            ApplyFromPrincipal(httpPrincipal, ref userId, ref sessionId);
        }

        if ((!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(sessionId)) || _authStateProvider == null)
        {
            return (userId, sessionId);
        }

        try
        {
            var authTask = _authStateProvider.GetAuthenticationStateAsync();
            if (authTask.IsCompletedSuccessfully && authTask.Result?.User?.Identity?.IsAuthenticated == true)
            {
                ApplyFromPrincipal(authTask.Result.User, ref userId, ref sessionId);
            }
        }
        catch
        {
            // Best-effort fallback only
        }

        return (userId, sessionId);
    }

    private void ApplyFromPrincipal(ClaimsPrincipal principal, ref string? userId, ref string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = principal.FindFirst("EmployeeId")?.Value;
            if (string.IsNullOrWhiteSpace(userId) && _userService != null)
            {
                userId = _userService.GetEmployee(principal)?.Id;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = principal.FindFirst("SessionId")?.Value;
        }
    }
}
