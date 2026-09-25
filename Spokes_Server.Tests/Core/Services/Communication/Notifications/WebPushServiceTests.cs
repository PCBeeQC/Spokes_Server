using Moq;
using Moq.Protected;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System;
using Xunit;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.HR;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public class WebPushServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    // Repos
    private readonly DeviceSessionRepository _sessions;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ServerConfigRepository _serverConfigs;

    // Services
    private readonly ChatStateService _chatState;
    private readonly PresenceStateService _presenceState;
    private readonly NotificationQueueService _notificationQueue;
    private readonly EncryptionService _encryptionService;
    private readonly LicenseValidationService _licenseValidation;

    // Mocks
    private readonly Mock<ILogger<WebPushService>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IDataProtectionProvider> _mockDataProtection;
    private readonly Mock<IDataProtector> _mockProtector;
    private readonly Mock<Spokes_Server.Core.Services.Logging.ISystemLogService> _mockSystemLog;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;

    // SUT
    private readonly WebPushService _sut;

    public WebPushServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_WebPush_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        
        _encryptionService = new EncryptionService(_config);
        
        _sessions = new DeviceSessionRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _companyProfile = new CompanyProfileRepository(_persistence, _config);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
        _serverConfigs = new ServerConfigRepository(_persistence, _config, _encryptionService);

        _chatState = new ChatStateService();
        _presenceState = new PresenceStateService(_chatState, null);
        _notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object);

        var versionMeta = new VersionMetadata("server-v2026.4.1");
        _licenseValidation = new LicenseValidationService(new Mock<ILogger<LicenseValidationService>>().Object, _encryptionService, versionMeta, SpokesConstants.LicensePublicKeyPem);

        _mockLogger = new Mock<ILogger<WebPushService>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockDataProtection = new Mock<IDataProtectionProvider>();
        _mockProtector = new Mock<IDataProtector>();
        _mockSystemLog = new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>();

        _mockDataProtection.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(_mockProtector.Object);
        _mockProtector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns([1, 2, 3, 4]);

        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            _sut = new WebPushService(
                _sessions,
                _employees,
                _companyProfile,
                _systemConfigs,
                _serverConfigs,
                _presenceState,
                _notificationQueue,
                _mockLogger.Object,
                _config,
                _mockHttpClientFactory.Object,
                _mockDataProtection.Object,
                _mockSystemLog.Object,
                _encryptionService,
                _licenseValidation);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        private void SetupValidVapidConfig()
        {
            var (pub, priv) = WebPushService.GenerateVapidKeys();

            var profile = new CompanyProfile
            {
                Id = "1",
                VapidPublicKey = pub,
                VapidPrivateKey = priv,
                VapidSubject = "mailto:test@example.com",
                LicensePayload = null // Use demo mode
            };
            _companyProfile.Save(profile);
            
            string currentDemoVer = $"server-v{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.1";
            var serverConfig = new ServerConfig 
            { 
                Id = "Global",
                DatabaseCreationVersion = currentDemoVer,
                DatabaseCreationSignature = LicenseValidationService.SignDemoVersion(currentDemoVer, _encryptionService.KeyHash)
            };
            _serverConfigs.Save(serverConfig);
        }

        [Fact]
        public void GetVapidPublicKey_WhenNotConfigured_ReturnsEmptyString()
        {
            var key = _sut.GetVapidPublicKey();
            Assert.Equal(string.Empty, key);
        }

        [Fact]
        public void GetVapidPublicKey_WhenConfigured_ReturnsPublicKey()
        {
            SetupValidVapidConfig();
            var key = _sut.GetVapidPublicKey();
            Assert.NotEmpty(key);
        }

        [Fact]
        public void IsConfigured_WhenNoKeys_ReturnsFalse()
        {
            Assert.False(_sut.IsConfigured);
        }

        [Fact]
        public void IsConfigured_WhenBothKeys_ReturnsTrue()
        {
            SetupValidVapidConfig();
            Assert.True(_sut.IsConfigured);
        }

        [Fact]
        public void DetermineNotificationTier_WhenUserFocused_ReturnsNone()
        {
            _presenceState.RegisterConnection("user1", "sub1", "Desktop", true, "sess1", true);
            var tier = _sut.DetermineNotificationTier("user1");
            Assert.Equal(PresenceTier.None, tier);
        }

        [Fact]
        public void DetermineNotificationTier_WhenUserHasDesktopButNotAway_ReturnsDesktopOnly()
        {
            _presenceState.RegisterConnection("user1", "sub1", "Desktop", true, "sess1", false);
            // Simulate not being away (last seen is now)
            _presenceState.PingUserActive("user1");
            var tier = _sut.DetermineNotificationTier("user1");
            Assert.Equal(PresenceTier.DesktopOnly, tier);
        }

        [Fact]
        public void DetermineNotificationTier_WhenUserOffline_ReturnsAll()
        {
            var tier = _sut.DetermineNotificationTier("user2");
            Assert.Equal(PresenceTier.All, tier);
        }

        [Fact]
        public async Task SendNotificationAsync_WhenNotConfigured_SkipsSend()
        {
            await _sut.SendNotificationAsync("user1", "Test", "Body");
            // No exceptions, just returns
        }

        [Fact]
        public async Task SendNotificationAsync_WhenUserNotificationsDisabled_SkipsSend()
        {
            SetupValidVapidConfig();
            _employees.Save(new Employee { Id = "user1", PushNotificationsEnabled = false });
            await _sut.SendNotificationAsync("user1", "Test", "Body");
            Assert.Empty(_notificationQueue.DequeueAll("user1"));
        }

        [Fact]
        public async Task SendNotificationAsync_WhenOutsideSchedule_QueuesNotification()
        {
            SetupValidVapidConfig();
            
            var emp = new Employee 
            { 
                Id = "user1", 
                PushNotificationsEnabled = true,
                NotificationScheduleEnabled = true,
                NotificationSchedule = [new DaySchedule { Day = DateTime.Now.DayOfWeek, IsEnabled = false }]
            };
            _employees.Save(emp);

            await _sut.SendNotificationAsync("user1", "Test", "Body");
            
            var queued = _notificationQueue.DequeueAll("user1");
            Assert.Single(queued);
            Assert.Equal("Test", queued[0].Title);
        }

        [Fact]
        public async Task SendNotificationAsync_WhenWithinSchedule_SendsPush()
        {
            SetupValidVapidConfig();
            
            var emp = new Employee { Id = "user1", PushNotificationsEnabled = true };
            _employees.Save(emp);
            
            _sessions.Save(new DeviceSession 
            {
                Id = "sub1",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay"
            });

            await _sut.SendNotificationAsync("user1", "Test", "Body");

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendClearNotificationAsync_WhenValid_SendsSilentPush()
        {
            SetupValidVapidConfig();
            var emp = new Employee { Id = "user1", PushNotificationsEnabled = true };
            _employees.Save(emp);
            
            _sessions.Save(new DeviceSession 
            {
                Id = "sub1",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay",
                DeviceType = "Mobile"
            });

            await _sut.SendClearNotificationAsync("user1", "thread1");

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendNotificationDirectAsync_WhenValid_SendsPush()
        {
            SetupValidVapidConfig();
            var emp = new Employee { Id = "user1", PushNotificationsEnabled = true };
            _employees.Save(emp);
            
            _sessions.Save(new DeviceSession 
            {
                Id = "sub1",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay"
            });

            await _sut.SendNotificationDirectAsync("user1", "Test Direct", "Body");

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenNotConfigured_ThrowsInvalidOperationException()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => 
                _sut.SendDeviceTestNotificationAsync("sub1", "user1", "Test", "Body"));
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenSubscriptionNotFound_ThrowsInvalidOperationException()
        {
            SetupValidVapidConfig();
            await Assert.ThrowsAsync<InvalidOperationException>(() => 
                _sut.SendDeviceTestNotificationAsync("sub1", "user1", "Test", "Body"));
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenValid_SendsPush()
        {
            SetupValidVapidConfig();
            
            _sessions.Save(new DeviceSession 
            {
                Id = "sub1",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay"
            });

            await _sut.SendDeviceTestNotificationAsync("sub1", "user1", "Test", "Body");

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenUserHasCustomSound_SendsPushWithCustomSound()
        {
            SetupValidVapidConfig();
            
            var emp = new Employee
            {
                Id = "user_custom_sound",
                FirstName = "Test",
                LastName = "User"
            };
            emp.SetNotificationSound("chat", "mixkit_happy_bell_alert_601");
            _employees.Save(emp);

            _sessions.Save(new DeviceSession 
            {
                Id = "sub_custom",
                EmployeeId = "user_custom_sound",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay"
            });

            await _sut.SendDeviceTestNotificationAsync("sub_custom", "user_custom_sound", "Test", "Body");

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => 
                    req.Method == HttpMethod.Post && 
                    req.Content!.ReadAsStringAsync().Result.Contains("mixkit_happy_bell_alert_601.wav")),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendNotificationDirectAsync_WhenIsSilentTrue_OmitsSoundInRelayPayload()
        {
            SetupValidVapidConfig();
            var emp = new Employee { Id = "user1", PushNotificationsEnabled = true };
            _employees.Save(emp);

            _sessions.Save(new DeviceSession
            {
                Id = "sub1",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = "NativeRelay"
            });

            await _sut.SendNotificationDirectAsync("user1", "Test Silent", "Body", isSilent: true);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.Content!.ReadAsStringAsync().Result.Contains("\"isSilent\":true", StringComparison.OrdinalIgnoreCase) &&
                    req.Content!.ReadAsStringAsync().Result.Contains("\"sound\":null", StringComparison.OrdinalIgnoreCase)),
                ItExpr.IsAny<CancellationToken>()
            );
        }


        [Fact]
        public void GenerateVapidKeys_ReturnsValidKeyPair()
        {
            var (pub, priv) = WebPushService.GenerateVapidKeys();
            
            Assert.False(string.IsNullOrEmpty(pub));
            Assert.False(string.IsNullOrEmpty(priv));
        }
    }

