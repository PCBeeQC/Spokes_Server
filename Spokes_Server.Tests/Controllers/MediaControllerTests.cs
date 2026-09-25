using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Security.Claims;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Spokes_Server.Tests.Controllers
{
    public class MediaControllerTests : TestDataTestBase
    {
        private readonly string _webRootPath;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly CompanyProfileRepository _companyRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly OpenIdAccountRepository _openIdAccountRepo;
        private readonly Mock<IWebHostEnvironment> _mockEnv;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly AvatarGeneratorService _avatarGenerator;
        private readonly UserService _userService;
        private readonly MediaController _controller;

        public MediaControllerTests()
        {
            _webRootPath = Path.Combine(Path.GetTempPath(), "Spokes_Test_WebRoot_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_webRootPath);

            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            _companyRepo = new CompanyProfileRepository(writer, _mockConfig.Object);

            _mockEnv = new Mock<IWebHostEnvironment>();
            _mockEnv.Setup(e => e.WebRootPath).Returns(_webRootPath);

            var services = new ServiceCollection();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var serviceProvider = services.BuildServiceProvider();
            _dataProtection = serviceProvider.GetRequiredService<IDataProtectionProvider>();

            _employeeRepo = new EmployeeRepository(writer, _mockConfig.Object);
            _openIdAccountRepo = new OpenIdAccountRepository(writer, _mockConfig.Object);

            _employeeRepo.Save(new Employee { Id = "TestUser", IsActive = true, FirstName = "Test", LastName = "User" });
            _openIdAccountRepo.Save(new OpenIdAccount { Sub = "TestUser", LinkedEmployeeId = "TestUser" });

            var mockPrincipal = CreateClaimsPrincipal("TestUser");

            var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            mockAuthStateProvider
                .Setup(a => a.GetAuthenticationStateAsync())
                .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(mockPrincipal));

            _userService = new UserService(mockAuthStateProvider.Object, _employeeRepo, _openIdAccountRepo);
            _avatarGenerator = new AvatarGeneratorService(_mockEnv.Object);

            _controller = CreateController(mockPrincipal);
        }

        private static ClaimsPrincipal CreateClaimsPrincipal(string employeeId)
        {
            var identity = new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, employeeId),
                new Claim("sub", employeeId),
                new Claim("EmployeeId", employeeId)
            }, "mock");
            return new ClaimsPrincipal(identity);
        }

        private MediaController CreateController(
            ClaimsPrincipal? user = null,
            IEnumerable<IFileAccessProvider>? providers = null,
            IDataProtectionProvider? dataProtection = null)
        {
            var provs = providers ?? new List<IFileAccessProvider> { new AvatarFileAccessProvider() };
            var dp = dataProtection ?? _dataProtection;
            var controller = new MediaController(
                _companyRepo,
                _employeeRepo,
                _mockEnv.Object,
                dp,
                _avatarGenerator,
                _mockConfig.Object,
                provs,
                _userService);

            var httpContext = new DefaultHttpContext();
            if (user != null)
            {
                httpContext.User = user;
            }
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (Directory.Exists(_webRootPath))
            {
                try 
                { 
                    Directory.Delete(_webRootPath, true); 
                } 
                catch (Exception ex) 
                { 
                    System.Diagnostics.Debug.WriteLine($"Failed to delete test directory: {ex.Message}"); 
                }
            }
        }

        [Fact]
        public void GetIcon_ReturnsFallbackFavicon_WhenNoIconAndNoDefaultAndNoFavicon()
        {
            // Even if favicon doesn't exist, PhysicalFile result is returned. ASP.NET core serves PhysicalFile based on path.
            var result = _controller.GetIcon();
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Contains("favicon.ico", physicalFileResult.FileName);
            Assert.Equal("image/x-icon", physicalFileResult.ContentType);
        }

        [Fact]
        public void GetIcon_ReturnsDefaultIcon_WhenExists()
        {
            File.WriteAllText(Path.Combine(_webRootPath, "default-icon-192.png"), "dummy content");

            var result = _controller.GetIcon();
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Contains("default-icon-192.png", physicalFileResult.FileName);
            Assert.Equal("image/png", physicalFileResult.ContentType);
        }

        [Fact]
        public void GetIcon_ReturnsBase64Image_WhenConfigured()
        {
            var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            var profile = new CompanyProfile { IconBase64 = "data:image/png;base64," + base64 };
            _companyRepo.Save(profile);

            var result = _controller.GetIcon();
            var fileContentResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileContentResult.ContentType);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, fileContentResult.FileContents);
        }

        [Fact]
        public void GetIcon_ReturnsFallback_WhenBase64IsInvalid()
        {
            var profile = new CompanyProfile { IconBase64 = "data:image/png;base64,invalid-base64!" };
            _companyRepo.Save(profile);

            var result = _controller.GetIcon();
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Contains("favicon.ico", physicalFileResult.FileName);
        }

        [Fact]
        public void GetLogo_ReturnsLogoBase64_WhenConfigured()
        {
            var logoBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02 };
            var base64 = Convert.ToBase64String(logoBytes);
            var profile = new CompanyProfile { LogoBase64 = "data:image/png;base64," + base64 };
            _companyRepo.Save(profile);

            var result = _controller.GetLogo();
            var fileContentResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileContentResult.ContentType);
            Assert.Equal(logoBytes, fileContentResult.FileContents);
        }

        [Fact]
        public void GetLogo_FallsBackToIcon_WhenLogoNotConfigured()
        {
            var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x09, 0x09 };
            var base64 = Convert.ToBase64String(iconBytes);
            var profile = new CompanyProfile 
            { 
                LogoBase64 = string.Empty,
                IconBase64 = "data:image/png;base64," + base64 
            };
            _companyRepo.Save(profile);

            var result = _controller.GetLogo();
            var fileContentResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileContentResult.ContentType);
            Assert.Equal(iconBytes, fileContentResult.FileContents);
        }

        [Fact]
        public void MediaController_IconAndLogo_AllowAnonymous()
        {
            var getIconMethod = typeof(MediaController).GetMethod(nameof(MediaController.GetIcon));
            var getLogoMethod = typeof(MediaController).GetMethod(nameof(MediaController.GetLogo));

            Assert.NotNull(getIconMethod);
            Assert.NotNull(getLogoMethod);

            var iconAllowAnon = getIconMethod.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true);
            var logoAllowAnon = getLogoMethod.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true);

            Assert.NotEmpty(iconAllowAnon);
            Assert.NotEmpty(logoAllowAnon);
        }

        // ==========================================
        // GetAvatar Tests
        // ==========================================

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task GetAvatar_UnauthenticatedWithoutToken_ReturnsUnauthorized(string? token)
        {
            // Arrange
            var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(user: unauthenticatedUser);

            // Act
            var result = await controller.GetAvatar("TestUser", t: token);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_AuthenticatedUserNotFoundInRepo_ReturnsUnauthorized()
        {
            // Arrange
            var user = CreateClaimsPrincipal("GhostUser");
            var controller = CreateController(user: user);

            // Act
            var result = await controller.GetAvatar("TestUser", t: null);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_InactiveUserWithoutToken_ReturnsUnauthorized()
        {
            // Arrange
            var empId = "InactiveUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = empId, IsActive = false, FirstName = "Inactive", LastName = "User" });
            _openIdAccountRepo.Save(new OpenIdAccount { Sub = empId, LinkedEmployeeId = empId });

            var user = CreateClaimsPrincipal(empId);
            var controller = CreateController(user: user);

            // Act
            var result = await controller.GetAvatar("TestUser", t: null);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_SuspendedUserWithoutToken_ReturnsUnauthorized()
        {
            // Arrange
            var empId = "SuspendedUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = empId, IsActive = true, IsSuspended = true, FirstName = "Suspended", LastName = "User" });
            _openIdAccountRepo.Save(new OpenIdAccount { Sub = empId, LinkedEmployeeId = empId });

            var user = CreateClaimsPrincipal(empId);
            var controller = CreateController(user: user);

            // Act
            var result = await controller.GetAvatar("TestUser", t: null);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_BannedUserWithoutToken_ReturnsUnauthorized()
        {
            // Arrange
            var empId = "BannedUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = empId, IsActive = true, IsBanned = true, FirstName = "Banned", LastName = "User" });
            _openIdAccountRepo.Save(new OpenIdAccount { Sub = empId, LinkedEmployeeId = empId });

            var user = CreateClaimsPrincipal(empId);
            var controller = CreateController(user: user);

            // Act
            var result = await controller.GetAvatar("TestUser", t: null);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_UnauthenticatedWithValidPushToken_AllowsAccess()
        {
            // Arrange
            var targetEmpId = "TargetUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = targetEmpId, FirstName = "Jane", LastName = "Doe", IsActive = true });

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var expiry = DateTime.UtcNow.AddMinutes(30).Ticks;
            var token = protector.Protect($"{targetEmpId}|{expiry}");

            var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(user: unauthenticatedUser);

            // Act
            var result = await controller.GetAvatar(targetEmpId, t: token);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);
            Assert.NotEmpty(fileResult.FileContents);
        }

        [Fact]
        public async Task GetAvatar_ExpiredPushToken_ReturnsUnauthorized()
        {
            // Arrange
            var targetEmpId = "TargetUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = targetEmpId, FirstName = "Jane", LastName = "Doe", IsActive = true });

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var expiry = DateTime.UtcNow.AddMinutes(-10).Ticks;
            var token = protector.Protect($"{targetEmpId}|{expiry}");

            var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(user: unauthenticatedUser);

            // Act
            var result = await controller.GetAvatar(targetEmpId, t: token);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_PushTokenForDifferentUser_ReturnsUnauthorized()
        {
            // Arrange
            var targetEmpId = "TargetUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = targetEmpId, FirstName = "Jane", LastName = "Doe", IsActive = true });

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var expiry = DateTime.UtcNow.AddMinutes(30).Ticks;
            var token = protector.Protect($"DifferentUser|{expiry}");

            var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(user: unauthenticatedUser);

            // Act
            var result = await controller.GetAvatar(targetEmpId, t: token);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Theory]
        [InlineData("not-a-valid-encrypted-token")]
        [InlineData("gibberish==")]
        public async Task GetAvatar_MalformedPushToken_ReturnsUnauthorized(string invalidToken)
        {
            // Arrange
            var controller = CreateController(user: new ClaimsPrincipal(new ClaimsIdentity()));

            // Act
            var result = await controller.GetAvatar("TestUser", t: invalidToken);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Theory]
        [InlineData("TestUser")] // Missing delimiter and ticks
        [InlineData("TestUser|")] // Missing ticks
        [InlineData("TestUser|NotNumericTicks")] // Non-numeric ticks
        [InlineData("TestUser|12345|ExtraPart")] // Too many parts
        public async Task GetAvatar_PushTokenInvalidPayloadFormat_ReturnsUnauthorized(string payload)
        {
            // Arrange
            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var token = protector.Protect(payload);

            var controller = CreateController(user: new ClaimsPrincipal(new ClaimsIdentity()));

            // Act
            var result = await controller.GetAvatar("TestUser", t: token);

            // Assert
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetAvatar_EmployeeNotFound_ReturnsCompanyIconFallback()
        {
            // Arrange & Act
            var result = await _controller.GetAvatar("NonExistentUserId", t: null);

            // Assert
            var physicalFileResult = Assert.IsType<PhysicalFileResult>(result);
            Assert.Contains("favicon.ico", physicalFileResult.FileName);
            Assert.Equal("image/x-icon", physicalFileResult.ContentType);
        }

        [Fact]
        public async Task GetAvatar_EmployeeNotFound_WithConfiguredCompanyIcon_ReturnsCompanyIcon()
        {
            // Arrange
            var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xFE, 0xED };
            _companyRepo.Save(new CompanyProfile
            {
                IconBase64 = "data:image/png;base64," + Convert.ToBase64String(iconBytes)
            });

            // Act
            var result = await _controller.GetAvatar("NonExistentUserId", t: null);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);
            Assert.Equal(iconBytes, fileResult.FileContents);
        }

        [Fact]
        public async Task GetAvatar_FileAccessProviderDeniesAccess_ReturnsForbid()
        {
            // Arrange
            var targetEmpId = "TargetUser_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee { Id = targetEmpId, FirstName = "Target", LastName = "User", IsActive = true });

            var mockProvider = new Mock<IFileAccessProvider>();
            mockProvider.Setup(p => p.Category).Returns("avatars");
            mockProvider.Setup(p => p.CanAccessAsync(It.IsAny<Employee>(), targetEmpId)).ReturnsAsync(false);

            var controller = CreateController(
                user: CreateClaimsPrincipal("TestUser"),
                providers: new[] { mockProvider.Object });

            // Act
            var result = await controller.GetAvatar(targetEmpId, t: null);

            // Assert
            Assert.IsType<ForbidResult>(result);
            mockProvider.Verify(p => p.CanAccessAsync(It.Is<Employee>(e => e.Id == "TestUser"), targetEmpId), Times.Once);
        }

        [Theory]
        [InlineData("../avatar.png")]
        [InlineData("folder/avatar.png")]
        [InlineData("folder\\avatar.png")]
        [InlineData("..\\avatar.png")]
        [InlineData("avatar..png")]
        [InlineData("/avatar.png")]
        [InlineData("\\avatar.png")]
        public async Task GetAvatar_InvalidAvatarFileName_ReturnsBadRequest(string invalidFileName)
        {
            // Arrange
            var empId = "InvalidAvatarEmp_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "Bad",
                LastName = "Avatar",
                AvatarFile = invalidFileName,
                IsActive = true
            });

            // Act
            var result = await _controller.GetAvatar(empId, t: null);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal("Invalid avatar file name.", badRequest.Value);
        }

        [Fact]
        public async Task GetAvatar_ValidAvatarFileExistsOnDisk_ReturnsPhysicalFileResult()
        {
            // Arrange
            var empId = "DiskAvatarEmp_" + Guid.NewGuid().ToString("N");
            var employeeDir = Path.Combine(_testDataPath, "Employees", empId);
            Directory.CreateDirectory(employeeDir);
            var avatarPath = Path.Combine(employeeDir, "my_avatar.png");
            File.WriteAllBytes(avatarPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x05, 0x06 });

            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "Disk",
                LastName = "User",
                AvatarFile = "my_avatar.png",
                IsActive = true
            });

            // Act
            var result = await _controller.GetAvatar(empId, t: null);

            // Assert
            var physicalFile = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physicalFile.ContentType);
            Assert.Equal(Path.GetFullPath(avatarPath), physicalFile.FileName);
        }

        [Fact]
        public async Task GetAvatar_NoAvatarFile_GeneratesDynamicAvatar()
        {
            // Arrange
            var empId = "DynamicEmp_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "Alice",
                LastName = "Smith",
                ProfileColor = "#4CAF50",
                AvatarFile = null,
                IsActive = true
            });

            // Act
            var result = await _controller.GetAvatar(empId, t: null);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);
            Assert.NotEmpty(fileResult.FileContents);
        }

        [Fact]
        public async Task GetAvatar_AvatarFileDoesNotExistOnDisk_FallsBackToDynamicAvatar()
        {
            // Arrange
            var empId = "MissingDiskEmp_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "Bob",
                LastName = "Builder",
                ProfileColor = "#FF9800",
                AvatarFile = "nonexistent_avatar.png",
                IsActive = true
            });

            // Act
            var result = await _controller.GetAvatar(empId, t: null);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);
            Assert.NotEmpty(fileResult.FileContents);
        }

        [Fact]
        public async Task GetAvatar_UnauthenticatedWithValidToken_ServesExistingAvatarFile()
        {
            // Arrange
            var empId = "TokenDiskEmp_" + Guid.NewGuid().ToString("N");
            var employeeDir = Path.Combine(_testDataPath, "Employees", empId);
            Directory.CreateDirectory(employeeDir);
            var avatarPath = Path.Combine(employeeDir, "token_avatar.png");
            File.WriteAllBytes(avatarPath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x11, 0x22 });

            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "Token",
                LastName = "Target",
                AvatarFile = "token_avatar.png",
                IsActive = true
            });

            var protector = _dataProtection.CreateProtector("AvatarPushToken");
            var token = protector.Protect($"{empId}|{DateTime.UtcNow.AddMinutes(15).Ticks}");

            var unauthenticatedUser = new ClaimsPrincipal(new ClaimsIdentity());
            var controller = CreateController(user: unauthenticatedUser);

            // Act
            var result = await controller.GetAvatar(empId, t: token);

            // Assert
            var physicalFile = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("image/png", physicalFile.ContentType);
            Assert.Equal(Path.GetFullPath(avatarPath), physicalFile.FileName);
        }

        [Fact]
        public async Task GetAvatar_NoAvatarProviderRegistered_AllowsAccess()
        {
            // Arrange
            var empId = "NoProviderEmp_" + Guid.NewGuid().ToString("N");
            _employeeRepo.Save(new Employee
            {
                Id = empId,
                FirstName = "No",
                LastName = "Provider",
                IsActive = true
            });

            var controller = CreateController(
                user: CreateClaimsPrincipal("TestUser"),
                providers: Enumerable.Empty<IFileAccessProvider>());

            // Act
            var result = await controller.GetAvatar(empId, t: null);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("image/png", fileResult.ContentType);
            Assert.NotEmpty(fileResult.FileContents);
        }
    }
}

