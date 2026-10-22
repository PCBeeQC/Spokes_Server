namespace Spokes_Server.Aggregate.Contexts;

using System.Threading.Tasks;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Models.Core;

public class CoreContext
{
    public virtual CompanyProfileRepository CompanyProfile { get; }
    public virtual DocumentTemplateRepository DocumentTemplates { get; }
    public virtual OpenIdAccountRepository OpenIdAccounts { get; }
    public virtual ServerConfigRepository ServerConfigs { get; }
    public virtual SystemConfigRepository SystemConfigs { get; }
    public virtual DeviceSessionRepository DeviceSessions { get; }
    public virtual DemoConfigRepository DemoConfigs { get; }
    public virtual StandardDocumentRepository StandardDocuments { get; }

    protected CoreContext()
    {
        CompanyProfile = null!;
        DocumentTemplates = null!;
        OpenIdAccounts = null!;
        ServerConfigs = null!;
        SystemConfigs = null!;
        DeviceSessions = null!;
        DemoConfigs = null!;
        StandardDocuments = null!;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public CoreContext(
        CompanyProfileRepository companyProfile,
        DocumentTemplateRepository documentTemplates,
        OpenIdAccountRepository openIdAccounts,
        ServerConfigRepository serverConfigs,
        SystemConfigRepository systemConfigs,
        DeviceSessionRepository deviceSessions,
        DemoConfigRepository demoConfigs,
        StandardDocumentRepository standardDocuments)
    {
        CompanyProfile = companyProfile;
        DocumentTemplates = documentTemplates;
        OpenIdAccounts = openIdAccounts;
        ServerConfigs = serverConfigs;
        SystemConfigs = systemConfigs;
        DeviceSessions = deviceSessions;
        DemoConfigs = demoConfigs;
        StandardDocuments = standardDocuments;
    }

    /// <summary>
    /// Exposes core domain branding configuration.
    /// </summary>
    public virtual BrandingConfig GetBranding() => CompanyProfile?.GetBranding() ?? new BrandingConfig();

    /// <summary>
    /// Exposes core domain contact configuration.
    /// </summary>
    public virtual ContactConfig GetContact() => CompanyProfile?.GetContact() ?? new ContactConfig();

    /// <summary>
    /// Exposes core domain backup configuration.
    /// </summary>
    public virtual BackupConfig GetBackup() => CompanyProfile?.GetBackup() ?? new BackupConfig();

    /// <summary>
    /// Exposes core domain licensing configuration.
    /// </summary>
    public virtual LicensingConfig GetLicensing() => CompanyProfile?.GetLicensing() ?? new LicensingConfig();

    /// <summary>
    /// Exposes core domain storage configuration.
    /// </summary>
    public virtual StorageConfig GetStorage() => CompanyProfile?.GetStorage() ?? new StorageConfig();

    /// <summary>
    /// Exposes core domain authentication configuration.
    /// </summary>
    public virtual AuthConfig GetAuth() => CompanyProfile?.GetAuth() ?? new AuthConfig();

    public virtual BrandingConfig Branding => GetBranding();
    public virtual ContactConfig Contact => GetContact();
    public virtual BackupConfig Backup => GetBackup();
    public virtual LicensingConfig Licensing => GetLicensing();
    public virtual StorageConfig Storage => GetStorage();
    public virtual AuthConfig Auth => GetAuth();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => CompanyProfile.LoadFromDisk(),
            () => DocumentTemplates.LoadFromDisk(),
            () => OpenIdAccounts.LoadFromDisk(),
            () => ServerConfigs.LoadFromDisk(),
            () => SystemConfigs.LoadFromDisk(),
            () => DeviceSessions.LoadFromDisk(),
            () => DemoConfigs.LoadFromDisk(),
            () => StandardDocuments?.LoadFromDisk()
        );
    }
}
