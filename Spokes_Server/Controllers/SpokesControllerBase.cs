using Microsoft.AspNetCore.Mvc;

namespace Spokes_Server.Controllers;

/// <summary>
/// Base class for all Spokes API controllers.
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
}
