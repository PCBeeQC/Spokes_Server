using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Communication;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Controllers;

[Route("spokesapi/[controller]")]
[ApiController]
[AllowAnonymous]
public class MediaController : SpokesControllerBase
{
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmployeeRepository _employeeRepo;
    private readonly IWebHostEnvironment _env;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly AvatarGeneratorService _avatarGenerator;
    private readonly UserService _userService;
    private readonly IFileService _fileService;
    private readonly IFileAuthorizationService _fileAuthService;
    private readonly ILogger<MediaController>? _logger;
    private readonly IMemoryCache? _memoryCache;

    [ActivatorUtilitiesConstructor]
    public MediaController(
        CompanyProfileRepository companyProfile,
        EmployeeRepository employeeRepo,
        IWebHostEnvironment env,
        IDataProtectionProvider dataProtection,
        AvatarGeneratorService avatarGenerator,
        UserService userService,
        IFileService fileService,
        IFileAuthorizationService fileAuthService,
        ILogger<MediaController>? logger = null,
        IMemoryCache? memoryCache = null)
    {
        _companyProfile = companyProfile;
        _employeeRepo = employeeRepo;
        _env = env;
        _dataProtection = dataProtection;
        _avatarGenerator = avatarGenerator;
        _userService = userService;
        _fileService = fileService;
        _fileAuthService = fileAuthService;
        _logger = logger;
        _memoryCache = memoryCache;
    }

