using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using SkiaSharp;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class ShortcutIconServiceTests : TestDataTestBase
{
    private readonly Mock<IWebHostEnvironment> _mockEnv;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly IMemoryCache _cache;
    private readonly AvatarGeneratorService _avatarGenerator;
    private readonly ShortcutIconService _service;

    public ShortcutIconServiceTests()
    {
        _mockEnv = new Mock<IWebHostEnvironment>();
        _mockEnv.Setup(e => e.WebRootPath).Returns(_testDataPath);

        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _avatarGenerator = new AvatarGeneratorService(_mockEnv.Object);
        _service = new ShortcutIconService(_mockEnv.Object, _mockConfig.Object, _avatarGenerator, _cache);
    }

    public override void Dispose()
    {
        _cache.Dispose();
        base.Dispose();
    }

    private static byte[] CreateTestPng(int width = 32, int height = 32)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    #region GetShortcutIconBase64 Tests

    [Fact]
    public void GetShortcutIconBase64_NullEmployee_ReturnsEmptyString()
    {
        var result = _service.GetShortcutIconBase64(null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetShortcutIconBase64_EmployeeWithNoAvatarFile_GeneratesAvatarAndReturnsDataUri()
    {
        var employee = new Employee
        {
            Id = "emp-no-avatar",
            FirstName = "John",
            LastName = "Doe",
            ProfileColor = "#673ab7",
            AvatarFile = null,
            AvatarVersion = 1
        };

        var result = _service.GetShortcutIconBase64(employee);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
        Assert.True(result.Length > "data:image/png;base64,".Length);
    }

    [Fact]
    public void GetShortcutIconBase64_EmployeeWithEmptyAvatarFile_GeneratesAvatarAndReturnsDataUri()
    {
        var employee = new Employee
        {
            Id = "emp-empty-avatar",
            FirstName = "Alice",
            LastName = "Smith",
            ProfileColor = "#e91e63",
            AvatarFile = string.Empty,
            AvatarVersion = 1
        };

        var result = _service.GetShortcutIconBase64(employee);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
    }

    [Fact]
    public void GetShortcutIconBase64_ValidAvatarFileOnDisk_ReadsDiskFileAndReturnsDataUri()
    {
        var employee = new Employee
        {
            Id = "emp-disk-avatar",
            FirstName = "Jane",
            LastName = "Doe",
            AvatarFile = "avatar.png",
            AvatarVersion = 1
        };

        var empDir = Path.Combine(_testDataPath, "Employees", employee.Id);
        Directory.CreateDirectory(empDir);
        var avatarFilePath = Path.Combine(empDir, employee.AvatarFile);
        File.WriteAllBytes(avatarFilePath, CreateTestPng(64, 64));

        var result = _service.GetShortcutIconBase64(employee);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
    }

    [Fact]
    public void GetShortcutIconBase64_AvatarFileMissingOnDisk_FallsBackToAvatarGenerator()
    {
        var employee = new Employee
        {
            Id = "emp-missing-disk-avatar",
            FirstName = "Bob",
            LastName = "Ross",
            AvatarFile = "nonexistent.png",
            AvatarVersion = 1
        };

        var result = _service.GetShortcutIconBase64(employee);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
    }

    [Fact]
    public void GetShortcutIconBase64_CachingBehavior_SubsequentCallReturnsCachedString()
    {
        var employee = new Employee
        {
            Id = "emp-cached",
            FirstName = "Cached",
            LastName = "User",
            AvatarVersion = 1
        };

        var firstResult = _service.GetShortcutIconBase64(employee);
        var secondResult = _service.GetShortcutIconBase64(employee);

        Assert.False(string.IsNullOrEmpty(firstResult));
        Assert.Equal(firstResult, secondResult);

        var cacheKey = $"ShortcutIcon_{employee.Id}_{employee.AvatarVersion}";
        Assert.True(_cache.TryGetValue(cacheKey, out string? cachedValue));
        Assert.Equal(firstResult, cachedValue);
    }

    [Fact]
    public void GetShortcutIconBase64_CacheInvalidation_ChangingAvatarVersionProducesCacheMissAndRecalculates()
    {
        var employee = new Employee
        {
            Id = "emp-versioned",
            FirstName = "Version",
            LastName = "User",
            ProfileColor = "#ff0000",
            AvatarVersion = 1
        };

        var firstResult = _service.GetShortcutIconBase64(employee);
        var firstKey = $"ShortcutIcon_{employee.Id}_1";
        Assert.True(_cache.TryGetValue(firstKey, out string? cachedFirst));
        Assert.Equal(firstResult, cachedFirst);

        // Change profile color and advance version to trigger recalculation
        employee.ProfileColor = "#00ff00";
        employee.AvatarVersion = 2;

        var secondKey = $"ShortcutIcon_{employee.Id}_2";
        Assert.False(_cache.TryGetValue(secondKey, out _));

        var secondResult = _service.GetShortcutIconBase64(employee);

        Assert.True(_cache.TryGetValue(secondKey, out string? cachedSecond));
        Assert.Equal(secondResult, cachedSecond);
        Assert.NotEqual(firstResult, secondResult);
    }

    #endregion

    #region GetCompanyLogoBase64 Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetCompanyLogoBase64_NullOrEmpty_ReturnsEmptyString(string? logo)
    {
        var result = _service.GetCompanyLogoBase64(logo);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetCompanyLogoBase64_ValidRawBase64_DecodesResizesAndReturnsDataUri()
    {
        var pngBytes = CreateTestPng(50, 50);
        var rawBase64 = Convert.ToBase64String(pngBytes);

        var result = _service.GetCompanyLogoBase64(rawBase64);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
        Assert.True(result.Length > "data:image/png;base64,".Length);
    }

    [Fact]
    public void GetCompanyLogoBase64_ValidDataUriPrefix_StripsPrefixDecodesResizesAndReturnsDataUri()
    {
        var pngBytes = CreateTestPng(50, 50);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(pngBytes);

        var result = _service.GetCompanyLogoBase64(dataUri, 128);

        Assert.NotNull(result);
        Assert.StartsWith("data:image/png;base64,", result);
    }

    [Theory]
    [InlineData("not_a_valid_base64_string!!!")]
    [InlineData("AQIDBA==")] // Valid base64, but corrupt/non-image bytes
    public void GetCompanyLogoBase64_InvalidOrCorruptBase64_HandlesExceptionAndReturnsEmptyString(string corruptLogo)
    {
        var result = _service.GetCompanyLogoBase64(corruptLogo);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetCompanyLogoBase64_CachingBehavior_SubsequentCallReturnsCachedString()
    {
        var pngBytes = CreateTestPng(40, 40);
        var rawBase64 = Convert.ToBase64String(pngBytes);
        int customSize = 128;

        var firstResult = _service.GetCompanyLogoBase64(rawBase64, customSize);
        var secondResult = _service.GetCompanyLogoBase64(rawBase64, customSize);

        Assert.False(string.IsNullOrEmpty(firstResult));
        Assert.Equal(firstResult, secondResult);

        var cacheKey = $"ShortcutIcon_Company_{customSize}_{rawBase64.GetHashCode()}";
        Assert.True(_cache.TryGetValue(cacheKey, out string? cachedValue));
        Assert.Equal(firstResult, cachedValue);
    }

    #endregion
}
