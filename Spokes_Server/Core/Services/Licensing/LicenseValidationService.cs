using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Services.Licensing
{
    public class SpokesLicenseFile
    {
        public string Email { get; set; } = string.Empty;
        public string LicenseId { get; set; } = string.Empty;
        public DateTime ValidForUpdatesUntil { get; set; }
        public List<string> AvailableEditions { get; set; } = new();
        public string Signature { get; set; } = string.Empty;
    }

    public enum LicenseStatus
    {
        Valid,
        Tolerated,
        HardLock,
        Expired
    }

    public class LicenseValidationResult
    {
        public LicenseStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public SpokesLicenseFile? ParsedLicense { get; set; }
        public string? FirstInstalledVersion { get; set; }
        public string? LockupVersion { get; set; }
        public string? MaxAllowedVersion { get; set; }
    }

    public class LicenseValidationService
    {
        private readonly ILogger<LicenseValidationService> _logger;
        private readonly Spokes_Server.Core.Services.Core.EncryptionService _encryptionService;
        private readonly VersionMetadata _versionMetadata;
        private readonly string _publicKeyPem;

        // This is the release version of THIS specific compiled binary. 
        // Example format: 2026.4.102 (Year.Month.Revision)
        public static string AppVersion => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2026.4.102";


        // Hardcoded blacklist of leaked licenses
        private static readonly List<string> BlacklistedKeys = new()
        {
            // "SPK-12345678",
        };

        public LicenseValidationService(ILogger<LicenseValidationService> logger, Spokes_Server.Core.Services.Core.EncryptionService encryptionService, VersionMetadata versionMetadata, string? publicKeyPem = null)
        {
            _logger = logger;
            _encryptionService = encryptionService;
            _versionMetadata = versionMetadata;
            _publicKeyPem = publicKeyPem ?? SpokesConstants.LicensePublicKeyPem;
        }

        public static string SignDemoVersion(string version, string? securityKeyHash = null)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SpokesConstants.LicenseHMACSalt));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(version + (securityKeyHash ?? "")));
            return Convert.ToBase64String(hash);
        }

        public static bool VerifyDemoVersion(string version, string signature, string? securityKeyHash = null)
        {
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(signature)) return false;
            return SignDemoVersion(version, securityKeyHash) == signature;
        }

        public static List<string> GetAvailableEditions(LicenseValidationResult validationResult)
        {
            if (validationResult.ParsedLicense != null && validationResult.ParsedLicense.AvailableEditions.Any())
            {
                return validationResult.ParsedLicense.AvailableEditions;
            }

            // Demo Mode fallback (or invalid license format)
            return new List<string> { "Family" };
        }

        private static string GetCleanVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "0.0.0";

            // Extract substring starting from the first digit to handle prefixes like 'server-v' or 'v'
            int firstDigitIndex = -1;
            for (int i = 0; i < version.Length; i++)
            {
                if (char.IsDigit(version[i]))
                {
                    firstDigitIndex = i;
                    break;
                }
            }

            string cleanVersion = firstDigitIndex >= 0 ? version.Substring(firstDigitIndex) : version.TrimStart('v', 'V');

            int plusIndex = cleanVersion.IndexOf('+');
            if (plusIndex >= 0) cleanVersion = cleanVersion.Substring(0, plusIndex);

            int minusIndex = cleanVersion.IndexOf('-');
            if (minusIndex >= 0) cleanVersion = cleanVersion.Substring(0, minusIndex);

            return cleanVersion;
        }

        public static string CalculateMaxAllowedVersion(string baseVersion, int monthOffset = 2)
        {
            if (string.IsNullOrEmpty(baseVersion)) return "vUnknown";
            string cleanVersion = GetCleanVersion(baseVersion);
            var parts = cleanVersion.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out int year) && int.TryParse(parts[1], out int month))
            {
                month += monthOffset;
                while (month > 12)
                {
                    month -= 12;
                    year++;
                }
                return $"v{year}.{month}.0";
            }
            return "vUnknown";
        }

        public LicenseValidationResult ValidateLicense(string? jsonPayload, Models.Core.ServerConfig? config = null)
        {
            try
            {
                var cleanAppVersion = GetCleanVersion(AppVersion);
                _ = Version.TryParse(cleanAppVersion, out var appVerObj);

                // DEMO / TRIAL MODE CHECK if no license
                if (string.IsNullOrEmpty(jsonPayload))
                {
                    string? firstInstalled = config?.DatabaseCreationVersion;
                    string finalMaxAllowed = config != null ? CalculateMaxAllowedVersion(config.DatabaseCreationVersion, 2) : "vUnknown";

                    if (config != null)
                    {
                        if (!VerifyDemoVersion(config.DatabaseCreationVersion, config.DatabaseCreationSignature, _encryptionService.KeyHash))
                        {
                            return new LicenseValidationResult
                            {
                                Status = LicenseStatus.HardLock,
                                Message = "Demo version signature is invalid or tampered with.",
                                FirstInstalledVersion = firstInstalled,
                                MaxAllowedVersion = finalMaxAllowed
                            };
                        }

                        var cleanDbVersion = GetCleanVersion(config.DatabaseCreationVersion);
                        var dbVersionParts = cleanDbVersion.Split('.');

                        if (dbVersionParts.Length >= 2 && int.TryParse(dbVersionParts[0], out int demoDbYear) && int.TryParse(dbVersionParts[1], out int demoDbMonth))
                        {
                            int currentYear = DateTime.UtcNow.Year;
                            int currentMonth = DateTime.UtcNow.Month;
                            int demoMonthsDifference = (currentYear - demoDbYear) * 12 + (currentMonth - demoDbMonth);

                            if (demoMonthsDifference >= 0 && demoMonthsDifference <= 2)
                            {
                                return new LicenseValidationResult 
                                { 
                                    Status = LicenseStatus.Valid, 
                                    Message = "Free Trial Active.", 
                                    FirstInstalledVersion = firstInstalled, 
                                    MaxAllowedVersion = finalMaxAllowed 
                                };
                            }
                        }
                    }

                    return new LicenseValidationResult 
                    { 
                        Status = LicenseStatus.Expired, 
                        Message = "Your 2-month free trial for the Spokes Push Relay has expired.", 
                        FirstInstalledVersion = firstInstalled, 
                        MaxAllowedVersion = finalMaxAllowed 
                    };
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                var license = JsonSerializer.Deserialize<SpokesLicenseFile>(jsonPayload, options);
                if (license == null) return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "Invalid license format." };

                if (license.AvailableEditions == null) license.AvailableEditions = new List<string>();

                if (BlacklistedKeys.Contains(license.LicenseId))
                {
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "This license key has been revoked." };
                }

                // Verify Cryptographic Signature
                string dataToSign = $"{license.Email}|{license.LicenseId}|{license.ValidForUpdatesUntil:O}|{string.Join(",", license.AvailableEditions)}";
                byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSign);
                byte[] signatureBytes = Convert.FromBase64String(license.Signature);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(_publicKeyPem);

                bool isSignatureValid = rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                if (!isSignatureValid)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "License signature is invalid or tampered with." };
                }

                string maxPaidAllowed = $"v{license.ValidForUpdatesUntil.Year}.{license.ValidForUpdatesUntil.Month}.0";

                // Date Validation Logic: License gives access to Push Relay until ValidForUpdatesUntil
                if (license.ValidForUpdatesUntil < DateTime.UtcNow)
                {
                    return new LicenseValidationResult 
                    { 
                        Status = LicenseStatus.Expired, 
                        Message = $"Your Spokes license expired on {license.ValidForUpdatesUntil:yyyy-MM-dd}.", 
                        ParsedLicense = license, 
                        MaxAllowedVersion = maxPaidAllowed 
                    };
                }

                return new LicenseValidationResult 
                { 
                    Status = LicenseStatus.Valid, 
                    Message = "Spokes license is active.", 
                    ParsedLicense = license, 
                    MaxAllowedVersion = maxPaidAllowed 
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate license payload.");
                return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = $"Failed to parse license payload: {ex.Message}" };
            }
        }
    }
}
