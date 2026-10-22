using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Controllers;

[Route("branding")]
[Route("spokesapi/branding")]
[ApiController]
[AllowAnonymous]
public class PublicBrandingController : SpokesControllerBase
{
    private readonly CoreContext _coreContext;
    private readonly IWebHostEnvironment _env;

    public PublicBrandingController(CoreContext coreContext, IWebHostEnvironment env)
    {
        _coreContext = coreContext;
        _env = env;
    }

    private bool ValidateToken(string token)
    {
        var config = _coreContext.SystemConfigs.Get();
        if (string.IsNullOrEmpty(config.PublicBrandingToken))
        {
            return token == "default";
        }
        return config.PublicBrandingToken == token;
    }

    private IActionResult DefaultIcon()
    {
        return PhysicalFile(System.IO.Path.Combine(_env.WebRootPath, "default-icon-192.png"), "image/png");
    }

    private IActionResult ServeImage(string base64String)
    {
        if (string.IsNullOrEmpty(base64String) || base64String == "null")
            return DefaultIcon();

        if (!base64String.Contains(','))
            return DefaultIcon();

        if (DataUriHelper.TryParse(base64String, out var imageBytes, out var mimeType))
        {
            // XSS Mitigation: Ensure only safe raster image mime types are served
            if (mimeType != "image/png" && mimeType != "image/jpeg" && mimeType != "image/webp")
            {
                mimeType = "image/png";
            }

            Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self' data:;";
            return File(imageBytes, mimeType);
        }

        return DefaultIcon();
    }

    [HttpGet("{token}/logo")]
    [HttpGet("{token}/logo.png")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult GetLogo(string token)
    {
        if (!ValidateToken(token)) return UnauthorizedError("Invalid or missing branding token.", "INVALID_TOKEN");
        return ServeImage(_coreContext.CompanyProfile.Get().LogoBase64);
    }

    [HttpGet("{token}/icon")]
    [HttpGet("{token}/icon.png")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)]
    public IActionResult GetIcon(string token)
    {
        if (!ValidateToken(token)) return UnauthorizedError("Invalid or missing branding token.", "INVALID_TOKEN");
        return ServeImage(_coreContext.CompanyProfile.Get().IconBase64);
    }
}
