using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Tests.Core.Services.Licensing;

public class LicenseValidationServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly EncryptionService _encryptionService;
    private readonly LicenseValidationService _licenseService;
    private readonly RSA _testRsa;
    private readonly string _testPublicKeyPem;
    private readonly LicenseValidationService _testKeyLicenseService;

    public LicenseValidationServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Licensing_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "DataPath", _testDataDir }
        }).Build();
            _encryptionService = new EncryptionService(config);
            var mockLogger = new Mock<ILogger<LicenseValidationService>>();
            _licenseService = new LicenseValidationService(mockLogger.Object, _encryptionService, new VersionMetadata("2026.8.102"));

            _testRsa = RSA.Create(2048);
            _testPublicKeyPem = _testRsa.ExportSubjectPublicKeyInfoPem();
            _testKeyLicenseService = new LicenseValidationService(mockLogger.Object, _encryptionService, new VersionMetadata("2026.8.102"), _testPublicKeyPem);
        }

        public void Dispose()
        {
            _testRsa.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
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

        [Fact]
        public void ValidateLicense_ReturnsValid_WhenTrialIsActive()
        {
            var currentVersion = $"{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.1";
            var serverConfig = new ServerConfig
            {
                DatabaseCreationVersion = currentVersion,
                DatabaseCreationSignature = LicenseValidationService.SignDemoVersion(currentVersion, _encryptionService.KeyHash)
            };

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Equal("Free Trial Active.", result.Message);
        }

        [Fact]
        public void ValidateLicense_ReturnsExpired_WhenTrialIsExpired()
        {
            var expiredDate = DateTime.UtcNow.AddMonths(-4);
            var expiredVersion = $"{expiredDate.Year}.{expiredDate.Month}.1";
            var serverConfig = new ServerConfig
            {
                DatabaseCreationVersion = expiredVersion,
                DatabaseCreationSignature = LicenseValidationService.SignDemoVersion(expiredVersion, _encryptionService.KeyHash)
            };

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Expired, result.Status);
            Assert.Contains("free trial for the Spokes Push Relay has expired", result.Message);
        }

        [Fact]
        public void ValidateLicense_ReturnsHardLock_WhenDemoSignatureIsTampered()
        {
            var currentVersion = $"{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.1";
            var serverConfig = new ServerConfig
            {
                DatabaseCreationVersion = currentVersion,
                DatabaseCreationSignature = "invalid-tampered-signature"
            };

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.HardLock, result.Status);
            Assert.Contains("Demo version signature is invalid or tampered with.", result.Message);
        }

        [Fact]
        public void ValidateLicense_HandlesVersionWithPrefixCorrectly()
        {
            var currentVersion = $"server-v{DateTime.UtcNow.Year}.{DateTime.UtcNow.Month}.50";
            var serverConfig = new ServerConfig
            {
                DatabaseCreationVersion = currentVersion,
                DatabaseCreationSignature = LicenseValidationService.SignDemoVersion(currentVersion, _encryptionService.KeyHash)
            };

            var result = _licenseService.ValidateLicense(null, serverConfig);

            Assert.Equal(LicenseStatus.Valid, result.Status);
            Assert.Equal("Free Trial Active.", result.Message);
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

        [Theory]
        [InlineData(null, "some-signature")]
        [InlineData("", "some-signature")]
        [InlineData("2026.4.1", null)]
        [InlineData("2026.4.1", "")]
        public void VerifyDemoVersion_NullOrEmptyArguments_ReturnsFalse(string? version, string? signature)
        {
            var result = LicenseValidationService.VerifyDemoVersion(version!, signature!);
            Assert.False(result);
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
        public void ValidateLicense_NullConfigInDemoMode_ReturnsExpiredWithUnknownMaxAllowed()
        {
            var result = _licenseService.ValidateLicense(null, null);

            Assert.Equal(LicenseStatus.Expired, result.Status);
            Assert.Equal("vUnknown", result.MaxAllowedVersion);
            Assert.Null(result.FirstInstalledVersion);
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
