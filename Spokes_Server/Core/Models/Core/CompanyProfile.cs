namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

// Moved TextModerationAction to its own file or keep it simple. Actually, we can just put TextModerationAction inside Spokes_Server.Core.Models.Core namespace or next to ModerationSettings.


using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

public class CompanyProfile : IDataEntity
{
    public string Id { get; set; } = "GlobalProfile";

    

    // Application Edition
    [JsonConverter(typeof(LegacyEditionConverter))]
    public string Edition { get; set; } = "Family";

    // Branding
    public string CompanyName { get; set; } = "My Spokes";
    public string LogoBase64 { get; set; } = string.Empty;
    public string IconBase64 { get; set; } = string.Empty; // Small square logo / favicon
    public int LogoVersion { get; set; } = 1;
    public int IconVersion { get; set; } = 1;

    [JsonIgnore]
    public bool HasCustomLogo => !string.IsNullOrEmpty(LogoBase64);

    [JsonIgnore]
    public bool HasCustomIcon => !string.IsNullOrEmpty(IconBase64);

    public string PrimaryColor { get; set; } = "#7e6fff";
    public string SecondaryColor { get; set; } = "#1E88E5";

    // Document Numbering
    public string QuotePrefix { get; set; } = "Q";
    public string InvoicePrefix { get; set; } = "INV";

    // Task Translations
    public List<string> TaskTranslationLanguages { get; set; } = new();

    // Contact (Physical Location)
    public string AddressStreet { get; set; } = string.Empty;
    public string AddressCity { get; set; } = string.Empty;
    public string AddressZip { get; set; } = string.Empty;
    public string AddressState { get; set; } = string.Empty;
    public string AddressCountry { get; set; } = "Canada";
    public string PhoneNumber { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string PrivacyPolicyUrl { get; set; } = "https://spokes.sh/privacy/";

    // Commission Defaults
    public decimal DefaultInternalCommission { get; set; } = 5.0m;
    public decimal DefaultExternalCommission { get; set; } = 10.0m;

    // Expenses
    public decimal KilometrageRate { get; set; } = 0.55m; // $/km
    public decimal DefaultTaxRate { get; set; } = 0.14975m;
    public string CurrencySymbol { get; set; } = "$";
    public List<string> SupportedCurrencies { get; set; } = new() { "CAD", "USD", "EUR" };


    // REMOVED: Banking and Tax ID fields (Now in Template)

    // Project Status Configuration
    public List<ProjectStatusConfig> ProjectStatuses { get; set; } = new();

    // Calendar Category Configuration
    public List<CalendarCategory> CalendarCategories { get; set; } = new();

    // Email Server Settings (Global)
    public EmailServerSettings EmailSettings { get; set; } = new();
    public string TimeZoneId { get; set; } = string.Empty;

    // Permissions & Roles
    public List<PermissionGroup> PermissionGroups { get; set; } = new();
    public string DefaultPermissionGroupId { get; set; } = string.Empty;

    // Push Notifications (VAPID Keys)
    public string VapidPublicKey { get; set; } = string.Empty;
    public string VapidPrivateKey { get; set; } = string.Empty;
    public string VapidSubject { get; set; } = "mailto:admin@yourcompany.com";

    // OpenID Authentication Settings
    public bool AutoCreateEmployeeOnFirstLogin { get; set; } = true;

    // Chat Settings
    public string DefaultPublicChannelSubscription { get; set; } = "Mentions";
    public string PublicChannelMasterPassword { get; set; } = string.Empty;
    public int MaxChatCacheSizeMb { get; set; } = 500;

    // File Uploads
    public long MaxFileUploadSizeBytes { get; set; } = 268435456; // 256MB default

    // Gif Integration
    public string GifProvider { get; set; } = "Spokes Default Provider"; // 'Spokes Default Provider', 'Tenor', 'Giphy', or 'Klipy'
    public string GifApiKey { get; set; } = string.Empty;

    // Backup Settings
    public bool BackupsEnabled { get; set; } = false;
    public int BackupRetentionCount { get; set; } = 5;
    public string BackupTimeLocal { get; set; } = "03:00"; // HH:mm Local
    public bool BackupEmployeeEmails { get; set; } = false;

    // Licensing Settings
    public string LicensePayload { get; set; } = string.Empty;
    public bool SendUsageStatistics { get; set; } = true;

    // Content Moderation
    public ModerationSettings Moderation { get; set; } = new();

    // Timesheet Settings
    public TimesheetSettings TimesheetConfig { get; set; } = new();

    public CompanyProfile()
    {
        // Initialize Defaults if empty (Constructor runs on new object, JSON deserialization might overwrite this if property exists)
        // We will also check this on load in the Service/Repository if needed, but a constructor init is safe for new profiles.
        if (ProjectStatuses == null || !ProjectStatuses.Any())
        {
            ProjectStatuses = new List<ProjectStatusConfig>
            {
                new ProjectStatusConfig { Id = "draft", Name = ProjectStatus.Draft, Color = "Default", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "quoted", Name = ProjectStatus.Quoted, Color = "Info", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "production", Name = ProjectStatus.InProduction, Color = "Primary", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "waiting_payment", Name = ProjectStatus.WaitingForPayment, Color = "Warning", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "discovery", Name = ProjectStatus.Discovery, Color = "Secondary", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "on_hold", Name = ProjectStatus.OnHold, Color = "Error", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "completed", Name = ProjectStatus.Completed, Color = "Success", IsSystemDefault = false },
                new ProjectStatusConfig { Id = "archived", Name = ProjectStatus.Archived, Color = "Dark", IsSystemDefault = false }
            };
        }

        if (CalendarCategories == null || !CalendarCategories.Any())
        {
            CalendarCategories = new List<CalendarCategory>
            {
                new CalendarCategory { Id = "work", Name = "Work", Color = "Info" },
                new CalendarCategory { Id = "meeting", Name = "Meeting", Color = "Primary" },
                new CalendarCategory { Id = "holiday", Name = "Holiday", Color = "Success" },
                new CalendarCategory { Id = "personal", Name = "Personal", Color = "Warning" },
                new CalendarCategory { Id = "important", Name = "Important", Color = "Error" }
            };
        }
    }
}

public class EmailServerSettings
{
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public bool ImapSsl { get; set; } = true;

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = false; // StartTLS usually
}

public class CalendarCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "Default"; // MudBlazor Color Enum as string
}

public enum TextModerationAction
{
    Block,
    Sanitize
}

public class ModerationSettings
{
    public bool EnableTextFilter { get; set; } = false;
    public TextModerationAction TextAction { get; set; } = TextModerationAction.Block;
    public List<string> BlockedWords { get; set; } = new();
}

public class TimesheetSettings
{
    // Features
    public bool EnableWeeklyPlan { get; set; } = true;
    
    // Approval Workflow
    public bool RequireApproval { get; set; } = false;
    
    // Timesheet Locking
    public bool EnableTimesheetLocking { get; set; } = false;
    public int LockTimesheetsOlderThanDays { get; set; } = 30;
    
    // Required Fields
    public bool RequireProject { get; set; } = true;
    public bool RequireTask { get; set; } = true;
    public bool RequireNote { get; set; } = false;
    
    // Rounding (applied to reports/exports only)
    public bool EnableRounding { get; set; } = false;
    public int RoundingIntervalMinutes { get; set; } = 15;     // 5, 6, 15, or 30
    public string RoundingDirection { get; set; } = "Nearest"; // "Up", "Down", "Nearest"
}

public class LegacyEditionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            int value = reader.GetInt32();
            return value == 0 ? "Family" : "Business";
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? "Business";
        }

        return "Business";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}


