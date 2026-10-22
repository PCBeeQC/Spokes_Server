using System;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Security;

namespace Spokes_Server.Controllers;

/// <summary>
/// Base class for all Spokes API controllers.
/// 
/// Response Envelope Architecture & Conventions:
/// Spokes intentionally adopts standard REST response conventions (consistent with RFC 7807 problem details):
/// 1. Error envelopes: All non-2xx error responses MUST use the standardized SpokesResult error envelope
///    via the helper methods below (BadRequestError, NotFoundError, UnauthorizedError, ForbiddenError, StatusCodeError).
///    This guarantees HTTP, web, and native mobile clients receive a uniform { isSuccess: false, error: string, code?: string }
///    payload for any failure across the entire API surface.
/// 2. Query endpoints (GET): Return domain models or strongly-typed response DTO records directly (e.g. Ok(devices), Ok(profile))
///    without arbitrary wrapping, enabling standard REST client consumption and direct typed deserialization without unwrapping.
///    Arbitrary anonymous objects (e.g. new { ... }) are prohibited in favor of strongly-typed DTO records.
/// 3. Mutating commands (POST/PUT/DELETE): Return strongly-typed outcome records, Success() envelopes, or standard HTTP status codes.
/// 4. File transfers: Return PhysicalFileResult or FileStreamResult directly.
/// 
/// Antiforgery Strategy:
/// API controllers receive JSON payloads from JavaScript fetch() and native mobile
/// HTTP clients. CSRF protection is provided by SameSite=Lax cookies and [Authorize]
/// bearer-token validation — antiforgery tokens are unnecessary for JSON APIs.
/// app.UseAntiforgery() remains active for Blazor interactive form submissions.
/// </summary>
[IgnoreAntiforgeryToken]
public abstract class SpokesControllerBase : ControllerBase
{
    /// <summary>
    /// Returns a standardized 200 OK response with a SpokesResult success envelope.
    /// </summary>
    protected OkObjectResult Success() => Ok(SpokesResult.Success());

    /// <summary>
    /// Returns a standardized 200 OK response wrapping data in a SpokesResult envelope.
    /// </summary>
    protected OkObjectResult Success<T>(T value) => Ok(SpokesResult<T>.Success(value));

    protected BadRequestObjectResult BadRequestError(string message, string? code = null)
        => BadRequest(SpokesResult.Failure(message, code));

    protected NotFoundObjectResult NotFoundError(string message = "Resource not found.", string? code = null)
        => NotFound(SpokesResult.Failure(message, code));

    protected UnauthorizedObjectResult UnauthorizedError(string message = "Authentication required.", string? code = null)
        => Unauthorized(SpokesResult.Failure(message, code));

    protected ObjectResult StatusCodeError(int statusCode, string message, string? code = null)
        => StatusCode(statusCode, SpokesResult.Failure(message, code));

    protected ObjectResult ForbiddenError(string message = "Access forbidden.", string? code = null)
        => StatusCode(403, SpokesResult.Failure(message, code));

    /// <summary>
    /// Translates a domain LogoutHandoffResult into the appropriate presentation IActionResult
    /// (e.g. mobile HTML bridge, client-side redirect, or ASP.NET Core SignOutResult).
    /// </summary>
    protected IActionResult ProcessLogoutHandoff(LogoutHandoffResult handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);

        return handoff.HandoffType switch
        {
            LogoutHandoffType.MobileLocalRedirect =>
                Content(MobileSsoHelper.CreateMobileLocalLogoutPage(handoff.ServerBaseUrl ?? $"{Request.Scheme}://{Request.Host}"), "text/html"),

            LogoutHandoffType.MobileIdpLogout =>
                Content(MobileSsoHelper.CreateMobileLogoutPage(HttpContext, handoff.LogoutRequestId!), "text/html"),

            LogoutHandoffType.Redirect =>
                handoff.HandledViaSignOutAsync
                    ? new EmptyResult()
                    : Redirect(handoff.RedirectUri ?? "/sso/login"),

            LogoutHandoffType.SignOut =>
                SignOut(
                    new AuthenticationProperties { RedirectUri = handoff.RedirectUri ?? "/" },
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme),

            _ => Redirect("/")
        };
    }
}


