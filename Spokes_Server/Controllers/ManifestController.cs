using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Security.Claims;
using Microsoft.AspNetCore.Cors;

namespace Spokes_Server.Controllers
{
    [Route("spokesapi/[controller]")]
    [ApiController]
    [EnableCors("AllowPublicApi")]
    public class ManifestController : SpokesControllerBase
    {
        private readonly CompanyProfileRepository _companyProfile;
        private readonly ShortcutIconService _shortcutIconService;
        private readonly SystemConfigRepository _systemConfig;
        private readonly EmployeeRepository _employees;
        private readonly UserService _userService;

        public ManifestController(
            CompanyProfileRepository companyProfile, 
            ShortcutIconService shortcutIconService,
            SystemConfigRepository systemConfig,
            EmployeeRepository employees,
            UserService userService)
        {
            _companyProfile = companyProfile;
            _shortcutIconService = shortcutIconService;
            _systemConfig = systemConfig;
            _employees = employees;
            _userService = userService;
        }

        [HttpGet]
        public IActionResult Get()
        {
            var profile = _companyProfile.Get();
            var config = _systemConfig.Get();
            var appName = !string.IsNullOrEmpty(profile?.CompanyName) ? profile.CompanyName : "Spokes";
            var shortName = !string.IsNullOrEmpty(profile?.CompanyName) ? profile.CompanyName : "Spokes";
            var token = config?.PublicBrandingToken ?? "default";

            // Determine start_url based on permissions
            var startUrl = "/";
            if (User.Identity?.IsAuthenticated == true)
            {
                var employee = _userService.GetEmployee(User);
                bool hasChatPermission = false;
                if (employee != null)
                {
                    hasChatPermission = employee.IsActive && !employee.IsSuspended && !employee.IsBanned && 
                                        (employee.IsAdmin || employee.HasPermission(AppPermissions.Chat.Use));
                }

                if (hasChatPermission)
                {
                    startUrl = "/chat";
                }
            }

            var manifest = new
            {
                name = appName,
                short_name = shortName,
                start_url = startUrl,
                display = "standalone",
                background_color = "#1e1e2d",
                theme_color = "#1e1e2d",
                icons = new[]
                {
                    new
                    {
                        src = $"/branding/{token}/icon.png",
                        sizes = "192x192",
                        type = "image/png"
                    },
                    new
                    {
                        src = $"/branding/{token}/icon.png",
                        sizes = "512x512",
                        type = "image/png"
                    }
                },
                spokes_version = Spokes_Server.Core.Services.Licensing.LicenseValidationService.AppVersion
            };

            return Content(System.Text.Json.JsonSerializer.Serialize(manifest), "application/manifest+json");
        }
    }
}



