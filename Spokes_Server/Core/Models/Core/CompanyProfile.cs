namespace Spokes_Server.Core.Models.Core;

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.HR;

public class CompanyProfile : IDataEntity
{
    public string Id { get; set; } = "GlobalProfile";

    // Application Edition
    [JsonConverter(typeof(LegacyEditionConverter))]
    public string Edition { get; set; } = "Family";

    // Domain Configurations
    public BrandingConfig Branding { get; set; } = new();
    public ContactConfig Contact { get; set; } = new();
    public DocumentNumberingConfig DocumentNumbering { get; set; } = new();
    public AccountingConfig Accounting { get; set; } = new();
    public ChatConfig Chat { get; set; } = new();
    public PushConfig Push { get; set; } = new();
    private BackupConfig _backup = new();
    public BackupConfig Backup { get => _backup ??= new(); set => _backup = value ?? new(); }
    public LicensingConfig Licensing { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();
    public AlbumConfig Albums { get; set; } = new();

    // Domain settings
    public EmailServerSettings EmailSettings { get; set; } = new();
    public ModerationSettings Moderation { get; set; } = new();
    public TimesheetSettings TimesheetConfig { get; set; } = new();
    public HRSettings HRConfig { get; set; } = new();

    // Branding accessors (delegated to BrandingConfig)
    public string CompanyName { get => Branding.CompanyName; set => Branding.CompanyName = value; }
    public string LogoBase64 { get => Branding.LogoBase64; set => Branding.LogoBase64 = value; }
    public string IconBase64 { get => Branding.IconBase64; set => Branding.IconBase64 = value; }
    public int LogoVersion { get => Branding.LogoVersion; set => Branding.LogoVersion = value; }
    public int IconVersion { get => Branding.IconVersion; set => Branding.IconVersion = value; }
    public string PrimaryColor { get => Branding.PrimaryColor; set => Branding.PrimaryColor = value; }
    public string SecondaryColor { get => Branding.SecondaryColor; set => Branding.SecondaryColor = value; }

    [JsonIgnore]
    public bool HasCustomLogo => Branding.HasCustomLogo;

    [JsonIgnore]
    public bool HasCustomIcon => Branding.HasCustomIcon;

    // Document Numbering accessors (delegated to DocumentNumberingConfig)
    public string QuotePrefix { get => DocumentNumbering.QuotePrefix; set => DocumentNumbering.QuotePrefix = value; }
    public string InvoicePrefix { get => DocumentNumbering.InvoicePrefix; set => DocumentNumbering.InvoicePrefix = value; }
    public string ReportPrefix { get => DocumentNumbering.ReportPrefix; set => DocumentNumbering.ReportPrefix = value; }

    // Contact accessors (delegated to ContactConfig)
    public string AddressStreet { get => Contact.AddressStreet; set => Contact.AddressStreet = value; }
    public string AddressCity { get => Contact.AddressCity; set => Contact.AddressCity = value; }
    public string AddressZip { get => Contact.AddressZip; set => Contact.AddressZip = value; }
    public string AddressState { get => Contact.AddressState; set => Contact.AddressState = value; }
    public string AddressCountry { get => Contact.AddressCountry; set => Contact.AddressCountry = value; }
    public string PhoneNumber { get => Contact.PhoneNumber; set => Contact.PhoneNumber = value; }
    public string Website { get => Contact.Website; set => Contact.Website = value; }
    public string PrivacyPolicyUrl { get => Contact.PrivacyPolicyUrl; set => Contact.PrivacyPolicyUrl = value; }

    // Accounting accessors (delegated to AccountingConfig)
    public decimal DefaultInternalCommission { get => Accounting.DefaultInternalCommission; set => Accounting.DefaultInternalCommission = value; }
    public decimal DefaultExternalCommission { get => Accounting.DefaultExternalCommission; set => Accounting.DefaultExternalCommission = value; }
    public decimal KilometrageRate { get => Accounting.KilometrageRate; set => Accounting.KilometrageRate = value; }
    public decimal DefaultTaxRate { get => Accounting.DefaultTaxRate; set => Accounting.DefaultTaxRate = value; }
    public string CurrencySymbol { get => Accounting.CurrencySymbol; set => Accounting.CurrencySymbol = value; }
    public string DateFormat { get => Accounting.DateFormat; set => Accounting.DateFormat = value; }
    public List<string> SupportedCurrencies { get => Accounting.SupportedCurrencies; set => Accounting.SupportedCurrencies = value; }

    // Chat accessors (delegated to ChatConfig)
    public string DefaultPublicChannelSubscription { get => Chat.DefaultPublicChannelSubscription; set => Chat.DefaultPublicChannelSubscription = value; }
    public string PublicChannelMasterPassword { get => Chat.PublicChannelMasterPassword; set => Chat.PublicChannelMasterPassword = value; }
    public int MaxChatCacheSizeMb { get => Chat.MaxChatCacheSizeMb; set => Chat.MaxChatCacheSizeMb = value; }
    public string GifProvider { get => Chat.GifProvider; set => Chat.GifProvider = value; }
    public string GifApiKey { get => Chat.GifApiKey; set => Chat.GifApiKey = value; }

    // Push accessors (delegated to PushConfig)
    public string VapidPublicKey { get => Push.VapidPublicKey; set => Push.VapidPublicKey = value; }
    public string VapidPrivateKey { get => Push.VapidPrivateKey; set => Push.VapidPrivateKey = value; }
    public string VapidSubject { get => Push.VapidSubject; set => Push.VapidSubject = value; }

    // Backup accessors (delegated to BackupConfig)
    public bool BackupsEnabled { get => Backup.BackupsEnabled; set => Backup.BackupsEnabled = value; }
    public int BackupRetentionCount { get => Backup.BackupRetentionCount; set => Backup.BackupRetentionCount = value; }
    public string BackupTimeLocal { get => Backup.BackupTimeLocal; set => Backup.BackupTimeLocal = value; }
    public bool BackupEmployeeEmails { get => Backup.BackupEmployeeEmails; set => Backup.BackupEmployeeEmails = value; }
    public bool EncryptBackupsWithMasterPassword { get => Backup.EncryptBackupsWithMasterPassword; set => Backup.EncryptBackupsWithMasterPassword = value; }

    // Licensing accessors (delegated to LicensingConfig)
    public string LicensePayload { get => Licensing.LicensePayload; set => Licensing.LicensePayload = value; }
    public bool SendUsageStatistics { get => Licensing.SendUsageStatistics; set => Licensing.SendUsageStatistics = value; }

    // Storage accessors (delegated to StorageConfig)
    public long MaxFileUploadSizeBytes { get => Storage.MaxFileUploadSizeBytes; set => Storage.MaxFileUploadSizeBytes = value; }

    // Auth accessors (delegated to AuthConfig)
    public bool AutoCreateEmployeeOnFirstLogin { get => Auth.AutoCreateEmployeeOnFirstLogin; set => Auth.AutoCreateEmployeeOnFirstLogin = value; }

    // Privacy accessors (delegated to PrivacyConfig)
    public PrivacyPreset PrivacyPreset { get => Privacy.Preset; set => Privacy.Preset = value; }

    // Task Translations
    public List<string> TaskTranslationLanguages { get; set; } = [];

    // Project Status Configuration
    public List<ProjectStatusConfig> ProjectStatuses { get; set; } = [];

    // Default Calendar Category Configuration
    public List<CalendarCategory> CalendarCategories { get; set; } = [];

    // System TimeZone
    public string TimeZoneId { get; set; } = string.Empty;

    // Server Time & NTP Configuration
    public TimeConfig Time { get; set; } = new();

    // Permissions & Roles
    public List<PermissionGroup> PermissionGroups { get; set; } = [];
    public string DefaultPermissionGroupId { get; set; } = string.Empty;

    public CompanyProfile()
    {
        ProjectStatuses =
        [
            new ProjectStatusConfig { Id = "draft", Name = ProjectStatus.Draft, Color = "Default", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "quoted", Name = ProjectStatus.Quoted, Color = "Info", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "production", Name = ProjectStatus.InProduction, Color = "Primary", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "waiting_payment", Name = ProjectStatus.WaitingForPayment, Color = "Warning", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "discovery", Name = ProjectStatus.Discovery, Color = "Secondary", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "on_hold", Name = ProjectStatus.OnHold, Color = "Error", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "completed", Name = ProjectStatus.Completed, Color = "Success", IsSystemDefault = false },
            new ProjectStatusConfig { Id = "archived", Name = ProjectStatus.Archived, Color = "Dark", IsSystemDefault = false }
        ];

        CalendarCategories = CalendarCategoryDefaults.GetSystemDefaults();
    }

    /// <summary>
    /// Extracts a normalized domain name (e.g. "acme.com") from a website URL or bare hostname.
    /// Returns null for localhost, IP addresses, or invalid inputs.
    /// </summary>
    public static string? ExtractDomainFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var trimmed = url.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var host = uri.Host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
        {
            host = host[4..];
        }

        if (!host.Contains('.') ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host, out _))
        {
            return null;
        }

