using System;
using System.IO;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;

namespace Spokes_Server.Tests.Controllers;

public class PublicBrandingControllerTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly string _webRootPath;
    private readonly PublicBrandingController _controller;

    public PublicBrandingControllerTests()
    {
        _webRootPath = Path.Combine(Path.GetTempPath(), $"Spokes_Test_WebRoot_{Guid.NewGuid()}");
        Directory.CreateDirectory(_webRootPath);

        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        // Set PublicBrandingToken to empty by default so fallback "default" token is valid for general tests
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = string.Empty;
        _db.SystemConfigs.Save(config);

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.WebRootPath).Returns(_webRootPath);

        _controller = new PublicBrandingController(_db, mockEnv.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_webRootPath))
        {
            try
            {
                Directory.Delete(_webRootPath, true);
            }
            catch { }
        }
        base.Dispose();
    }

    #region Token Validation Tests

    [Theory]
    [InlineData("invalid")]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData("null")]
    public void GetLogo_InvalidToken_WhenPublicBrandingTokenNotConfigured_ReturnsUnauthorized(string token)
    {
        // Arrange: SystemConfig.PublicBrandingToken is null by default
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = null;
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetLogo(token);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("wrong-token")]
    [InlineData("")]
    public void GetLogo_MismatchedToken_WhenPublicBrandingTokenConfigured_ReturnsUnauthorized(string token)
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = "custom-brand-token-123";
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetLogo(token);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void GetLogo_MatchingCustomToken_WhenPublicBrandingTokenConfigured_DoesNotReturnUnauthorized()
    {
        // Arrange
        var customToken = "secure-branding-token";
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = customToken;
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetLogo(customToken);

        // Assert: When logo is not set, returns DefaultIcon (PhysicalFileResult), not Unauthorized
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("wrong")]
    [InlineData("")]
    public void GetIcon_InvalidToken_WhenPublicBrandingTokenNotConfigured_ReturnsUnauthorized(string token)
    {
        // Arrange: default token is "default"
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = null;
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetIcon(token);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("wrong-token")]
    public void GetIcon_MismatchedToken_WhenPublicBrandingTokenConfigured_ReturnsUnauthorized(string token)
    {
        // Arrange
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = "custom-brand-token-123";
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetIcon(token);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public void GetIcon_MatchingCustomToken_WhenPublicBrandingTokenConfigured_DoesNotReturnUnauthorized()
    {
        // Arrange
        var customToken = "secure-branding-token";
        var config = _db.SystemConfigs.Get();
        config.PublicBrandingToken = customToken;
        _db.SystemConfigs.Save(config);

        // Act
        var result = _controller.GetIcon(customToken);

        // Assert: When icon is not set, returns DefaultIcon (PhysicalFileResult)
        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
    }

    #endregion

    #region GetLogo Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    public void GetLogo_NullOrEmptyOrLiteralNullLogo_ReturnsDefaultIconPhysicalFile(string? logoBase64)
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = logoBase64!;
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
        Assert.Equal("image/png", physicalResult.ContentType);
    }

    [Fact]
    public void GetLogo_ValidPngDataUri_ReturnsFileContentResultWithPngAndCspHeader()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-png-image-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = $"data:image/png;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.True(_controller.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetLogo_ValidJpegDataUri_ReturnsFileContentResultWithJpeg()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-jpeg-image-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = $"data:image/jpeg;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetLogo_ValidWebpDataUri_ReturnsFileContentResultWithWebp()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-webp-image-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = $"data:image/webp;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/webp", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/javascript")]
    public void GetLogo_NonRasterOrDangerousMimeType_SanitizesToPngForXssMitigation(string mimeType)
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-image-bytes");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = $"data:{mimeType};base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetLogo_DataUriWithoutComma_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = "data:image/png;base64;invalid-no-comma";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    [Fact]
    public void GetLogo_DataUriWithMultipleCommas_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = "data:image/png;base64,part1,part2";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    [Fact]
    public void GetLogo_CorruptedBase64Data_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.LogoBase64 = "data:image/png;base64,!!!invalid-base64-bytes!!!";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetLogo("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    #endregion

    #region GetIcon Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    public void GetIcon_NullOrEmptyOrLiteralNullIcon_ReturnsDefaultIconPhysicalFile(string? iconBase64)
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = iconBase64!;
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
        Assert.Equal("image/png", physicalResult.ContentType);
    }

    [Fact]
    public void GetIcon_ValidPngDataUri_ReturnsFileContentResultWithPngAndCspHeader()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-icon-png-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = $"data:image/png;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.True(_controller.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetIcon_ValidJpegDataUri_ReturnsFileContentResultWithJpeg()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-icon-jpeg-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = $"data:image/jpeg;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetIcon_ValidWebpDataUri_ReturnsFileContentResultWithWebp()
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-icon-webp-content");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = $"data:image/webp;base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/webp", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/bmp")]
    [InlineData("application/octet-stream")]
    public void GetIcon_NonStandardMimeType_SanitizesToPngForXssMitigation(string mimeType)
    {
        // Arrange
        var imageBytes = Encoding.UTF8.GetBytes("fake-icon-bytes");
        var base64Data = Convert.ToBase64String(imageBytes);
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = $"data:{mimeType};base64,{base64Data}";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", fileResult.ContentType);
        Assert.Equal(imageBytes, fileResult.FileContents);
        Assert.Equal("default-src 'none'; img-src 'self' data:;", _controller.Response.Headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public void GetIcon_DataUriWithoutComma_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = "data:image/png;base64;no-comma";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    [Fact]
    public void GetIcon_DataUriWithMultipleCommas_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = "data:image/png;base64,part1,part2";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    [Fact]
    public void GetIcon_CorruptedBase64Data_ReturnsDefaultIcon()
    {
        // Arrange
        var profile = _db.CompanyProfile.Get();
        profile.IconBase64 = "data:image/png;base64,***corrupted-base64***";
        _db.CompanyProfile.Save(profile);

        // Act
        var result = _controller.GetIcon("default");

        // Assert
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(Path.Combine(_webRootPath, "default-icon-192.png"), physicalResult.FileName);
    }

    #endregion
}
