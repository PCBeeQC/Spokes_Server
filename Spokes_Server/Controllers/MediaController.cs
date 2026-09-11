using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Microsoft.AspNetCore.DataProtection;

namespace Spokes_Server.Controllers
{
    [Route("spokesapi/[controller]")]
    [ApiController]
    public class MediaController : SpokesControllerBase
    {
        private readonly CompanyProfileRepository _companyProfile;
        private readonly EmployeeRepository _employeeRepo;
        private readonly IWebHostEnvironment _env;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly AvatarGeneratorService _avatarGenerator;
        private readonly IConfiguration _config;
        private readonly IEnumerable<IFileAccessProvider> _providers;
        private readonly UserService _userService;

        public MediaController(
            CompanyProfileRepository companyProfile,
            EmployeeRepository employeeRepo,
            IWebHostEnvironment env,
            IDataProtectionProvider dataProtection,
            AvatarGeneratorService avatarGenerator,
            IConfiguration config,
            IEnumerable<IFileAccessProvider> providers,
            UserService userService)
        {
            _companyProfile = companyProfile;
            _employeeRepo = employeeRepo;
            _env = env;
            _dataProtection = dataProtection;
            _avatarGenerator = avatarGenerator;
            _config = config;
            _providers = providers;
            _userService = userService;
        }

        [HttpGet("Icon")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)] // Cache for 1 hour locally
        public IActionResult GetIcon([FromQuery] string? t = null)
        {
            bool isAuthorized = false;
            if (User?.Identity?.IsAuthenticated == true)
            {
                var currentUser = _userService.GetEmployee(User);
                isAuthorized = currentUser != null && currentUser.IsActive && !currentUser.IsSuspended && !currentUser.IsBanned;
            }

            // Validate token for push workers requesting the server icon
            if (!isAuthorized && !string.IsNullOrEmpty(t))
            {
                try
                {
                    var protector = _dataProtection.CreateProtector("AvatarPushToken");
                    var decrypted = protector.Unprotect(t);
                    var parts = decrypted.Split('|');
                    // As long as the token has not expired, it is considered valid to get the company icon.
                    if (parts.Length == 2 && new DateTime(long.Parse(parts[1])) > DateTime.UtcNow)
                    {
                        isAuthorized = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaController] Icon token validation failed: {ex.Message}");
                }
            }

            if (!isAuthorized)
            {
                return Unauthorized();
            }

            var profile = _companyProfile.Get();

            if (!string.IsNullOrEmpty(profile?.IconBase64) && profile.IconBase64.Contains(","))
            {
                try
                {
                    // Extract Base64 part
                    var base64Data = profile.IconBase64.Substring(profile.IconBase64.IndexOf(",") + 1);
                    var bytes = Convert.FromBase64String(base64Data);
                    return File(bytes, "image/png");
                }
                catch (Exception ex)
                {
                    // If parsing fails, fall back to default
                    Console.WriteLine($"[MediaController] Failed to parse custom icon: {ex.Message}");
                }
            }

            // Fallback to default-icon-192.png
            var defaultIconPath = Path.Combine(_env.WebRootPath, "default-icon-192.png");
            if (System.IO.File.Exists(defaultIconPath))
            {
                return PhysicalFile(defaultIconPath, "image/png");
            }

            // Last resort: standard favicon
            return PhysicalFile(Path.Combine(_env.WebRootPath, "favicon.ico"), "image/x-icon");
        }

        [HttpGet("Avatar/{userId}")]
        [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Client)] // Cache for 1 year locally
        public async Task<IActionResult> GetAvatar(string userId, [FromQuery] string? t)
        {
            Employee? currentUser = null;
            if (User?.Identity?.IsAuthenticated == true)
            {
                currentUser = _userService.GetEmployee(User);
            }

            bool isAuthorized = currentUser != null && currentUser.IsActive && !currentUser.IsSuspended && !currentUser.IsBanned;

            // Validate token for push workers
            if (!isAuthorized && !string.IsNullOrEmpty(t))
            {
                try
                {
                    var protector = _dataProtection.CreateProtector("AvatarPushToken");
                    var decrypted = protector.Unprotect(t);
                    var parts = decrypted.Split('|');
                    if (parts.Length == 2 && parts[0] == userId && new DateTime(long.Parse(parts[1])) > DateTime.UtcNow)
                    {
                        isAuthorized = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaController] Avatar token validation failed: {ex.Message}");
                }
            }

            if (!isAuthorized)
            {
                return Unauthorized();
            }

            var employee = _employeeRepo.GetById(userId);
            if (employee == null)
            {
                // Fallback to company icon if no avatar found
                return GetIcon(t);
            }

            // Check file access provider
            var provider = _providers.FirstOrDefault(p => p.Category.Equals("avatars", StringComparison.OrdinalIgnoreCase));
            if (provider != null && currentUser != null)
            {
                bool canAccess = await provider.CanAccessAsync(currentUser, userId);
                if (!canAccess) return Forbid();
            }

            if (!string.IsNullOrEmpty(employee.AvatarFile))
            {
                try
                {
                    var safeFileName = Path.GetFileName(employee.AvatarFile);
                    if (safeFileName != employee.AvatarFile || safeFileName.Contains("..") || safeFileName.Contains("/") || safeFileName.Contains("\\"))
                    {
                        return BadRequest("Invalid avatar file name.");
                    }

                    var dataPath = _config["DataPath"] ?? "Data";
                    var path = Path.Combine(dataPath, "Employees", employee.Id, safeFileName);
                    if (System.IO.File.Exists(path))
                    {
                        return PhysicalFile(Path.GetFullPath(path), "image/png");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MediaController] Failed to read avatar file: {ex.Message}");
                }
            }

            if (employee != null)
            {
                try
                {
                    var bytes = _avatarGenerator.GenerateAvatar(employee.FirstName, employee.LastName, employee.ProfileColor);
                    return File(bytes, "image/png");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MediaController] Failed to generate dynamic avatar: {ex.Message}");
                }
            }

            // Fallback to company icon if no avatar found
            return GetIcon(t);
        }
    }
}


