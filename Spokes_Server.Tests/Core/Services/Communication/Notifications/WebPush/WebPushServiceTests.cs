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
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.WebPush;

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
        
        _sessions = new DeviceSessionRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _companyProfile = new CompanyProfileRepository(_persistence, _config);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
        _serverConfigs = new ServerConfigRepository(_persistence, _config);

        _chatState = new ChatStateService();
        _presenceState = new PresenceStateService(_chatState);
        _notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _persistence, _config, _companyProfile);

        var versionMeta = new VersionMetadata("server-v2026.4.1");
        _licenseValidation = new LicenseValidationService(new Mock<ILogger<LicenseValidationService>>().Object, versionMeta, SpokesConstants.LicensePublicKeyPem);

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

            var coreContext = new CoreContext(_companyProfile, null!, null!, _serverConfigs, _systemConfigs, _sessions, null!, null!);
            var hrContext = new HrContext(_employees, null!, null!, null!, null!, null!, _companyProfile);

            _sut = new WebPushService(
                coreContext,
                hrContext,
                _presenceState,
                _notificationQueue,
                _mockLogger.Object,
                _config,
                _mockHttpClientFactory.Object,
                _mockSystemLog.Object,
                _licenseValidation,
                dataProtection: _mockDataProtection.Object);
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
                DatabaseCreationVersion = currentDemoVer
            };
            _serverConfigs.Save(serverConfig);
        }

        private static void SetExpiredRelayTrialTicket(ServerConfig config)
        {
            using var escrow = System.Security.Cryptography.RSA.Create(2048);
            config.ServerMasterRsaPublicKey = escrow.ToXmlString(false);
            config.RelayTrialTicket = new RelayTrialTicket
            {
                Fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey)!,
                IssuedAt = DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ExpiresAt = DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                Signature = "relay-signature"
            };
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
        public async Task SendNotificationAsync_WhenNotConfigured_SkipsSend()
        {
            await _sut.SendNotificationAsync(new PushNotificationRequest { UserId = "user1", Title = "Test", Body = "Body" });
            // No exceptions, just returns
        }

        [Fact]
        public async Task SendNotificationAsync_WhenUserNotificationsDisabled_SkipsSend()
        {
            SetupValidVapidConfig();
            _employees.Save(new Employee { Id = "user1", PushNotificationsEnabled = false });
            await _sut.SendNotificationAsync(new PushNotificationRequest { UserId = "user1", Title = "Test", Body = "Body" });
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

            await _sut.SendNotificationAsync(new PushNotificationRequest { UserId = "user1", Title = "Test", Body = "Body" });
            
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            await _sut.SendNotificationAsync(new PushNotificationRequest { UserId = "user1", Title = "Test", Body = "Body" });

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
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                DeviceType = DeviceType.Mobile
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            await _sut.SendNotificationDirectAsync(new PushNotificationRequest { UserId = "user1", Title = "Test Direct", Body = "Body" });

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenNotConfigured_ThrowsDomainException()
        {
            var ex = await Assert.ThrowsAsync<DomainException>(() => 
                _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "sub1", UserId = "user1", Title = "Test", Body = "Body" }));
            Assert.Equal("VAPID_NOT_CONFIGURED", ex.ErrorCode);
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenSubscriptionNotFound_ThrowsDomainException()
        {
            SetupValidVapidConfig();
            var ex = await Assert.ThrowsAsync<DomainException>(() => 
                _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "sub1", UserId = "user1", Title = "Test", Body = "Body" }));
            Assert.Equal("SUBSCRIPTION_NOT_FOUND", ex.ErrorCode);
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            var result = await _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "sub1", UserId = "user1", Title = "Test", Body = "Body" });
            Assert.True(result.IsSuccess);
            Assert.False(result.IsPayloadEncrypted);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WithRequestObject_SendsPush()
        {
            SetupValidVapidConfig();
            
            _sessions.Save(new DeviceSession 
            {
                Id = "sub_req",
                EmployeeId = "user_req",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            var request = new DeviceTestNotificationRequest
            {
                SubscriptionId = "sub_req",
                UserId = "user_req",
                Title = "Test",
                Body = "Body",
                Category = "chat"
            };

            var result = await _sut.SendDeviceTestNotificationAsync(request);
            Assert.True(result.IsSuccess);
            Assert.False(result.IsPayloadEncrypted);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendDeviceTestNotificationAsync_WhenNativePushHasPublicKey_ReturnsEncryptedTrue()
        {
            SetupValidVapidConfig();
            
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

            _sessions.Save(new DeviceSession 
            {
                Id = "sub_enc",
                EmployeeId = "user_enc",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                PushPublicKey = publicKeyBase64
            });

            var result = await _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "sub_enc", UserId = "user_enc", Title = "Test", Body = "Body" });
            Assert.True(result.IsSuccess);
            Assert.True(result.IsPayloadEncrypted);
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            await _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "sub_custom", UserId = "user_custom_sound", Title = "Test", Body = "Body" });

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req => 
                    req.Method == HttpMethod.Post && 
                    ContentContains(req.Content, "mixkit_happy_bell_alert_601.wav")),
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            });

            await _sut.SendNotificationDirectAsync(new PushNotificationRequest { UserId = "user1", Title = "Test Silent", Body = "Body", IsSilent = true });

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    ContentContains(req.Content, "\"isSilent\":true", StringComparison.OrdinalIgnoreCase) &&
                    ContentContains(req.Content, "\"sound\":null", StringComparison.OrdinalIgnoreCase)),
                ItExpr.IsAny<CancellationToken>()
            );
        }


        [Fact]
        public async Task SendNotificationDirectAsync_WhenWebPushSubscription_CreatesClientFromHttpClientFactory()
        {
            SetupValidVapidConfig();
            var emp = new Employee { Id = "user1", PushNotificationsEnabled = true };
            _employees.Save(emp);

            _sessions.Save(new DeviceSession
            {
                Id = "sub_webpush",
                EmployeeId = "user1",
                PushEnabled = true,
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.WebPush,
                PushP256dh = "BM123",
                PushAuth = "auth123"
            });

            await _sut.SendNotificationDirectAsync(new PushNotificationRequest { UserId = "user1", Title = "Test Pooled", Body = "Body" });

            _mockHttpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.AtLeastOnce());
        }

        [Fact]
        public void GenerateVapidKeys_ReturnsValidKeyPair()
        {
            var (pub, priv) = WebPushService.GenerateVapidKeys();
            
            Assert.False(string.IsNullOrEmpty(pub));
            Assert.False(string.IsNullOrEmpty(priv));
        }

        [Fact]
        public async Task TestPushAsync_ClearsPushFields_OnHttp410Gone()
        {
            SetupValidVapidConfig();
            var session = new DeviceSession 
            { 
                Id = "s1_410", 
                EmployeeId = "u1_410", 
                PushEnabled = true, 
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u1_410", PushNotificationsEnabled = true });

            _mockHttpMessageHandler.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.Gone));

            var exception = await Assert.ThrowsAsync<DomainException>(() => _sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "s1_410", UserId = "u1_410", Title = "test title", Body = "test body" }));
            Assert.Equal("PUSH_TOKEN_EXPIRED", exception.ErrorCode);
            Assert.Contains("has expired or the app was uninstalled", exception.Message);

            var updatedSession = _sessions.GetById("s1_410");
            Assert.Null(updatedSession.PushEndpoint);
            Assert.False(updatedSession.PushEnabled);
        }

        [Fact]
        public async Task TestPushAsync_ThrowsLicenseExpired_WhenTrialExpired()
        {
            var session = new DeviceSession 
            { 
                Id = "s2_expired", 
                EmployeeId = "u2_expired", 
                PushEnabled = true, 
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u2_expired", PushNotificationsEnabled = true });
            var (pub, priv) = WebPushService.GenerateVapidKeys();
            _companyProfile.Save(new CompanyProfile { VapidSubject = "mailto:test@test.com", VapidPublicKey = pub, VapidPrivateKey = priv });

            // Expired 6 months ago
            var expiredVersion = $"{DateTime.UtcNow.AddMonths(-6).Year}.{DateTime.UtcNow.AddMonths(-6).Month}.1";
            var globalCfg = _serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = expiredVersion;
            SetExpiredRelayTrialTicket(globalCfg);
            _serverConfigs.Save(globalCfg);

            var licenseValidation = new LicenseValidationService(
                new Mock<ILogger<LicenseValidationService>>().Object,
                new VersionMetadata(expiredVersion));

            var coreContext = new CoreContext(_companyProfile, null!, null!, _serverConfigs, _systemConfigs, _sessions, null!, null!);
            var hrContext = new HrContext(_employees, null!, null!, null!, null!, null!, _companyProfile);

            var sut = new WebPushService(
                coreContext,
                hrContext,
                _presenceState,
                _notificationQueue,
                _mockLogger.Object,
                _config,
                _mockHttpClientFactory.Object,
                _mockSystemLog.Object,
                licenseValidation,
                dataProtection: _mockDataProtection.Object);

            var exception = await Assert.ThrowsAsync<LicenseExpiredException>(() => sut.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest { SubscriptionId = "s2_expired", UserId = "u2_expired", Title = "test title", Body = "test body" }));
            Assert.Equal("TRIAL_EXPIRED", exception.ErrorCode);
            Assert.Contains("Cannot send test notification: Your 2-month free trial", exception.Message);
        }

        [Fact]
        public async Task SendNotificationAsync_SkipsRelayPush_WhenTrialExpired()
        {
            var session = new DeviceSession 
            { 
                Id = "s3_expired", 
                EmployeeId = "u3_expired", 
                PushEnabled = true, 
                PushEndpoint = "https://example.com/push",
                PushSubscriptionType = PushSubscriptionType.NativeRelay
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u3_expired", PushNotificationsEnabled = true });

            var expiredVersion = $"{DateTime.UtcNow.AddMonths(-6).Year}.{DateTime.UtcNow.AddMonths(-6).Month}.1";
            var globalCfg = _serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = expiredVersion;
            SetExpiredRelayTrialTicket(globalCfg);
            _serverConfigs.Save(globalCfg);

            var licenseValidation = new LicenseValidationService(
                new Mock<ILogger<LicenseValidationService>>().Object,
                new VersionMetadata(expiredVersion));

            var mockHttpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);

            var coreContext = new CoreContext(_companyProfile, null!, null!, _serverConfigs, _systemConfigs, _sessions, null!, null!);
            var hrContext = new HrContext(_employees, null!, null!, null!, null!, null!, _companyProfile);

            var sut = new WebPushService(
                coreContext,
                hrContext,
                _presenceState,
                _notificationQueue,
                _mockLogger.Object,
                _config,
                mockHttpFactory.Object,
                _mockSystemLog.Object,
                licenseValidation,
                dataProtection: _mockDataProtection.Object);

            // Because license is expired, CreateClient must NOT be called on mockHttpFactory (strict mock will throw if called)
            await sut.SendNotificationAsync(new PushNotificationRequest { UserId = "u3_expired", Title = "title", Body = "body" });
        }

        [Fact]
        public void Constructor_WithBoundedContexts_InstantiatesSuccessfully()
        {
            var mockCore = new Mock<CoreContext>();
            mockCore.Setup(c => c.DeviceSessions).Returns(_sessions);
            mockCore.Setup(c => c.CompanyProfile).Returns(_companyProfile);
            mockCore.Setup(c => c.SystemConfigs).Returns(_systemConfigs);
            mockCore.Setup(c => c.ServerConfigs).Returns(_serverConfigs);

            var mockHr = new Mock<HrContext>();
            mockHr.Setup(h => h.Employees).Returns(_employees);

            var pushTokenService = new PushTokenService(_mockDataProtection.Object);
            var payloadBuilder = new PushPayloadBuilder();
            var relayDispatcher = new NativeRelayDispatcher(_mockHttpClientFactory.Object, _sessions, _mockSystemLog.Object);

            var service = new WebPushService(
                mockCore.Object,
                mockHr.Object,
                _presenceState,
                _notificationQueue,
                _mockLogger.Object,
                _config,
                _mockHttpClientFactory.Object,
                _mockSystemLog.Object,
                _licenseValidation,
                pushTokenService,
                payloadBuilder,
                relayDispatcher);

            Assert.NotNull(service);
        }

        [Fact]
        public void IsNativePushAllowed_ReturnsExpectedStatus()
        {
            // Without a relay trial ticket the trial has not started yet: the server must try the relay.
            Assert.True(_sut.IsNativePushAllowed());
            var statusBefore = _sut.GetNativePushStatus();
            Assert.True(statusBefore.IsAvailable);
            Assert.Equal("Valid", statusBefore.Status);

            // Once the relay's ticket has expired, native push is blocked locally.
            var globalCfg = _serverConfigs.GetOrCreateGlobalConfig();
            SetExpiredRelayTrialTicket(globalCfg);
            _serverConfigs.Save(globalCfg);

            Assert.False(_sut.IsNativePushAllowed());
            var statusAfter = _sut.GetNativePushStatus();
            Assert.False(statusAfter.IsAvailable);
            Assert.Equal("Expired", statusAfter.Status);
        }

        [Fact]
        public void GenerateVapidKeys_ReturnsValidP256Keys()
        {
            var (publicKey, privateKey) = WebPushService.GenerateVapidKeys();

            Assert.False(string.IsNullOrWhiteSpace(publicKey));
            Assert.False(string.IsNullOrWhiteSpace(privateKey));

            var pubBytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(publicKey);
            var privBytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(privateKey);

            // Uncompressed P-256 public key is 65 bytes starting with 0x04
            Assert.Equal(65, pubBytes.Length);
            Assert.Equal(0x04, pubBytes[0]);

            // P-256 private key scalar is 32 bytes
            Assert.Equal(32, privBytes.Length);
        }

        private static string GetContentString(HttpContent? content)
        {
            if (content == null) return string.Empty;
            var stream = content.ReadAsStream();
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var text = reader.ReadToEnd();
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
            return text;
        }

        private static bool ContentContains(HttpContent? content, string text)
        {
            return ContentContains(content, text, StringComparison.Ordinal);
        }

        private static bool ContentContains(HttpContent? content, string text, StringComparison comparison)
        {
            var body = GetContentString(content);
            return body.Contains(text, comparison);
        }

        [Fact]
        public async Task SendNotificationAsync_EncryptedMessage_WhenDeviceLacksVaultCookie_SendsMaskedPayload()
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                HasVaultCookie = false
            });

            var request = new PushNotificationRequest
            {
                UserId = "user1",
                Title = "Alice",
                Body = "Decrypted Secret Plaintext",
                IsEncrypted = true,
                MaskedBody = "🔒 New Secure Message"
            };

            await _sut.SendNotificationAsync(request);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    ContentContains(req.Content, "New Secure Message") &&
                    !ContentContains(req.Content, "Decrypted Secret Plaintext")),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Fact]
        public async Task SendNotificationAsync_EncryptedMessage_WhenDeviceHasVaultCookie_SendsDecryptedPayload()
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
                PushSubscriptionType = PushSubscriptionType.NativeRelay,
                HasVaultCookie = true
            });

            var request = new PushNotificationRequest
            {
                UserId = "user1",
                Title = "Alice",
                Body = "Decrypted Secret Plaintext",
                IsEncrypted = true,
                MaskedBody = "🔒 New Secure Message"
            };

            await _sut.SendNotificationAsync(request);

            _mockHttpMessageHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    ContentContains(req.Content, "Decrypted Secret Plaintext")),
                ItExpr.IsAny<CancellationToken>()
            );
        }

        [Theory]
        [InlineData(true, false, "Decrypted Plaintext", "🔒 New Secure Message", "🔒 New Secure Message")]
        [InlineData(true, true, "Decrypted Plaintext", "🔒 New Secure Message", "Decrypted Plaintext")]
        [InlineData(false, false, "Regular Message", null, "Regular Message")]
        [InlineData(false, true, "Regular Message", null, "Regular Message")]
        public void PushNotificationRequest_GetEffectiveBodyForDevice_ReturnsExpectedBody(
            bool isEncrypted, bool hasVaultCookie, string body, string? maskedBody, string expected)
        {
            var request = new PushNotificationRequest
            {
                UserId = "u1",
                Title = "Test",
                Body = body,
                IsEncrypted = isEncrypted,
                MaskedBody = maskedBody
            };

            var result = request.GetEffectiveBodyForDevice(hasVaultCookie);
            Assert.Equal(expected, result);
        }
    }


