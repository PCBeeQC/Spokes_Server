using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace Spokes_Server.Controllers;

[Route("spokesapi/culture")]
[ApiController]
public class CultureController : SpokesControllerBase
{
    [HttpGet("set")]
    [HttpPost("set")]
    [AllowAnonymous]
    public IActionResult SetCulture([FromQuery] string culture, [FromQuery] string? redirectUri)
    {
        if (!string.IsNullOrWhiteSpace(culture))
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture: "en-US", uiCulture: culture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    HttpOnly = false,
                    Path = "/"
                });
        }

        if (string.IsNullOrWhiteSpace(redirectUri) || !Url.IsLocalUrl(redirectUri))
        {
            redirectUri = "/";
        }

        return LocalRedirect(redirectUri);
    }
}