    [HttpGet("Icon")]
    [HttpHead("Icon")]
    [Spokes_Server.Core.Security.AllowDuringSetup]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)] // Cache for 1 hour locally
    public IActionResult GetIcon([FromQuery] int? h = null)
    {
        if (HttpContext?.Response?.Headers != null)
        {
            HttpContext.Response.Headers["Priority"] = "u=3, i=?0";
        }

        var profile = _companyProfile.Get();
        if (DataUriHelper.TryParseImage(profile?.IconBase64, out var bytes, out var mimeType))
        {
            var (outBytes, outMime) = ResolveSizedBrandingImage("icon", profile?.IconVersion ?? 0, bytes, mimeType, h);
            return File(outBytes, outMime);
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

    [HttpGet("Logo")]
    [HttpHead("Logo")]
    [Spokes_Server.Core.Security.AllowDuringSetup]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public IActionResult GetLogo([FromQuery] int? h = null)
    {
        if (HttpContext?.Response?.Headers != null)
        {
            HttpContext.Response.Headers["Priority"] = "u=3, i=?0";
        }

        var profile = _companyProfile.Get();
        if (DataUriHelper.TryParseImage(profile?.LogoBase64, out var bytes, out var mimeType))
        {
            var (outBytes, outMime) = ResolveSizedBrandingImage("logo", profile?.LogoVersion ?? 0, bytes, mimeType, h);
            return File(outBytes, outMime);
        }

        // Fallback to icon if no custom logo is uploaded
        return GetIcon(h);
    }

    private (byte[] Bytes, string MimeType) ResolveSizedBrandingImage(
        string kind,
        int version,
        byte[] originalBytes,
        string originalMimeType,
        int? requestedHeight)
    {
        if (!requestedHeight.HasValue || requestedHeight.Value <= 0)
        {
            return (originalBytes, originalMimeType);
        }

        if (originalMimeType.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
            originalMimeType.Contains("gif", StringComparison.OrdinalIgnoreCase) ||
            originalMimeType.Contains("icon", StringComparison.OrdinalIgnoreCase))
        {
            return (originalBytes, originalMimeType);
        }

        int targetHeight = Math.Clamp(requestedHeight.Value, 32, 512);
        string contentHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        string cacheKey = $"BrandingMedia_{kind}_v{version}_h{targetHeight}_{contentHash}";

        if (_memoryCache != null && _memoryCache.TryGetValue<(byte[] Bytes, string MimeType)>(cacheKey, out var cached))
        {
            return cached;
        }

        var computed = EncodeResizedWebpOrOriginal(originalBytes, originalMimeType, targetHeight);
        _memoryCache?.Set(cacheKey, computed, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(12)
        });
        return computed;
    }

    private static (byte[] Bytes, string MimeType) EncodeResizedWebpOrOriginal(
        byte[] originalBytes,
        string originalMimeType,
        int targetHeight)
    {
        try
        {
            using var originalBitmap = SKBitmap.Decode(originalBytes);
            if (originalBitmap == null || originalBitmap.Width <= 0 || originalBitmap.Height <= 0)
            {
                return (originalBytes, originalMimeType);
            }

            SKBitmap? resizedBitmap = null;
            try
            {
                SKBitmap sourceForEncode = originalBitmap;
                if (originalBitmap.Height > targetHeight)
                {
                    int targetWidth = Math.Max(1, (int)Math.Round(originalBitmap.Width * ((double)targetHeight / originalBitmap.Height)));
                    resizedBitmap = originalBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (resizedBitmap != null)
                    {
                        sourceForEncode = resizedBitmap;
                    }
                }

                using var image = SKImage.FromBitmap(sourceForEncode);
                using var encoded = image.Encode(SKEncodedImageFormat.Webp, 85);
                if (encoded != null)
                {
                    var webpBytes = encoded.ToArray();
                    if (webpBytes.Length > 0 && (resizedBitmap != null || webpBytes.Length < originalBytes.Length))
                    {
                        return (webpBytes, "image/webp");
                    }
                }
            }
            finally
            {
                resizedBitmap?.Dispose();
            }
        }
        catch
        {
            // Fall back to original bytes on decode/encode failure
        }

        return (originalBytes, originalMimeType);
    }

    [HttpGet("Avatar/{userId}")]
    [HttpHead("Avatar/{userId}")]
    [AllowAnonymous]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Client)] // Cache for 1 year locally
    public IActionResult GetAvatar(string userId, [FromQuery] string? t)
    {
        if (HttpContext?.Response?.Headers != null)
        {
            HttpContext.Response.Headers["Priority"] = "u=3, i=?0";
        }
        Employee? currentUser = null;
        if (User?.Identity?.IsAuthenticated == true)
        {
            currentUser = _userService.GetEmployee(User);
        }

        bool isAuthorized = currentUser != null && currentUser.IsActive && !currentUser.IsBanned;
        bool authorizedViaPushToken = false;

        // Validate token for push workers
        if (!isAuthorized && !string.IsNullOrEmpty(t))
        {
            try
            {
                var protector = _dataProtection.CreateProtector("AvatarPushToken");
                var decrypted = protector.Unprotect(t);
                var parts = decrypted.Split('|');
                var safeUserId = userId.Replace("|", string.Empty);
                if (parts.Length == 2 &&
                    (string.Equals(parts[0], userId, StringComparison.Ordinal) ||
                     string.Equals(parts[0], safeUserId, StringComparison.Ordinal)) &&
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiryTicks) &&
                    new DateTime(expiryTicks, DateTimeKind.Utc) > DateTime.UtcNow)
                {
                    isAuthorized = true;
                    authorizedViaPushToken = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Avatar token validation failed");
            }
        }

        if (!isAuthorized)
        {
            return UnauthorizedError("Authentication required to access avatar.", "UNAUTHORIZED");
        }

        var employee = _employeeRepo.GetById(userId);
        if (employee == null)
        {
            // Fallback to company icon if no avatar found
            return GetIcon();
        }

        // Check file access authorization unless authorized by a signed push token
        if (!authorizedViaPushToken && (currentUser == null || !_fileAuthService.CanAccess("avatars", userId, currentUser)))
        {
            return ForbiddenError("Access to avatar forbidden.", "FORBIDDEN");
        }

        if (!string.IsNullOrEmpty(employee.AvatarFile))
        {
            try
            {
                var safeFileName = Path.GetFileName(employee.AvatarFile);
                if (safeFileName != employee.AvatarFile || safeFileName.Contains("..") || safeFileName.Contains("/") || safeFileName.Contains("\\"))
                {
                    return BadRequestError("Invalid avatar file name.");
                }

                var path = _fileService.GetAvatarPhysicalPath(employee.Id, safeFileName);
                if (System.IO.File.Exists(path))
                {
                    return PhysicalFile(Path.GetFullPath(path), "image/png");
                }
            }
            catch (ArgumentException)
            {
                return BadRequestError("Invalid avatar file name.");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[MediaController] Failed to read avatar file");
            }
        }

        try
        {
            var bytes = _avatarGenerator.GenerateAvatar(employee.FirstName, employee.LastName, employee.ProfileColor);
            return File(bytes, "image/png");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MediaController] Failed to generate dynamic avatar");
        }

        // Fallback to company icon if no avatar found
        return GetIcon();
    }
}
