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
using Spokes_Server.Core.Services;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Controllers
{
    public class PushControllerTests : TestDataTestBase
    {
        private readonly PushController _controller;
        private readonly DeviceSessionRepository _sessionRepo;
        private readonly EmployeeRepository _empRepo;
        private readonly CompanyProfileRepository _companyRepo;
        private readonly WebPushService _webPushService;
        private readonly UserService _userService;
        private readonly OpenIdAccountRepository _openIdRepo;

        public PushControllerTests()
        {
            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            var writer = new DiskPersistenceService(mockLogger.Object);

            _sessionRepo = new DeviceSessionRepository(writer, mockConfig.Object);
            _empRepo = new EmployeeRepository(writer, mockConfig.Object);
            _companyRepo = new CompanyProfileRepository(writer, mockConfig.Object);
            _openIdRepo = new OpenIdAccountRepository(writer, mockConfig.Object);

            var mockPresenceState = new Mock<PresenceStateService>(new ChatStateService());
            var mockWebPushLogger = new Mock<ILogger<WebPushService>>();
            var mockHttpClientFactory = new Mock<System.Net.Http.IHttpClientFactory>();
            var systemConfigRepo = new SystemConfigRepository(writer, mockConfig.Object);
            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(mockConfig.Object);
            var serverConfigRepo = new ServerConfigRepository(writer, mockConfig.Object, encService);
            _webPushService = new WebPushService(_sessionRepo, _empRepo, _companyRepo, systemConfigRepo, serverConfigRepo, mockPresenceState.Object, new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object), mockWebPushLogger.Object, mockConfig.Object, mockHttpClientFactory.Object, new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().Object, new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>().Object);

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();

            _userService = new UserService(mockAuthState.Object, _empRepo, _openIdRepo);

            _controller = new PushController(_sessionRepo, _empRepo, _webPushService, _userService);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        private void SetUser(string sub)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
                new Claim("sub", sub)
            }, "mock"));
            _controller.ControllerContext.HttpContext.User = user;
        }

        private void SetUserWithSession(string sub, string sessionId)
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
                new Claim("sub", sub),
                new Claim("SessionId", sessionId)
            }, "mock"));
            _controller.ControllerContext.HttpContext.User = user;
        }

        [Fact]
        public async Task GetVapidPublicKey_ReturnsBadRequest_WhenNotConfigured()
        {
            var result = await _controller.GetVapidPublicKey();
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Push notifications not configured", badRequestResult.Value.ToString());
        }

        [Fact]
        public async Task GetVapidPublicKey_ReturnsKey_WhenConfigured()
        {
            var profile = new CompanyProfile { VapidPublicKey = "key123", VapidPrivateKey = "priv123" };
            _companyRepo.Save(profile);

            var result = await _controller.GetVapidPublicKey();
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("key123", okResult.Value.ToString());
        }

        [Fact]
        public async Task Subscribe_ReturnsUnauthorized_WhenUserNotAuthenticated()
        {
            var req = new SubscribeRequest { Endpoint = "ep", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Subscribe_CreatesSubscription()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            // Create a DeviceSession so Subscribe can find it
            var session = new DeviceSession { Id = "session1", EmployeeId = "emp1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "session1");

            var req = new SubscribeRequest { Endpoint = "ep123", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Subscribed successfully", okResult.Value.ToString());

            var updated = _sessionRepo.GetByPushEndpoint("ep123");
            Assert.NotNull(updated);
            Assert.Equal("emp1", updated.EmployeeId);
            Assert.True(updated.PushEnabled);
        }

        [Fact]
        public async Task Unsubscribe_ClearsPushFields()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            // Create a DeviceSession with push data
            var session = new DeviceSession { Id = "session1", EmployeeId = "emp1", PushEndpoint = "ep123", PushEnabled = true };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "session1");

            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "ep123" });
            var okResult = Assert.IsType<OkObjectResult>(result);

            var updated = _sessionRepo.GetById("session1");
            Assert.NotNull(updated);
            Assert.Null(updated.PushEndpoint);
            Assert.False(updated.PushEnabled);
        }

        [Fact]
        public async Task GetStatus_ReturnsStatus()
        {
            _empRepo.Save(new Employee { Id = "emp1", PushNotificationsEnabled = true });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.GetStatus();
            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value.ToString();
            Assert.Contains("isConfigured", value);
        }

        [Fact]
        public async Task SendTestNotification_ReturnsOk()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.SendTestNotification();
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Test notification sent", okResult.Value.ToString());
        }
    }
}

