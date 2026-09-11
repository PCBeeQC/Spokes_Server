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
        HardLock
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

        // This is the release version of THIS specific compiled binary. 
        // Example format: 2026.4.102 (Year.Month.Revision)
        public static string AppVersion => Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2026.4.102";


        // Hardcoded blacklist of leaked licenses
        private static readonly List<string> BlacklistedKeys = new()
        {
            // "SPK-12345678",
        };

        public LicenseValidationService(ILogger<LicenseValidationService> logger, Spokes_Server.Core.Services.Core.EncryptionService encryptionService, VersionMetadata versionMetadata)
        {
            _logger = logger;
            _encryptionService = encryptionService;
            _versionMetadata = versionMetadata;
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

                // DEMO MODE CHECK if no license
                if (string.IsNullOrEmpty(jsonPayload))
                {
                    string? firstInstalled = config?.DatabaseCreationVersion;
                    string? lockupVersion = firstInstalled;

                    if (config != null && VerifyDemoVersion(config.DatabaseCreationVersion, config.DatabaseCreationSignature, _encryptionService.KeyHash))
                    {
                        var cleanDbVersion = GetCleanVersion(config.DatabaseCreationVersion);
                        var dbVersionParts = cleanDbVersion.Split('.');
                        var appVersionParts = cleanAppVersion.Split('.');

                        if (dbVersionParts.Length >= 2 && int.TryParse(dbVersionParts[0], out int demoDbYear) && int.TryParse(dbVersionParts[1], out int demoDbMonth))
                        {
                            // Lockup occurs when difference > 2, so the first locked version is month + 3
                            int lockedMonth = demoDbMonth + 3;
                            int lockedYear = demoDbYear;
                            while (lockedMonth > 12)
                            {
                                lockedMonth -= 12;
                                lockedYear++;
                            }
                            
                            int firstDigitIndex = -1;
                            for (int i = 0; i < config.DatabaseCreationVersion.Length; i++)
                            {
                                if (char.IsDigit(config.DatabaseCreationVersion[i]))
                                {
                                    firstDigitIndex = i;
                                    break;
                                }
                            }
                            string prefix = firstDigitIndex > 0 ? config.DatabaseCreationVersion.Substring(0, firstDigitIndex) : "";
                            lockupVersion = $"{prefix}{lockedYear}.{lockedMonth}.0";
                        }

                        bool isValid = false;
                        if (appVersionParts.Length >= 2 && dbVersionParts.Length >= 2)
                        {
                            int.TryParse(appVersionParts[0], out int demoAppYear);
                            int.TryParse(appVersionParts[1], out int demoAppMonth);
                            int.TryParse(dbVersionParts[0], out int dbYear);
                            int.TryParse(dbVersionParts[1], out int dbMonth);
                            
                            int demoMonthsDifference = (demoAppYear - dbYear) * 12 + (demoAppMonth - dbMonth);
                            isValid = demoMonthsDifference <= 2;
                        }
                        else
                        {
                            isValid = true; // Fallback for dev builds if format isn't year.month
                        }

                        string maxAllowed = CalculateMaxAllowedVersion(firstInstalled ?? "0.0.0", 2);
                        if (isValid)
                        {
                            return new LicenseValidationResult { Status = LicenseStatus.Valid, Message = "Free Demo Mode Active.", FirstInstalledVersion = firstInstalled, LockupVersion = lockupVersion, MaxAllowedVersion = maxAllowed };
                        }
                    }
                    
                    string finalMaxAllowed = config != null ? CalculateMaxAllowedVersion(config.DatabaseCreationVersion, 2) : "vUnknown";
                    
                    // Security patch bypass — allow expired demos to run security updates
                    if (_versionMetadata.IsSecurityPatch)
                    {
                        return new LicenseValidationResult { Status = LicenseStatus.Tolerated, Message = "This is a security patch. Your demo has expired — feature updates require a valid license.", FirstInstalledVersion = firstInstalled, LockupVersion = lockupVersion, MaxAllowedVersion = finalMaxAllowed };
                    }
                    
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "No license installed and Demo Mode expired (Server was updated).", FirstInstalledVersion = firstInstalled, LockupVersion = lockupVersion, MaxAllowedVersion = finalMaxAllowed };
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                var license = JsonSerializer.Deserialize<SpokesLicenseFile>(jsonPayload, options);
                if (license == null) return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "Invalid license format." };

                if (license.AvailableEditions == null) license.AvailableEditions = new List<string>();

                // Security patch bypass — allow all instances to run security updates regardless of license status
                if (_versionMetadata.IsSecurityPatch)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.Tolerated, Message = "This is a security patch. Your license is expired — feature updates require a valid license.", ParsedLicense = license };
                }

                if (BlacklistedKeys.Contains(license.LicenseId))
                {
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "This license key has been revoked." };
                }

                // Verify Cryptographic Signature
                string dataToSign = $"{license.Email}|{license.LicenseId}|{license.ValidForUpdatesUntil:O}|{string.Join(",", license.AvailableEditions)}";
                byte[] dataBytes = Encoding.UTF8.GetBytes(dataToSign);
                byte[] signatureBytes = Convert.FromBase64String(license.Signature);

                using var rsa = RSA.Create();
                rsa.ImportFromPem(SpokesConstants.LicensePublicKeyPem);

                bool isSignatureValid = rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

                if (!isSignatureValid)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "License signature is invalid or tampered with." };
                }

                // Version Validation Logic
                // AppVersion format: 2026.4.102 -> [0] = 2026, [1] = 4, [2] = 102
                var versionParts = cleanAppVersion.Split('.');
                if (versionParts.Length < 2)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.Valid, Message = "Dev build. Assuming Valid." };
                }

                int appYear = int.Parse(versionParts[0]);
                int appMonth = int.Parse(versionParts[1]);
                int appRevision = versionParts.Length > 2 ? int.Parse(versionParts[2]) : 0;

                int licenseYear = license.ValidForUpdatesUntil.Year;
                int licenseMonth = license.ValidForUpdatesUntil.Month;

                int monthsDifference = (appYear - licenseYear) * 12 + (appMonth - licenseMonth);

                string maxPaidAllowed = $"v{license.ValidForUpdatesUntil.Year}.{license.ValidForUpdatesUntil.Month}.0";

                if (monthsDifference <= 0)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.Valid, Message = "License is active.", ParsedLicense = license, MaxAllowedVersion = maxPaidAllowed };
                }
                else if (monthsDifference == 1 && appRevision == 0)
                {
                    return new LicenseValidationResult { Status = LicenseStatus.Tolerated, Message = $"Your maintenance expired on {license.ValidForUpdatesUntil:yyyy-MM}. You are using a tolerated version, but future updates will be locked.", ParsedLicense = license, MaxAllowedVersion = maxPaidAllowed };
                }
                else
                {
                    return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = $"Your maintenance expired on {license.ValidForUpdatesUntil:yyyy-MM}. Please renew or downgrade to an older version.", ParsedLicense = license, MaxAllowedVersion = maxPaidAllowed };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate license payload.");
                return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = $"Failed to parse license payload: {ex.Message}" };
            }
        }
    }
}
