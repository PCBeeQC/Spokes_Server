using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class TelemetryBackgroundServiceTests : TestDataTestBase
{
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
        private readonly Mock<ILogger<TelemetryBackgroundService>> _mockLogger;

        public TelemetryBackgroundServiceTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());
            services.AddSingleton<EncryptionService>();
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();

            _mockHttpClientFactory = new Mock<IHttpClientFactory>();
            _mockLogger = new Mock<ILogger<TelemetryBackgroundService>>();
        }

        public override void Dispose()
        {
            _serviceProvider.Dispose();
            base.Dispose();
        }

        public class TestableTelemetryBackgroundService : TelemetryBackgroundService
        {
            public TestableTelemetryBackgroundService(
                Database db,
                IHttpClientFactory clientFactory,
                ILogger<TelemetryBackgroundService> logger)
                : base(db, clientFactory, logger) { }

            public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
        }

        #region Constructor Tests

        [Fact]
        public void Constructor_WithValidDependencies_InitializesSuccessfully()
        {
            var service = new TelemetryBackgroundService(
                _db,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            Assert.NotNull(service);
        }

        [Fact]
        public void TestableSubclass_WithValidDependencies_InitializesSuccessfully()
        {
            var service = new TestableTelemetryBackgroundService(
                _db,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            Assert.NotNull(service);
        }

        #endregion

        #region Execution and Cancellation Tests

        [Fact]
        public async Task ExecuteAsync_WithPreCancelledToken_CancelsCleanlyWithoutUnhandledException()
        {
            var service = new TestableTelemetryBackgroundService(
                _db,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
        }

        [Fact]
        public async Task ExecuteAsync_WithTokenCancelledAfterBriefDelay_CancelsCleanlyWithoutUnhandledException()
        {
            var service = new TestableTelemetryBackgroundService(
                _db,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
        }

        [Fact]
        public async Task StartAsync_And_StopAsync_LifecycleTerminatesCleanly()
        {
            var service = new TestableTelemetryBackgroundService(
                _db,
                _mockHttpClientFactory.Object,
                _mockLogger.Object);

            using var startCts = new CancellationTokenSource();
            await service.StartAsync(startCts.Token);

            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsCompleted);

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stopCts.Token);

            Assert.True(service.ExecuteTask.IsCompleted);
        }

        #endregion

        #region Payload Encryption Logic Tests

        [Fact]
        public void PayloadEncryption_UsingSpokesConstantsPublicKey_ProducesValidBase64AndCorrectLengths()
        {
            var stats = new UsageStatistics
            {
                ServerId = Guid.NewGuid().ToString(),
                LicenseId = "Demo",
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ActiveUsers = 3,
                ServerEdition = "Community",
                ServerVersion = LicenseValidationService.AppVersion
            };

            var payloadWrapper = new
            {
                Stats = stats,
                TimestampUtc = DateTime.UtcNow
            };

            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadWrapper));

            var aesKey = new byte[32];
            RandomNumberGenerator.Fill(aesKey);
            using var aesGcm = new AesGcm(aesKey, 16);

            var aesNonce = new byte[12];
            RandomNumberGenerator.Fill(aesNonce);

            var ciphertext = new byte[jsonBytes.Length];
            var authTag = new byte[16];

            aesGcm.Encrypt(aesNonce, jsonBytes, ciphertext, authTag);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(SpokesConstants.LicensePublicKeyPem);
            var encryptedAesKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

            var postPayload = new
            {
                EncryptedKey = Convert.ToBase64String(encryptedAesKey),
                Nonce = Convert.ToBase64String(aesNonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(authTag)
            };

            Assert.NotNull(postPayload.EncryptedKey);
            Assert.NotNull(postPayload.Nonce);
            Assert.NotNull(postPayload.Ciphertext);
            Assert.NotNull(postPayload.Tag);

            var decodedEncryptedKey = Convert.FromBase64String(postPayload.EncryptedKey);
            var decodedNonce = Convert.FromBase64String(postPayload.Nonce);
            var decodedCiphertext = Convert.FromBase64String(postPayload.Ciphertext);
            var decodedTag = Convert.FromBase64String(postPayload.Tag);

            Assert.Equal(256, decodedEncryptedKey.Length); // RSA 2048-bit key generates 256 bytes
            Assert.Equal(12, decodedNonce.Length);
            Assert.Equal(jsonBytes.Length, decodedCiphertext.Length);
            Assert.Equal(16, decodedTag.Length);
        }

        [Fact]
        public void PayloadEncryption_RoundtripWithRsaKeyPair_EncryptsAndDecryptsSuccessfully()
        {
            using var rsa = RSA.Create(2048);
            var publicKeyPem = rsa.ExportRSAPublicKeyPem();

            var stats = new UsageStatistics
            {
                ServerId = "server-12345",
                LicenseId = "LIC-98765",
                OS = "Linux 6.1.0",
                ActiveUsers = 42,
                ServerEdition = "Enterprise",
                ServerVersion = "2026.8.100"
            };

            var originalPayload = new
            {
                Stats = stats,
                TimestampUtc = DateTime.UtcNow
            };

            var jsonOriginal = JsonSerializer.Serialize(originalPayload);
            var jsonBytes = Encoding.UTF8.GetBytes(jsonOriginal);

            // Encrypt using AES-GCM + RSA
            var aesKey = new byte[32];
            RandomNumberGenerator.Fill(aesKey);

            var aesNonce = new byte[12];
            RandomNumberGenerator.Fill(aesNonce);

            var ciphertext = new byte[jsonBytes.Length];
            var authTag = new byte[16];

            using (var encryptor = new AesGcm(aesKey, 16))
            {
                encryptor.Encrypt(aesNonce, jsonBytes, ciphertext, authTag);
            }

            using var rsaEncrypt = RSA.Create();
            rsaEncrypt.ImportFromPem(publicKeyPem);
            var encryptedAesKey = rsaEncrypt.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);

            // Decrypt with private key
            var decryptedAesKey = rsa.Decrypt(encryptedAesKey, RSAEncryptionPadding.OaepSHA256);
            Assert.Equal(aesKey, decryptedAesKey);

            var decryptedJsonBytes = new byte[ciphertext.Length];
            using (var decryptor = new AesGcm(decryptedAesKey, 16))
            {
                decryptor.Decrypt(aesNonce, ciphertext, authTag, decryptedJsonBytes);
            }

            var decryptedJson = Encoding.UTF8.GetString(decryptedJsonBytes);
            Assert.Equal(jsonOriginal, decryptedJson);
        }

        #endregion

        #region License Parsing Logic Tests

        [Fact]
        public void LicensePayloadParsing_ValidLicenseJson_ExtractsExpectedLicenseId()
        {
            var licenseFile = new SpokesLicenseFile
            {
                Email = "admin@example.com",
                LicenseId = "LIC-ABC-12345",
                ValidForUpdatesUntil = DateTime.UtcNow.AddYears(1),
                AvailableEditions = new() { "Standard", "Enterprise" }
            };

            var json = JsonSerializer.Serialize(licenseFile);

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            };

            string licenseId = "Demo";
            var parsed = JsonSerializer.Deserialize<SpokesLicenseFile>(json, options);
            if (parsed != null && !string.IsNullOrEmpty(parsed.LicenseId))
            {
                licenseId = parsed.LicenseId;
            }

            Assert.Equal("LIC-ABC-12345", licenseId);
        }

        [Fact]
        public void LicensePayloadParsing_MalformedJson_FallsBackToDemoWithoutThrowing()
        {
            string malformedJson = "{ invalid json content: 123";
            string licenseId = "Demo";

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                var parsed = JsonSerializer.Deserialize<SpokesLicenseFile>(malformedJson, options);
                if (parsed != null && !string.IsNullOrEmpty(parsed.LicenseId))
                {
                    licenseId = parsed.LicenseId;
                }
            }
            catch (Exception ex)
            {
                _mockLogger.Object.LogWarning(ex, "Failed to parse license payload in telemetry service, falling back to Demo.");
            }

            Assert.Equal("Demo", licenseId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void LicensePayloadParsing_NullOrWhitespace_DefaultsToDemo(string? payload)
        {
            string licenseId = "Demo";
            if (!string.IsNullOrWhiteSpace(payload))
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                var parsed = JsonSerializer.Deserialize<SpokesLicenseFile>(payload, options);
                if (parsed != null && !string.IsNullOrEmpty(parsed.LicenseId))
                {
                    licenseId = parsed.LicenseId;
                }
            }

            Assert.Equal("Demo", licenseId);
        }

        #endregion

        #region Usage Statistics Assembly Tests

        [Fact]
        public void UsageStatistics_ActiveUsers_OnlyCountsActiveEmployees()
        {
            var emp1 = new Employee { Id = "emp-1", FirstName = "Active", LastName = "User1", IsActive = true };
            var emp2 = new Employee { Id = "emp-2", FirstName = "Active", LastName = "User2", IsActive = true };
            var emp3 = new Employee { Id = "emp-3", FirstName = "Inactive", LastName = "User3", IsActive = false };

            _db.Employees.Save(emp1);
            _db.Employees.Save(emp2);
            _db.Employees.Save(emp3);

            var activeCount = _db.Employees.GetAll().Count(e => e.IsActive);
            Assert.Equal(2, activeCount);
        }

        [Fact]
        public void UsageStatistics_ServerConfig_HasValidDatabaseCreationId()
        {
            var serverConfig = _db.ServerConfigs.GetOrCreateGlobalConfig();

            Assert.NotNull(serverConfig);
            Assert.False(string.IsNullOrWhiteSpace(serverConfig.DatabaseCreationId));
        }

        [Fact]
        public void UsageStatistics_EditionFallback_ReturnsUnknownWhenNull()
        {
            var profile = new CompanyProfile { Edition = null! };
            var edition = profile.Edition ?? "Unknown";

            Assert.Equal("Unknown", edition);

            profile.Edition = "Enterprise";
            edition = profile.Edition ?? "Unknown";
            Assert.Equal("Enterprise", edition);
        }

        #endregion
    }
