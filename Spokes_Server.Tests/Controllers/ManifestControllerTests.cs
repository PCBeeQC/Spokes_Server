using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Http;
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
using Spokes_Server.Core.Constants;
using System.Security.Claims;
using System.Text.Json;

namespace Spokes_Server.Tests.Controllers
{
    public class ManifestControllerTests : TestDataTestBase
    {
        private readonly ManifestController _controller;
        private readonly CompanyProfileRepository _companyRepo;
        private readonly SystemConfigRepository _systemConfigRepo;
        private readonly EmployeeRepository _employeeRepo;

        public ManifestControllerTests()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            var mockShortcutIconService = new Mock<ShortcutIconService>(null, null, null, null);
            mockShortcutIconService
                .Setup(s => s.GetCompanyLogoBase64(It.IsAny<string>(), It.IsAny<int>()))
                .Returns((string logo, int size) => logo);

            _companyRepo = new CompanyProfileRepository(writer, mockConfig.Object);
            _systemConfigRepo = new SystemConfigRepository(writer, mockConfig.Object);
            _employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
            var openIdRepo = new OpenIdAccountRepository(writer, mockConfig.Object);

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeeRepo, openIdRepo);

            _controller = new ManifestController(_companyRepo, mockShortcutIconService.Object, _systemConfigRepo, _employeeRepo, userService);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        [Fact]
        public void Get_ReturnsDefaultManifest_WhenProfileIsEmpty()
        {
            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            Assert.Equal("application/manifest+json", contentResult.ContentType);
            Assert.Contains("\"My Spokes\"", contentResult.Content);
        }

        [Fact]
        public void Get_ReturnsProfileValues_WhenProfileExists()
        {
            var profile = new CompanyProfile { CompanyName = "Custom App", IconBase64 = "data:image/png;base64,xxxx" };
            _companyRepo.Save(profile);

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            Assert.Contains("Custom App", contentResult.Content);
        }

        [Fact]
        public void Get_SetsStartUrlToChat_WhenUserHasPermissions()
        {
            var emp = new Employee { Id = "emp1", IsAdmin = true };
            _employeeRepo.Save(emp);

            var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
                new Claim("EmployeeId", "emp1")
            }, "mock"));

            _controller.ControllerContext.HttpContext.User = user;

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            Assert.Contains("\"/chat\"", contentResult.Content);
        }

        [Fact]
        public void Get_SetsStartUrlToChat_WhenUserHasChatPermission()
        {
            var emp = new Employee
            {
                Id = "emp_chat_user",
                IsAdmin = false,
                IsActive = true,
                Permissions = new List<string> { AppPermissions.Chat.Use }
            };
            _employeeRepo.Save(emp);

            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("EmployeeId", emp.Id)
            }, "mock"));

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("/chat", doc.RootElement.GetProperty("start_url").GetString());
        }

        [Theory]
        [InlineData(false, false, false)] // Inactive
        [InlineData(true, true, false)]  // Suspended
        [InlineData(true, false, true)]  // Banned
        public void Get_KeepsStartUrlAsRoot_WhenAuthenticatedUserIsInactiveSuspendedOrBanned(bool isActive, bool isSuspended, bool isBanned)
        {
            var emp = new Employee
            {
                Id = $"emp_blocked_{isActive}_{isSuspended}_{isBanned}",
                IsAdmin = true,
                IsActive = isActive,
                IsSuspended = isSuspended,
                IsBanned = isBanned,
                Permissions = new List<string> { AppPermissions.Chat.Use }
            };
            _employeeRepo.Save(emp);

            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("EmployeeId", emp.Id)
            }, "mock"));

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("/", doc.RootElement.GetProperty("start_url").GetString());
        }

        [Fact]
        public void Get_KeepsStartUrlAsRoot_WhenAuthenticatedUserLacksChatPermissionAndNotAdmin()
        {
            var emp = new Employee
            {
                Id = "emp_no_chat_perm",
                IsAdmin = false,
                IsActive = true,
                Permissions = new List<string> { AppPermissions.Projects.View }
            };
            _employeeRepo.Save(emp);

            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("EmployeeId", emp.Id)
            }, "mock"));

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("/", doc.RootElement.GetProperty("start_url").GetString());
        }

        [Fact]
        public void Get_KeepsStartUrlAsRoot_WhenAuthenticatedEmployeeNotFound()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim("EmployeeId", "unknown_employee_id")
            }, "mock"));

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("/", doc.RootElement.GetProperty("start_url").GetString());
        }

        [Fact]
        public void Get_KeepsStartUrlAsRoot_WhenUserIsUnauthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("/", doc.RootElement.GetProperty("start_url").GetString());
        }

        [Fact]
        public void Get_IncludesPublicBrandingTokenInIconUrls_WhenCustomTokenConfigured()
        {
            var customToken = "brand-token-custom-xyz";
            var config = new SystemConfig { Id = "system_config", PublicBrandingToken = customToken };
            _systemConfigRepo.Save(config);

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            var icons = doc.RootElement.GetProperty("icons");
            Assert.Equal(2, icons.GetArrayLength());
            Assert.Equal($"/branding/{customToken}/icon.png", icons[0].GetProperty("src").GetString());
            Assert.Equal("192x192", icons[0].GetProperty("sizes").GetString());
            Assert.Equal("image/png", icons[0].GetProperty("type").GetString());
            Assert.Equal($"/branding/{customToken}/icon.png", icons[1].GetProperty("src").GetString());
            Assert.Equal("512x512", icons[1].GetProperty("sizes").GetString());
            Assert.Equal("image/png", icons[1].GetProperty("type").GetString());
        }

        [Fact]
        public void Get_IncludesSpokesVersion_MatchingLicenseValidationServiceAppVersion()
        {
            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal(
                Spokes_Server.Core.Services.Licensing.LicenseValidationService.AppVersion,
                doc.RootElement.GetProperty("spokes_version").GetString());
        }

        [Fact]
        public void Get_ReturnsFallbackSpokesName_WhenCompanyNameIsEmpty()
        {
            var profile = new CompanyProfile { CompanyName = "" };
            _companyRepo.Save(profile);

            var result = _controller.Get();
            var contentResult = Assert.IsType<ContentResult>(result);
            using var doc = JsonDocument.Parse(contentResult.Content!);
            Assert.Equal("Spokes", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal("Spokes", doc.RootElement.GetProperty("short_name").GetString());
            Assert.Equal("standalone", doc.RootElement.GetProperty("display").GetString());
            Assert.Equal("#1e1e2d", doc.RootElement.GetProperty("background_color").GetString());
            Assert.Equal("#1e1e2d", doc.RootElement.GetProperty("theme_color").GetString());
        }
    }
}

