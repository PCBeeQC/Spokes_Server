namespace Spokes_Server.Core.Security;

/// <summary>
/// Specifies the type of action a controller should perform to complete a logout workflow.
/// </summary>
public enum LogoutHandoffType
{
    /// <summary>
    /// Mobile client with no id_token. Clear client storage/cookies and redirect to server base URL.
    /// </summary>
    MobileLocalRedirect,

    /// <summary>
    /// Mobile client with id_token. Render mobile logout bridge page to challenge IdP end_session.
    /// </summary>
    MobileIdpLogout,

    /// <summary>
    /// Local redirect to a specified URI.
    /// </summary>
    Redirect,

    /// <summary>
    /// Standard OIDC sign-out challenging Cookie and OpenIdConnect authentication schemes.
    /// </summary>
    SignOut
}

/// <summary>
/// Encapsulates the outcome of a session logout initiation, allowing presentation controllers
/// to render appropriate HTTP responses without backend services coupling to MVC action results.
/// </summary>
public class LogoutHandoffResult
{
    public LogoutHandoffType HandoffType { get; init; }
    public string? RedirectUri { get; init; }
    public string? ServerBaseUrl { get; init; }
    public string? LogoutRequestId { get; init; }
    public bool HandledViaSignOutAsync { get; init; }

    public static LogoutHandoffResult MobileLocal(string serverBaseUrl) =>
        new() { HandoffType = LogoutHandoffType.MobileLocalRedirect, ServerBaseUrl = serverBaseUrl };

    public static LogoutHandoffResult MobileIdp(string logoutRequestId) =>
        new() { HandoffType = LogoutHandoffType.MobileIdpLogout, LogoutRequestId = logoutRequestId };

    public static LogoutHandoffResult LocalRedirect(string redirectUri, bool handledViaSignOutAsync = false) =>
        new() { HandoffType = LogoutHandoffType.Redirect, RedirectUri = redirectUri, HandledViaSignOutAsync = handledViaSignOutAsync };

    public static LogoutHandoffResult SignOut(string redirectUri) =>
        new() { HandoffType = LogoutHandoffType.SignOut, RedirectUri = redirectUri };
}
