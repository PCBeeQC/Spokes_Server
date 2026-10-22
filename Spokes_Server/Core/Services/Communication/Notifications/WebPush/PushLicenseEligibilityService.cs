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

    public NativePushAvailability GetNativePushStatus()
    {
        var profile = _companyProfiles.Get();
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        var licenseResult = _licenseValidation.ValidateLicense(profile?.LicensePayload, serverConfig);
        bool isAllowed = LicenseValidationService.IsLicenseActive(licenseResult.Status);
        return new NativePushAvailability(isAllowed, licenseResult.Status.ToString(), licenseResult.Message);
    }
}
