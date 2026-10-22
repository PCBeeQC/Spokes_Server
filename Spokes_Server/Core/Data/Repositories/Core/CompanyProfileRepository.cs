using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class CompanyProfileRepository : JsonRepository<CompanyProfile>
{
    [ActivatorUtilitiesConstructor]
    public CompanyProfileRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        ILogger<CompanyProfileRepository>? logger = null)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "company.json", logger)
    {
    }

    public CompanyProfileRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    protected override string GetFilePath(CompanyProfile item) =>
        Path.Combine(_basePath, "company.json");

    /// <summary>
    /// Bootstraps the company profile: creates and persists a default profile if missing from disk.
    /// Pure bootstrap method: does not execute image processing or mutate branding on read.
    /// </summary>
    public virtual CompanyProfile GetOrCreate()
    {
        EnsureLoaded();
        var profile = _cache.Values.FirstOrDefault();
        if (profile == null)
        {
            profile = new CompanyProfile();
            Save(profile);
        }

        return profile;
    }

    /// <summary>
    /// Pure query accessor to retrieve the active company profile from memory without mutating disk state.
    /// Returns the cached profile or a default in-memory instance if not yet initialized.
    /// </summary>
    public virtual CompanyProfile Get()
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault() ?? new CompanyProfile();
    }

    // Domain-owned configuration accessors
    public BrandingConfig GetBranding() => Get().Branding;
    public ContactConfig GetContact() => Get().Contact;
    public DocumentNumberingConfig GetDocumentNumbering() => Get().DocumentNumbering;
    public AccountingConfig GetAccounting() => Get().Accounting;
    public ChatConfig GetChat() => Get().Chat;
    public PushConfig GetPush() => Get().Push;
    public BackupConfig GetBackup() => Get().Backup;
    public LicensingConfig GetLicensing() => Get().Licensing;
    public StorageConfig GetStorage() => Get().Storage;
    public AuthConfig GetAuth() => Get().Auth;
    public EmailServerSettings GetEmailSettings() => Get().EmailSettings;
    public TimesheetSettings GetTimesheetSettings() => Get().TimesheetConfig;
    public HRSettings GetHRSettings() => Get().HRConfig;
}
