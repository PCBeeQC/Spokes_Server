using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
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
using SkiaSharp;
using Spokes_Server.Aggregate;

namespace Spokes_Server.Tests.Controllers
{
    public class FileControllerTests : TestDataTestBase
    {
        private readonly FileController _controller;
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
                { "Spokes_DemoMode", "false" }
            }).Build();

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            _employeesRepo = new EmployeeRepository(writer, config);
            _openIdRepo = new OpenIdAccountRepository(writer, config);

            _mockFileService = new Mock<IFileService>();

            _mockAccessProvider = new Mock<IFileAccessProvider>();
            _mockAccessProvider.Setup(p => p.Category).Returns("projects");

            _mockAlbumsAccessProvider = new Mock<IFileAccessProvider>();
            _mockAlbumsAccessProvider.Setup(p => p.Category).Returns("albums");

            _mockChatAccessProvider = new Mock<IFileAccessProvider>();
            _mockChatAccessProvider.Setup(p => p.Category).Returns("chat");

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

            var db = new Database(deviceSessionsRepo, _employeesRepo);
            var escrowService = new ServerEscrowService(db, _mockCrypto.Object);

            _controller = new FileController(
                _mockFileService.Object,
                providers,
                _employeesRepo,
                _channelsRepo,
                keystore,
                _mockCrypto.Object,
                _companyProfilesRepo,
                _openIdRepo,
                new Mock<ILogger<FileController>>().Object,
                _albumsRepo,
                escrowService,
                userService,
                new ImageProcessingService(),
                deviceSessionsRepo);

            var httpContext = new DefaultHttpContext { User = user };
            httpContext.RequestServices = serviceProvider;

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
        }

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
            Assert.Equal("Authentication required.", unauthorizedResult.Value);
        }

        [Fact]
        public async Task GetFile_ReturnsBadRequest_WhenProviderNotFound()
        {
            // Add user to repo
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

            var result = await _controller.GetFile("unknown_category", "v1", "file.pdf");
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Unknown file category", badRequestResult.Value.ToString());
        }

        [Fact]
        public async Task GetFile_ReturnsForbid_WhenAccessDenied()
        {
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(false);

            var result = await _controller.GetFile("projects", "v1", "file.pdf");
            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task GetFile_ReturnsPhysicalFile_WhenAuthorizedAndFileExists()
        {
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "file.pdf");
            Directory.CreateDirectory(_testDataPath);
            File.WriteAllText(filePath, "dummy");

            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "file.pdf")).Returns(filePath);

            var result = await _controller.GetFile("projects", "v1", "file.pdf");
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/pdf", physicalFileResult.ContentType);
        }

        [Fact]
        public async Task GetFile_WithValid5PartToken_BypassesVaultAndReturnsSeekableStream()
        {
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
            _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", IsEncrypted = true });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "file.pdf");
            Directory.CreateDirectory(_testDataPath);
            File.WriteAllText(filePath, "dummy");

            _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file.pdf")).Returns(filePath);

            var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
            string b64Key = System.Convert.ToBase64String(new byte[32]); // Valid 32-byte AES key
            string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf", b64Key);

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
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "file.pdf");
            Directory.CreateDirectory(_testDataPath);
            File.WriteAllText(filePath, "dummy");

            _mockFileService.Setup(f => f.GetPhysicalPath("albums", "v1", "file.pdf")).Returns(filePath);

            var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
            string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf");

            var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
            
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/pdf", physicalFileResult.ContentType);
        }

        [Fact]
        public async Task GetFile_WithTokenForDifferentFile_FailsValidation()
        {
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });
            _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", IsEncrypted = true });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(false);

            var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
            string t = fileTokenService.GenerateAccessToken("albums", "v1", "wrong_file.pdf");

            var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
            // Token was for wrong_file.pdf but request is for file.pdf → 410
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(410, statusResult.StatusCode);
        }

        [Fact]
        public async Task GetFile_WithRevokedSession_ReturnsUnauthorized()
        {
            _employeesRepo.Save(new Employee { Id = "emp1" });
            _albumsRepo.Save(new Spokes_Server.Core.Models.Communication.Album { Id = "v1", IsEncrypted = true });
            
            var fileTokenService = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<Spokes_Server.Core.Services.Security.FileTokenService>();
            string b64Key = System.Convert.ToBase64String(new byte[32]);
            string t = fileTokenService.GenerateAccessToken("albums", "v1", "file.pdf", b64Key);

            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(); // Anonymous

            var result = await _controller.GetFile("albums", "v1", "file.pdf", t);
            
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal("Authentication required.", unauthorizedResult.Value);
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
            
            _mockAlbumsAccessProvider.Setup(a => a.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);
            
            var result = await _controller.UploadThumbnail(category, contextId, fileName, formFile.Object);
            
            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task UploadFile_WithTempCategory_SucceedsForActiveEmployee()
        {
            _employeesRepo.Save(new Employee { Id = "emp1", IsActive = true, IsSuspended = false, IsBanned = false });
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
        public async Task GetFile_WithTempCategory_ReturnsNotFound()
        {
            _employeesRepo.Save(new Employee { Id = "emp1", IsActive = true });
            _openIdRepo.Save(new OpenIdAccount { Sub = "auth0|123", LinkedEmployeeId = "emp1" });

            var result = await _controller.GetFile("temp", "ctx-1", "file.spokes-license");
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task UploadThumbnail_WithTempCategory_ReturnsBadRequest()
        {
            var formFile = new Mock<IFormFile>();
            formFile.Setup(f => f.Length).Returns(100);
            formFile.Setup(f => f.FileName).Returns("thumb.jpg");

            var result = await _controller.UploadThumbnail("temp", "ctx-1", "file.jpg", formFile.Object);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Thumbnails are not supported for temporary files.", badRequest.Value);
        }

        #region Helpers

        private void SetConfiguration(Dictionary<string, string?> configValues)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddMemoryCache();
            services.AddSingleton<Spokes_Server.Core.Services.Security.FileTokenService>();
            var serviceProvider = services.BuildServiceProvider();

            _controller.ControllerContext.HttpContext.RequestServices = serviceProvider;
        }

        private Employee SetupUser(string id = "emp1", bool isActive = true, bool isSuspended = false, bool isBanned = false, bool isAdmin = false)
        {
            var emp = new Employee { Id = id, IsActive = isActive, IsSuspended = isSuspended, IsBanned = isBanned, IsAdmin = isAdmin };
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

        private static byte[] CreateTestImageBytes(int width = 50, int height = 50)
        {
            using var bitmap = new SKBitmap(width, height);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        private string CreateFileToken(string category, string contextId, string fileName, string? encryptionKey = null, TimeSpan? lifetime = null)
        {
            var provider = _controller.ControllerContext.HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
            var protector = provider.CreateProtector("SpokesFileToken");
            var payload = new Spokes_Server.Core.Services.Security.FileTokenPayload
            {
                Category = category,
                ContextId = contextId,
                FileName = fileName,
                EncryptionKeyBase64 = encryptionKey,
                ExpiryTicks = (DateTime.UtcNow + (lifetime ?? TimeSpan.FromDays(14))).Ticks
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
            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public void GetDemoMedia_WhenDemoModeActiveAndFileMissing_ReturnsNotFound()
        {
            SetConfiguration(new Dictionary<string, string?> {
                { "DataPath", _testDataPath },
                { "Spokes_DemoMode", "true" }
            });

            var result = _controller.GetDemoMedia("missing_file.jpg");
            Assert.IsType<NotFoundResult>(result);
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

        [Fact]
        public void GetDemoMedia_WithUnmappedExtension_DefaultsToOctetStream()
        {
            SetConfiguration(new Dictionary<string, string?> {
                { "DataPath", _testDataPath },
                { "Spokes_DemoMode", "true" }
            });

            var demoDir = Path.Combine(_testDataPath, "DemoMedia");
            Directory.CreateDirectory(demoDir);
            var filePath = Path.Combine(demoDir, "custom.unknownext");
            File.WriteAllText(filePath, "binary-content");

            var result = _controller.GetDemoMedia("custom.unknownext");
            var physicalResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("application/octet-stream", physicalResult.ContentType);
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
            Assert.Equal("Invalid path parameters", badRequest.Value);
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
            Assert.Equal("Uploads are disabled in demo mode.", statusResult.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WhenDemoModeAndAdmin_AllowsUpload()
        {
            SetConfiguration(new Dictionary<string, string?> {
                { "DataPath", _testDataPath },
                { "Spokes_DemoMode", "true" }
            });

            SetupUser("emp1", isAdmin: true);
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);
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
            Assert.Equal("No file uploaded", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WhenFileEmpty_ReturnsBadRequest()
        {
            var formFile = CreateMockFormFile("empty.jpg", "image/jpeg", Array.Empty<byte>());
            var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("No file uploaded", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WhenFileExceeds5MB_ReturnsBadRequest()
        {
            var mockFile = new Mock<IFormFile>();
            mockFile.Setup(f => f.Length).Returns(5 * 1024 * 1024 + 1);
            mockFile.Setup(f => f.FileName).Returns("large.jpg");

            var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", mockFile.Object);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Thumbnail must be smaller than 5MB", badRequest.Value);
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
            Assert.Equal("Authentication required.", unauthorizedResult.Value);
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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(false);
            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

            var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile);
            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task UploadThumbnail_WithInvalidToken_Returns410()
        {
            SetupUser("emp1");
            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);

            var result = await _controller.UploadThumbnail("projects", "ctx1", "pic.jpg", formFile, "tampered-token");
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(410, statusResult.StatusCode);
            Assert.Equal("Upload Token Expired or Invalid", statusResult.Value);
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
            Assert.Equal("Upload Token Expired or Invalid", statusResult.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WhenThumbnailAlreadyExists_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

            var existingPath = Path.Combine(_testDataPath, "existing_file.jpg_thumb.jpg");
            File.WriteAllText(existingPath, "existing-thumb");

            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
            var result = await _controller.UploadThumbnail("projects", "ctx1", "existing_file.jpg", formFile);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Thumbnail already exists for this file. Overwriting is not permitted.", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WhenImageFormatInvalid_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "ctx1", "")).Returns(_testDataPath);

            var corruptFile = CreateMockFormFile("thumb.jpg", "image/jpeg", System.Text.Encoding.UTF8.GetBytes("not-an-image-payload"));
            var result = await _controller.UploadThumbnail("projects", "ctx1", "valid_name.jpg", corruptFile);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Invalid image format or dimensions too large.", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WithEncryptedChat_WhenVaultLocked_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);

            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
            var result = await _controller.UploadThumbnail("chat", "c1", "pic.jpg", formFile);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Your chat vault is locked.", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_WithEncryptedAlbum_WhenKeyCannotBeResolved_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _albumsRepo.Save(new Album { Id = "alb1", OwnerId = "emp1", IsEncrypted = true });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb1")).ReturnsAsync(true);

            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[50]);
            var result = await _controller.UploadThumbnail("albums", "alb1", "pic.jpg", formFile);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("No encryption key.", badRequest.Value);
        }

        [Fact]
        public async Task UploadThumbnail_Success_SavesThumbnailFileAndReturnsOk()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);
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
            _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);
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
            Assert.Equal("Invalid path parameters", badRequest.Value);
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
            Assert.Equal("Uploads are disabled in demo mode.", statusResult.Value);
        }

        [Fact]
        public async Task UploadFile_WhenDemoModeAndAdmin_AllowsUpload()
        {
            SetConfiguration(new Dictionary<string, string?> {
                { "DataPath", _testDataPath },
                { "Spokes_DemoMode", "true" }
            });

            SetupUser("emp1", isAdmin: true);
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);
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
            Assert.Equal("No files uploaded", badRequest.Value);

            var resultNull = await _controller.UploadFile("projects", "ctx1", null!);
            var badRequestNull = Assert.IsType<BadRequestObjectResult>(resultNull);
            Assert.Equal("No files uploaded", badRequestNull.Value);
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
            Assert.Equal("Authentication required.", unauthorizedResult.Value);
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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(false);
            var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

            var result = await _controller.UploadFile("projects", "ctx1", files);
            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task UploadFile_WhenFileExceedsCompanyMaxFileSize_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _companyProfilesRepo.Save(new CompanyProfile { Id = "GlobalProfile", MaxFileUploadSizeBytes = 200 });

            var files = new FormFileCollection { CreateMockFormFile("big.pdf", "application/pdf", new byte[300]) };
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "ctx1")).ReturnsAsync(true);

            var result = await _controller.UploadFile("projects", "ctx1", files);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("exceeds the max size of 200 bytes", badRequest.Value?.ToString());
        }

        [Fact]
        public async Task UploadFile_WithInvalidToken_Returns410()
        {
            SetupUser("emp1");
            var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

            var result = await _controller.UploadFile("projects", "ctx1", files, "invalid-token");
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(410, statusResult.StatusCode);
            Assert.Equal("Upload Token Expired or Invalid", statusResult.Value);
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
            Assert.Equal("Upload Token Expired or Invalid", statusResult.Value);
        }

        [Fact]
        public async Task UploadFile_EncryptedChatWithoutToken_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);

            var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

            var result = await _controller.UploadFile("chat", "c1", files);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("You do not have the encryption key for this channel (missing token).", badRequest.Value);
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
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);

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
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb1")).ReturnsAsync(true);

            var files = new FormFileCollection { CreateMockFormFile("doc.pdf", "application/pdf", new byte[50]) };

            var result = await _controller.UploadFile("albums", "alb1", files);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("You do not have the encryption key to upload to this album.", badRequest.Value);
        }

        [Fact]
        public async Task UploadFile_NonImageFile_UploadsSuccessfully()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);
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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);
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
            _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);

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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var thumbPath = Path.Combine(_testDataPath, "photo.jpg_thumb.jpg");
            File.WriteAllText(thumbPath, "thumbnail-bits");

            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg_thumb.jpg")).Returns(thumbPath);

            var result = await _controller.GetFileThumb("projects", "v1", "photo.jpg");
            var physicalResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(thumbPath, physicalResult.FileName);
        }

        [Fact]
        public async Task GetFileThumb_WhenThumbnailMissing_FallsBackToOriginalPhysicalFile()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var origPath = Path.Combine(_testDataPath, "photo.jpg");
            File.WriteAllText(origPath, "original-bits");

            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg_thumb.jpg"))
                .Returns(Path.Combine(_testDataPath, "nonexistent_thumb.jpg"));
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "photo.jpg"))
                .Returns(origPath);

            var result = await _controller.GetFileThumb("projects", "v1", "photo.jpg");
            var physicalResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(origPath, physicalResult.FileName);
        }

        [Fact]
        public async Task GetFileThumb_WhenBothThumbnailAndOriginalMissing_ReturnsNotFound()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", It.IsAny<string>()))
                .Returns(Path.Combine(_testDataPath, "missing.jpg"));

            var result = await _controller.GetFileThumb("projects", "v1", "missing.jpg");
            Assert.IsType<NotFoundResult>(result);
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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, fileName);
            File.WriteAllText(filePath, "dangerous-payload");
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", fileName)).Returns(filePath);

            var result = await _controller.GetFile("projects", "v1", fileName);
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
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "doc.pdf");
            File.WriteAllText(filePath, "pdf-data");
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "doc.pdf")).Returns(filePath);

            await _controller.GetFile("projects", "v1", "doc.pdf");

            Assert.Equal("nosniff", _controller.Response.Headers["X-Content-Type-Options"].ToString());
            Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"].ToString());
            Assert.Equal("private, max-age=1209600", _controller.Response.Headers["Cache-Control"].ToString());
        }

        [Fact]
        public async Task GetFile_WhenFileMissingOnDisk_ReturnsNotFound()
        {
            SetupUser("emp1");
            _mockAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "v1")).ReturnsAsync(true);
            _mockFileService.Setup(f => f.GetPhysicalPath("projects", "v1", "missing.pdf"))
                .Returns(Path.Combine(_testDataPath, "missing.pdf"));

            var result = await _controller.GetFile("projects", "v1", "missing.pdf");
            Assert.IsType<NotFoundResult>(result);
        }

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, true, false)]  // Suspended
        [InlineData(true, false, true)]  // Banned
        public async Task GetFile_WhenUserInactiveOrSuspendedOrBanned_ReturnsUnauthorized(bool isActive, bool isSuspended, bool isBanned)
        {
            SetupUser("emp1", isActive: isActive, isSuspended: isSuspended, isBanned: isBanned);

            var result = await _controller.GetFile("projects", "v1", "file.pdf");
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(result);
            Assert.Equal("Authentication required.", unauthorizedResult.Value);
        }

        [Fact]
        public async Task GetFile_EncryptedChatWithoutKey_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _channelsRepo.Save(new ChatChannel { Id = "c1", IsEncrypted = true });
            _mockChatAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "c1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "chat.pdf");
            File.WriteAllText(filePath, "encrypted-data");
            _mockFileService.Setup(f => f.GetPhysicalPath("chat", "c1", "chat.pdf")).Returns(filePath);

            var result = await _controller.GetFile("chat", "c1", "chat.pdf");
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("You do not have the encryption key for this channel.", badRequest.Value);
        }

        [Fact]
        public async Task GetFile_EncryptedAlbumWithoutKey_ReturnsBadRequest()
        {
            SetupUser("emp1");
            _albumsRepo.Save(new Album { Id = "alb1", IsEncrypted = true });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb1")).ReturnsAsync(true);

            var filePath = Path.Combine(_testDataPath, "album.pdf");
            File.WriteAllText(filePath, "album-data");
            _mockFileService.Setup(f => f.GetPhysicalPath("albums", "alb1", "album.pdf")).Returns(filePath);

            var result = await _controller.GetFile("albums", "alb1", "album.pdf");
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("You do not have the encryption key to view this album.", badRequest.Value);
        }

        [Fact]
        public async Task GetFile_WithExpiredToken_Returns410()
        {
            SetupUser("emp1");
            var expiredToken = CreateFileToken("projects", "v1", "doc.pdf", lifetime: TimeSpan.FromMinutes(-10));

            var result = await _controller.GetFile("projects", "v1", "doc.pdf", expiredToken);
            var statusResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(410, statusResult.StatusCode);
            Assert.Equal("File Token Expired or Invalid", statusResult.Value);
        }

        [Fact]
        public async Task UploadThumbnail_Albums_ReturnsForbid_WhenUserIsNotOwnerNorContributor()
        {
            SetupUser("emp1");
            _albumsRepo.Save(new Album { Id = "alb-stranger", OwnerId = "other_user", ContributorUserIds = new List<string> { "someone_else" } });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb-stranger")).ReturnsAsync(true);

            var formFile = CreateMockFormFile("thumb.jpg", "image/jpeg", new byte[] { 1, 2, 3, 4 });
            var result = await _controller.UploadThumbnail("albums", "alb-stranger", "thumb.jpg", formFile);

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task UploadThumbnail_Albums_AllowsOwner()
        {
            SetupUser("emp1");
            _albumsRepo.Save(new Album { Id = "alb-owner", OwnerId = "emp1", IsEncrypted = false });
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb-owner")).ReturnsAsync(true);
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
            _mockAlbumsAccessProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), "alb-contrib")).ReturnsAsync(true);
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
                { "Spokes_DemoMode", "false" }
            }).Build();
            var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
            var devRepo = new DeviceSessionRepository(writer, config);
            var db = new Database(devRepo, _employeesRepo);
            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
            var escrowService = new ServerEscrowService(db, _mockCrypto.Object);

            var controller = new FileController(
                _mockFileService.Object,
                new[] { _mockAccessProvider.Object },
                _employeesRepo,
                _channelsRepo,
                new ScopedKeystoreService(Mock.Of<IDataProtectionProvider>()),
                _mockCrypto.Object,
                _companyProfilesRepo,
                _openIdRepo,
                Mock.Of<ILogger<FileController>>(),
                _albumsRepo,
                escrowService,
                userService,
                new ImageProcessingService(),
                devRepo,
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
                { "Spokes_DemoMode", "false" }
            }).Build();
            var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
            var devRepo = new DeviceSessionRepository(writer, config);
            var db = new Database(devRepo, _employeesRepo);
            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
            var escrowService = new ServerEscrowService(db, _mockCrypto.Object);

            var controller = new FileController(
                _mockFileService.Object,
                new[] { _mockAccessProvider.Object },
                _employeesRepo,
                _channelsRepo,
                new ScopedKeystoreService(Mock.Of<IDataProtectionProvider>()),
                _mockCrypto.Object,
                _companyProfilesRepo,
                _openIdRepo,
                Mock.Of<ILogger<FileController>>(),
                _albumsRepo,
                escrowService,
                userService,
                new ImageProcessingService(),
                devRepo,
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
        public void FileController_CanBeActivatedByActivatorUtilities_WithoutAmbiguousConstructors()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                { "DataPath", _testDataPath }
            }).Build();
            var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
            var devRepo = new DeviceSessionRepository(writer, config);
            var db = new Database(devRepo, _employeesRepo);
            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
            var escrowService = new ServerEscrowService(db, _mockCrypto.Object);

            var services = new ServiceCollection();
            services.AddSingleton(_mockFileService.Object);
            services.AddSingleton<IEnumerable<IFileAccessProvider>>(new List<IFileAccessProvider>());
            services.AddSingleton(_employeesRepo);
            services.AddSingleton(_channelsRepo);
            services.AddSingleton(new ScopedKeystoreService(Mock.Of<IDataProtectionProvider>()));
            services.AddSingleton(_mockCrypto.Object);
            services.AddSingleton(_companyProfilesRepo);
            services.AddSingleton(_openIdRepo);
            services.AddSingleton(Mock.Of<ILogger<FileController>>());
            services.AddSingleton(_albumsRepo);
            services.AddSingleton(escrowService);
            services.AddSingleton(userService);
            services.AddSingleton(new ImageProcessingService());
            services.AddSingleton(devRepo);
            services.AddSingleton(Mock.Of<IStorageHealthService>());

            var sp = services.BuildServiceProvider();
            var controller = ActivatorUtilities.CreateInstance<FileController>(sp);
            Assert.NotNull(controller);
        }

        [Fact]
        public void FileController_CanBeActivatedByActivatorUtilities_WhenStorageHealthNotRegistered()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                { "DataPath", _testDataPath }
            }).Build();
            var writer = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
            var devRepo = new DeviceSessionRepository(writer, config);
            var db = new Database(devRepo, _employeesRepo);
            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);
            var escrowService = new ServerEscrowService(db, _mockCrypto.Object);

            var services = new ServiceCollection();
            services.AddSingleton(_mockFileService.Object);
            services.AddSingleton<IEnumerable<IFileAccessProvider>>(new List<IFileAccessProvider>());
            services.AddSingleton(_employeesRepo);
            services.AddSingleton(_channelsRepo);
            services.AddSingleton(new ScopedKeystoreService(Mock.Of<IDataProtectionProvider>()));
            services.AddSingleton(_mockCrypto.Object);
            services.AddSingleton(_companyProfilesRepo);
            services.AddSingleton(_openIdRepo);
            services.AddSingleton(Mock.Of<ILogger<FileController>>());
            services.AddSingleton(_albumsRepo);
            services.AddSingleton(escrowService);
            services.AddSingleton(userService);
            services.AddSingleton(new ImageProcessingService());
            services.AddSingleton(devRepo);

            var sp = services.BuildServiceProvider();
            var controller = ActivatorUtilities.CreateInstance<FileController>(sp);
            Assert.NotNull(controller);
        }

        #endregion
    }
}


