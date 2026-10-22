using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SkiaSharp;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Content;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Content;

public class CompanyBrandingServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _repo;
    private readonly ImageProcessingService _imageService;
    private readonly CompanyBrandingService _brandingService;

    public CompanyBrandingServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Branding_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var persistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(persistenceLogger.Object);

        _repo = new CompanyProfileRepository(_persistence, configMock.Object);
        _imageService = new ImageProcessingService();
        _brandingService = new CompanyBrandingService(_repo, _imageService);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void OptimizeBranding_WhenNullProfile_ReturnsFalse()
    {
        Assert.False(_brandingService.OptimizeBranding(null!));
    }

    [Fact]
    public void OptimizeBranding_DownsamplesOversizedIcon_AndIncrementsVersion()
    {
        using var bitmap = new SKBitmap(500, 500);
        var random = new Random(42);
        for (int x = 0; x < 500; x++)
        {
            for (int y = 0; y < 500; y++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var rawBase64 = Convert.ToBase64String(data.ToArray());
        var dataUri = $"data:image/png;base64,{rawBase64}";

        Assert.True(dataUri.Length > 50_000);

        var profile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };

        var changed = _brandingService.OptimizeBranding(profile);

        Assert.True(changed);
        Assert.Equal(1, profile.IconVersion);
        Assert.True(profile.IconBase64.Length < dataUri.Length);

        var commaIdx = profile.IconBase64.IndexOf(',');
        var optBytes = Convert.FromBase64String(profile.IconBase64[(commaIdx + 1)..]);
        using var optBitmap = SKBitmap.Decode(optBytes);

        Assert.NotNull(optBitmap);
        Assert.True(optBitmap.Width <= 256);
        Assert.True(optBitmap.Height <= 256);
    }

    [Fact]
    public void OptimizeBranding_DoesNotTouchSmallIcon()
    {
        using var bitmap = new SKBitmap(32, 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Green);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var rawBase64 = Convert.ToBase64String(data.ToArray());
        var dataUri = $"data:image/png;base64,{rawBase64}";

        Assert.True(dataUri.Length < 50_000);

        var profile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };

        var changed = _brandingService.OptimizeBranding(profile);

        Assert.False(changed);
        Assert.Equal(0, profile.IconVersion);
        Assert.Equal(dataUri, profile.IconBase64);
    }

    [Fact]
    public void SaveWithOptimizedBranding_OptimizesAndPersistsProfile()
    {
        using var bitmap = new SKBitmap(400, 400);
        var random = new Random(99);
        for (int x = 0; x < 400; x++)
        {
            for (int y = 0; y < 400; y++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(data.ToArray())}";

        var profile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Saved Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };

        _brandingService.SaveWithOptimizedBranding(profile);

        var saved = _repo.Get();
        Assert.Equal("Saved Company", saved.CompanyName);
        Assert.Equal(1, saved.IconVersion);
        Assert.True(saved.IconBase64.Length < dataUri.Length);
    }
}
