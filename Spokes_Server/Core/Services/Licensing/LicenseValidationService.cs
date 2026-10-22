using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Licensing;

public class SpokesLicenseFile
{
    public string Email { get; set; } = string.Empty;
    public string LicenseId { get; set; } = string.Empty;
    public DateTime ValidForUpdatesUntil { get; set; }
    public List<string> AvailableEditions { get; set; } = [];
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
    private readonly VersionMetadata _versionMetadata;
    private readonly string _publicKeyPem;

    // This is the release version of THIS specific compiled binary. 
    // Example format: 2026.4.102 (Year.Month.Revision)
    public static string AppVersion => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "2026.4.102";


    // Hardcoded blacklist of leaked licenses
    private static readonly List<string> BlacklistedKeys = [];

    public LicenseValidationService(
        ILogger<LicenseValidationService> logger,
        VersionMetadata versionMetadata,
        string? publicKeyPem = null,
        Microsoft.Extensions.Options.IOptions<Spokes_Server.Core.Configuration.SpokesOptions>? options = null,
        IConfiguration? configuration = null)
    {
        _logger = logger;
        _versionMetadata = versionMetadata;
        _publicKeyPem = publicKeyPem ?? options?.Value.LicensePublicKeyPem ?? configuration?["Spokes:LicensePublicKeyPem"] ?? SpokesConstants.LicensePublicKeyPem;
    }

    public static List<string> GetAvailableEditions(LicenseValidationResult validationResult)
    {
        if (validationResult.ParsedLicense != null && validationResult.ParsedLicense.AvailableEditions.Count > 0)
        {
            return validationResult.ParsedLicense.AvailableEditions;
        }

        // Demo Mode fallback (or invalid license format)
        return ["Family"];
    }

    public static bool IsLicenseActive(LicenseStatus status) =>
        status != LicenseStatus.HardLock && status != LicenseStatus.Expired;

    public bool IsNativePushAllowed(string? jsonPayload, ServerConfig? config = null)
    {
        var result = ValidateLicense(jsonPayload, config);
        return IsLicenseActive(result.Status);
    }


        public static string CalculateMaxAllowedVersion(string baseVersion, int monthOffset = 2)
        {
            if (string.IsNullOrEmpty(baseVersion)) return "vUnknown";
            string cleanVersion = VersionMetadata.GetCleanVersion(baseVersion);
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

    /// <summary>
    /// Ensures the server's creation version and id exist. Called once at boot, which saves the config when this returns true.
    /// </summary>
    public bool EnsureDatabaseCreationIdentity(ServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        bool changed = false;

        if (string.IsNullOrEmpty(config.DatabaseCreationVersion))
        {
            config.DatabaseCreationVersion = AppVersion;
            changed = true;
        }

        if (string.IsNullOrEmpty(config.DatabaseCreationId))
        {
            config.DatabaseCreationId = Guid.NewGuid().ToString();
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Trial status for servers without a license. The authoritative trial clock lives on the Spokes Push Relay, which issues
    /// a signed ticket on the first native push; the server only mirrors it. Without a ticket the trial has not started yet,
    /// so the server must keep contacting the relay (which decides).
    /// </summary>
    private static LicenseValidationResult EvaluateTrial(ServerConfig? config)
    {
        string? firstInstalled = config?.DatabaseCreationVersion;
        // Update gating for unlicensed servers stays version-based (2 months of free updates from the first installed version).
        string maxAllowed = config != null ? CalculateMaxAllowedVersion(config.DatabaseCreationVersion, 2) : "vUnknown";

        var ticket = config != null ? RelayTrialTicketService.GetTicketForCurrentKey(config) : null;
        if (ticket == null || !RelayTrialTicketService.TryGetExpiry(ticket, out var expiresAt))
        {
            return new LicenseValidationResult
            {
                Status = LicenseStatus.Valid,
                Message = "Your 2-month free trial of native mobile push notifications starts with the first mobile notification.",
                FirstInstalledVersion = firstInstalled,
                MaxAllowedVersion = maxAllowed
            };
        }

        if (expiresAt > DateTime.UtcNow)
        {
            return new LicenseValidationResult
            {
                Status = LicenseStatus.Valid,
                Message = $"Your free trial of native mobile push notifications is active until {expiresAt:yyyy-MM-dd}.",
                FirstInstalledVersion = firstInstalled,
                MaxAllowedVersion = maxAllowed
            };
        }

        return new LicenseValidationResult
        {
            Status = LicenseStatus.Expired,
            Message = $"Your 2-month free trial for the Spokes Push Relay expired on {expiresAt:yyyy-MM-dd}.",
            FirstInstalledVersion = firstInstalled,
            MaxAllowedVersion = maxAllowed
        };
    }

    public LicenseValidationResult ValidateLicense(string? jsonPayload, ServerConfig? config = null)
    {
            try
            {
                // DEMO / TRIAL MODE CHECK if no license
                if (string.IsNullOrEmpty(jsonPayload))
                {
                    return EvaluateTrial(config);
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true
                };
                var license = JsonSerializer.Deserialize<SpokesLicenseFile>(jsonPayload, options);
                if (license == null) return new LicenseValidationResult { Status = LicenseStatus.HardLock, Message = "Invalid license format." };

                license.AvailableEditions ??= [];

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

