using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Reflection;
using SkiaSharp;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Controllers;

public class FileControllerTests : TestDataTestBase
{
    private FileController _controller;
    private readonly FileAuthorizationService _fileAuthService;
    private readonly UserService _userService;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IFileAccessProvider> _mockAccessProvider;
    private readonly Mock<IFileAccessProvider> _mockAlbumsAccessProvider;
    private readonly Mock<IFileAccessProvider> _mockChatAccessProvider;
    private readonly EmployeeRepository _employeesRepo;
    private readonly OpenIdAccountRepository _openIdRepo;
    private readonly AlbumRepository _albumsRepo;
    private readonly ChatChannelRepository _channelsRepo;
    private readonly CompanyProfileRepository _companyProfilesRepo;
    private readonly Mock<ICryptoService> _mockCrypto;
    private readonly ScopedKeystoreService _keystore;

    public FileControllerTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" },
            { "Spokes_DemoMode", "false" }
        }).Build();

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        var writer = new DiskPersistenceService(mockLogger.Object);

        _employeesRepo = new EmployeeRepository(writer, config);
        _openIdRepo = new OpenIdAccountRepository(writer, config);

        var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        _mockFileService = new Mock<IFileService>();
        _mockFileService.Setup(f => f.GenerateSafeName(It.IsAny<string>()))
            .Returns((string fn) => Path.GetFileName(fn));
        _mockFileService.Setup(f => f.GetContentType(It.IsAny<string>()))
            .Returns((string fn) => contentTypeProvider.TryGetContentType(fn, out var ct) ? ct : "application/octet-stream");
        _mockFileService.Setup(f => f.UploadThumbnailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(async (string cat, string ctx, Stream stream, string baseName, string? encKey, bool small) =>
            {
                var thumbName = Spokes_Server.Core.Services.Storage.FileService.GetThumbnailFileName(baseName, small);
                var folder = _mockFileService.Object.GetPhysicalPath(cat, ctx, "");
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    var fullPath = Path.Combine(folder, thumbName);
                    using var fs = new FileStream(fullPath, FileMode.Create);
                    if (!string.IsNullOrEmpty(encKey))
                    {
                        await _mockCrypto.Object.EncryptStreamAsync(stream, fs, encKey);
                    }
                    else
                    {
                        await stream.CopyToAsync(fs);
                    }
                }
                return $"/spokesapi/files/{cat}/{ctx}/{thumbName}";
            });
        _mockFileService.Setup(f => f.OpenDecryptedStream(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string path, string key) =>
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var keyBytes = Convert.FromBase64String(key);
                return new SeekableAesStream(fs, keyBytes);
            });
        _mockFileService.Setup(f => f.OpenDecryptedStream(It.IsAny<byte[]>(), It.IsAny<string>()))
            .Returns((byte[] bytes, string key) =>
            {
                var ms = new MemoryStream(bytes, writable: false);
                var keyBytes = Convert.FromBase64String(key);
                return new SeekableAesStream(ms, keyBytes);
            });
        _mockFileService.Setup(f => f.FileExists(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string cat, string ctx, string fn) =>
            {
                var path = _mockFileService.Object.GetPhysicalPath(cat, ctx, fn);
                return File.Exists(path);
            });

        _mockAccessProvider = new Mock<IFileAccessProvider>();
        _mockAccessProvider.Setup(p => p.Category).Returns("projects");
        _mockAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), It.IsAny<string>()))
            .Returns((Employee u, string c) => _mockAccessProvider.Object.CanAccess(u, c));

        _mockAlbumsAccessProvider = new Mock<IFileAccessProvider>();
        _mockAlbumsAccessProvider.Setup(p => p.Category).Returns("albums");
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), It.IsAny<string>()))
            .Returns((Employee u, string c) => _mockAlbumsAccessProvider.Object.CanAccess(u, c));

        _mockChatAccessProvider = new Mock<IFileAccessProvider>();
        _mockChatAccessProvider.Setup(p => p.Category).Returns("chat");
        _mockChatAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), It.IsAny<string>()))
            .Returns((Employee u, string c) => _mockChatAccessProvider.Object.CanAccess(u, c));

        var providers = new List<IFileAccessProvider> {
            _mockAccessProvider.Object,
            _mockAlbumsAccessProvider.Object,
            _mockChatAccessProvider.Object,
            new TempFileAccessProvider()
        };

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);

        _albumsRepo = new AlbumRepository(writer, config);
        _channelsRepo = new ChatChannelRepository(writer, config);
        _companyProfilesRepo = new CompanyProfileRepository(writer, config);
        _mockCrypto = new Mock<ICryptoService>();

        var deviceSessionsRepo = new DeviceSessionRepository(writer, config);
        deviceSessionsRepo.Save(new DeviceSession { Id = "valid_session", EmployeeId = "emp1" });
        deviceSessionsRepo.Save(new DeviceSession { Id = "revoked_session", EmployeeId = "emp1", RevokedAt = DateTime.UtcNow });

        // Setup User Claims
        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "auth0|123"),
            new Claim("SessionId", "valid_session"),
        }, "mock"));

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMemoryCache();
        services.AddSingleton<Spokes_Server.Core.Services.Security.FileTokenService>();
        var serviceProvider = services.BuildServiceProvider();

        var dataProtectionProvider = serviceProvider.GetRequiredService<IDataProtectionProvider>();
        var keystore = new ScopedKeystoreService(dataProtectionProvider);
        _keystore = keystore;

        var db = TestDatabaseFactory.Create(deviceSessionsRepo, _employeesRepo);
        var escrowService = new ServerEscrowService(db.CompanyProfile, db.ServerConfigs, _mockCrypto.Object);
        var albumService = new AlbumService(_albumsRepo, _channelsRepo, _mockCrypto.Object, escrowService, _employeesRepo, new ChatAuthorizationService(_channelsRepo, new Spokes_Server.Core.Data.Repositories.Projects.ProjectRepository(writer, config), new Spokes_Server.Core.Data.Repositories.HR.TeamRepository(writer, config), _employeesRepo));
        var keyResolvers = new IFileKeyResolver[]
        {
            new ChatFileKeyResolver(_channelsRepo, keystore, _mockCrypto.Object),
            new AlbumFileKeyResolver(_albumsRepo, albumService, keystore)
        };
        var fileAuthService = new FileAuthorizationService(
            providers,
            keyResolvers);

        _fileAuthService = fileAuthService;
        _userService = userService;

        _controller = new FileController(
            _mockFileService.Object,
            fileAuthService,
            _companyProfilesRepo,
            new Mock<ILogger<FileController>>().Object,
            userService,
            new ImageProcessingService(),
            _channelsRepo,
            config: config,
            fileTokenService: serviceProvider.GetRequiredService<FileTokenService>());

        var httpContext = new DefaultHttpContext { User = user };
        httpContext.RequestServices = serviceProvider;

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private static string? GetErrorMessage(object? resultValue)
        => (resultValue as Spokes_Server.Core.Helpers.SpokesResult)?.ErrorMessage ?? resultValue?.ToString();

    [Fact]
    public async Task GetFile_ReturnsBadRequest_OnInvalidPathSegment()
    {
        var result = await _controller.GetFile("projects", "../invalid", "file.pdf");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetFile_ReturnsUnauthorized_WhenUserNotFound()
    {
        var result = await _controller.GetFile("projects", "v1", "file.pdf");
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Authentication required.", GetErrorMessage(unauthorizedResult.Value));
    }

    [Fact]
    public async Task GetFile_ReturnsBadRequest_WhenProviderNotFound()
    {
        // Add user to repo
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        var token = CreateFileToken("unknown_category", "v1", "file.pdf");
        var result = await _controller.GetFile("unknown_category", "v1", "file.pdf", token);
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unknown file category", badRequestResult.Value.ToString());
    }

    [Fact]
    public async Task GetFile_ReturnsForbid_WhenAccessDenied()
    {
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(false);

        var token = CreateFileToken("projects", "v1", "file.pdf");
        var result = await _controller.GetFile("projects", "v1", "file.pdf", token);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task GetFile_ReturnsPhysicalFile_WhenAuthorizedAndFileExists()
    {
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "file.pdf")).Returns(filePath);

        var token = CreateFileToken("projects", "v1", "file.pdf");
        var result = await _controller.GetFile("projects", "v1", "file.pdf", token);
        var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", physicalFileResult.ContentType);
    }

    [Fact]
    public async Task GetFile_WithValid5PartToken_BypassesVaultAndReturnsSeekableStream()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album
        {
            Id = "v1",
            OwnerId = "emp1",
            IsEncrypted = true,
            EncryptedAlbumKeys = { ["emp1"] = "cipher" }
        });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file.pdf")).Returns(filePath);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]); // Valid 32-byte AES key
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf", b64Key, "emp1", "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
        
        // Should return FileStreamResult wrapping the SeekableAesStream
        var fileStreamResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/pdf", fileStreamResult.ContentType);
    }

    [Fact]
    public async Task GetFile_WithInvalidToken_Returns410WhenAnonymous()
    {
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        // A tampered/invalid token that can't be unprotected
        string t = "invalid-token-that-cannot-be-unprotected";

        var result = await _controller.GetFile("projects", "v1", "file.pdf", t);
        // Invalid token → 410 (File Token Expired or Invalid)
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_WithValidToken_ReturnsPhysicalFile()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file.pdf")).Returns(filePath);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf", userId: "emp1", sessionId: "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
        
        var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", physicalFileResult.ContentType);
    }

    [Fact]
    public async Task GetFile_WithTokenForDifferentFile_FailsValidation()
    {
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(false);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "wrong_file.pdf", userId: "emp1", sessionId: "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
        // Token was for wrong_file.pdf but request is for file.pdf → 410
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_WithRevokedSession_ReturnsUnauthorized()
    {
        _employeesRepo.Save(new Employee { Id = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        
        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf", b64Key, "emp1", "valid_session");

        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(); // Anonymous

        var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
        
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Authentication required.", GetErrorMessage(unauthorizedResult.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenNotOwner_ReturnsForbid()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", IsActive = true }); // The logged-in user
        _employeesRepo.Save(new Employee { Id = "other_user" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        // File owner is other_user
        var fileName = "other_user-12345.jpg";
        var category = "albums";
        var contextId = "v1";

        var formFile = new Mock<IFormFile>();
        formFile.Setup(f => f.Length).Returns(100);
        formFile.Setup(f => f.FileName).Returns("thumb.jpg");
        formFile.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(new byte[100]));
        
        _mockAlbumsAccessProvider.Setup(a => a.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);
        _mockAlbumsAccessProvider.Setup(a => a.CanUpload(It.IsAny<Employee>(), "v1")).Returns(false);
        
        var result = await _controller.UploadThumbnail(category, contextId, fileName, formFile.Object);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadFile_WithTempCategory_SucceedsForActiveEmployee()
    {
        _employeesRepo.Save(new Employee
        {
            Id = "emp1",
            IsActive = true,
            IsSuspended = false,
            IsBanned = false,
            Permissions = new List<string> { Spokes_Server.Core.Constants.AppPermissions.Admin.ManageSettings }
        });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        var formFile = new Mock<IFormFile>();
        formFile.Setup(f => f.Length).Returns(100);
        formFile.Setup(f => f.FileName).Returns("license.spokes-license");
        formFile.Setup(f => f.ContentType).Returns("application/json");
        formFile.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(new byte[100]));

        var files = new FormFileCollection { formFile.Object };

        _mockFileService.Setup(f => f.GenerateSafeName("license.spokes-license")).Returns("safe_license.spokes-license");
        _mockFileService.Setup(f => f.UploadStreamAsync("temp", "ctx-1", It.IsAny<Stream>(), "license.spokes-license", null, "safe_license.spokes-license"))
            .ReturnsAsync("/spokesapi/files/temp/ctx-1/safe_license.spokes-license");

        var result = await _controller.UploadFile("temp", "ctx-1", files);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task UploadFile_WithTempCategory_WithoutPermission_ReturnsForbid()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", IsActive = true, IsSuspended = false, IsBanned = false });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        var formFile = new Mock<IFormFile>();
        formFile.Setup(f => f.Length).Returns(100);
        formFile.Setup(f => f.FileName).Returns("license.spokes-license");
        formFile.Setup(f => f.ContentType).Returns("application/json");
        formFile.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(new byte[100]));

        var files = new FormFileCollection { formFile.Object };

        var result = await _controller.UploadFile("temp", "ctx-1", files);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
    }

    [Fact]
    public async Task GetFile_WithTempCategory_ReturnsNotFound()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", IsActive = true });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

        var token = CreateFileToken("temp", "ctx-1", "file.spokes-license");
        var result = await _controller.GetFile("temp", "ctx-1", "file.spokes-license", token);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadThumbnail_WithTempCategory_ReturnsBadRequest()
    {
        var formFile = new Mock<IFormFile>();
        formFile.Setup(f => f.Length).Returns(100);
        formFile.Setup(f => f.FileName).Returns("thumb.jpg");

        var result = await _controller.UploadThumbnail("temp", "ctx-1", "file.jpg", formFile.Object);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Thumbnails are not supported for temporary files.", GetErrorMessage(badRequest.Value));
    }

    #region Helpers

    private void SetConfiguration(Dictionary<string, string?> configValues)
    {
        if (!configValues.ContainsKey("SPOKES_MASTER_PASSWORD"))
        {
            configValues["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddMemoryCache();
        services.AddSingleton<Spokes_Server.Core.Services.Security.FileTokenService>();
        var serviceProvider = services.BuildServiceProvider();

        var existingUser = _controller.ControllerContext?.HttpContext?.User;
        _controller = new FileController(
            _mockFileService.Object,
            _fileAuthService,
            _companyProfilesRepo,
            new Mock<ILogger<FileController>>().Object,
            _userService,
            new ImageProcessingService(),
            _channelsRepo,
            config: config,
            fileTokenService: serviceProvider.GetRequiredService<FileTokenService>());

        var httpContext = new DefaultHttpContext { User = existingUser ?? new ClaimsPrincipal() };
        httpContext.RequestServices = serviceProvider;
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    private Employee SetupUser(string id = "emp1", bool isActive = true, bool isSuspended = false, bool isBanned = false, bool isAdmin = false, List<string>? permissions = null)
    {
        var emp = new Employee { Id = id, IsActive = isActive, IsSuspended = isSuspended, IsBanned = isBanned, IsAdmin = isAdmin, Permissions = permissions ?? [] };
        _employeesRepo.Save(emp);
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = id });
        return emp;
    }

    private static IFormFile CreateMockFormFile(string fileName, string contentType, byte[] content)
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.ContentType).Returns(contentType);
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((target, _) => {
                target.Write(content, 0, content.Length);
                return Task.CompletedTask;
            });
        return fileMock.Object;
    }

    private static IFormFile CreateMockLargeFormFile(string fileName, string contentType, long length)
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.ContentType).Returns(contentType);
        fileMock.Setup(f => f.Length).Returns(length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(new byte[16]));
        return fileMock.Object;
    }

    private static byte[] CreateTestImageBytes(int width = 50, int height = 50)
    {
        using var bitmap = new SKBitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private string CreateFileToken(string category, string contextId, string fileName, string? encryptionKey = null, TimeSpan? lifetime = null, string userId = "emp1", string sessionId = "valid_session", bool isThumbnail = false)
    {
        var provider = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
        var protector = provider.CreateProtector("SpokesFileToken");
        var payload = new Spokes_Server.Core.Services.Security.FileTokenPayload
        {
            Category = category,
            ContextId = contextId,
            FileName = fileName,
            EncryptionKeyBase64 = encryptionKey,
            ExpiryTicks = (DateTime.UtcNow + (lifetime ?? TimeSpan.FromDays(14))).Ticks,
            UserId = userId,
            SessionId = sessionId,
            IsThumbnail = isThumbnail,
            TokenSequence = DateTime.UtcNow.Ticks
        };
        return protector.Protect(JsonSerializer.Serialize(payload));
    }

    #endregion

    #region GetDemoMedia & GetDemoMediaThumb Tests

    [Fact]
    public void GetDemoMedia_WhenDemoModeAndSetupDisabled_ReturnsNotFound()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "false" },
            { "Spokes_DemoSetup", "false" }
        });

        var result = _controller.GetDemoMedia("sample.png");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetDemoMedia_WhenDemoModeActiveAndFileMissing_ReturnsNotFound()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        var result = _controller.GetDemoMedia("missing_file.jpg");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetDemoMedia_WhenDemoModeActiveAndFileExists_ReturnsPhysicalFile()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        var filePath = Path.Combine(demoDir, "sample.jpg");
        File.WriteAllText(filePath, "demo-image-content");

        var result = _controller.GetDemoMedia("sample.jpg");
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/jpeg", physicalResult.ContentType);
        Assert.Equal(Path.GetFullPath(filePath), physicalResult.FileName);
        Assert.Equal("nosniff", _controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Contains("sandbox", _controller.Response.Headers["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetDemoMedia_WhenDemoSetupActiveAndFileExists_ReturnsPhysicalFile()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "false" },
            { "Spokes_DemoSetup", "true" }
        });

        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        var filePath = Path.Combine(demoDir, "logo.png");
        File.WriteAllText(filePath, "logo-content");

        var result = _controller.GetDemoMedia("logo.png");
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/png", physicalResult.ContentType);
    }

    [Theory]
    [InlineData("custom.unknownext")]
    [InlineData("active.svg")]
    [InlineData("payload.html")]
    [InlineData("..\\secret.png")]
    public void GetDemoMedia_WithDisallowedOrTraversalFile_ReturnsNotFound(string fileName)
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        File.WriteAllText(Path.Combine(demoDir, "custom.unknownext"), "binary-content");
        File.WriteAllText(Path.Combine(demoDir, "active.svg"), "<svg onload='alert(1)'/>");
        File.WriteAllText(Path.Combine(demoDir, "payload.html"), "<script>alert(1)</script>");
        File.WriteAllText(Path.Combine(_testDataPath, "secret.png"), "outside-content");

        var result = _controller.GetDemoMedia(fileName);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Theory]
    [InlineData("active.svg")]
    [InlineData("payload.html")]
    [InlineData("../secret.png")]
    public async Task ImportDemoMedia_WithDisallowedOrTraversalFile_ReturnsNotFound(string fileName)
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1");
        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        File.WriteAllText(Path.Combine(demoDir, "active.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(demoDir, "payload.html"), "<html/>");
        File.WriteAllText(Path.Combine(_testDataPath, "secret.png"), "outside");

        var result = await _controller.ImportDemoMedia("albums", "album1", fileName);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetDemoMediaThumb_DelegatesToGetDemoMedia()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        var filePath = Path.Combine(demoDir, "avatar.jpg");
        File.WriteAllText(filePath, "avatar-content");

        var result = _controller.GetDemoMediaThumb("avatar.jpg");
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("image/jpeg", physicalResult.ContentType);
    }

    [Fact]
    public async Task ImportDemoMedia_WhenDemoModeDisabled_ReturnsNotFound()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "false" },
            { "Spokes_DemoSetup", "false" }
        });

        var result = await _controller.ImportDemoMedia("albums", "album1", "sample.jpg");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task ImportDemoMedia_WhenSampleFileMissing_ReturnsNotFound()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        var result = await _controller.ImportDemoMedia("albums", "album1", "missing.jpg");
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task ImportDemoMedia_WhenValidSampleFile_ImportsAndReturnsUploadResult()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1");

        var demoDir = Path.Combine(_testDataPath, "DemoMedia");
        Directory.CreateDirectory(demoDir);
        var sampleFile = Path.Combine(demoDir, "mountain.jpg");
        File.WriteAllBytes(sampleFile, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 });

        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "album1")).Returns(true);
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "album1")).Returns(true);

        _mockFileService.Setup(f => f.GenerateSafeName("mountain.jpg")).Returns("safe_mountain.jpg");
        _mockFileService.Setup(f => f.UploadStreamAsync("albums", "album1", It.IsAny<Stream>(), "mountain.jpg", It.IsAny<string?>(), "safe_mountain.jpg"))
            .ReturnsAsync("/spokesapi/files/albums/album1/safe_mountain.jpg");

        var result = await _controller.ImportDemoMedia("albums", "album1", "mountain.jpg");
        var okResult = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<List<FileUploadResult>>(okResult.Value);
        Assert.Single(list);
        Assert.Equal("/spokesapi/files/albums/album1/safe_mountain.jpg", list[0].FilePath);
        Assert.Equal("mountain.jpg", list[0].FileName);
    }

    #endregion

    #region UploadThumbnail Tests

    [Theory]
    [InlineData("../invalid", "ctx1", "thumb.jpg")]
    [InlineData("projects", "c/d", "thumb.jpg")]
    [InlineData("projects", "ctx1", "th..umb.jpg")]
    public async Task UploadThumbnail_WithInvalidPathSegments_ReturnsBadRequest(string category, string contextId, string fileName)
    {
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[10]);
        var result = await _controller.UploadThumbnail(category, contextId, fileName, formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Invalid path parameters", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenDemoModeAndNotAdmin_Returns403()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1", isAdmin: false);
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Uploads are disabled in demo mode.", payload.ErrorMessage);
    }

    [Fact]
    public async Task UploadThumbnail_WhenDemoModeAndAdmin_AllowsUpload()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1", isAdmin: true);
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());

        var result = await _controller.UploadThumbnail("projects", "ctx1", "admin_pic.jpg", formFile);
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UploadThumbnail_WhenFileNull_ReturnsBadRequest()
    {
        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", null!);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No file uploaded", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenFileEmpty_ReturnsBadRequest()
    {
        var formFile = CreateMockFormFile("empty.jpg", "image/jpeg", Array.Empty<byte>());
        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No file uploaded", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenFileExceeds5MB_ReturnsBadRequest()
    {
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.Length).Returns(5 * 1024 * 1024 + 1);
        mockFile.Setup(f => f.FileName).Returns("large.jpg");

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", mockFile.Object);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Thumbnail must be smaller than 5MB", GetErrorMessage(badRequest.Value));
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, true, false)]  // Suspended
    [InlineData(true, false, true)]  // Banned
    public async Task UploadThumbnail_WhenUserInactiveOrSuspendedOrBanned_ReturnsUnauthorized(bool isActive, bool isSuspended, bool isBanned)
    {
        SetupUser("emp1", isActive: isActive, isSuspended: isSuspended, isBanned: isBanned);
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Authentication required.", GetErrorMessage(unauthorizedResult.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenProviderNotFound_ReturnsBadRequest()
    {
        SetupUser("emp1");
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("nonexistent_provider", "ctx1", "pic.jpg", formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unknown file category: nonexistent_provider", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task UploadThumbnail_WhenProviderDeniesAccess_ReturnsForbid()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(false);
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadThumbnail_WithInvalidToken_Returns410()
    {
        SetupUser("emp1");
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile, "tampered-token");
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Upload Token Expired or Invalid", payload.ErrorMessage);
    }

    [Fact]
    public async Task UploadThumbnail_WithExpiredToken_Returns410()
    {
        SetupUser("emp1");
        var expiredToken = CreateFileToken("projects", "ctx1", "*", lifetime: TimeSpan.FromMinutes(-10));
        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

        var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile, expiredToken);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
        var expiredPayload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Upload Token Expired or Invalid", expiredPayload.ErrorMessage);
    }

    [Fact]
    public async Task UploadThumbnail_WhenThumbnailAlreadyExists_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

        var existingPath = Path.Combine(_testDataPath, "existing_file.jpg_thumb.jpg");
        File.WriteAllText(existingPath, "existing-thumb");

        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
        var result = await _controller.UploadThumbnail("projects", "ctx1", "existing_file.jpg", formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Thumbnail already exists for this file. Overwriting is not permitted.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WhenImageFormatInvalid_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

        var corruptFile = CreateMockFormFile("thumb.jpg", "image/jpeg", System.Text.Encoding.UTF8.GetBytes("not-an-image-payload"));
        var result = await _controller.UploadThumbnail("projects", "ctx1", "valid_name.jpg", corruptFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Invalid image format or dimensions too large.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WithEncryptedChat_WhenVaultLocked_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);

        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
        var result = await _controller.UploadThumbnail("chat", "c1", "pic.jpg", formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Your chat vault is locked.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_WithEncryptedAlbum_WhenKeyCannotBeResolved_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb1", OwnerId = "emp1", IsEncrypted = true });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "alb1")).Returns(true);

        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
        var result = await _controller.UploadThumbnail("albums", "alb1", "pic.jpg", formFile);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("No encryption key.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadThumbnail_Success_SavesThumbnailFileAndReturnsOk()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());

        var result = await _controller.UploadThumbnail("projects", "ctx1", "new_photo.jpg", formFile);
        Assert.IsType<OkResult>(result);

        var expectedThumb = Path.Combine(_testDataPath, "new_photo.jpg_thumb.jpg");
        Assert.True(File.Exists(expectedThumb));
    }

    [Fact]
    public async Task UploadThumbnail_WithValidEncryptionToken_EncryptsAndReturnsOk()
    {
        SetupUser("emp1");
        _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true, EncryptedChannelKeys = { ["emp1"] = "cipher" } });
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "c1", "")).Returns(_testDataPath);

        string aesKey = Convert.ToBase64String(new byte[32]);
        string token = CreateFileToken("chat", "c1", "*", aesKey);

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());

        var result = await _controller.UploadThumbnail("chat", "c1", "chat_photo.jpg", formFile, token);
        Assert.IsType<OkResult>(result);

        _mockCrypto.Verify(c => c.EncryptStreamAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), aesKey), Times.Once);
    }

    #endregion

    #region UploadFile Tests

    [Theory]
    [InlineData("../invalid", "ctx1")]
    [InlineData("projects", "c/d")]
    public async Task UploadFile_WithInvalidPathSegments_ReturnsBadRequest(string category, string contextId)
    {
        var files = new FormFileCollection { CreateMockFormFile("f.txt", "text/plain", new byte[10]) };
        var result = await _controller.UploadFile(category, contextId, files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Invalid path parameters", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadFile_WhenDemoModeAndNotAdmin_Returns403()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1", isAdmin: false);
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Uploads are disabled in demo mode.", payload.ErrorMessage);
    }

    [Fact]
    public async Task UploadFile_WhenDemoModeAndAdmin_AllowsUpload()
    {
        SetConfiguration(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "Spokes_DemoMode", "true" }
        });

        SetupUser("emp1", isAdmin: true);
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GenerateSafeName("doc.pdf")).Returns("safe_doc.pdf");
        _mockFileService.Setup(f => f.UploadStreamAsync("projects", "ctx1", It.IsAny<Stream>(), "doc.pdf", null, "safe_doc.pdf"))
            .ReturnsAsync("/spokesapi/files/projects/ctx1/safe_doc.pdf");

        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UploadFile_WhenFilesNullOrEmpty_ReturnsBadRequest()
    {
        var resultEmpty = await _controller.UploadFile("projects", "ctx1", new FormFileCollection());
        var badRequest = Assert.IsType<BadRequestObjectResult>(resultEmpty);
        Assert.Equal("No files uploaded", GetErrorMessage(badRequest.Value));

        var resultNull = await _controller.UploadFile("projects", "ctx1", null!);
        var badRequestNull = Assert.IsType<BadRequestObjectResult>(resultNull);
        Assert.Equal("No files uploaded", GetErrorMessage(badRequestNull.Value));
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, true, false)]  // Suspended
    [InlineData(true, false, true)]  // Banned
    public async Task UploadFile_WhenUserInactiveOrSuspendedOrBanned_ReturnsUnauthorized(bool isActive, bool isSuspended, bool isBanned)
    {
        SetupUser("emp1", isActive: isActive, isSuspended: isSuspended, isBanned: isBanned);
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Authentication required.", GetErrorMessage(unauthorizedResult.Value));
    }

    [Fact]
    public async Task UploadFile_WhenProviderNotFound_ReturnsBadRequest()
    {
        SetupUser("emp1");
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("unknown_cat", "ctx1", files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unknown file category: unknown_cat", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task UploadFile_WhenProviderDeniesAccess_ReturnsForbid()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(false);
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadFile_WhenFileExceedsCompanyMaxFileSize_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _companyProfilesRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = 200 });

        var files = new FormFileCollection { CreateMockFormFile("big.pdf", "application/pdf", new byte[300]) };
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("exceeds the max size of 200 bytes", badRequest.Value?.ToString());
    }

    [Fact]
    public void UploadFile_Action_HasDisableRequestSizeLimitAndUploadRequestGuardFilter()
    {
        var method = typeof(FileController).GetMethod("UploadFile");
        Assert.NotNull(method);

        var disableLimitAttr = method.GetCustomAttribute<DisableRequestSizeLimitAttribute>();
        Assert.NotNull(disableLimitAttr);

        var typeFilterAttr = method.GetCustomAttributes<TypeFilterAttribute>().FirstOrDefault();
        Assert.NotNull(typeFilterAttr);
        Assert.Equal(typeof(Spokes_Server.Core.Filters.UploadRequestGuardFilter), typeFilterAttr.ImplementationType);
    }

    [Fact]
    public async Task UploadFile_WhenFileIsWithinHighConfiguredLimit_ProcessesSuccessfully()
    {
        SetupUser("emp1");
        // Configure 5GB max upload limit
        _companyProfilesRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = 5_368_709_120L });

        // 3GB file (well above the old 256MB/2048MB limits)
        var files = new FormFileCollection { CreateMockLargeFormFile("large-video.mp4", "video/mp4", 3_221_225_472L) };
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("/spokesapi/files/projects/ctx1/large-video.mp4");

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var resList = Assert.IsType<List<FileUploadResult>>(okResult.Value);
        Assert.Single(resList);
        Assert.Equal("large-video.mp4", resList[0].FileName);
    }

    [Fact]
    public async Task UploadFile_WhenFileExceedsHighConfiguredLimit_ReturnsBadRequest()
    {
        SetupUser("emp1");
        // Configure 5GB max upload limit
        _companyProfilesRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = 5_368_709_120L });

        // 6GB file exceeds the 5GB limit
        var files = new FormFileCollection { CreateMockLargeFormFile("massive-archive.zip", "application/zip", 6_442_450_944L) };
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);

        var result = await _controller.UploadFile("projects", "ctx1", files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("exceeds the max size of 5368709120 bytes", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task UploadFile_WithInvalidToken_Returns410()
    {
        SetupUser("emp1");
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files, "invalid-token");
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Upload Token Expired or Invalid", payload.ErrorMessage);
    }

    [Fact]
    public async Task UploadFile_WithExpiredToken_Returns410()
    {
        SetupUser("emp1");
        var expiredToken = CreateFileToken("projects", "ctx1", "*", lifetime: TimeSpan.FromMinutes(-10));
        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("projects", "ctx1", files, expiredToken);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
        var expiredPayload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("Upload Token Expired or Invalid", expiredPayload.ErrorMessage);
    }

    [Fact]
    public async Task UploadFile_EncryptedChatWithoutToken_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);

        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("chat", "c1", files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("You do not have the encryption key for this channel (missing token).", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadFile_EncryptedChat_WhenTokenMissing_FallsBackToKeystore()
    {
        SetupUser("emp1");
        var channel = new ChatChannel
        {
            Id = "c1",
            IsEncrypted = true,
            EncryptedChannelKeys = { ["emp1"] = "cipher_key" }
        };
        _channelsRepo.Save(channel);
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);

        _keystore.Unlock("plain_private_key");
        _mockCrypto.Setup(c => c.DecryptRsa("cipher_key", "plain_private_key")).Returns("aes_key");

        _mockFileService.Setup(f => f.GenerateSafeName("doc.pdf")).Returns("safe_doc.pdf");
        _mockFileService.Setup(f => f.UploadStreamAsync("chat", "c1", It.IsAny<Stream>(), "doc.pdf", "aes_key", "safe_doc.pdf"))
            .ReturnsAsync("/spokesapi/files/chat/c1/safe_doc.pdf");

        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("chat", "c1", files, t: null);
        Assert.IsType<OkObjectResult>(result);

        _mockFileService.Verify(f => f.UploadStreamAsync("chat", "c1", It.IsAny<Stream>(), "doc.pdf", "aes_key", "safe_doc.pdf"), Times.Once);
    }

    [Fact]
    public async Task UploadFile_EncryptedAlbumWithoutKey_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb1", IsEncrypted = true });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "alb1")).Returns(true);

        var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("albums", "alb1", files);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("You do not have the encryption key to upload to this album.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task UploadFile_NonImageFile_UploadsSuccessfully()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);
        _mockFileService.Setup(f => f.GenerateSafeName("notes.txt")).Returns("safe_notes.txt");
        _mockFileService.Setup(f => f.UploadStreamAsync("projects", "v1", It.IsAny<Stream>(), "notes.txt", null, "safe_notes.txt"))
            .ReturnsAsync("/spokesapi/files/projects/v1/safe_notes.txt");

        var files = new FormFileCollection { CreateMockFormFile("notes.txt", "text/plain", new byte[50]) };

        var result = await _controller.UploadFile("projects", "v1", files);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task UploadFile_ImageFile_GeneratesThumbnailAndUploadsSuccessfully()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "")).Returns(_testDataPath);
        _mockFileService.Setup(f => f.GenerateSafeName("picture.png")).Returns("safe_picture.png");
        _mockFileService.Setup(f => f.UploadStreamAsync("projects", "v1", It.IsAny<Stream>(), "picture.png", null, "safe_picture.png"))
            .ReturnsAsync("/spokesapi/files/projects/v1/safe_picture.png");

        var files = new FormFileCollection { CreateMockFormFile("picture.png", "image/png", CreateTestImageBytes()) };

        var result = await _controller.UploadFile("projects", "v1", files);
        Assert.IsType<OkObjectResult>(result);

        var expectedThumb = Path.Combine(_testDataPath, "safe_picture.png_thumb.jpg");
        Assert.True(File.Exists(expectedThumb));
    }

    [Fact]
    public async Task UploadFile_WithValidEncryptionToken_UploadsEncryptedChatFileSuccessfully()
    {
        SetupUser("emp1");
        _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true, EncryptedChannelKeys = { ["emp1"] = "cipher" } });
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);

        string aesKey = Convert.ToBase64String(new byte[32]);
        string token = CreateFileToken("chat", "c1", "*", aesKey);

        _mockFileService.Setup(f => f.GenerateSafeName("secret.pdf")).Returns("safe_secret.pdf");
        _mockFileService.Setup(f => f.UploadStreamAsync("chat", "c1", It.IsAny<Stream>(), "secret.pdf", aesKey, "safe_secret.pdf"))
            .ReturnsAsync("/spokesapi/files/chat/c1/safe_secret.pdf");

        var files = new FormFileCollection { CreateMockFormFile("secret.pdf", "application/pdf", new byte[50]) };

        var result = await _controller.UploadFile("chat", "c1", files, token);
        Assert.IsType<OkObjectResult>(result);

        _mockFileService.Verify(f => f.UploadStreamAsync("chat", "c1", It.IsAny<Stream>(), "secret.pdf", aesKey, "safe_secret.pdf"), Times.Once);
    }

    #endregion

    #region GetFileThumb Tests

    [Fact]
    public async Task GetFileThumb_WhenThumbnailExists_ReturnsThumbnailPhysicalFile()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var thumbPath = Path.Combine(_testDataPath, "photo.jpg_thumb.jpg");
        File.WriteAllText(thumbPath, "thumbnail-bits");

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg_thumb.jpg")).Returns(thumbPath);

        var token = CreateFileToken("projects", "v1", "photo.jpg", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "photo.jpg", token);
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(thumbPath, physicalResult.FileName);
    }

    [Fact]
    public async Task GetFileThumb_WhenThumbnailMissing_FallsBackToOriginalPhysicalFile()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var origPath = Path.Combine(_testDataPath, "photo.jpg");
        File.WriteAllText(origPath, "original-bits");

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg_thumb.jpg"))
            .Returns(Path.Combine(_testDataPath, "nonexistent_thumb.jpg"));
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg"))
            .Returns(origPath);

        var token = CreateFileToken("projects", "v1", "photo.jpg", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "photo.jpg", token);
        var physicalResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(origPath, physicalResult.FileName);
    }

    [Fact]
    public async Task GetFileThumb_WhenBothThumbnailAndOriginalMissing_ReturnsNotFound()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", It.IsAny<string>()))
            .Returns(Path.Combine(_testDataPath, "missing.jpg"));

        var token = CreateFileToken("projects", "v1", "missing.jpg", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "missing.jpg", token);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task GetFileThumb_WhenThumbnailMissing_AndFileIsVideo_ReturnsNotFound()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var origVideoPath = Path.Combine(_testDataPath, "video.mkv");
        File.WriteAllText(origVideoPath, "mkv-bits");

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "video.mkv_thumb.jpg"))
            .Returns(Path.Combine(_testDataPath, "nonexistent_thumb.jpg"));
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "video.mkv"))
            .Returns(origVideoPath);

        var token = CreateFileToken("projects", "v1", "video.mkv", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "video.mkv", token);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("Thumbnail not found.", payload.ErrorMessage);
    }

    [Fact]
    public async Task GetFileThumb_WhenThumbnailMissingForRealImage_SelfHealsAndPersistsJpegThumbnail()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var origPath = Path.Combine(_testDataPath, "photo_real.png");
        var thumbPath = Path.Combine(_testDataPath, "photo_real.png_thumb.jpg");

        using (var bitmap = new SKBitmap(64, 64))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            using var fs = File.OpenWrite(origPath);
            data.SaveTo(fs);
        }

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "")).Returns(_testDataPath);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo_real.png_thumb.jpg")).Returns(thumbPath);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo_real.png")).Returns(origPath);

        var token = CreateFileToken("projects", "v1", "photo_real.png", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "photo_real.png", token);
        var streamResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", streamResult.ContentType);
        Assert.True(File.Exists(thumbPath));
        using (streamResult.FileStream)
        using (var decoded = SKBitmap.Decode(streamResult.FileStream))
        {
            Assert.NotNull(decoded);
            Assert.Equal(64, decoded!.Width);
        }
        _mockFileService.Verify(
            f => f.UploadThumbnailAsync("projects", "v1", It.IsAny<Stream>(), "photo_real.png", null, false),
            Times.Once);
    }

    #endregion

    #region GetFile Security Headers & Dangerous Extensions Tests

    [Theory]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("vector.svg")]
    [InlineData("feed.xml")]
    [InlineData("page.xhtml")]
    public async Task GetFile_DangerousExtensions_ForcesDownloadAttachment(string fileName)
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, fileName);
        File.WriteAllText(filePath, "dangerous-payload");
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", fileName)).Returns(filePath);

        var token = CreateFileToken("projects", "v1", fileName);
        var result = await _controller.GetFile("projects", "v1", fileName, token);
        Assert.IsType<PhysicalFileResult>(result);

        Assert.True(_controller.Response.Headers.ContainsKey("Content-Disposition"));
        var disposition = _controller.Response.Headers["Content-Disposition"].ToString();
        Assert.Contains("attachment", disposition);
        Assert.Contains(fileName, disposition);
    }

    [Fact]
    public async Task GetFile_SetsStandardSecurityHeaders()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "doc.pdf");
        File.WriteAllText(filePath, "pdf-data");
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "doc.pdf")).Returns(filePath);

        var token = CreateFileToken("projects", "v1", "doc.pdf");
        await _controller.GetFile("projects", "v1", "doc.pdf", token);

        Assert.Equal("nosniff", _controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("private, max-age=1209600", _controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("standard", _controller.Response.Headers["X-Spokes-Media-Scope"].ToString());
    }

    [Fact]
    public async Task GetFile_WhenFileMissingOnDisk_ReturnsNotFound()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "missing.pdf"))
            .Returns(Path.Combine(_testDataPath, "missing.pdf"));

        var token = CreateFileToken("projects", "v1", "missing.pdf");
        var result = await _controller.GetFile("projects", "v1", "missing.pdf", token);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(notFound.Value);
        Assert.False(payload.IsSuccess);
    }

    [Theory]
    [InlineData(false, false, false)] // Inactive
    [InlineData(true, false, true)]   // Banned
    public async Task GetFile_WhenUserInactiveOrBanned_ReturnsUnauthorized(bool isActive, bool isSuspended, bool isBanned)
    {
        SetupUser("emp1", isActive: isActive, isSuspended: isSuspended, isBanned: isBanned);

        var result = await _controller.GetFile("projects", "v1", "file.pdf");
        var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal("Authentication required.", GetErrorMessage(unauthorizedResult.Value));
    }

    [Fact]
    public async Task GetFile_WhenUserIsSuspended_AllowsReadOnlyDownload()
    {
        SetupUser("emp1", isActive: true, isSuspended: true, isBanned: false);
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "readonly_suspended.pdf");
        File.WriteAllText(filePath, "pdf-bytes");
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "readonly_suspended.pdf")).Returns(filePath);

        var token = CreateFileToken("projects", "v1", "readonly_suspended.pdf");
        var result = await _controller.GetFile("projects", "v1", "readonly_suspended.pdf", token);
        var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", physicalFileResult.ContentType);
    }

    [Fact]
    public async Task GetFile_EncryptedChatWithoutKey_ReturnsBadRequest()
    {
        SetupUser("emp1");
        _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "c1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "chat.pdf");
        File.WriteAllText(filePath, "encrypted-data");
        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "c1", "chat.pdf")).Returns(filePath);

        var token = CreateFileToken("chat", "c1", "chat.pdf");
        var result = await _controller.GetFile("chat", "c1", "chat.pdf", token);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("You do not have the encryption key for this channel.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task GetFile_EncryptedAlbumWithoutKey_ReturnsBadRequest()
    {
        SetupUser("emp1", permissions: [AppPermissions.Albums.View]);
        _albumsRepo.Save(new Album { Id = "alb1", IsEncrypted = true });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "alb1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "album.pdf");
        File.WriteAllText(filePath, "album-data");
        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "alb1", "album.pdf")).Returns(filePath);

        var token = CreateFileToken("albums", "alb1", "album.pdf");
        var result = await _controller.GetFile("albums", "alb1", "album.pdf", token);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("You do not have the encryption key to view this album.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task GetFile_WithExpiredToken_Returns410()
    {
        SetupUser("emp1");
        var expiredToken = CreateFileToken("projects", "v1", "doc.pdf", lifetime: TimeSpan.FromMinutes(-10));

        var result = await _controller.GetFile("projects", "v1", "doc.pdf", expiredToken);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, statusResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(statusResult.Value);
        Assert.Equal("File Token Expired or Invalid", payload.ErrorMessage);
    }

    [Fact]
    public async Task UploadThumbnail_Albums_ReturnsForbid_WhenUserIsNotOwnerNorContributor()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb-stranger", OwnerId = "other_user", ContributorUserIds = new List<string> { "someone_else" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "alb-stranger")).Returns(false);

        var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[] { 1, 2, 3, 4 });
        var result = await _controller.UploadThumbnail("albums", "alb-stranger", "thumb.jpg", formFile);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadFile_Albums_ReturnsForbid_WhenUserIsNotOwnerNorContributor()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb-stranger", OwnerId = "other_user", ContributorUserIds = new List<string> { "someone_else" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "alb-stranger")).Returns(false);

        var formFile = CreateMockFormFile("photo.jpg", "image/jpeg", new byte[] { 1, 2, 3, 4 });
        var files = new FormFileCollection { formFile };
        var result = await _controller.UploadFile("albums", "alb-stranger", files);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
        var payload = Assert.IsType<SpokesResult>(forbidden.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task UploadThumbnail_Albums_AllowsOwner()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb-owner", OwnerId = "emp1", IsEncrypted = false });
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "alb-owner")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "alb-owner", "")).Returns(_testDataPath);

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());
        var result = await _controller.UploadThumbnail("albums", "alb-owner", "photo1", formFile);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UploadThumbnail_Albums_AllowsContributor()
    {
        SetupUser("emp1");
        _albumsRepo.Save(new Album { Id = "alb-contrib", OwnerId = "other_user", ContributorUserIds = new List<string> { "emp1" }, IsEncrypted = false });
        _mockAlbumsAccessProvider.Setup(p => p.CanUpload(It.IsAny<Employee>(), "alb-contrib")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "alb-contrib", "")).Returns(_testDataPath);

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());
        var result = await _controller.UploadThumbnail("albums", "alb-contrib", "photo2", formFile);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task UploadFile_WhenStorageCriticallyFull_ReturnsBadRequest()
    {
        SetupUser("emp1");
        var mockStorage = new Mock<IStorageHealthService>();
        mockStorage.Setup(s => s.IsUploadAllowed).Returns(false);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" },
            { "Spokes_DemoMode", "false" }
        }).Build();
        var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var devRepo = new DeviceSessionRepository(writer, config);
        var db = TestDatabaseFactory.Create(devRepo, _employeesRepo);
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
        var controller = new FileController(
            _mockFileService.Object,
            Mock.Of<IFileAuthorizationService>(),
            _companyProfilesRepo,
            Mock.Of<ILogger<FileController>>(),
            userService,
            new ImageProcessingService(),
            _channelsRepo,
            mockStorage.Object);

        var httpContext = new DefaultHttpContext();
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "emp1") }, "Test"));
        httpContext.User = claims;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var fileCollection = new FormFileCollection
        {
            CreateMockFormFile("test.txt", "text/plain", new byte[] { 1, 2, 3 })
        };

        var result = await controller.UploadFile("chat", "chan1", fileCollection);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Server storage is critically full", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task UploadThumbnail_WhenStorageCriticallyFull_ReturnsBadRequest()
    {
        SetupUser("emp1");
        var mockStorage = new Mock<IStorageHealthService>();
        mockStorage.Setup(s => s.IsUploadAllowed).Returns(false);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            { "DataPath", _testDataPath },
            { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" },
            { "Spokes_DemoMode", "false" }
        }).Build();
        var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var devRepo = new DeviceSessionRepository(writer, config);
        var db = TestDatabaseFactory.Create(devRepo, _employeesRepo);
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
        var controller = new FileController(
            _mockFileService.Object,
            Mock.Of<IFileAuthorizationService>(),
            _companyProfilesRepo,
            Mock.Of<ILogger<FileController>>(),
            userService,
            new ImageProcessingService(),
            _channelsRepo,
            mockStorage.Object);

        var httpContext = new DefaultHttpContext();
        var claims = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "emp1") }, "Test"));
        httpContext.User = claims;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());
        var result = await controller.UploadThumbnail("albums", "alb1", "photo1", formFile);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Server storage is critically full", badRequest.Value?.ToString());
    }

    [Fact]
    public void FileController_MediatedConstructor_ResolvesWithMinimalDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockFileService.Object);
        services.AddSingleton(Mock.Of<IFileAuthorizationService>());
        services.AddSingleton<IEnumerable<IFileAccessProvider>>(new List<IFileAccessProvider>());
        services.AddSingleton(_companyProfilesRepo);
        services.AddSingleton(Mock.Of<ILogger<FileController>>());
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        services.AddSingleton(new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo));
        services.AddSingleton(new ImageProcessingService());
        services.AddSingleton(_channelsRepo);
        services.AddSingleton(new CoreContext(_companyProfilesRepo, null!, null!, null!, null!, null!, null!, null!));
        services.AddSingleton(new CommunicationContext(_channelsRepo, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!));

        var sp = services.BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<FileController>(sp);
        Assert.NotNull(controller);
    }

    [Fact]
    public async Task UploadThumbnail_WhenStorageIoFails_ReturnsInternalServerError()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "ctx1")).Returns(true);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);
        _mockFileService.Setup(f => f.UploadThumbnailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ThrowsAsync(new IOException("Disk write failure"));

        var formFile = CreateMockFormFile("thumb.png", "image/png", CreateTestImageBytes());
        var result = await _controller.UploadThumbnail("projects", "ctx1", "new_photo_io.jpg", formFile);
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_WhenDecryptedStreamThrowsFileNotFound_ReturnsNotFound()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file_fnf.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file_fnf.pdf")).Returns(filePath);
        _mockFileService.Setup(f => f.OpenDecryptedStream(filePath, It.IsAny<string>()))
            .Throws(new FileNotFoundException("Missing file"));

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file_fnf.pdf", b64Key, "emp1", "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file_fnf.pdf", t);
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFoundResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_WhenDecryptedStreamThrowsIoException_Returns500()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file_io.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file_io.pdf")).Returns(filePath);
        _mockFileService.Setup(f => f.OpenDecryptedStream(filePath, It.IsAny<string>()))
            .Throws(new IOException("Disk read error"));

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file_io.pdf", b64Key, "emp1", "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file_io.pdf", t);
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_WhenDecryptedStreamThrowsCryptographicException_ReturnsBadRequest()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "file_crypto.pdf");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "dummy");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file_crypto.pdf")).Returns(filePath);
        _mockFileService.Setup(f => f.OpenDecryptedStream(filePath, It.IsAny<string>()))
            .Throws(new System.Security.Cryptography.CryptographicException("Corrupt crypto block"));

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "file_crypto.pdf", b64Key, "emp1", "valid_session");

        var result = await _controller.GetFile("albums", "v1", "file_crypto.pdf", t);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Failed to decrypt channel key.", GetErrorMessage(badRequest.Value));
    }

    [Fact]
    public async Task GetFile_WithoutToken_Returns403TokenRequired_EvenWhenAuthenticated()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "unshared.pdf");
        File.WriteAllText(filePath, "secret");
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "unshared.pdf")).Returns(filePath);

        var fileResult = await _controller.GetFile("projects", "v1", "unshared.pdf");
        var forbiddenFile = Assert.IsType<ObjectResult>(fileResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenFile.StatusCode);
        var filePayload = Assert.IsType<SpokesResult>(forbiddenFile.Value);
        Assert.Equal("TOKEN_REQUIRED", filePayload.Code);

        var thumbResult = await _controller.GetFileThumb("projects", "v1", "unshared.pdf");
        var forbiddenThumb = Assert.IsType<ObjectResult>(thumbResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenThumb.StatusCode);
        var thumbPayload = Assert.IsType<SpokesResult>(forbiddenThumb.Value);
        Assert.Equal("TOKEN_REQUIRED", thumbPayload.Code);
    }

    [Fact]
    public async Task GetFile_WithAnotherUsersValidToken_Returns410_PreventingLinkSharing()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<FileTokenService>();
        string otherUsersToken = fileTokenService.GenerateAccessToken("projects", "v1", "shared.jpg", null, "emp2", "other_session");

        var result = await _controller.GetFile("projects", "v1", "shared.jpg", otherUsersToken);
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, statusResult.StatusCode);
    }

    [Fact]
    public async Task GetFile_EncryptedFile_SetsPrivateNoStoreAndMediaScopeHeaders_AndRejectsRevokedToken()
    {
        _employeesRepo.Save(new Employee { Id = "emp1", Permissions = [AppPermissions.Albums.View] });
        _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
        _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", OwnerId = "emp1", IsEncrypted = true, EncryptedAlbumKeys = { ["emp1"] = "cipher" } });
        _mockAlbumsAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var filePath = Path.Combine(_testDataPath, "photo_enc.jpg");
        Directory.CreateDirectory(_testDataPath);
        File.WriteAllText(filePath, "encrypted-image-bytes");

        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "photo_enc.jpg")).Returns(filePath);
        _mockFileService.Setup(f => f.GetContentType("photo_enc.jpg")).Returns("image/jpeg");
        _mockFileService.Setup(f => f.OpenDecryptedStream(filePath, It.IsAny<string>()))
            .Returns(() => new MemoryStream([1, 2, 3, 4]));

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);
        string t = fileTokenService.GenerateAccessToken("albums", "v1", "photo_enc.jpg", b64Key, "emp1", "valid_session");

        var result = await _controller.GetFile("albums", "v1", "photo_enc.jpg", t);
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("private, no-store", _controller.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("encrypted", _controller.Response.Headers["X-Spokes-Media-Scope"].ToString());
        Assert.Equal("u=3, i=?0", _controller.Response.Headers["Priority"].ToString());
        Assert.False(fileResult.EnableRangeProcessing);
        Assert.Null(fileResult.EntityTag);
        Assert.Null(fileResult.LastModified);
        fileResult.FileStream.Dispose();

        // 1. HEAD probe on encrypted image file short-circuits with 200 OK without decrypting
        _controller.Response.Headers.Clear();
        _controller.Request.Method = HttpMethods.Head;
        var headResult = await _controller.GetFile("albums", "v1", "photo_enc.jpg", t);
        Assert.IsType<OkResult>(headResult);
        Assert.False(_controller.Response.Headers.ContainsKey("Accept-Ranges"));
        _mockFileService.Verify(f => f.OpenDecryptedStream(filePath, It.IsAny<string>()), Times.Once);

        // 2. Seekable media (video/mp4) retains EnableRangeProcessing = true.
        var videoPath = Path.Combine(_testDataPath, "clip.mp4");
        File.WriteAllText(videoPath, "video-bytes");
        _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "clip.mp4")).Returns(videoPath);
        _mockFileService.Setup(f => f.GetContentType("clip.mp4")).Returns("video/mp4");
        _mockFileService.Setup(f => f.OpenDecryptedStream(videoPath, It.IsAny<string>()))
            .Returns(() => new MemoryStream([1, 2, 3, 4]));
        string videoToken = fileTokenService.GenerateAccessToken("albums", "v1", "clip.mp4", b64Key, "emp1", "valid_session");
        _controller.Response.Headers.Clear();
        _controller.Request.Method = HttpMethods.Get;
        var videoResult = Assert.IsType<FileStreamResult>(await _controller.GetFile("albums", "v1", "clip.mp4", videoToken));
        Assert.True(videoResult.EnableRangeProcessing);
        videoResult.FileStream.Dispose();

        // 3. Vault lock / token revocation invalidates the token, returning 410 Gone.
        fileTokenService.InvalidateUserEncryptedTokens("emp1");
        _controller.Response.Headers.Clear();
        _controller.Request.Method = HttpMethods.Get;
        var revokedResult = await _controller.GetFile("albums", "v1", "photo_enc.jpg", t);
        var status410 = Assert.IsType<ObjectResult>(revokedResult);
        Assert.Equal(StatusCodes.Status410Gone, status410.StatusCode);
    }

    [Fact]
    public void FileTokenService_BuildTokenizedUrl_ScopesThumbnailsOnlyOnThumbEndpoints_AndAppendsDisplayFileName()
    {
        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
        string b64Key = System.Convert.ToBase64String(new byte[32]);

        // 1. Thumbnail endpoint with isThumbnail: true -> mints thumbnail-scoped token (rejected for full-file download)
        string thumbUrl = fileTokenService.BuildTokenizedUrl(
            "chat",
            "c1",
            "/spokesapi/files/thumb/chat/c1/img1.jpg",
            b64Key,
            "emp1",
            "valid_session",
            isThumbnail: true);
        Assert.StartsWith("/spokesapi/files/thumb/chat/c1/img1.jpg?t=", thumbUrl);
        string thumbToken = thumbUrl.Split("?t=")[1];
        Assert.True(fileTokenService.ValidateAccessToken("chat", "c1", "img1.jpg", thumbToken, "emp1", "valid_session", isThumbnail: true).IsSuccess);
        Assert.False(fileTokenService.ValidateAccessToken("chat", "c1", "img1.jpg", thumbToken, "emp1", "valid_session", isThumbnail: false).IsSuccess);

        // 2. Fallback to original endpoint when no server thumbnail exists (isThumbnail: true on non-/thumb/ URL)
        // -> must mint a full-file token so GetFile (thumb: false) accepts it
        string fallbackUrl = fileTokenService.BuildTokenizedUrl(
            "albums",
            "a1",
            "/spokesapi/files/albums/a1/img2.jpg",
            b64Key,
            "emp1",
            "valid_session",
            isThumbnail: true,
            displayFileName: "My Photo.jpg");
        Assert.Contains("?t=", fallbackUrl);
        Assert.EndsWith("&fn=My%20Photo.jpg", fallbackUrl);
        string fallbackToken = fallbackUrl.Split("?t=")[1].Split("&fn=")[0];
        Assert.True(fileTokenService.ValidateAccessToken("albums", "a1", "img2.jpg", fallbackToken, "emp1", "valid_session", isThumbnail: false).IsSuccess);

        // 3. Small grid thumbnail variant (s=sm) appends s=sm query while keeping clean fileName inside token payload
        string smallThumbUrl = fileTokenService.BuildTokenizedUrl(
            "albums",
            "a1",
            "/spokesapi/files/thumb/albums/a1/img3.jpg",
            b64Key,
            "emp1",
            "valid_session",
            isThumbnail: true,
            smallThumbnail: true);
        Assert.Contains("?t=", smallThumbUrl);
        Assert.EndsWith("&s=sm", smallThumbUrl);
        string smallThumbToken = smallThumbUrl.Split("?t=")[1].Split("&s=")[0];
        Assert.True(fileTokenService.ValidateAccessToken("albums", "a1", "img3.jpg", smallThumbToken, "emp1", "valid_session", isThumbnail: true).IsSuccess);
    }

    [Fact]
    public async Task GetFileThumb_WithSmallVariant_LazilyDerives400pxFromExistingStandardThumb()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var standardThumbPath = Path.Combine(_testDataPath, "photo_grid.jpg_thumb.jpg");
        var smallThumbPath = Path.Combine(_testDataPath, "photo_grid.jpg_thumb_sm.jpg");
        var missingOrigPath = Path.Combine(_testDataPath, "photo_grid.jpg");

        // Write a real 1280x960 standard thumbnail on disk (without even needing the original)
        using (var bitmap = new SKBitmap(1280, 960))
        {
            bitmap.Erase(SKColors.SteelBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            using var fs = File.OpenWrite(standardThumbPath);
            data.SaveTo(fs);
        }

        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "")).Returns(_testDataPath);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo_grid.jpg_thumb_sm.jpg")).Returns(smallThumbPath);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo_grid.jpg_thumb.jpg")).Returns(standardThumbPath);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo_grid.jpg")).Returns(missingOrigPath);

        var token = CreateFileToken("projects", "v1", "photo_grid.jpg", isThumbnail: true);
        var result = await _controller.GetFileThumb("projects", "v1", "photo_grid.jpg", t: token, s: "sm");
        var streamResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", streamResult.ContentType);
        Assert.True(File.Exists(smallThumbPath));

        using (streamResult.FileStream)
        using (var decodedStream = SKBitmap.Decode(streamResult.FileStream))
        {
            Assert.NotNull(decodedStream);
            Assert.Equal(400, decodedStream!.Width);
            Assert.Equal(300, decodedStream.Height);
        }

        using (var decoded = SKBitmap.Decode(smallThumbPath))
        {
            Assert.NotNull(decoded);
            Assert.Equal(400, decoded!.Width);
            Assert.Equal(300, decoded.Height);
        }

        _mockFileService.Verify(
            f => f.UploadThumbnailAsync("projects", "v1", It.IsAny<Stream>(), "photo_grid.jpg", null, true),
            Times.Once);

        var serverTiming = _controller.Response.Headers["Server-Timing"].ToString();
        Assert.Contains("auth;dur=", serverTiming);
        Assert.Contains("thumb;dur=", serverTiming);
        Assert.Contains("desc=\"self-heal\"", serverTiming);
        Assert.Contains("total;dur=", serverTiming);
    }

    [Fact]
    public void ImageProcessingService_TryProcessUploadImage_Preserves1280pxStandardAndGenerates400pxSmallThumb()
    {
        var service = new ImageProcessingService();
        using var inputMs = new MemoryStream();
        using (var bitmap = new SKBitmap(2000, 1500))
        {
            bitmap.Erase(SKColors.ForestGreen);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            data.SaveTo(inputMs);
        }
        inputMs.Position = 0;

        bool ok = service.TryProcessUploadImage(inputMs, "image/jpeg", inputMs.Length, out var processed);
        Assert.True(ok);
        Assert.NotNull(processed.ThumbnailBytes);
        Assert.NotNull(processed.SmallThumbnailBytes);

        using var standardBmp = SKBitmap.Decode(processed.ThumbnailBytes!);
        Assert.NotNull(standardBmp);
        Assert.Equal(1280, standardBmp!.Width);
        Assert.Equal(960, standardBmp.Height);

        using var smallBmp = SKBitmap.Decode(processed.SmallThumbnailBytes!);
        Assert.NotNull(smallBmp);
        Assert.Equal(400, smallBmp!.Width);
        Assert.Equal(300, smallBmp.Height);
    }

    [Fact]
    public void FileAndMediaEndpoints_ExposeHttpHeadAlongsideHttpGet_ForBrokenImageProbes()
    {
        string[] fileActions =
        [
            nameof(FileController.GetFile),
            nameof(FileController.GetFileThumb),
            nameof(FileController.GetLegacyAttachment),
            nameof(FileController.GetLegacyAttachmentThumb),
            nameof(FileController.GetEmailAttachment),
            nameof(FileController.GetDemoMedia),
            nameof(FileController.GetDemoMediaThumb)
        ];

        foreach (var actionName in fileActions)
        {
            var method = typeof(FileController).GetMethod(actionName);
            Assert.NotNull(method);
            var getRoutes = method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
                .Cast<HttpGetAttribute>()
                .Select(a => a.Template)
                .ToArray();
            var headRoutes = method.GetCustomAttributes(typeof(HttpHeadAttribute), inherit: false)
                .Cast<HttpHeadAttribute>()
                .Select(a => a.Template)
                .ToArray();
            Assert.NotEmpty(getRoutes);
            Assert.Equal(getRoutes, headRoutes);
        }

        string[] mediaActions =
        [
            nameof(MediaController.GetIcon),
            nameof(MediaController.GetLogo),
            nameof(MediaController.GetAvatar)
        ];

        foreach (var actionName in mediaActions)
        {
            var method = typeof(MediaController).GetMethod(actionName);
            Assert.NotNull(method);
            var getRoutes = method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
                .Cast<HttpGetAttribute>()
                .Select(a => a.Template)
                .ToArray();
            var headRoutes = method.GetCustomAttributes(typeof(HttpHeadAttribute), inherit: false)
                .Cast<HttpHeadAttribute>()
                .Select(a => a.Template)
                .ToArray();
            Assert.NotEmpty(getRoutes);
            Assert.Equal(getRoutes, headRoutes);
        }
    }

    [Fact]
    public async Task GetFileThumb_WithThumbnailCache_ServesHotHitsAnd304FromRamWithZeroDiskAccess()
    {
        SetupUser("emp1");
        _mockAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "v1")).Returns(true);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<FileTokenService>();
        var thumbCache = new ThumbnailCacheService();
        var cachedController = new FileController(
            _mockFileService.Object,
            _fileAuthService,
            _companyProfilesRepo,
            new Mock<ILogger<FileController>>().Object,
            _userService,
            new ImageProcessingService(),
            _channelsRepo,
            fileTokenService: fileTokenService,
            thumbnailCache: thumbCache);
        cachedController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = _controller.HttpContext.User }
        };

        var thumbPath = Path.Combine(_testDataPath, "cached_photo.jpg_thumb.jpg");
        var payload = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02, 0x03, 0x04 };
        await File.WriteAllBytesAsync(thumbPath, payload);
        _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "cached_photo.jpg_thumb.jpg")).Returns(thumbPath);

        var thumbToken = fileTokenService.GenerateAccessToken("projects", "v1", "cached_photo.jpg", null, "emp1", "valid_session", isThumbnail: true);

        // 1. Cold read populates RAM cache ("disk-cached")
        var coldResult = await cachedController.GetFileThumb("projects", "v1", "cached_photo.jpg", thumbToken);
        var coldStream = Assert.IsType<FileStreamResult>(coldResult);
        var coldEtag = coldStream.EntityTag!.Tag.ToString();
        Assert.Contains("desc=\"disk-cached\"", cachedController.Response.Headers["Server-Timing"].ToString());
        using (var ms = new MemoryStream())
        {
            await coldStream.FileStream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        // Delete file on disk to prove subsequent requests execute zero disk syscalls
        File.Delete(thumbPath);

        // 2. Warm RAM hit ("mem")
        cachedController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = _controller.HttpContext.User }
        };
        var warmResult = await cachedController.GetFileThumb("projects", "v1", "cached_photo.jpg", thumbToken);
        var warmStream = Assert.IsType<FileStreamResult>(warmResult);
        Assert.Contains("desc=\"mem\"", cachedController.Response.Headers["Server-Timing"].ToString());
        using (var ms = new MemoryStream())
        {
            await warmStream.FileStream.CopyToAsync(ms);
            Assert.Equal(payload, ms.ToArray());
        }

        // 3. Revalidation hit ("mem-304")
        var revalidateCtx = new DefaultHttpContext { User = _controller.HttpContext.User };
        revalidateCtx.Request.Method = HttpMethods.Get;
        revalidateCtx.Request.Headers["If-None-Match"] = coldEtag;
        cachedController.ControllerContext = new ControllerContext { HttpContext = revalidateCtx };

        var notModified = await cachedController.GetFileThumb("projects", "v1", "cached_photo.jpg", thumbToken);
        var statusResult = Assert.IsType<StatusCodeResult>(notModified);
        Assert.Equal(StatusCodes.Status304NotModified, statusResult.StatusCode);
        Assert.Contains("desc=\"mem-304\"", cachedController.Response.Headers["Server-Timing"].ToString());
    }

    [Fact]
    public async Task GetFileThumb_EncryptedWithThumbnailCache_CachesCiphertextAndDecryptsPerRequestInRam()
    {
        SetupUser("emp1");
        _mockChatAccessProvider.Setup(p => p.CanAccess(It.IsAny<Employee>(), "enc-chan-cache")).Returns(true);
        _channelsRepo.Save(new ChatChannel
        {
            Id = "enc-chan-cache",
            Name = "Encrypted Cache Channel",
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string> { ["emp1"] = "rsa-wrapped-key" }
        });

        var rawKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rawKey);
        var b64Key = Convert.ToBase64String(rawKey);

        var realCrypto = new CryptoService();
        _mockCrypto.Setup(c => c.DecryptRsa("rsa-wrapped-key", It.IsAny<string>())).Returns(b64Key);
        _keystore.Unlock("fake-private-key");

        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret-decrypted-thumbnail-jpeg-bytes");
        var thumbPath = Path.Combine(_testDataPath, "enc_photo.jpg_thumb.jpg");
        using (var plainMs = new MemoryStream(plaintext))
        using (var cipherFs = new FileStream(thumbPath, FileMode.Create))
        {
            await realCrypto.EncryptStreamAsync(plainMs, cipherFs, b64Key);
        }

        _mockFileService.Setup(f => f.GetPhysicalPath("chat", "enc-chan-cache", "enc_photo.jpg_thumb.jpg")).Returns(thumbPath);

        var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<FileTokenService>();
        var thumbCache = new ThumbnailCacheService();
        var cachedController = new FileController(
            _mockFileService.Object,
            _fileAuthService,
            _companyProfilesRepo,
            new Mock<ILogger<FileController>>().Object,
            _userService,
            new ImageProcessingService(),
            _channelsRepo,
            fileTokenService: fileTokenService,
            thumbnailCache: thumbCache);
        cachedController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = _controller.HttpContext.User }
        };

        var encThumbToken = fileTokenService.GenerateAccessToken("chat", "enc-chan-cache", "enc_photo.jpg", b64Key, "emp1", "valid_session", isThumbnail: true);

        // 1. Cold encrypted read decrypts & caches ciphertext only
        var first = Assert.IsType<FileStreamResult>(await cachedController.GetFileThumb("chat", "enc-chan-cache", "enc_photo.jpg", encThumbToken));
        Assert.Contains("desc=\"disk-enc-cached\"", cachedController.Response.Headers["Server-Timing"].ToString());
        Assert.Equal("private, no-store", cachedController.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("encrypted", cachedController.Response.Headers["X-Spokes-Media-Scope"].ToString());
        using (var outMs = new MemoryStream())
        {
            await first.FileStream.CopyToAsync(outMs);
            Assert.Equal(plaintext, outMs.ToArray());
        }

        // Verify shared cache NEVER stores plaintext of encrypted thumbnails
        Assert.True(thumbCache.TryGet(thumbPath, out var cachedEntry));
        Assert.NotEqual(plaintext, cachedEntry!.RawBytes);

        // Delete disk file and verify RAM hit decrypts cleanly ("mem-enc")
        File.Delete(thumbPath);
        cachedController.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = _controller.HttpContext.User }
        };
        var second = Assert.IsType<FileStreamResult>(await cachedController.GetFileThumb("chat", "enc-chan-cache", "enc_photo.jpg", encThumbToken));
        Assert.Contains("desc=\"mem-enc\"", cachedController.Response.Headers["Server-Timing"].ToString());
        Assert.Equal("private, no-store", cachedController.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("encrypted", cachedController.Response.Headers["X-Spokes-Media-Scope"].ToString());
        using (var outMs = new MemoryStream())
        {
            await second.FileStream.CopyToAsync(outMs);
            Assert.Equal(plaintext, outMs.ToArray());
        }
    }

    #endregion
}

