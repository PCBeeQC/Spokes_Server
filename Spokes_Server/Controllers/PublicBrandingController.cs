using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using System;

namespace Spokes_Server.Controllers
{
    [Route("branding")]
    [ApiController]
    public class PublicBrandingController : SpokesControllerBase
    {
        private readonly Database _db;
        private readonly IWebHostEnvironment _env;

        public PublicBrandingController(Database db, IWebHostEnvironment env)
        {
            _db = db;
            _env = env;
        }

        private bool ValidateToken(string token)
        {
            var config = _db.SystemConfigs.Get();
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

            var parts = base64String.Split(',');
            if (parts.Length != 2)
                return DefaultIcon();

            var base64Data = parts[1];
            var mimeType = parts[0].Replace("data:", "").Replace(";base64", "");

            // XSS Mitigation: Ensure only safe raster image mime types are served
            if (mimeType != "image/png" && mimeType != "image/jpeg" && mimeType != "image/webp")
            {
                mimeType = "image/png";
            }

            try
            {
                var imageBytes = Convert.FromBase64String(base64Data);
                Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self' data:;";
                return File(imageBytes, mimeType);
            }
            catch
            {
                return DefaultIcon();
            }
        }

        [HttpGet("{token}/logo")]
        [HttpGet("{token}/logo.png")]
        public IActionResult GetLogo(string token)
        {
            if (!ValidateToken(token)) return Unauthorized();

            var profile = _db.CompanyProfile.Get();
            if (string.IsNullOrEmpty(profile.LogoBase64) || profile.LogoBase64 == "null") return DefaultIcon();
            return ServeImage(profile.LogoBase64);
        }

        [HttpGet("{token}/icon")]
        [HttpGet("{token}/icon.png")]
        public IActionResult GetIcon(string token)
        {
            if (!ValidateToken(token)) return Unauthorized();

            var profile = _db.CompanyProfile.Get();
            if (string.IsNullOrEmpty(profile.IconBase64) || profile.IconBase64 == "null") return DefaultIcon();
            return ServeImage(profile.IconBase64);
        }
    }
}
