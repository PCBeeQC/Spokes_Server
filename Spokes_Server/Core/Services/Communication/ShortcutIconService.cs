using System;
using System.IO;
using SkiaSharp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication;

public class ShortcutIconService
{
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly AvatarGeneratorService _avatarGenerator;
    private readonly IMemoryCache _cache;

    public ShortcutIconService(IWebHostEnvironment env, IConfiguration config, AvatarGeneratorService avatarGenerator, IMemoryCache cache)
    {
        _env = env;
        _config = config;
        _avatarGenerator = avatarGenerator;
        _cache = cache;
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
                catch { }
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
                var base64Data = logoBase64.Contains(",") ? logoBase64.Substring(logoBase64.IndexOf(",") + 1) : logoBase64;
                var originalBytes = Convert.FromBase64String(base64Data);
                return ResizeAndEncodeBase64(originalBytes, size);
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
            using var resizedBitmap = originalBitmap.Resize(imageInfo, SKFilterQuality.Medium);
            if (resizedBitmap == null) return string.Empty;

            using var image = SKImage.FromBitmap(resizedBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }
        catch { return string.Empty; }
    }
}
