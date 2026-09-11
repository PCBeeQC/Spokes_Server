using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
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
using System.Linq;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class WebPushServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly DeviceSessionRepository _sessions;
        private readonly EmployeeRepository _employees;
        private readonly CompanyProfileRepository _companyProfile;
        private readonly SystemConfigRepository _systemConfigs;
        private readonly WebPushService _service;

        public WebPushServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_WebPush_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _sessions = new DeviceSessionRepository(_persistence, _config);
            _employees = new EmployeeRepository(_persistence, _config);
            _companyProfile = new CompanyProfileRepository(_persistence, _config);
            _systemConfigs = new SystemConfigRepository(_persistence, _config);
            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config, encService);
            var currentVersion = $"{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.1";
            var globalCfg = serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = currentVersion;
            globalCfg.DatabaseCreationSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(currentVersion, encService.KeyHash);
            serverConfigs.Save(globalCfg);

            var licenseValidation = new Spokes_Server.Core.Services.Licensing.LicenseValidationService(
                new Mock<ILogger<Spokes_Server.Core.Services.Licensing.LicenseValidationService>>().Object,
                encService,
                new Spokes_Server.Core.Services.Licensing.VersionMetadata(currentVersion));

            var dataProtector = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtector>();
            dataProtector.Setup(x => x.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
            dataProtector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
            var dataProtectionProvider = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            dataProtectionProvider.Setup(x => x.CreateProtector(It.IsAny<string>())).Returns(dataProtector.Object);

            _service = new WebPushService(
                _sessions,
                _employees,
                _companyProfile,
                _systemConfigs,
                serverConfigs,
                new Mock<PresenceStateService>(new ChatStateService()).Object,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new Mock<ILogger<WebPushService>>().Object,
                _config,
                new Mock<System.Net.Http.IHttpClientFactory>().Object,
                dataProtectionProvider.Object,
                new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>().Object,
                encService,
                licenseValidation);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public void IsConfigured_ReturnsFalse_WhenKeysMissing()
        {
            _companyProfile.Save(new CompanyProfile { VapidPublicKey = "", VapidPrivateKey = "" });
            Assert.False(_service.IsConfigured);
        }

        [Fact]
        public void IsConfigured_ReturnsTrue_WhenKeysPresent()
        {
            _companyProfile.Save(new CompanyProfile { VapidPublicKey = "pub", VapidPrivateKey = "priv" });
            Assert.True(_service.IsConfigured);
        }

        [Fact]
        public void GetVapidPublicKey_ReturnsCorrectKey()
        {
            _companyProfile.Save(new CompanyProfile { VapidPublicKey = "my-key" });
            Assert.Equal("my-key", _service.GetVapidPublicKey());
        }

        [Fact]
        public async Task SendNotificationAsync_Skips_WhenNotConfigured()
        {
            _companyProfile.Save(new CompanyProfile { VapidPublicKey = null, VapidPrivateKey = null });

            // Should not throw even if WebPushClient would normally fail without keys
            await _service.SendNotificationAsync("user-1", "title", "body");
        }

        [Fact]
        public async Task SendNotificationAsync_Skips_WhenUserDisabled()
        {
            _companyProfile.Save(new CompanyProfile { VapidPublicKey = "a", VapidPrivateKey = "b" });
            _employees.Save(new Employee { Id = "u1", PushNotificationsEnabled = false });

            await _service.SendNotificationAsync("u1", "title", "body");
        }

        [Fact]
        public void GenerateVapidKeys_ReturnsNewKeys()
        {
            var keys = WebPushService.GenerateVapidKeys();
            Assert.NotEmpty(keys.publicKey);
            Assert.NotEmpty(keys.privateKey);
        }
        [Fact]
        public async Task TestPushAsync_ClearsPushFields_OnHttp410Gone()
        {
            var session = new DeviceSession 
            { 
                Id = "s1", 
                EmployeeId = "u1", 
                PushEnabled = true, 
                PushEndpoint = "some-endpoint",
                PushSubscriptionType = "NativeRelay"
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u1", PushNotificationsEnabled = true });
            var vapidKeys = WebPush.VapidHelper.GenerateVapidKeys();
            _companyProfile.Save(new CompanyProfile { VapidSubject = "mailto:test@test.com", VapidPublicKey = vapidKeys.PublicKey, VapidPrivateKey = vapidKeys.PrivateKey });

            var httpClient = new System.Net.Http.HttpClient(new MockHttpMessageHandler());
            var httpClientFactory = new Mock<System.Net.Http.IHttpClientFactory>();
            httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var dataProtector = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtector>();
            dataProtector.Setup(x => x.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
            dataProtector.Setup(x => x.Unprotect(It.IsAny<byte[]>())).Returns((byte[] b) => b);

            var dataProtectionProvider = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            dataProtectionProvider.Setup(x => x.CreateProtector(It.IsAny<string>())).Returns(dataProtector.Object);

            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config, encService);
            var currentVersion = $"{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.1";
            var globalCfg = serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = currentVersion;
            globalCfg.DatabaseCreationSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(currentVersion, encService.KeyHash);
            serverConfigs.Save(globalCfg);

            var licenseValidation = new Spokes_Server.Core.Services.Licensing.LicenseValidationService(
                new Mock<ILogger<Spokes_Server.Core.Services.Licensing.LicenseValidationService>>().Object,
                encService,
                new Spokes_Server.Core.Services.Licensing.VersionMetadata(currentVersion));

            var service = new WebPushService(
                _sessions,
                _employees,
                _companyProfile,
                _systemConfigs,
                serverConfigs,
                new Mock<PresenceStateService>(new ChatStateService()).Object,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new Mock<ILogger<WebPushService>>().Object,
                _config,
                httpClientFactory.Object,
                dataProtectionProvider.Object,
                new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>().Object,
                encService,
                licenseValidation);

            var exception = await Assert.ThrowsAsync<Exception>(() => service.SendDeviceTestNotificationAsync("s1", "u1", "test title", "test body"));
            Assert.Contains("has expired or the app was uninstalled", exception.Message);

            var updatedSession = _sessions.GetById("s1");
            Assert.Null(updatedSession.PushEndpoint);
            Assert.False(updatedSession.PushEnabled);
        }

        [Fact]
        public async Task TestPushAsync_ThrowsLicenseExpired_WhenTrialExpired()
        {
            var session = new DeviceSession 
            { 
                Id = "s2", 
                EmployeeId = "u2", 
                PushEnabled = true, 
                PushEndpoint = "some-endpoint",
                PushSubscriptionType = "NativeRelay"
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u2", PushNotificationsEnabled = true });
            var vapidKeys = WebPush.VapidHelper.GenerateVapidKeys();
            _companyProfile.Save(new CompanyProfile { VapidSubject = "mailto:test@test.com", VapidPublicKey = vapidKeys.PublicKey, VapidPrivateKey = vapidKeys.PrivateKey });

            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config, encService);
            // Expired 6 months ago
            var expiredVersion = $"{DateTime.UtcNow.AddMonths(-6).Year}.{DateTime.UtcNow.AddMonths(-6).Month}.1";
            var globalCfg = serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = expiredVersion;
            globalCfg.DatabaseCreationSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(expiredVersion, encService.KeyHash);
            serverConfigs.Save(globalCfg);

            var licenseValidation = new Spokes_Server.Core.Services.Licensing.LicenseValidationService(
                new Mock<ILogger<Spokes_Server.Core.Services.Licensing.LicenseValidationService>>().Object,
                encService,
                new Spokes_Server.Core.Services.Licensing.VersionMetadata(expiredVersion));

            var dataProtector = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtector>();
            dataProtector.Setup(x => x.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
            var dataProtectionProvider = new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            dataProtectionProvider.Setup(x => x.CreateProtector(It.IsAny<string>())).Returns(dataProtector.Object);

            var service = new WebPushService(
                _sessions,
                _employees,
                _companyProfile,
                _systemConfigs,
                serverConfigs,
                new Mock<PresenceStateService>(new ChatStateService()).Object,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new Mock<ILogger<WebPushService>>().Object,
                _config,
                new Mock<System.Net.Http.IHttpClientFactory>().Object,
                dataProtectionProvider.Object,
                new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>().Object,
                encService,
                licenseValidation);

            var exception = await Assert.ThrowsAsync<Spokes_Server.Core.Services.Licensing.LicenseExpiredException>(() => service.SendDeviceTestNotificationAsync("s2", "u2", "test title", "test body"));
            Assert.Contains("Cannot send test notification: Your server license has expired", exception.Message);
        }

        [Fact]
        public async Task SendNotificationAsync_SkipsRelayPush_WhenTrialExpired()
        {
            var session = new DeviceSession 
            { 
                Id = "s3", 
                EmployeeId = "u3", 
                PushEnabled = true, 
                PushEndpoint = "some-endpoint",
                PushSubscriptionType = "NativeRelay"
            };
            _sessions.Save(session);
            _employees.Save(new Employee { Id = "u3", PushNotificationsEnabled = true });

            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config, encService);
            var expiredVersion = $"{DateTime.UtcNow.AddMonths(-6).Year}.{DateTime.UtcNow.AddMonths(-6).Month}.1";
            var globalCfg = serverConfigs.GetOrCreateGlobalConfig();
            globalCfg.DatabaseCreationVersion = expiredVersion;
            globalCfg.DatabaseCreationSignature = Spokes_Server.Core.Services.Licensing.LicenseValidationService.SignDemoVersion(expiredVersion, encService.KeyHash);
            serverConfigs.Save(globalCfg);

            var licenseValidation = new Spokes_Server.Core.Services.Licensing.LicenseValidationService(
                new Mock<ILogger<Spokes_Server.Core.Services.Licensing.LicenseValidationService>>().Object,
                encService,
                new Spokes_Server.Core.Services.Licensing.VersionMetadata(expiredVersion));

            var mockHttpFactory = new Mock<System.Net.Http.IHttpClientFactory>(MockBehavior.Strict);

            var service = new WebPushService(
                _sessions,
                _employees,
                _companyProfile,
                _systemConfigs,
                serverConfigs,
                new Mock<PresenceStateService>(new ChatStateService()).Object,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new Mock<ILogger<WebPushService>>().Object,
                _config,
                mockHttpFactory.Object,
                new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().Object,
                new Mock<Spokes_Server.Core.Services.Logging.ISystemLogService>().Object,
                encService,
                licenseValidation);

            // Because license is expired, CreateClient must NOT be called on mockHttpFactory (strict mock will throw if called)
            await service.SendNotificationAsync("u3", "title", "body");
        }

        private class MockHttpMessageHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.Gone });
            }
        }
    }
}



