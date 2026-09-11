using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
using Spokes_Server.Core.Constants;
using System.IO;
using System.Security.Claims;
using System.Text.Json;

namespace Spokes_Server.Tests.Controllers
{
    public class ManifestControllerTests : TestDataTestBase
    {
        private readonly ManifestController _controller;
        private readonly CompanyProfileRepository _companyRepo;
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
            var systemConfigRepo = new SystemConfigRepository(writer, mockConfig.Object);
            _employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
            var openIdRepo = new OpenIdAccountRepository(writer, mockConfig.Object);

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employeeRepo, openIdRepo);

            _controller = new ManifestController(_companyRepo, mockShortcutIconService.Object, systemConfigRepo, _employeeRepo, userService);

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
    }
}