        return host;
    }

    /// <summary>
    /// Extracts a normalized domain name (e.g. "acme.com") from an email address or "@domain" string.
    /// </summary>
    public static string? ExtractDomainFromEmail(string? emailOrDomain)
    {
        if (string.IsNullOrWhiteSpace(emailOrDomain)) return null;
        var trimmed = emailOrDomain.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        var domain = (atIndex >= 0 ? trimmed[(atIndex + 1)..] : trimmed).Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain) || !domain.Contains('.') || domain.Contains(' '))
        {
            return null;
        }

        return domain;
    }

    /// <summary>
    /// Returns all distinct custom (non-public) domains configured for this organization across Website,
    /// Email server presets, and Bill Inbox settings.
    /// </summary>
    public IReadOnlyList<string> GetInternalDomains(string? additionalEmailOrDomain = null)
    {
        var domains = new List<string>();

        void TryAddDomain(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (EmailProviderPresets.IsPublicMailboxDomain(candidate)) return;
            if (!domains.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                domains.Add(candidate);
            }
        }

        TryAddDomain(ExtractDomainFromUrl(Website));

        if (EmailSettings?.IncomingServers != null)
        {
            foreach (var server in EmailSettings.IncomingServers)
            {
                TryAddDomain(ExtractDomainFromEmail(server.Domain));
            }
        }

        if (EmailSettings?.SendingServers != null)
        {
            foreach (var server in EmailSettings.SendingServers)
            {
                TryAddDomain(ExtractDomainFromEmail(server.Domain));
            }
        }

        TryAddDomain(ExtractDomainFromEmail(Accounting?.BillInbox?.EmailAddress));
        TryAddDomain(ExtractDomainFromEmail(additionalEmailOrDomain));

        return domains;
    }

    /// <summary>
    /// Returns true if the candidate domain matches one of the organization's configured custom domains.
    /// </summary>
    public bool IsInternalDomain(string? candidateDomain, string? additionalEmailOrDomain = null)
    {
        var normalized = ExtractDomainFromEmail(candidateDomain);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        foreach (var internalDomain in GetInternalDomains(additionalEmailOrDomain))
        {
            if (string.Equals(normalized, internalDomain, StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("." + internalDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the primary custom domain configured on this profile (e.g. from Website or Email settings),
    /// or the specified fallback (defaults to "example.com") when unconfigured.
    /// </summary>
    public string GetPrimaryDomainOrDefault(string fallback = "example.com")
    {
        var internalDomains = GetInternalDomains();
        return internalDomains.Count > 0 ? internalDomains[0] : fallback;
    }
}
