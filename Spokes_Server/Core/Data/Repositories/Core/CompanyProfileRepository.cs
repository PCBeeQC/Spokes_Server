using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class CompanyProfileRepository : JsonRepository<CompanyProfile>
{
    private sealed record BackupCredentialSlot(
        string Section,
        string Property,
        Func<BackupConfig, string> Getter,
        Action<BackupConfig, string> Setter)
    {
        public string CacheKey => $"{Section}.{Property}";
    }

    private static readonly BackupCredentialSlot[] BackupCredentialSlots =
    [
        new("S3", "AccessKey", b => b.S3.AccessKey, (b, v) => b.S3.AccessKey = v),
        new("S3", "SecretKey", b => b.S3.SecretKey, (b, v) => b.S3.SecretKey = v),
        new("GoogleDrive", "RefreshToken", b => b.GoogleDrive.RefreshToken, (b, v) => b.GoogleDrive.RefreshToken = v),
        new("GitHub", "PersonalAccessToken", b => b.GitHub.PersonalAccessToken, (b, v) => b.GitHub.PersonalAccessToken = v),
        new("WebDav", "Username", b => b.WebDav.Username, (b, v) => b.WebDav.Username = v),
        new("WebDav", "Password", b => b.WebDav.Password, (b, v) => b.WebDav.Password = v),
        new("Sftp", "Username", b => b.Sftp.Username, (b, v) => b.Sftp.Username = v),
        new("Sftp", "Password", b => b.Sftp.Password, (b, v) => b.Sftp.Password = v)
    ];

    private readonly EncryptedFieldCipherCache _cipherCache;
    private readonly AsyncLocal<bool> _suppressRaiseOnLoaded = new();

    [ActivatorUtilitiesConstructor]
    public CompanyProfileRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        EncryptionService? encryptionService = null,
        ILogger<CompanyProfileRepository>? logger = null)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings"), "company.json", logger)
    {
        _cipherCache = new EncryptedFieldCipherCache(encryptionService ?? new EncryptionService(config));
    }

    public CompanyProfileRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null, null)
    {
    }

    public CompanyProfileRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        ILogger<CompanyProfileRepository>? logger)
        : this(writer, config, null, logger)
    {
    }

    internal EncryptionService EncryptionService => _cipherCache.EncryptionService;

    protected override string GetFilePath(CompanyProfile item) =>
        Path.Combine(_basePath, "company.json");

    protected override void RaiseOnLoaded()
    {
        if (_suppressRaiseOnLoaded.Value)
        {
            return;
        }

        base.RaiseOnLoaded();
    }

    public override void LoadFromDisk()
    {
        bool needsMigrationWrite = false;
        CompanyProfile? loaded = null;

        _suppressRaiseOnLoaded.Value = true;
        try
        {
            base.LoadFromDisk();
            loaded = RawCache.Values.FirstOrDefault();
            if (loaded?.Backup != null)
            {
                needsMigrationWrite = DecryptLoadedCredentials(loaded.Backup);
            }
        }
        finally
        {
            _suppressRaiseOnLoaded.Value = false;
        }

        if (needsMigrationWrite && loaded != null)
        {
            QueueEncryptedWrite(loaded);
        }

        base.RaiseOnLoaded();
    }

    public override void Save(CompanyProfile item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureLoaded();
        NormalizeInMemoryCredentials(item.Backup);
        _cache[item.Id] = item;
        QueueEncryptedWrite(item);
        RaiseOnSaved(item);
    }

    private bool DecryptLoadedCredentials(BackupConfig backup)
    {
        bool needsMigrationWrite = false;

        foreach (var slot in BackupCredentialSlots)
        {
            var raw = slot.Getter(backup);
            if (_cipherCache.TryDecryptLoaded(slot.CacheKey, raw, out var decrypted, out var slotNeedsMigration))
            {
                slot.Setter(backup, decrypted);
            }
            if (slotNeedsMigration)
            {
                needsMigrationWrite = true;
            }
        }

        return needsMigrationWrite;
    }

    private void NormalizeInMemoryCredentials(BackupConfig? backup)
    {
        if (backup == null)
        {
            return;
        }

        foreach (var slot in BackupCredentialSlots)
        {
            var normalized = _cipherCache.NormalizeInMemory(slot.CacheKey, slot.Getter(backup));
            if (!string.Equals(normalized, slot.Getter(backup), StringComparison.Ordinal))
            {
                slot.Setter(backup, normalized);
            }
        }
    }

    private void QueueEncryptedWrite(CompanyProfile item)
    {
        var rootNode = JsonSerializer.SerializeToNode(item)?.AsObject() ?? new JsonObject();
        if (item.Backup != null && rootNode["Backup"] is JsonObject backupObj)
        {
            foreach (var slot in BackupCredentialSlots)
            {
                if (backupObj[slot.Section] is JsonObject sectionObj)
                {
                    var currentValue = slot.Getter(item.Backup);
                    sectionObj[slot.Property] = _cipherCache.EncryptForDisk(slot.CacheKey, currentValue);
                }
            }
        }

        var utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(rootNode, EncryptedFieldCipherCache.WriteIndentedOptions);
        _writer.QueueWriteBytes(GetFilePath(item), utf8Bytes);
    }

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
