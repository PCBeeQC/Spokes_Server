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
using Spokes_Server.Core.Helpers;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;

namespace Spokes_Server.Tests.Controllers;

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
        _mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_writer, _mockConfig.Object);
        _empRepo = new EmployeeRepository(_writer, _mockConfig.Object);
        _companyRepo = new CompanyProfileRepository(_writer, _mockConfig.Object);
        _openIdRepo = new OpenIdAccountRepository(_writer, _mockConfig.Object);

        var mockPresenceState = new Mock<PresenceStateService>(new VoiceStateService());
        var mockWebPushLogger = new Mock<ILogger<WebPushService>>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var systemConfigRepo = new SystemConfigRepository(_writer, _mockConfig.Object);
        _serverConfigRepo = new ServerConfigRepository(_writer, _mockConfig.Object);
        _licenseValidation = new LicenseValidationService(
            new Mock<ILogger<LicenseValidationService>>().Object);
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        var sp = services.BuildServiceProvider();
        _dataProtection = sp.GetRequiredService<IDataProtectionProvider>();

        var coreContext = new CoreContext(_companyRepo, null!, null!, _serverConfigRepo, systemConfigRepo, _sessionRepo, null!, null!);
        var hrContext = new HrContext(_empRepo, null!, null!, null!, null!, null!, _companyRepo);
        _webPushService = new WebPushService(
            coreContext,
            hrContext,
            mockPresenceState.Object,
            new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _writer, _mockConfig.Object, _companyRepo),
            mockWebPushLogger.Object,
            mockHttpClientFactory.Object,
            new Mock<ISystemLogService>().Object,
            _licenseValidation,
            dataProtection: _dataProtection);

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();

        _userService = new UserService(mockAuthState.Object, _empRepo, _openIdRepo);

        _controller = new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            licenseEligibility: new PushLicenseEligibilityService(_companyRepo, _serverConfigRepo, _licenseValidation),
            logger: null,
            dataProtection: _dataProtection)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static string CreateValidRsaPublicKeyBase64()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
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

    private PushController CreateControllerWithWebPush(IWebPushService webPush, ISystemLogService? systemLog = null)
    {
        return new PushController(
            _sessionRepo,
            _empRepo,
            webPush,
            _userService,
            licenseEligibility: new PushLicenseEligibilityService(_companyRepo, _serverConfigRepo, _licenseValidation),
            logger: null,
            dataProtection: _dataProtection,
            systemLog: systemLog)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = _controller.ControllerContext.HttpContext
            }
        };
    }

    private PushController CreateControllerWithServices(
        NotificationRoutingService? routingService = null,
        ISystemLogService? systemLog = null)
    {
        return new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            licenseEligibility: new PushLicenseEligibilityService(_companyRepo, _serverConfigRepo, _licenseValidation),
            logger: null,
            dataProtection: _dataProtection,
            systemLog: systemLog,
            routingService: routingService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = _controller.ControllerContext.HttpContext
            }
        };
    }

    private NotificationRoutingService CreateNotificationRoutingService()
    {
        var mockChatAccess = new Mock<IChatChannelAccessService>();
        mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns(new List<ChatChannel>());

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ChatChannels).Returns(new ChatChannelRepository(_writer, _mockConfig.Object));
        commContext.Setup(c => c.ChatMessages).Returns(new ChatMessageRepository(_writer, _mockConfig.Object, _companyRepo));
        commContext.Setup(c => c.ChatReadStates).Returns(new ChatReadStateRepository(_writer, _mockConfig.Object, _companyRepo));
        commContext.Setup(c => c.EmailFolders).Returns(new EmailFolderRepository(_writer, _mockConfig.Object));
        commContext.Setup(c => c.EmailMessages).Returns(new EmailMessageRepository(_writer, _mockConfig.Object));

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_empRepo);
        hrContext.Setup(h => h.Teams).Returns(new TeamRepository(_writer, _mockConfig.Object));

        var projectsContext = new Mock<ProjectsContext>();
        projectsContext.Setup(p => p.Projects).Returns(new ProjectRepository(_writer, _mockConfig.Object));

        var coreContext = new Mock<CoreContext>();
        coreContext.Setup(c => c.CompanyProfile).Returns(_companyRepo);

        return new NotificationRoutingService(
            commContext.Object,
            hrContext.Object,
            coreContext.Object,
            _webPushService,
            new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _writer, _mockConfig.Object, _companyRepo),
            new GlobalKeystoreService(),
            new Mock<ICryptoService>().Object,
            _dataProtection,
            mockChatAccess.Object,
            logger: new Mock<ILogger<NotificationRoutingService>>().Object);
    }

    // ==========================================
    // Subscribe Tests
    // ==========================================

    [Fact]
    public void Subscribe_ReturnsUnauthorized_WhenUserNotAuthenticated()
    {
        var req = new SubscribeRequest { Endpoint = "ep", P256dh = "dh", Auth = "auth" };
        var result = _controller.Subscribe(req);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void Subscribe_ReturnsBadRequest_WhenEndpointIsEmpty()
    {
        SetUser("sub1");
        var req = new SubscribeRequest { Endpoint = "", P256dh = "p256", Auth = "auth" };
        var result = _controller.Subscribe(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid subscription data", badRequest.Value?.ToString());
    }

    [Theory]
    [InlineData("", "auth")]
    [InlineData("p256", "")]
    public void Subscribe_ReturnsBadRequest_WhenP256dhOrAuthMissing_ForNonNativeRelay(string p256dh, string auth)
    {
        SetUser("sub1");
        var req = new SubscribeRequest { Endpoint = "https://push.example.com", P256dh = p256dh, Auth = auth };
        var result = _controller.Subscribe(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid subscription data", badRequest.Value?.ToString());
    }

    [Fact]
    public void Subscribe_ReturnsBadRequest_WhenNoActiveSessionFound()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var req = new SubscribeRequest { Endpoint = "https://push.example.com", P256dh = "p256", Auth = "auth", DeviceId = "unknown_device" };
        var result = _controller.Subscribe(req);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No active session found", badRequest.Value?.ToString());
    }

    [Fact]
    public void Subscribe_CreatesSubscription()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var session = new DeviceSession { Id = "session1", EmployeeId = "emp1" };
        _sessionRepo.Save(session);
        SetUserWithSession("sub1", "session1");

        var req = new SubscribeRequest { Endpoint = "ep123", P256dh = "dh", Auth = "auth" };
        var result = _controller.Subscribe(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Subscribed successfully", okResult.Value?.ToString());

        var updated = _sessionRepo.GetByPushEndpoint("ep123");
        Assert.NotNull(updated);
        Assert.Equal("emp1", updated.EmployeeId);
        Assert.True(updated.PushEnabled);
    }

    [Fact]
    public void Subscribe_ResolvesSession_ViaDeviceIdFallback_WhenSessionIdMissing()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var session = new DeviceSession { Id = "session_dev", EmployeeId = "emp1", DeviceId = "device_fallback" };
        _sessionRepo.Save(session);
        SetUser("sub1");

        var req = new SubscribeRequest { Endpoint = "https://push.example.com/fb", P256dh = "dh", Auth = "auth", DeviceId = "device_fallback" };
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("session_dev");
        Assert.NotNull(updated);
        Assert.Equal("https://push.example.com/fb", updated.PushEndpoint);
    }

    [Fact]
    public void Subscribe_ResolvesSession_ViaDeviceIdFallback_WhenSessionIdRevokedOrExpired()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var revokedSession = new DeviceSession { Id = "sess_revoked", EmployeeId = "emp1", DeviceId = "dev_shared", RevokedAt = DateTime.UtcNow };
        var activeSession = new DeviceSession { Id = "sess_active", EmployeeId = "emp1", DeviceId = "dev_shared" };
        _sessionRepo.Save(revokedSession);
        _sessionRepo.Save(activeSession);
        SetUserWithSession("sub1", "sess_revoked");

        var req = new SubscribeRequest { Endpoint = "https://push.example.com/rev", P256dh = "dh", Auth = "auth", DeviceId = "dev_shared" };
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updatedActive = _sessionRepo.GetById("sess_active");
        Assert.Equal("https://push.example.com/rev", updatedActive?.PushEndpoint);
    }

    [Fact]
    public void Subscribe_DeduplicatesEndpoint_ClearingOtherSession_ForSameUser()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var oldSession = new DeviceSession { Id = "sess_old", EmployeeId = "emp1", PushEndpoint = "https://ep.shared", PushEnabled = true };
        var newSession = new DeviceSession { Id = "sess_new", EmployeeId = "emp1" };
        _sessionRepo.Save(oldSession);
        _sessionRepo.Save(newSession);
        SetUserWithSession("sub1", "sess_new");

        var req = new SubscribeRequest { Endpoint = "https://ep.shared", P256dh = "dh", Auth = "auth" };
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updatedOld = _sessionRepo.GetById("sess_old");
        Assert.Null(updatedOld?.PushEndpoint);
        Assert.False(updatedOld?.PushEnabled);
    }

    [Fact]
    public void Subscribe_DoesNotClearEndpoint_OnOtherUsersSession()
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
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var reloadedEmp2 = _sessionRepo.GetById("sess_emp2");
        Assert.Equal("https://ep.cross", reloadedEmp2?.PushEndpoint);
        Assert.True(reloadedEmp2?.PushEnabled);
    }

    [Fact]
    public void Subscribe_CleansStalePushSubscriptions_OnSameDevice()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var staleSession = new DeviceSession { Id = "sess_stale", EmployeeId = "emp1", DeviceId = "device_same", PushEndpoint = "https://old", PushEnabled = true };
        var currentSession = new DeviceSession { Id = "sess_current", EmployeeId = "emp1", DeviceId = "device_same" };
        _sessionRepo.Save(staleSession);
        _sessionRepo.Save(currentSession);
        SetUserWithSession("sub1", "sess_current");

        var req = new SubscribeRequest { Endpoint = "https://new", P256dh = "dh", Auth = "auth", DeviceId = "device_same" };
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var reloadedStale = _sessionRepo.GetById("sess_stale");
        Assert.Null(reloadedStale?.PushEndpoint);
        Assert.False(reloadedStale?.PushEnabled);
    }

    [Fact]
    public void Subscribe_SetsDefaultValues_WhenOptionalFieldsOmitted()
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
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var reloaded = _sessionRepo.GetById("sess_defaults");
        Assert.Equal(PushSubscriptionType.WebPush, reloaded?.PushSubscriptionType);
        Assert.Equal(DeviceType.Desktop, reloaded?.DeviceType);
        Assert.Equal("Custom Name", reloaded?.DeviceName);
    }

    [Fact]
    public void Subscribe_NativeRelay_ReturnsIsNativePushLicensed()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var session = new DeviceSession { Id = "session_native", EmployeeId = "emp1", DeviceId = "native_dev_1" };
        _sessionRepo.Save(session);
        SetUserWithSession("sub1", "session_native");

        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/api/push/send",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceType = "iOS",
            DeviceId = "native_dev_1",
            PublicKey = CreateValidRsaPublicKeyBase64()
        };

        var result = _controller.Subscribe(req);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value?.ToString();
        Assert.Contains("isNativePushLicensed", value);

        var reloaded = _sessionRepo.GetById("session_native");
        Assert.True(reloaded?.IsCapacitor);
        Assert.Equal(DeviceType.Mobile, reloaded?.DeviceType);
    }

    [Fact]
    public void Subscribe_WhenIsCapacitorTrue_ForcesMobileDeviceType()
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
            DeviceType = DeviceType.Desktop,
            IsCapacitor = true
        };
        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var reloaded = _sessionRepo.GetById("sess_cap");
        Assert.True(reloaded?.IsCapacitor);
        Assert.Equal(DeviceType.Mobile, reloaded?.DeviceType);
    }

    // ==========================================
    // Unsubscribe Tests
    // ==========================================

    [Fact]
    public void Unsubscribe_ReturnsBadRequest_WhenEndpointIsEmpty()
    {
        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "" });
        var badReq = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Endpoint is required", badReq.Value?.ToString());
    }

    [Fact]
    public void Unsubscribe_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://ep" });
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void Unsubscribe_ReturnsOk_WhenNoSubscriptionFound()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://nonexistent" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("No subscription found", ok.Value?.ToString());
    }

    [Fact]
    public void Unsubscribe_DoesNotClearSubscription_WhenBelongsToAnotherUser()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _empRepo.Save(new Employee { Id = "emp2" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub2", LinkedEmployeeId = "emp2" });
        var emp2Session = new DeviceSession { Id = "sess_emp2_unsub", EmployeeId = "emp2", PushEndpoint = "https://ep.other", PushEnabled = true };
        _sessionRepo.Save(emp2Session);
        SetUser("sub1");

        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "https://ep.other" });
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("No subscription found", ok.Value?.ToString());

        var reloaded = _sessionRepo.GetById("sess_emp2_unsub");
        Assert.Equal("https://ep.other", reloaded?.PushEndpoint);
        Assert.True(reloaded?.PushEnabled);
    }

    [Fact]
    public void Unsubscribe_ClearsPushFields()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        var session = new DeviceSession { Id = "session1", EmployeeId = "emp1", PushEndpoint = "ep123", PushEnabled = true };
        _sessionRepo.Save(session);
        SetUserWithSession("sub1", "session1");

        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "ep123" });
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
    public void GetStatus_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = _controller.GetStatus();
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetStatus_ReturnsStatus()
    {
        _empRepo.Save(new Employee { Id = "emp1", PushNotificationsEnabled = true });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var result = _controller.GetStatus();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value?.ToString();
        Assert.Contains("isConfigured", value);
        Assert.Contains("nativePush", value);
    }

    // ==========================================
    // UpdateDevice Tests
    // ==========================================

    [Fact]
    public void UpdateDevice_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = _controller.UpdateDevice("any", new UpdateDeviceRequest());
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void UpdateDevice_ReturnsNotFound_WhenDeviceNotFound()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var result = _controller.UpdateDevice("nonexistent_id", new UpdateDeviceRequest());
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("Device not found", notFound.Value?.ToString());
    }

    [Fact]
    public void UpdateDevice_ReturnsNotFound_WhenDeviceBelongsToAnotherUser()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _empRepo.Save(new Employee { Id = "emp2" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        _sessionRepo.Save(new DeviceSession { Id = "sess_emp2", EmployeeId = "emp2" });
        SetUser("sub1");

        var result = _controller.UpdateDevice("sess_emp2", new UpdateDeviceRequest { DeviceName = "Hacked" });
        Assert.IsType<NotFoundObjectResult>(result);

        var reloaded = _sessionRepo.GetById("sess_emp2");
        Assert.NotEqual("Hacked", reloaded?.DeviceName);
    }

    [Fact]
    public void UpdateDevice_UpdatesAllFieldsAndSaves()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        _sessionRepo.Save(new DeviceSession { Id = "sess_upd", EmployeeId = "emp1", DeviceName = "OldName", DeviceType = DeviceType.Desktop, PushEnabled = false });
        SetUser("sub1");

        var req = new UpdateDeviceRequest
        {
            DeviceName = "NewName",
            DeviceType = DeviceType.Mobile,
            IsEnabled = true
        };
        var result = _controller.UpdateDevice("sess_upd", req);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Device updated", ok.Value?.ToString());

        var updated = _sessionRepo.GetById("sess_upd");
        Assert.Equal("NewName", updated?.DeviceName);
        Assert.Equal(DeviceType.Mobile, updated?.DeviceType);
        Assert.True(updated?.PushEnabled);
    }

    // ==========================================
    // DeleteDevice Tests
    // ==========================================

    [Fact]
    public void DeleteDevice_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = _controller.DeleteDevice("any");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void DeleteDevice_ReturnsNotFound_WhenDeviceNotFound()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var result = _controller.DeleteDevice("nonexistent");
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void DeleteDevice_ReturnsNotFound_WhenDeviceBelongsToAnotherUser()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _empRepo.Save(new Employee { Id = "emp2" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        _sessionRepo.Save(new DeviceSession { Id = "sess_other", EmployeeId = "emp2", PushEndpoint = "ep", PushEnabled = true });
        SetUser("sub1");

        var result = _controller.DeleteDevice("sess_other");
        Assert.IsType<NotFoundObjectResult>(result);

        var reloaded = _sessionRepo.GetById("sess_other");
        Assert.Equal("ep", reloaded?.PushEndpoint);
    }

    [Fact]
    public void DeleteDevice_ClearsPushFieldsAndKeepsSession()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        _sessionRepo.Save(new DeviceSession { Id = "sess_del", EmployeeId = "emp1", PushEndpoint = "ep_to_del", PushEnabled = true, PushP256dh = "dh", PushAuth = "auth" });
        SetUser("sub1");

        var result = _controller.DeleteDevice("sess_del");
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
        var result = await _controller.SendDeviceTestNotification("d1");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsUnauthorized_WhenSenderNotFound()
    {
        var mockEmpRepo = new Mock<EmployeeRepository>(_writer, _mockConfig.Object);
        mockEmpRepo.SetupSequence(e => e.GetById("emp_ghost"))
            .Returns(new Employee { Id = "emp_ghost", IsActive = true })
            .Returns((Employee?)null);
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, mockEmpRepo.Object, _openIdRepo);
        var controller = new PushController(
            _sessionRepo,
            mockEmpRepo.Object,
            _webPushService,
            userService,
            licenseEligibility: new PushLicenseEligibilityService(_companyRepo, _serverConfigRepo, _licenseValidation),
            logger: null,
            dataProtection: _dataProtection)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("EmployeeId", "emp_ghost") }, "mock"));
        controller.ControllerContext.HttpContext.User = user;

        var result = await controller.SendDeviceTestNotification("d1");
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsOk_WhenEncrypted()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.Is<DeviceTestNotificationRequest>(r => r.SubscriptionId == "dev1" && r.UserId == "emp1" && r.Body.Contains("Alex") && r.Category == "chat"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestNotificationResult.Success(isPayloadEncrypted: true));
        var controller = CreateControllerWithWebPush(mockPush.Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Test notification sent (Encrypted)", ok.Value?.ToString());
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsOk_WhenEncryptedText()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.Is<DeviceTestNotificationRequest>(r => r.SubscriptionId == "dev1" && r.UserId == "emp1" && r.Category == "chat"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestNotificationResult.Success(isPayloadEncrypted: false));
        var controller = CreateControllerWithWebPush(mockPush.Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("Test notification sent (Content-Free Wakeup - Re-enable Push to Encrypt)", ok.Value?.ToString());
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenLicenseExpired()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<DeviceTestNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LicenseExpiredException("License expired"));
        var mockLog = new Mock<ISystemLogService>();
        var controller = CreateControllerWithWebPush(mockPush.Object, mockLog.Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("LICENSE_EXPIRED", badRequest.Value?.ToString());
        mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), "LICENSE_EXPIRED: License expired"), Times.Once);
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsRelayProblemCode_WhenRelayRejects()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<DeviceTestNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RelayRejectedException("License Expired: trial over.", "TRIAL_EXPIRED"));
        var controller = CreateControllerWithWebPush(mockPush.Object, new Mock<ISystemLogService>().Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("TRIAL_EXPIRED", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenWebPushException()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        var wpe = new Lib.Net.Http.WebPush.PushServiceClientException("Push rejected", HttpStatusCode.BadRequest);
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<DeviceTestNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(wpe);
        var mockLog = new Mock<ISystemLogService>();
        var controller = CreateControllerWithWebPush(mockPush.Object, mockLog.Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("WebPush Error: Push rejected (Status: BadRequest)", badRequest.Value?.ToString());
        mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), It.Is<string>(s => s.Contains("WebPush Error"))), Times.Once);
    }

    [Fact]
    public async Task SendDeviceTestNotification_ReturnsBadRequest_WhenGeneralException()
    {
        _empRepo.Save(new Employee { Id = "emp1", FirstName = "Alex" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var mockPush = new Mock<IWebPushService>();
        mockPush.Setup(p => p.SendDeviceTestNotificationAsync(It.IsAny<DeviceTestNotificationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Something broke"));
        var mockLog = new Mock<ISystemLogService>();
        var controller = CreateControllerWithWebPush(mockPush.Object, mockLog.Object);

        var result = await controller.SendDeviceTestNotification("dev1");
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Something broke", badRequest.Value?.ToString());
        mockLog.Verify(l => l.LogError("Notifications", It.Is<string>(s => s.Contains("dev1")), "Something broke"), Times.Once);
    }

    // ==========================================
    // GetUnreadCount Tests
    // ==========================================

    [Fact]
    public void GetUnreadCount_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        var routingService = CreateNotificationRoutingService();
        var controller = CreateControllerWithServices(routingService: routingService);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        var result = controller.GetUnreadCount();
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        var payload = Assert.IsType<SpokesResult>(unauthorized.Value);
        Assert.False(payload.IsSuccess);
    }

    [Fact]
    public void GetUnreadCount_ReturnsOkWithBreakdown_WhenAuthenticated()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var routingService = CreateNotificationRoutingService();
        var controller = CreateControllerWithServices(routingService: routingService);
        var result = controller.GetUnreadCount();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetUnreadCount_ReturnsStatusCodeError_WhenRoutingServiceIsNull()
    {
        _empRepo.Save(new Employee { Id = "emp1" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub1", LinkedEmployeeId = "emp1" });
        SetUser("sub1");

        var controller = CreateControllerWithServices(routingService: null);
        var result = controller.GetUnreadCount();
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(objResult.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("Notification routing service not available.", payload.ErrorMessage);
        Assert.Equal("SERVICE_UNAVAILABLE", payload.Code);
    }

    [Fact]
    public void PushController_WithPushLicenseEligibilityService_EvaluatesNativeRelayLicenseWithoutActionParameters()
    {
        var mockEligibility = new Mock<IPushLicenseEligibilityService>();
        mockEligibility.Setup(e => e.IsNativePushAllowed()).Returns(true);
        mockEligibility.Setup(e => e.GetNativePushStatus()).Returns(new NativePushAvailability(true, "Active", "Licensed"));

        var controller = new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            logger: null,
            licenseEligibility: mockEligibility.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        _empRepo.Save(new Employee { Id = "emp_elig" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_elig", LinkedEmployeeId = "emp_elig" });
        var session = new DeviceSession { Id = "sess_elig", EmployeeId = "emp_elig", DeviceId = "dev_elig" };
        _sessionRepo.Save(session);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_elig"),
            new Claim("SessionId", "sess_elig")
        }, "mock"));
        controller.ControllerContext.HttpContext.User = user;

        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/send",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev_elig",
            PublicKey = CreateValidRsaPublicKeyBase64()
        };

        var subscribeResult = controller.Subscribe(req);
        var okSubscribe = Assert.IsType<OkObjectResult>(subscribeResult);
        var resp = Assert.IsType<PushSubscribeResponse>(okSubscribe.Value);
        Assert.True(resp.isNativePushLicensed);
        mockEligibility.Verify(e => e.IsNativePushAllowed(), Times.Once);

        var statusResult = controller.GetStatus();
        var okStatus = Assert.IsType<OkObjectResult>(statusResult);
        var statusResp = Assert.IsType<PushStatusResponse>(okStatus.Value);
        Assert.True(statusResp.nativePush.isAvailable);
        Assert.Equal("Active", statusResp.nativePush.status);
        mockEligibility.Verify(e => e.GetNativePushStatus(), Times.Once);
    }

    [Fact]
    public void Subscribe_WhenResubscribingWithNullOrEmptyPublicKey_PreservesExistingPushPublicKey()
    {
        _empRepo.Save(new Employee { Id = "emp_key_preserve" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_key_preserve", LinkedEmployeeId = "emp_key_preserve" });

        var existingKey = CreateValidRsaPublicKeyBase64();
        var session = new DeviceSession
        {
            Id = "sess_key_preserve",
            EmployeeId = "emp_key_preserve",
            DeviceId = "dev_key_preserve",
            PushPublicKey = existingKey
        };
        _sessionRepo.Save(session);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_key_preserve"),
            new Claim("SessionId", "sess_key_preserve")
        }, "mock"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/send",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev_key_preserve",
            PublicKey = null
        };

        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updatedSession = _sessionRepo.GetById("sess_key_preserve");
        Assert.NotNull(updatedSession);
        Assert.Equal(existingKey, updatedSession.PushPublicKey);
    }

    [Fact]
    public void Subscribe_NativeRelay_ReturnsForbidden_WhenLicenseExpired()
    {
        _empRepo.Save(new Employee { Id = "emp_exp" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_exp", LinkedEmployeeId = "emp_exp" });
        var session = new DeviceSession { Id = "sess_exp", EmployeeId = "emp_exp", DeviceId = "dev_exp" };
        _sessionRepo.Save(session);

        var mockEligibility = new Mock<IPushLicenseEligibilityService>();
        mockEligibility.Setup(e => e.IsNativePushAllowed()).Returns(false);

        var controller = new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            logger: null,
            licenseEligibility: mockEligibility.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_exp"),
            new Claim("SessionId", "sess_exp")
        }, "mock"));
        controller.ControllerContext.HttpContext.User = user;

        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/send",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev_exp"
        };

        var result = controller.Subscribe(req);
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objResult.StatusCode);
        var payload = Assert.IsType<SpokesResult>(objResult.Value);
        Assert.False(payload.IsSuccess);
        Assert.Equal("PASS_EXPIRED", payload.Code);

        // Verify session was NOT updated with push info
        var notUpdatedSession = _sessionRepo.GetById("sess_exp");
        Assert.NotNull(notUpdatedSession);
        Assert.Null(notUpdatedSession.PushEndpoint);
        Assert.False(notUpdatedSession.PushEnabled);
    }

    [Fact]
    public void Subscribe_DesktopNativeRelay_PreservesDesktopDeviceType()
    {
        _empRepo.Save(new Employee { Id = "emp_desk" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_desk", LinkedEmployeeId = "emp_desk" });
        var session = new DeviceSession
        {
            Id = "sess_desk",
            EmployeeId = "emp_desk",
            DeviceId = "dev_desk",
            DeviceType = DeviceType.Desktop,
            IsCapacitor = false
        };
        _sessionRepo.Save(session);

        var mockEligibility = new Mock<IPushLicenseEligibilityService>();
        mockEligibility.Setup(e => e.IsNativePushAllowed()).Returns(true);

        var controller = new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            logger: null,
            licenseEligibility: mockEligibility.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_desk"),
            new Claim("SessionId", "sess_desk")
        }, "mock"));
        controller.ControllerContext.HttpContext.User = user;

        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/send",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "dev_desk",
            DeviceType = DeviceType.Desktop,
            DeviceName = "Windows Desktop App",
            PublicKey = CreateValidRsaPublicKeyBase64()
        };

        var result = controller.Subscribe(req);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult);

        var updated = _sessionRepo.GetById("sess_desk");
        Assert.NotNull(updated);
        Assert.Equal("https://relay.spokes.com/send", updated.PushEndpoint);
        Assert.Equal(PushSubscriptionType.NativeRelay, updated.PushSubscriptionType);
        Assert.Equal(DeviceType.Desktop, updated.DeviceType);
        Assert.False(updated.IsCapacitor);
        Assert.Equal("Windows Desktop App", updated.DeviceName);
    }

    [Fact]
    public void Subscribe_WebPush_Succeeds_EvenWhenNativePushExpired()
    {
        _empRepo.Save(new Employee { Id = "emp_web" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_web", LinkedEmployeeId = "emp_web" });
        var session = new DeviceSession
        {
            Id = "sess_web",
            EmployeeId = "emp_web",
            DeviceId = "dev_web"
        };
        _sessionRepo.Save(session);

        var mockEligibility = new Mock<IPushLicenseEligibilityService>();
        // Native push is disabled/expired!
        mockEligibility.Setup(e => e.IsNativePushAllowed()).Returns(false);

        var controller = new PushController(
            _sessionRepo,
            _empRepo,
            _webPushService,
            _userService,
            logger: null,
            licenseEligibility: mockEligibility.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_web"),
            new Claim("SessionId", "sess_web")
        }, "mock"));
        controller.ControllerContext.HttpContext.User = user;

        var req = new SubscribeRequest
        {
            Endpoint = "https://fcm.googleapis.com/fcm/send/direct-webpush",
            P256dh = "test-p256dh-key",
            Auth = "test-auth-secret",
            SubscriptionType = PushSubscriptionType.WebPush,
            DeviceId = "dev_web"
        };

        var result = controller.Subscribe(req);
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult);

        var updated = _sessionRepo.GetById("sess_web");
        Assert.NotNull(updated);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/direct-webpush", updated.PushEndpoint);
        Assert.Equal(PushSubscriptionType.WebPush, updated.PushSubscriptionType);
        Assert.True(updated.PushEnabled);
    }

    [Fact]
    public void Subscribe_WhenSessionHasLegacyDevId_UpdatesSessionDeviceIdToIncomingStableId()
    {
        _empRepo.Save(new Employee { Id = "emp_bind" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_bind", LinkedEmployeeId = "emp_bind" });

        var session = new DeviceSession
        {
            Id = "sess_bind",
            EmployeeId = "emp_bind",
            DeviceId = "dev_legacy_123"
        };
        _sessionRepo.Save(session);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_bind"),
            new Claim("SessionId", "sess_bind")
        }, "mock"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        var validPublicKey = CreateValidRsaPublicKeyBase64();
        var req = new SubscribeRequest
        {
            Endpoint = "https://relay.spokes.com/send-bind",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            DeviceId = "android_stable_hw_999",
            PublicKey = validPublicKey
        };

        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("sess_bind");
        Assert.NotNull(updated);
        Assert.Equal("android_stable_hw_999", updated.DeviceId);
        Assert.Equal(validPublicKey, updated.PushPublicKey);
    }

    [Fact]
    public void Subscribe_WhenSwitchingFromNativeRelayToWebPush_ClearsStalePushPublicKey()
    {
        _empRepo.Save(new Employee { Id = "emp_switch" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_switch", LinkedEmployeeId = "emp_switch" });

        var session = new DeviceSession
        {
            Id = "sess_switch",
            EmployeeId = "emp_switch",
            DeviceId = "web_uuid_1",
            PushSubscriptionType = PushSubscriptionType.NativeRelay,
            PushPublicKey = "STALE_RSA_PUBLIC_KEY"
        };
        _sessionRepo.Save(session);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new Claim[] {
            new Claim("sub", "sub_switch"),
            new Claim("SessionId", "sess_switch")
        }, "mock"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        var req = new SubscribeRequest
        {
            Endpoint = "https://fcm.googleapis.com/fcm/send/webpush-switch",
            P256dh = "p256dh-val",
            Auth = "auth-val",
            SubscriptionType = PushSubscriptionType.WebPush,
            DeviceId = "web_uuid_1",
            PublicKey = null
        };

        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("sess_switch");
        Assert.NotNull(updated);
        Assert.Equal(string.Empty, updated.PushPublicKey);
        Assert.Equal(PushSubscriptionType.WebPush, updated.PushSubscriptionType);
        Assert.True(updated.PushEnabled);
    }

    [Fact]
    public void Unsubscribe_MarksPushExplicitlyDisabled_AndPreservesCapacitorMobileIdentity()
    {
        _empRepo.Save(new Employee { Id = "emp_mob" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_mob", LinkedEmployeeId = "emp_mob" });

        var session = new DeviceSession
        {
            Id = "sess_mob",
            EmployeeId = "emp_mob",
            DeviceId = "android_hw_1",
            DeviceType = DeviceType.Desktop, // Previously misclassified before push
            IsCapacitor = false,
            PushEndpoint = "fcm_shared_token_abc",
            PushSubscriptionType = PushSubscriptionType.NativeRelay,
            PushUserAgent = "Mozilla/5.0 (Linux; Android 14) Capacitor",
            PushEnabled = true
        };
        _sessionRepo.Save(session);
        SetUserWithSession("sub_mob", "sess_mob");

        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "fcm_shared_token_abc" });
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("sess_mob");
        Assert.NotNull(updated);
        Assert.Null(updated.PushEndpoint);
        Assert.False(updated.PushEnabled);
        Assert.True(updated.PushExplicitlyDisabled);
        Assert.True(updated.IsCapacitor);
        Assert.True(updated.IsCapacitorApp);
        Assert.Equal(DeviceType.Mobile, updated.DeviceType);
    }

    [Fact]
    public void Unsubscribe_WithoutEndpoint_ClearsCallerSessionBySessionId_AndMarksExplicitlyDisabled()
    {
        _empRepo.Save(new Employee { Id = "emp_noep" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_noep", LinkedEmployeeId = "emp_noep" });

        var session = new DeviceSession
        {
            Id = "sess_noep",
            EmployeeId = "emp_noep",
            PushEndpoint = "fcm_token_xyz",
            PushSubscriptionType = PushSubscriptionType.NativeRelay,
            PushEnabled = true
        };
        _sessionRepo.Save(session);
        SetUserWithSession("sub_noep", "sess_noep");

        var result = _controller.Unsubscribe(new UnsubscribeRequest { Endpoint = "" });
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("sess_noep");
        Assert.NotNull(updated);
        Assert.Null(updated.PushEndpoint);
        Assert.False(updated.PushEnabled);
        Assert.True(updated.PushExplicitlyDisabled);
    }

    [Fact]
    public void Subscribe_ResetsPushExplicitlyDisabledToFalse()
    {
        _empRepo.Save(new Employee { Id = "emp_resub" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_resub", LinkedEmployeeId = "emp_resub" });

        var session = new DeviceSession
        {
            Id = "sess_resub",
            EmployeeId = "emp_resub",
            PushExplicitlyDisabled = true
        };
        _sessionRepo.Save(session);
        SetUserWithSession("sub_resub", "sess_resub");

        var req = new SubscribeRequest
        {
            Endpoint = "fcm_token_resub",
            SubscriptionType = PushSubscriptionType.NativeRelay,
            PublicKey = CreateValidRsaPublicKeyBase64()
        };

        var result = _controller.Subscribe(req);
        Assert.IsType<OkObjectResult>(result);

        var updated = _sessionRepo.GetById("sess_resub");
        Assert.NotNull(updated);
        Assert.True(updated.PushEnabled);
        Assert.False(updated.PushExplicitlyDisabled);
    }

    [Fact]
    public void UpdateDevice_TogglingIsEnabled_UpdatesPushExplicitlyDisabled()
    {
        _empRepo.Save(new Employee { Id = "emp_toggle" });
        _openIdRepo.Save(new OpenIdAccount { Sub = "sub_toggle", LinkedEmployeeId = "emp_toggle" });

        var session = new DeviceSession
        {
            Id = "sess_toggle",
            EmployeeId = "emp_toggle",
            PushEndpoint = "fcm_token_toggle",
            PushEnabled = true,
            PushExplicitlyDisabled = false
        };
        _sessionRepo.Save(session);
        SetUserWithSession("sub_toggle", "sess_toggle");

        var disableRes = _controller.UpdateDevice("sess_toggle", new UpdateDeviceRequest { IsEnabled = false });
        Assert.IsType<OkObjectResult>(disableRes);
        var afterDisable = _sessionRepo.GetById("sess_toggle");
        Assert.False(afterDisable!.PushEnabled);
        Assert.True(afterDisable.PushExplicitlyDisabled);

        var enableRes = _controller.UpdateDevice("sess_toggle", new UpdateDeviceRequest { IsEnabled = true });
        Assert.IsType<OkObjectResult>(enableRes);
        var afterEnable = _sessionRepo.GetById("sess_toggle");
        Assert.True(afterEnable!.PushEnabled);
        Assert.False(afterEnable.PushExplicitlyDisabled);
    }
}
