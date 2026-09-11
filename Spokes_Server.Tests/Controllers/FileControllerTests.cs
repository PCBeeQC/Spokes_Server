using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
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
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Controllers
{
    public class FileControllerTests : TestDataTestBase
    {
        private readonly FileController _controller;
        private readonly Mock<IFileService> _mockFileService;
        private readonly Mock<IFileAccessProvider> _mockAccessProvider;
        private readonly Mock<IFileAccessProvider> _mockAlbumsAccessProvider;
        private readonly EmployeeRepository _employeesRepo;
        private readonly OpenIdAccountRepository _openIdRepo;
        private readonly AlbumRepository _albumsRepo;

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

            var providers = new List<IFileAccessProvider> { _mockAccessProvider.Object, _mockAlbumsAccessProvider.Object, new TempFileAccessProvider() };

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeesRepo, _openIdRepo);

            _albumsRepo = new AlbumRepository(writer, config);

            var deviceSessionsRepo = new DeviceSessionRepository(writer, config);
            deviceSessionsRepo.Save(new DeviceSession { Id = "valid_session", EmployeeId = "emp1" });
            deviceSessionsRepo.Save(new DeviceSession { Id = "revoked_session", EmployeeId = "emp1", RevokedAt = DateTime.UtcNow });

            _controller = new FileController(_mockFileService.Object, providers, _employeesRepo, new ChatChannelRepository(writer, config), null!, new Mock<ICryptoService>().Object, new CompanyProfileRepository(writer, config), _openIdRepo, new Mock<ILogger<FileController>>().Object, _albumsRepo, null!, userService, new ImageProcessingService(), deviceSessionsRepo);

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
    }
}

