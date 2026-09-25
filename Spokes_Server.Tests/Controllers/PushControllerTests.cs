using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;

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
        private readonly ServerConfigRepository _serverConfigRepo;
        private readonly LicenseValidationService _licenseValidation;
        private readonly IDataProtectionProvider _dataProtection;
        private readonly DiskPersistenceService _writer;
        private readonly Mock<IConfiguration> _mockConfig;

        public PushControllerTests()
        {
            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _sessionRepo = new DeviceSessionRepository(_writer, _mockConfig.Object);
            _empRepo = new EmployeeRepository(_writer, _mockConfig.Object);
            _companyRepo = new CompanyProfileRepository(_writer, _mockConfig.Object);
            _openIdRepo = new OpenIdAccountRepository(_writer, _mockConfig.Object);

            var mockPresenceState = new Mock<PresenceStateService>(new ChatStateService());
            var mockWebPushLogger = new Mock<ILogger<WebPushService>>();
            var mockHttpClientFactory = new Mock<IHttpClientFactory>();
            var systemConfigRepo = new SystemConfigRepository(_writer, _mockConfig.Object);
            var encService = new EncryptionService(_mockConfig.Object);
            _serverConfigRepo = new ServerConfigRepository(_writer, _mockConfig.Object, encService);
            _licenseValidation = new LicenseValidationService(
                new Mock<ILogger<LicenseValidationService>>().Object,
                encService,
                new VersionMetadata("2026.8.102"));
            _webPushService = new WebPushService(_sessionRepo, _empRepo, _companyRepo, systemConfigRepo, _serverConfigRepo, mockPresenceState.Object, new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object), mockWebPushLogger.Object, _mockConfig.Object, mockHttpClientFactory.Object, new Mock<IDataProtectionProvider>().Object, new Mock<ISystemLogService>().Object, encService, _licenseValidation);

            var services = new ServiceCollection();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var sp = services.BuildServiceProvider();
            _dataProtection = sp.GetRequiredService<IDataProtectionProvider>();

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();

            _userService = new UserService(mockAuthState.Object, _empRepo, _openIdRepo);

            _controller = new PushController(_sessionRepo, _empRepo, _webPushService, _userService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
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

        private PushController CreateControllerWithWebPush(IWebPushService webPush)
        {
            return new PushController(_sessionRepo, _empRepo, webPush, _userService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = _controller.ControllerContext.HttpContext
                }
            };
        }

        private NotificationRoutingService CreateNotificationRoutingService()
        {
            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns(new List<ChatChannel>());
            mockServiceProvider.Setup(sp => sp.GetService(typeof(IChatChannelAccessService))).Returns(mockChatAccess.Object);

            return new NotificationRoutingService(
                _empRepo,
                new ProjectRepository(_writer, _mockConfig.Object),
                new ChatReadStateRepository(_writer, _mockConfig.Object, _companyRepo),
                new TeamRepository(_writer, _mockConfig.Object),
                _webPushService,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new GlobalKeystoreService(),
                new Mock<ICryptoService>().Object,
                _dataProtection,
                new EmailFolderRepository(_writer, _mockConfig.Object),
                new EmailMessageRepository(_writer, _mockConfig.Object),
                new ChatChannelRepository(_writer, _mockConfig.Object),
                new ChatMessageRepository(_writer, _mockConfig.Object, _companyRepo),
                _companyRepo,
                new Mock<ILogger<NotificationRoutingService>>().Object,
                mockServiceProvider.Object);
        }

        // ==========================================
        // GetVapidPublicKey Tests
        // ==========================================

        [Fact]
        public async Task GetVapidPublicKey_ReturnsBadRequest_WhenNotConfigured()
        {
            var result = await _controller.GetVapidPublicKey();
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Push notifications not configured", badRequestResult.Value?.ToString());
        }

        [Fact]
        public async Task GetVapidPublicKey_ReturnsKey_WhenConfigured()
        {
            var profile = new CompanyProfile { VapidPublicKey = "key123", VapidPrivateKey = "priv123" };
            _companyRepo.Save(profile);

            var result = await _controller.GetVapidPublicKey();
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("key123", okResult.Value?.ToString());
        }

        // ==========================================
        // Subscribe Tests
        // ==========================================

        [Fact]
        public async Task Subscribe_ReturnsUnauthorized_WhenUserNotAuthenticated()
        {
            var req = new SubscribeRequest { Endpoint = "ep", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Subscribe_ReturnsBadRequest_WhenEndpointIsEmpty()
        {
            SetUser("sub1");
            var req = new SubscribeRequest { Endpoint = "", P256dh = "p256", Auth = "auth" };
            var result = await _controller.Subscribe(req);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Invalid subscription data", badRequest.Value?.ToString());
        }

        [Theory]
        [InlineData("", "auth")]
        [InlineData("p256", "")]
        public async Task Subscribe_ReturnsBadRequest_WhenP256dhOrAuthMissing_ForNonNativeRelay(string p256dh, string auth)
        {
            SetUser("sub1");
            var req = new SubscribeRequest { Endpoint = "https://push.example.com", P256dh = p256dh, Auth = auth };
            var result = await _controller.Subscribe(req);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Invalid subscription data", badRequest.Value?.ToString());
        }

        [Fact]
        public async Task Subscribe_ReturnsBadRequest_WhenNoActiveSessionFound()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var req = new SubscribeRequest { Endpoint = "https://push.example.com", P256dh = "p256", Auth = "auth", DeviceId = "unknown_device" };
            var result = await _controller.Subscribe(req);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("No active session found", badRequest.Value?.ToString());
        }

        [Fact]
        public async Task Subscribe_CreatesSubscription()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "session1", EmployeeId = "emp1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "session1");

            var req = new SubscribeRequest { Endpoint = "ep123", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);

            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Subscribed successfully", okResult.Value?.ToString());

            var updated = _sessionRepo.GetByPushEndpoint("ep123");
            Assert.NotNull(updated);
            Assert.Equal("emp1", updated.EmployeeId);
            Assert.True(updated.PushEnabled);
        }

        [Fact]
        public async Task Subscribe_ResolvesSession_ViaDeviceIdFallback_WhenSessionIdMissing()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "session_dev", EmployeeId = "emp1", DeviceId = "device_fallback" };
            _sessionRepo.Save(session);
            SetUser("sub1");

            var req = new SubscribeRequest { Endpoint = "https://push.example.com/fb", P256dh = "dh", Auth = "auth", DeviceId = "device_fallback" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var updated = _sessionRepo.GetById("session_dev");
            Assert.NotNull(updated);
            Assert.Equal("https://push.example.com/fb", updated.PushEndpoint);
        }

        [Fact]
        public async Task Subscribe_ResolvesSession_ViaDeviceIdFallback_WhenSessionIdRevokedOrExpired()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var revokedSession = new DeviceSession { Id = "sess_revoked", EmployeeId = "emp1", DeviceId = "dev_shared", RevokedAt = DateTime.UtcNow };
            var activeSession = new DeviceSession { Id = "sess_active", EmployeeId = "emp1", DeviceId = "dev_shared" };
            _sessionRepo.Save(revokedSession);
            _sessionRepo.Save(activeSession);
            SetUserWithSession("sub1", "sess_revoked");

            var req = new SubscribeRequest { Endpoint = "https://push.example.com/rev", P256dh = "dh", Auth = "auth", DeviceId = "dev_shared" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var updatedActive = _sessionRepo.GetById("sess_active");
            Assert.Equal("https://push.example.com/rev", updatedActive?.PushEndpoint);
        }

        [Fact]
        public async Task Subscribe_DeduplicatesEndpoint_ClearingOtherSession_ForSameUser()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var oldSession = new DeviceSession { Id = "sess_old", EmployeeId = "emp1", PushEndpoint = "https://ep.shared", PushEnabled = true };
            var newSession = new DeviceSession { Id = "sess_new", EmployeeId = "emp1" };
            _sessionRepo.Save(oldSession);
            _sessionRepo.Save(newSession);
            SetUserWithSession("sub1", "sess_new");

            var req = new SubscribeRequest { Endpoint = "https://ep.shared", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var updatedOld = _sessionRepo.GetById("sess_old");
            Assert.Null(updatedOld?.PushEndpoint);
            Assert.False(updatedOld?.PushEnabled);
        }

        [Fact]
        public async Task Subscribe_DoesNotClearEndpoint_OnOtherUsersSession()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _empRepo.Save(new Employee { Id = "emp2" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub2", LinkedEmployeeId = "emp2" });
            var emp2Session = new DeviceSession { Id = "sess_emp2", EmployeeId = "emp2", PushEndpoint = "https://ep.cross", PushEnabled = true };
            var emp1Session = new DeviceSession { Id = "sess_emp1", EmployeeId = "emp1" };
            _sessionRepo.Save(emp2Session);
            _sessionRepo.Save(emp1Session);
            SetUserWithSession("sub1", "sess_emp1");

            var req = new SubscribeRequest { Endpoint = "https://ep.cross", P256dh = "dh", Auth = "auth" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var reloadedEmp2 = _sessionRepo.GetById("sess_emp2");
            Assert.Equal("https://ep.cross", reloadedEmp2?.PushEndpoint);
            Assert.True(reloadedEmp2?.PushEnabled);
        }

        [Fact]
        public async Task Subscribe_CleansStalePushSubscriptions_OnSameDevice()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var staleSession = new DeviceSession { Id = "sess_stale", EmployeeId = "emp1", DeviceId = "device_same", PushEndpoint = "https://old", PushEnabled = true };
            var currentSession = new DeviceSession { Id = "sess_current", EmployeeId = "emp1", DeviceId = "device_same" };
            _sessionRepo.Save(staleSession);
            _sessionRepo.Save(currentSession);
            SetUserWithSession("sub1", "sess_current");

            var req = new SubscribeRequest { Endpoint = "https://new", P256dh = "dh", Auth = "auth", DeviceId = "device_same" };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var reloadedStale = _sessionRepo.GetById("sess_stale");
            Assert.Null(reloadedStale?.PushEndpoint);
            Assert.False(reloadedStale?.PushEnabled);
        }

        [Fact]
        public async Task Subscribe_SetsDefaultValues_WhenOptionalFieldsOmitted()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "sess_defaults", EmployeeId = "emp1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "sess_defaults");

            var req = new SubscribeRequest
            {
                Endpoint = "https://endpoint.defaults",
                P256dh = "dh",
                Auth = "auth",
                DeviceName = "Custom Name"
            };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var reloaded = _sessionRepo.GetById("sess_defaults");
            Assert.Equal("WebPush", reloaded?.PushSubscriptionType);
            Assert.Equal("Desktop", reloaded?.DeviceType);
            Assert.False(reloaded?.IsIdleDetectionEnabled); // request.DeviceType was null, so (request.DeviceType == "Desktop") is false
            Assert.Equal("Custom Name", reloaded?.DeviceName);
        }

        [Fact]
        public async Task Subscribe_EnablesIdleDetection_WhenDeviceTypeIsDesktop()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "sess_desktop", EmployeeId = "emp1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "sess_desktop");

            var req = new SubscribeRequest
            {
                Endpoint = "https://endpoint.desktop",
                P256dh = "dh",
                Auth = "auth",
                DeviceType = "Desktop"
            };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var reloaded = _sessionRepo.GetById("sess_desktop");
            Assert.True(reloaded?.IsIdleDetectionEnabled);
        }

        [Fact]
        public async Task Subscribe_NativeRelay_ReturnsIsNativePushLicensed()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "session_native", EmployeeId = "emp1", DeviceId = "native_dev_1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "session_native");

            var req = new SubscribeRequest
            {
                Endpoint = "https://relay.spokes.com/api/push/send",
                SubscriptionType = "NativeRelay",
                DeviceType = "iOS",
                DeviceId = "native_dev_1"
            };

            var result = await _controller.Subscribe(req, _companyRepo, _serverConfigRepo, _licenseValidation);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value?.ToString();
            Assert.Contains("isNativePushLicensed", value);

            var reloaded = _sessionRepo.GetById("session_native");
            Assert.True(reloaded?.IsCapacitor);
            Assert.Equal("Mobile", reloaded?.DeviceType);
            Assert.False(reloaded?.IsIdleDetectionEnabled);
        }

        [Fact]
        public async Task Subscribe_WhenIsCapacitorTrue_ForcesMobileAndDisablesIdleDetection()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "sess_cap", EmployeeId = "emp1" };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "sess_cap");

            var req = new SubscribeRequest
            {
                Endpoint = "https://endpoint.cap",
                P256dh = "dh",
                Auth = "auth",
                DeviceType = "Desktop",
                IsCapacitor = true,
                IsIdleDetectionEnabled = true
            };
            var result = await _controller.Subscribe(req);
            Assert.IsType<OkObjectResult>(result);

            var reloaded = _sessionRepo.GetById("sess_cap");
            Assert.True(reloaded?.IsCapacitor);
            Assert.Equal("Mobile", reloaded?.DeviceType);
            Assert.False(reloaded?.IsIdleDetectionEnabled);
        }

        // ==========================================
        // Unsubscribe Tests
        // ==========================================

        [Fact]
        public async Task Unsubscribe_ReturnsBadRequest_WhenEndpointIsEmpty()
        {
            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "" });
            var badReq = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Endpoint is required", badReq.Value?.ToString());
        }

        [Fact]
        public async Task Unsubscribe_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://ep" });
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task Unsubscribe_ReturnsOk_WhenNoSubscriptionFound()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://nonexistent" });
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("No subscription found", ok.Value?.ToString());
        }

        [Fact]
        public async Task Unsubscribe_DoesNotClearSubscription_WhenBelongsToAnotherUser()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _empRepo.Save(new Employee { Id = "emp2" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub2", LinkedEmployeeId = "emp2" });
            var emp2Session = new DeviceSession { Id = "sess_emp2_unsub", EmployeeId = "emp2", PushEndpoint = "https://ep.other", PushEnabled = true };
            _sessionRepo.Save(emp2Session);
            SetUser("sub1");

            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://ep.other" });
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("No subscription found", ok.Value?.ToString());

            var reloaded = _sessionRepo.GetById("sess_emp2_unsub");
            Assert.Equal("https://ep.other", reloaded?.PushEndpoint);
            Assert.True(reloaded?.PushEnabled);
        }

        [Fact]
        public async Task Unsubscribe_ClearsPushFields()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            var session = new DeviceSession { Id = "session1", EmployeeId = "emp1", PushEndpoint = "ep123", PushEnabled = true };
            _sessionRepo.Save(session);
            SetUserWithSession("sub1", "session1");

            var result = await _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "ep123" });
            Assert.IsType<OkObjectResult>(result);

            var updated = _sessionRepo.GetById("session1");
            Assert.NotNull(updated);
            Assert.Null(updated.PushEndpoint);
            Assert.False(updated.PushEnabled);
        }

        // ==========================================
        // GetStatus Tests
        // ==========================================

        [Fact]
        public async Task GetStatus_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.GetStatus(_companyRepo, _serverConfigRepo, _licenseValidation);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetStatus_ReturnsStatus()
        {
            _empRepo.Save(new Employee { Id = "emp1", PushNotificationsEnabled = true });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.GetStatus(_companyRepo, _serverConfigRepo, _licenseValidation);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var value = okResult.Value?.ToString();
            Assert.Contains("isConfigured", value);
            Assert.Contains("nativePush", value);
        }

        // ==========================================
        // GetDevices Tests
        // ==========================================

        [Fact]
        public async Task GetDevices_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.GetDevices();
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetDevices_ReturnsActiveDevicesForCurrentUser()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _empRepo.Save(new Employee { Id = "emp2" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "d1", EmployeeId = "emp1", DeviceName = "Laptop", DeviceType = "Desktop", PushEndpoint = "ep1", PushEnabled = true });
            _sessionRepo.Save(new DeviceSession { Id = "d2", EmployeeId = "emp1", DeviceName = "Phone", DeviceType = "Mobile", PushEnabled = false });
            _sessionRepo.Save(new DeviceSession { Id = "d3", EmployeeId = "emp2", DeviceName = "Other Phone" });
            SetUser("sub1");

            var result = await _controller.GetDevices();
            var okResult = Assert.IsType<OkObjectResult>(result);
            var list = Assert.IsAssignableFrom<IEnumerable>(okResult.Value);
            var items = list.Cast<object>().ToList();
            Assert.Equal(2, items.Count);
        }

        [Fact]
        public async Task GetDevices_ExcludesRevokedOrExpiredSessions()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "d_active", EmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "d_revoked", EmployeeId = "emp1", RevokedAt = DateTime.UtcNow });
            _sessionRepo.Save(new DeviceSession { Id = "d_expired", EmployeeId = "emp1", ExpiresAt = DateTime.UtcNow.AddDays(-1) });
            SetUser("sub1");

            var result = await _controller.GetDevices();
            var okResult = Assert.IsType<OkObjectResult>(result);
            var list = Assert.IsAssignableFrom<IEnumerable>(okResult.Value);
            var items = list.Cast<object>().ToList();
            Assert.Single(items);
        }

        // ==========================================
        // UpdateDevice Tests
        // ==========================================

        [Fact]
        public async Task UpdateDevice_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.UpdateDevice("any", new UpdateDeviceRequest());
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task UpdateDevice_ReturnsNotFound_WhenDeviceNotFound()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.UpdateDevice("nonexistent_id", new UpdateDeviceRequest());
            var notFound = Assert.IsType<NotFoundObjectResult>(result);
            Assert.Contains("Device not found", notFound.Value?.ToString());
        }

        [Fact]
        public async Task UpdateDevice_ReturnsNotFound_WhenDeviceBelongsToAnotherUser()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _empRepo.Save(new Employee { Id = "emp2" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "sess_emp2", EmployeeId = "emp2" });
            SetUser("sub1");

            var result = await _controller.UpdateDevice("sess_emp2", new UpdateDeviceRequest { DeviceName = "Hacked" });
            Assert.IsType<NotFoundObjectResult>(result);

            var reloaded = _sessionRepo.GetById("sess_emp2");
            Assert.NotEqual("Hacked", reloaded?.DeviceName);
        }

        [Fact]
        public async Task UpdateDevice_UpdatesAllFieldsAndSaves()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "sess_upd", EmployeeId = "emp1", DeviceName = "OldName", DeviceType = "Desktop", PushEnabled = false, IsIdleDetectionEnabled = false });
            SetUser("sub1");

            var req = new UpdateDeviceRequest
            {
                DeviceName = "NewName",
                DeviceType = "Mobile",
                IsEnabled = true,
                IsIdleDetectionEnabled = true
            };
            var result = await _controller.UpdateDevice("sess_upd", req);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Device updated", ok.Value?.ToString());

            var updated = _sessionRepo.GetById("sess_upd");
            Assert.Equal("NewName", updated?.DeviceName);
            Assert.Equal("Mobile", updated?.DeviceType);
            Assert.True(updated?.PushEnabled);
            Assert.True(updated?.IsIdleDetectionEnabled);
        }

        [Fact]
        public async Task UpdateDevice_WhenSessionIsCapacitor_SuppressesIdleDetection()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession
            {
                Id = "sess_cap_upd",
                EmployeeId = "emp1",
                DeviceName = "Cap Phone",
                DeviceType = "Mobile",
                IsCapacitor = true,
                PushEnabled = true,
                IsIdleDetectionEnabled = false
            });
            SetUser("sub1");

            var req = new UpdateDeviceRequest
            {
                IsIdleDetectionEnabled = true
            };
            var result = await _controller.UpdateDevice("sess_cap_upd", req);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Device updated", ok.Value?.ToString());

            var updated = _sessionRepo.GetById("sess_cap_upd");
            Assert.False(updated?.IsIdleDetectionEnabled);
        }

        // ==========================================
        // DeleteDevice Tests
        // ==========================================

        [Fact]
        public async Task DeleteDevice_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.DeleteDevice("any");
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task DeleteDevice_ReturnsNotFound_WhenDeviceNotFound()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.DeleteDevice("nonexistent");
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task DeleteDevice_ReturnsNotFound_WhenDeviceBelongsToAnotherUser()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _empRepo.Save(new Employee { Id = "emp2" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "sess_other", EmployeeId = "emp2", PushEndpoint = "ep", PushEnabled = true });
            SetUser("sub1");

            var result = await _controller.DeleteDevice("sess_other");
            Assert.IsType<NotFoundObjectResult>(result);

            var reloaded = _sessionRepo.GetById("sess_other");
            Assert.Equal("ep", reloaded?.PushEndpoint);
        }

        [Fact]
        public async Task DeleteDevice_ClearsPushFieldsAndKeepsSession()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            _sessionRepo.Save(new DeviceSession { Id = "sess_del", EmployeeId = "emp1", PushEndpoint = "ep_to_del", PushEnabled = true, PushP256dh = "dh", PushAuth = "auth" });
            SetUser("sub1");

            var result = await _controller.DeleteDevice("sess_del");
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Device deleted", ok.Value?.ToString());

            var reloaded = _sessionRepo.GetById("sess_del");
            Assert.NotNull(reloaded);
            Assert.Null(reloaded.PushEndpoint);
            Assert.False(reloaded.PushEnabled);
        }

        // ==========================================
        // SendDeviceTestNotification Tests
        // ==========================================

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var mockLog = new Mock<ISystemLogService>();
            var result = await _controller.SendDeviceTestNotification("d1", _dataProtection, mockLog.Object);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsUnauthorized_WhenSenderNotFound()
        {
            var mockEmpRepo = new Mock<EmployeeRepository>(_writer, _mockConfig.Object);
            mockEmpRepo.Setup(e => e.GetById("emp_ghost")).Returns(new Employee { Id = "emp_ghost", IsActive = true });
            mockEmpRepo.Setup(e => e.GetByIdAsync("emp_ghost")).ReturnsAsync((Employee?)null);
            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, mockEmpRepo.Object, _openIdRepo);
            var controller = new PushController(_sessionRepo, mockEmpRepo.Object, _webPushService, userService)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("EmployeeId", "emp_ghost") }, "mock"));
            controller.ControllerContext.HttpContext.User = user;
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("d1", _dataProtection, mockLog.Object);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsOk_WhenEncrypted()
        {
            _empRepo.Save(new Employee { Id = "emp1", FirstName = "Mats" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var mockPush = new Mock<IWebPushService>();
            mockPush.Setup(p => p.SendDeviceTestNotificationAsync("dev1", "emp1", "Test Notification", It.Is<string>(s => s.Contains("Mats")), It.IsAny<string>(), "chat"))
                .ReturnsAsync(true);
            var controller = CreateControllerWithWebPush(mockPush.Object);
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("dev1", _dataProtection, mockLog.Object);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Test notification sent (Encrypted)", ok.Value?.ToString());
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsOk_WhenEncryptedText()
        {
            _empRepo.Save(new Employee { Id = "emp1", FirstName = "Mats" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var mockPush = new Mock<IWebPushService>();
            mockPush.Setup(p => p.SendDeviceTestNotificationAsync("dev1", "emp1", "Test Notification", It.IsAny<string>(), It.IsAny<string>(), "chat"))
                .ReturnsAsync(false);
            var controller = CreateControllerWithWebPush(mockPush.Object);
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("dev1", _dataProtection, mockLog.Object);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Test notification sent (Encrypted Text)", ok.Value?.ToString());
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenLicenseExpired()
        {
            _empRepo.Save(new Employee { Id = "emp1", FirstName = "Mats" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var mockPush = new Mock<IWebPushService>();
            mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new LicenseExpiredException("License expired"));
            var controller = CreateControllerWithWebPush(mockPush.Object);
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("dev1", _dataProtection, mockLog.Object);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("LICENSE_EXPIRED", badRequest.Value?.ToString());
            mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), "License expired"), Times.Once);
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenWebPushException()
        {
            _empRepo.Save(new Employee { Id = "emp1", FirstName = "Mats" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var mockPush = new Mock<IWebPushService>();
            var pushSub = new global::WebPush.PushSubscription("https://endpoint", "dh", "auth");
            var responseMsg = new HttpResponseMessage(HttpStatusCode.BadRequest);
            var wpe = new global::WebPush.WebPushException("Push rejected", pushSub, responseMsg);
            mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(wpe);
            var controller = CreateControllerWithWebPush(mockPush.Object);
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("dev1", _dataProtection, mockLog.Object);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("WebPush Error: Push rejected (Status: BadRequest)", badRequest.Value?.ToString());
            mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), It.Is<string>(s => s.Contains("WebPush Error"))), Times.Once);
        }

        [Fact]
        public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenGeneralException()
        {
            _empRepo.Save(new Employee { Id = "emp1", FirstName = "Mats" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var mockPush = new Mock<IWebPushService>();
            mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new InvalidOperationException("Something broke"));
            var controller = CreateControllerWithWebPush(mockPush.Object);
            var mockLog = new Mock<ISystemLogService>();

            var result = await controller.SendDeviceTestNotification("dev1", _dataProtection, mockLog.Object);
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Something broke", badRequest.Value?.ToString());
            mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), "Something broke"), Times.Once);
        }

        // ==========================================
        // SendTestNotification Tests
        // ==========================================

        [Fact]
        public async Task SendTestNotification_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var result = await _controller.SendTestNotification();
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task SendTestNotification_ReturnsOk()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var result = await _controller.SendTestNotification();
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("Test notification sent", okResult.Value?.ToString());
        }

        // ==========================================
        // GetUnreadCount Tests
        // ==========================================

        [Fact]
        public async Task GetUnreadCount_ReturnsUnauthorized_WhenNotAuthenticated()
        {
            _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            var routingService = CreateNotificationRoutingService();
            var result = await _controller.GetUnreadCount(routingService);
            Assert.IsType<UnauthorizedResult>(result);
        }

        [Fact]
        public async Task GetUnreadCount_ReturnsOkWithBreakdown_WhenAuthenticated()
        {
            _empRepo.Save(new Employee { Id = "emp1" });
            _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
            SetUser("sub1");

            var routingService = CreateNotificationRoutingService();
            var result = await _controller.GetUnreadCount(routingService);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
        }

        // ==========================================
        // MarkOffline Tests
        // ==========================================

        [Fact]
        public async Task MarkOffline_RemovesConnection_WhenSubscriptionIdProvided()
        {
            var presence = new PresenceStateService(new ChatStateService());
            presence.RegisterConnection("emp1", "conn1", "Desktop", hasPushEnabled: true, sessionId: "sess1", isFocused: true);
            Assert.True(presence.HasActiveConnection("emp1"));

            var result = await _controller.MarkOffline("conn1", presence);
            Assert.IsType<OkResult>(result);
            Assert.False(presence.HasActiveConnection("emp1"));
        }

        [Fact]
        public async Task MarkOffline_ReturnsOk_WhenSubscriptionIdIsNullOrEmpty()
        {
            var presence = new PresenceStateService(new ChatStateService());
            var resNull = await _controller.MarkOffline(null!, presence);
            Assert.IsType<OkResult>(resNull);

            var resEmpty = await _controller.MarkOffline("", presence);
            Assert.IsType<OkResult>(resEmpty);
        }

        // ==========================================
        // Unfocus Tests
        // ==========================================

        [Fact]
        public async Task Unfocus_SetsConnectionFocusFalse_WhenSubscriptionIdProvided()
        {
            var presence = new PresenceStateService(new ChatStateService());
            presence.RegisterConnection("emp1", "conn1", "Desktop", hasPushEnabled: true, sessionId: "sess1", isFocused: true);
            Assert.True(presence.IsUserFocused("emp1"));

            var mockClientState = new Mock<UserClientStateService>();
            var result = await _controller.Unfocus("conn1", presence, mockClientState.Object);
            Assert.IsType<OkResult>(result);
            Assert.False(presence.IsUserFocused("emp1"));
            mockClientState.Verify(c => c.FlushToDiskIfUnsent("emp1"), Times.Once);
        }

        [Fact]
        public async Task Unfocus_ReturnsOk_WhenSubscriptionIdIsNullOrEmpty()
        {
            var presence = new PresenceStateService(new ChatStateService());
            var mockClientState = new Mock<UserClientStateService>();

            var resNull = await _controller.Unfocus(null!, presence, mockClientState.Object);
            Assert.IsType<OkResult>(resNull);

            var resEmpty = await _controller.Unfocus("", presence, mockClientState.Object);
            Assert.IsType<OkResult>(resEmpty);
        }
    }
}
