using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
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
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Security.Claims;
using System.Collections.Generic;

namespace Spokes_Server.Tests.Controllers
{
    public class MediaControllerTests : TestDataTestBase
    {
        private readonly string _webRootPath;
        private readonly MediaController _controller;
        private readonly CompanyProfileRepository _companyRepo;
        private readonly Mock<IWebHostEnvironment> _mockEnv;

        public MediaControllerTests()
        {
            _webRootPath = Path.Combine(Path.GetTempPath(), "Spokes_Test_WebRoot_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_webRootPath);

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            _companyRepo = new CompanyProfileRepository(writer, mockConfig.Object);

            _mockEnv = new Mock<IWebHostEnvironment>();
            _mockEnv.Setup(e => e.WebRootPath).Returns(_webRootPath);

            var mockDataProtectionProvider = new Mock<IDataProtectionProvider>();
            var employeeRepo = new EmployeeRepository(writer, mockConfig.Object);
            var openIdAccountRepo = new OpenIdAccountRepository(writer, mockConfig.Object);
            var mockIdentity = new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier, "TestUser"),
                new Claim("sub", "TestUser")
            }, "mock");
            
            var mockPrincipal = new ClaimsPrincipal(mockIdentity);

            employeeRepo.Save(new Employee { Id = "TestUser", IsActive = true, FirstName = "Test", LastName = "User" });
            openIdAccountRepo.Save(new OpenIdAccount { Sub = "TestUser", LinkedEmployeeId = "TestUser" });

            var mockHttpContext = new Mock<Microsoft.AspNetCore.Http.HttpContext>();
            mockHttpContext.Setup(h => h.User).Returns(mockPrincipal);

            var mockAuthStateProvider = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            mockAuthStateProvider
                .Setup(a => a.GetAuthenticationStateAsync())
                .ReturnsAsync(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(mockPrincipal));

            var userService = new UserService(mockAuthStateProvider.Object, employeeRepo, openIdAccountRepo);

            var avatarProvider = new AvatarFileAccessProvider();
            var providers = new List<IFileAccessProvider> { avatarProvider };

            var avatarGenerator = new AvatarGeneratorService(_mockEnv.Object);
            _controller = new MediaController(_companyRepo, employeeRepo, _mockEnv.Object, mockDataProtectionProvider.Object, avatarGenerator, mockConfig.Object, providers, userService);
            
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = mockHttpContext.Object
            };
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
    }
}

