using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SkiaSharp;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class CompanyProfileRepositoryTests : TestDataTestBase
{
    private readonly CompanyProfileRepository _repository;
    private readonly DiskPersistenceService _writer;

    public CompanyProfileRepositoryTests()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _repository = new CompanyProfileRepository(_writer, mockConfig.Object);
    }

    public override void Dispose()
    {
        base.Dispose();
        _writer.Dispose();
    }

    [Fact]
    public void Get_CreatesDefaultProfile_WhenEmpty()
    {
        var profile = _repository.Get();

        Assert.NotNull(profile);
        Assert.Equal("GlobalProfile", profile.Id);
        Assert.Equal("My Spokes", profile.CompanyName);
    }

    [Fact]
    public void Get_DownsamplesOversizedIcon_AndIncrementsVersion()
    {
        // Generate an image with pseudo-random noise to ensure PNG payload > 50KB
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
        var bytes = data.ToArray();
        var rawBase64 = Convert.ToBase64String(bytes);
        var dataUri = $"data:image/png;base64,{rawBase64}";

        Assert.True(dataUri.Length > 50_000, $"Generated test image base64 length was {dataUri.Length}, expected > 50000");

        var initialProfile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };
        _repository.Save(initialProfile);

        // Fetch via Get() which triggers EnsureOptimizedBranding
        var profile = _repository.Get();

        Assert.Equal(1, profile.IconVersion);
        Assert.True(profile.IconBase64.Length < dataUri.Length, "Icon base64 was not downsampled");

        // Decode the downsampled image to verify dimensions <= 256px
        var commaIdx = profile.IconBase64.IndexOf(',');
        var optBytes = Convert.FromBase64String(profile.IconBase64[(commaIdx + 1)..]);
        using var optBitmap = SKBitmap.Decode(optBytes);

        Assert.NotNull(optBitmap);
        Assert.True(optBitmap.Width <= 256, $"Width was {optBitmap.Width}, expected <= 256");
        Assert.True(optBitmap.Height <= 256, $"Height was {optBitmap.Height}, expected <= 256");
    }

    [Fact]
    public void Get_DoesNotTouchSmallIcon()
    {
        // Small 32x32 image well below 50KB
        using var bitmap = new SKBitmap(32, 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Green);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var rawBase64 = Convert.ToBase64String(data.ToArray());
        var dataUri = $"data:image/png;base64,{rawBase64}";

        Assert.True(dataUri.Length < 50_000);

        var initialProfile = new CompanyProfile
        {
            Id = "GlobalProfile",
            CompanyName = "Test Company",
            IconBase64 = dataUri,
            IconVersion = 0
        };
        _repository.Save(initialProfile);

        var profile = _repository.Get();

        Assert.Equal(0, profile.IconVersion);
        Assert.Equal(dataUri, profile.IconBase64);
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new CompanyProfileRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetFilePath_SavesInSettingsCompanyJson()
    {
        var profile = new CompanyProfile { Id = "GlobalProfile", CompanyName = "Path Company" };
        _repository.Save(profile);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataPath, "Settings", "company.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Get_ReturnsExistingProfileWithoutRecreating()
    {
        var profile = new CompanyProfile { Id = "GlobalProfile", CompanyName = "Custom Name", AddressCity = "Montreal" };
        _repository.Save(profile);

        var retrieved = _repository.Get();
        Assert.Equal("Custom Name", retrieved.CompanyName);
        Assert.Equal("Montreal", retrieved.AddressCity);
    }
}
