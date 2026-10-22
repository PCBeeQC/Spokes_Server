using SkiaSharp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication;

public class ShortcutIconService
{
    private readonly IConfiguration _config;
    private readonly AvatarGeneratorService _avatarGenerator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ShortcutIconService>? _logger;

    public ShortcutIconService(
        IWebHostEnvironment env,
        IConfiguration config,
        AvatarGeneratorService avatarGenerator,
        IMemoryCache cache)
        : this(env, config, avatarGenerator, cache, null)
    {
    }

    public ShortcutIconService(
        IWebHostEnvironment _,
        IConfiguration config,
        AvatarGeneratorService avatarGenerator,
        IMemoryCache cache,
        ILogger<ShortcutIconService>? logger)
    {
        _config = config;
        _avatarGenerator = avatarGenerator;
        _cache = cache;
        _logger = logger;
    }

    public string GetShortcutIconBase64(Employee? employee)
    {
        if (employee == null) return string.Empty;

        // Cache key based on AvatarVersion allows instant auto-invalidation when they change avatars
        string cacheKey = $"ShortcutIcon_{employee.Id}_{employee.AvatarVersion}";

        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(12);

            byte[]? originalBytes = null;

            if (!string.IsNullOrEmpty(employee.AvatarFile))
            {
                try
                {
                    var dataPath = _config["DataPath"] ?? "Data";
                    var path = Path.Combine(dataPath, "Employees", employee.Id, employee.AvatarFile);
                    if (File.Exists(path))
                    {
                        originalBytes = File.ReadAllBytes(path);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ShortcutIconService] Failed to read avatar file for employee {EmployeeId}: {Message}", employee.Id, ex.Message);
                }
            }

            if (originalBytes == null)
            {
                originalBytes = _avatarGenerator.GenerateAvatar(employee.FirstName, employee.LastName, employee.ProfileColor);
            }

            return ResizeAndEncodeBase64(originalBytes, 192);
        }) ?? string.Empty;
    }

    public virtual string GetCompanyLogoBase64(string? logoBase64, int size = 192)
    {
        if (string.IsNullOrEmpty(logoBase64)) return string.Empty;

        string cacheKey = $"ShortcutIcon_Company_{size}_{logoBase64.GetHashCode()}";

        return _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromHours(12);

            try
            {
                if (DataUriHelper.TryParse(logoBase64, out var originalBytes, out _))
                {
                    return ResizeAndEncodeBase64(originalBytes, size);
                }
                return string.Empty;
            }
            catch { return string.Empty; }
        }) ?? string.Empty;
    }

    private string ResizeAndEncodeBase64(byte[] imageBytes, int size)
    {
        try
        {
            using var originalBitmap = SKBitmap.Decode(imageBytes);
            if (originalBitmap == null) return string.Empty;

            var imageInfo = new SKImageInfo(size, size);
            using var resizedBitmap = originalBitmap.Resize(imageInfo, new SKSamplingOptions(SKFilterMode.Linear));
            if (resizedBitmap == null) return string.Empty;

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }
        catch { return string.Empty; }
    }
}
