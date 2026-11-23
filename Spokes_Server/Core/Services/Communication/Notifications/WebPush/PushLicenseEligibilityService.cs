using System;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

/// <summary>
/// Contract for determining whether native push relay is licensed and available.
/// </summary>
public interface IPushLicenseEligibilityService
{
    bool IsNativePushAllowed();
    NativePushAvailability GetNativePushStatus();
}

/// <summary>
/// Encapsulates license validation and native push eligibility checks.
/// Decouples controllers from individual repository and licensing engine dependencies.
/// </summary>
public class PushLicenseEligibilityService : IPushLicenseEligibilityService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly LicenseValidationService _licenseValidation;

    public PushLicenseEligibilityService(
        CompanyProfileRepository companyProfiles,
        ServerConfigRepository serverConfigs,
        LicenseValidationService licenseValidation)
    {
        _companyProfiles = companyProfiles ?? throw new ArgumentNullException(nameof(companyProfiles));
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _licenseValidation = licenseValidation ?? throw new ArgumentNullException(nameof(licenseValidation));
    }

    public bool IsNativePushAllowed()
    {
        var profile = _companyProfiles.Get();
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        return _licenseValidation.IsNativePushAllowed(profile?.LicensePayload, serverConfig);
    }

    public NativePushAvailability GetNativePushStatus() =>
        EvaluateNativePushStatus(_companyProfiles, _serverConfigs, _licenseValidation);

    /// <summary>
    /// Evaluates whether native push notifications are permitted under the server's active license or trial window.
    /// </summary>
    public static NativePushAvailability EvaluateNativePushStatus(
        CompanyProfileRepository companyProfiles,
        ServerConfigRepository serverConfigs,
        LicenseValidationService licenseValidation)
    {
        var profile = companyProfiles.Get();
        var serverConfig = serverConfigs.GetOrCreateGlobalConfig();
        var licenseResult = licenseValidation.ValidateLicense(profile?.LicensePayload, serverConfig);
        bool isAllowed = LicenseValidationService.IsLicenseActive(licenseResult.Status);
        return new NativePushAvailability(isAllowed, licenseResult.Status.ToString(), licenseResult.Message);
    }
}
