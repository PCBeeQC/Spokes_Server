using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public class LicenseValidationServiceTests : IDisposable
{
    private readonly LicenseValidationService _licenseService;
    private readonly RSA _testRsa;
    private readonly string _testPublicKeyPem;
    private readonly LicenseValidationService _testKeyLicenseService;

    public LicenseValidationServiceTests()
    {
            var mockLogger = new Mock<ILogger<LicenseValidationService>>();
            _licenseService = new LicenseValidationService(mockLogger.Object, new VersionMetadata("2026.8.102"));

            _testRsa = RSA.Create(2048);
            _testPublicKeyPem = _testRsa.ExportSubjectPublicKeyInfoPem();
            _testKeyLicenseService = new LicenseValidationService(mockLogger.Object, new VersionMetadata("2026.8.102"), _testPublicKeyPem);
        }

        public void Dispose()
        {
            _testRsa.Dispose();
        }

        private string CreateSignedLicenseJson(string email, string licenseId, DateTime validUntil, List<string> editions)
        {
            string dataToSign = $"{email}|{licenseId}|{validUntil:O}|{string.Join(",", editions)}";
            byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSign);
            byte[] signatureBytes = _testRsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var license = new SpokesLicenseFile
            {
                Email = email,
                LicenseId = licenseId,
                ValidForUpdatesUntil = validUntil,
                AvailableEditions = editions,
                Signature = Convert.ToBase64String(signatureBytes)
            };

            return JsonSerializer.Serialize(license);
        }

        private static ServerConfig CreateEscrowConfig(string creationVersion = "2026.9.5")
        {
            using var escrow = RSA.Create(2048);
            return new ServerConfig
            {
                DatabaseCreationVersion = creationVersion,
                DatabaseCreationId = Guid.NewGuid().ToString(),
                ServerMasterRsaPublicKey = escrow.ToXmlString(false)
            };
        }

        private static RelayTrialTicket CreateTicketFor(ServerConfig config, DateTime expiresAtUtc) => new()
        {
            Fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey)!,
            IssuedAt = expiresAtUtc.AddMonths(-2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ExpiresAt = expiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Signature = "relay-signature"
        };

        [Fact]
        public void ValidateLicense_NoTicket_TrialNotStartedYet_ReturnsValid()
        {
            var serverConfig = CreateEscrowConfig();

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Contains("starts with the first mobile notification", result.Message);
        }

        [Fact]
        public void ValidateLicense_TicketNotExpired_ReturnsValidWithExpiryDate()
        {
            var serverConfig = CreateEscrowConfig();
            var expiresAt = DateTime.UtcNow.AddDays(30);
            serverConfig.RelayTrialTicket = CreateTicketFor(serverConfig, expiresAt);

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Contains($"active until {expiresAt:yyyy-MM-dd}", result.Message);
        }

        [Fact]
        public void ValidateLicense_TicketExpired_ReturnsExpired()
        {
            var serverConfig = CreateEscrowConfig();
            var expiresAt = DateTime.UtcNow.AddDays(-1);
            serverConfig.RelayTrialTicket = CreateTicketFor(serverConfig, expiresAt);

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Expired, result.Status);
            Assert.Contains($"expired on {expiresAt:yyyy-MM-dd}", result.Message);
            Assert.False(LicenseValidationService.IsLicenseActive(result.Status));
        }

        [Fact]
        public void ValidateLicense_ExpiredTicketForDifferentKey_IsIgnored()
        {
            var otherServer = CreateEscrowConfig();
            var serverConfig = CreateEscrowConfig();
            serverConfig.RelayTrialTicket = CreateTicketFor(otherServer, DateTime.UtcNow.AddDays(-1));

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Contains("starts with the first mobile notification", result.Message);
        }

        [Fact]
        public void ValidateLicense_TicketWithUnparseableExpiry_TreatedAsNoTicket()
        {
            var serverConfig = CreateEscrowConfig();
            var ticket = CreateTicketFor(serverConfig, DateTime.UtcNow.AddDays(-1));
            ticket.ExpiresAt = "not-a-date";
            serverConfig.RelayTrialTicket = ticket;

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
        }

        [Fact]
        public void ValidateLicense_TrialStatusIgnoresInstallAge()
        {
            // An old install is not "expired" locally: only the relay's ticket decides the trial.
            var serverConfig = CreateEscrowConfig("2025.1.1");

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
        }

        [Fact]
        public void ValidateLicense_DemoMode_ReportsVersionBasedUpdateWindow()
        {
            var serverConfig = CreateEscrowConfig("server-v2026.10.50");

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal("server-v2026.10.50", result.FirstInstalledVersion);
            Assert.Equal("v2026.12.0", result.MaxAllowedVersion);
        }

        [Fact]
        public void ValidateLicense_DemoMode_DoesNotMutateConfig()
        {
            var serverConfig = new ServerConfig();

            _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(string.Empty, serverConfig.DatabaseCreationVersion);
            Assert.Equal(string.Empty, serverConfig.DatabaseCreationId);
            Assert.Null(serverConfig.RelayTrialTicket);
        }

        [Fact]
        public void ValidateLicense_NullConfigInDemoMode_ReturnsValidWithUnknownMaxAllowed()
        {
            var result = _licenseService.ValidateLicense(null, null);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Equal("vUnknown", result.MaxAllowedVersion);
            Assert.Null(result.FirstInstalledVersion);
        }

        [Fact]
        public void EnsureDatabaseCreationIdentity_PopulatesVersionAndIdWhenMissing()
        {
            var config = new ServerConfig();
            var changed = _licenseService.EnsureDatabaseCreationIdentity(config);

            Assert.True(changed);
            Assert.Equal(LicenseValidationService.AppVersion, config.DatabaseCreationVersion);
            Assert.True(Guid.TryParse(config.DatabaseCreationId, out _));
        }

        [Fact]
        public void EnsureDatabaseCreationIdentity_PreservesExistingValues()
        {
            var config = new ServerConfig
            {
                DatabaseCreationVersion = "2025.1.1",
                DatabaseCreationId = "existing-id"
            };

            var changed = _licenseService.EnsureDatabaseCreationIdentity(config);

            Assert.False(changed);
            Assert.Equal("2025.1.1", config.DatabaseCreationVersion);
            Assert.Equal("existing-id", config.DatabaseCreationId);
        }

        [Fact]
        public void ValidateLicense_ReturnsHardLock_WhenLicenseSignatureIsTampered()
        {
            var license = new SpokesLicenseFile
            {
                Email = "test@example.com",
                LicenseId = "SPK-TEST",
                ValidForUpdatesUntil = DateTime.UtcNow.AddMonths(-2),
                Signature = Convert.ToBase64String(new byte[256])
            };

            var json = JsonSerializer.Serialize(license);
            var result = _licenseService.ValidateLicense(json, new ServerConfig());

            Assert.Equal(LicenseStatus.HardLock, result.Status);
            Assert.Equal("License signature is invalid or tampered with.", result.Message);
        }

        [Fact]
        public void ValidateLicense_ReturnsExpired_WhenAuthenticLicenseExpiredInPast()
        {
            var expiredDate = DateTime.UtcNow.AddMonths(-2);
            var json = CreateSignedLicenseJson("owner@example.com", "SPK-EXPIRED-TEST", expiredDate, new List<string> { "Family", "Business" });

            var result = _testKeyLicenseService.ValidateLicense(json, new ServerConfig());

            Assert.Equal(LicenseStatus.Expired, result.Status);
            Assert.Contains("expired", result.Message);
            Assert.NotNull(result.ParsedLicense);
            Assert.Equal("owner@example.com", result.ParsedLicense.Email);
            Assert.Equal(2, result.ParsedLicense.AvailableEditions.Count);
        }

        [Fact]
        public void ValidateLicense_ReturnsValid_WhenAuthenticLicenseIsActive()
        {
            var futureDate = DateTime.UtcNow.AddYears(1);
            var json = CreateSignedLicenseJson("active@example.com", "SPK-ACTIVE-TEST", futureDate, new List<string> { "Family" });

            var result = _testKeyLicenseService.ValidateLicense(json, new ServerConfig());

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Equal("Spokes license is active.", result.Message);
            Assert.NotNull(result.ParsedLicense);
            Assert.Equal("active@example.com", result.ParsedLicense.Email);
        }

        [Fact]
        public void GetAvailableEditions_WhenLicenseHasEditions_ReturnsLicenseEditions()
        {
            var validationResult = new LicenseValidationResult
            {
                ParsedLicense = new SpokesLicenseFile
                {
                    AvailableEditions = new List<string> { "Pro", "Enterprise" }
                }
            };

            var editions = LicenseValidationService.GetAvailableEditions(validationResult);

            Assert.Equal(new List<string> { "Pro", "Enterprise" }, editions);
        }

        [Fact]
        public void GetAvailableEditions_WhenLicenseNullOrEmptyEditions_ReturnsFamilyFallback()
        {
            var nullLicenseResult = new LicenseValidationResult
            {
                ParsedLicense = null
            };
            var emptyEditionsResult = new LicenseValidationResult
            {
                ParsedLicense = new SpokesLicenseFile
                {
                    AvailableEditions = new List<string>()
                }
            };

            var fallbackNull = LicenseValidationService.GetAvailableEditions(nullLicenseResult);
            var fallbackEmpty = LicenseValidationService.GetAvailableEditions(emptyEditionsResult);

            Assert.Equal(new List<string> { "Family" }, fallbackNull);
            Assert.Equal(new List<string> { "Family" }, fallbackEmpty);
        }

        [Fact]
        public void CalculateMaxAllowedVersion_HandlesOffsetsAndYearRollOver()
        {
            Assert.Equal("vUnknown", LicenseValidationService.CalculateMaxAllowedVersion(null!));
            Assert.Equal("vUnknown", LicenseValidationService.CalculateMaxAllowedVersion(""));
            Assert.Equal("vUnknown", LicenseValidationService.CalculateMaxAllowedVersion("invalid"));
            Assert.Equal("v2026.6.0", LicenseValidationService.CalculateMaxAllowedVersion("2026.4.1"));
            Assert.Equal("v2027.1.0", LicenseValidationService.CalculateMaxAllowedVersion("2026.11.1"));
            Assert.Equal("v2026.12.0", LicenseValidationService.CalculateMaxAllowedVersion("server-v2026.10.15"));
        }

        [Fact]
        public void ValidateLicense_InvalidJsonPayload_ReturnsHardLock()
        {
            var result = _licenseService.ValidateLicense("{ invalid json }", new ServerConfig());

            Assert.Equal(LicenseStatus.HardLock, result.Status);
            Assert.Contains("Failed to parse license payload", result.Message);
        }

        [Fact]
        public void AppVersion_ReturnsNonEmptyVersionString()
        {
            var version = LicenseValidationService.AppVersion;

            Assert.False(string.IsNullOrWhiteSpace(version));
        }

        [Fact]
        public void SpokesLicenseFile_And_LicenseValidationResult_PropertyCoverage()
        {
            var testDate = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
            List<string> editions = ["Enterprise", "Custom"];
            var license = new SpokesLicenseFile
            {
                Email = "admin@spokes.local",
                LicenseId = "SPK-PROPERTY-TEST",
                ValidForUpdatesUntil = testDate,
                AvailableEditions = editions,
                Signature = "base64-signature"
            };

            Assert.Equal("admin@spokes.local", license.Email);
            Assert.Equal("SPK-PROPERTY-TEST", license.LicenseId);
            Assert.Equal(testDate, license.ValidForUpdatesUntil);
            Assert.Equal(editions, license.AvailableEditions);
            Assert.Equal("base64-signature", license.Signature);

            var result = new LicenseValidationResult
            {
                Status = LicenseStatus.Tolerated,
                Message = "Test message",
                ParsedLicense = license,
                FirstInstalledVersion = "2026.1.0",
                LockupVersion = "2026.5.0",
                MaxAllowedVersion = "v2026.7.0"
            };

            Assert.Equal(LicenseStatus.Tolerated, result.Status);
            Assert.Equal("Test message", result.Message);
            Assert.Same(license, result.ParsedLicense);
            Assert.Equal("2026.1.0", result.FirstInstalledVersion);
            Assert.Equal("2026.5.0", result.LockupVersion);
            Assert.Equal("v2026.7.0", result.MaxAllowedVersion);
        }
    }
