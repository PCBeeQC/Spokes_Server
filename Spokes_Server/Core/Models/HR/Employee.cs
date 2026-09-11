namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;




public class Employee : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Employee)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string? OidcSub { get; set; } // Legacy property for backwards compatibility and data migration

    // Identity
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    // Helper for UI compatibility
    public string FullName => $"{FirstName} {LastName}".Trim();
    public string Initials => $"{(string.IsNullOrEmpty(FirstName) ? "" : FirstName[..1])}{(string.IsNullOrEmpty(LastName) ? "" : LastName[..1])}".ToUpper();

    public DateOnly DateOfJoining { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly? EmploymentEndDate { get; set; } // Null = currently employed, stops hour bank accumulation when set
    public string Email { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySubtitle => string.IsNullOrEmpty(Position) ? (IsEmailPublic ? Email : "") : Position;

    public string TeamId { get; set; } = string.Empty;
    public decimal WeeklyHours { get; set; } = 40.0m; // Default to 40 hours
    public decimal HourlyRate { get; set; } = 0; // For "Forward Looking" cost calculation only. Not for Payroll.

    // Personal Info
    public string PhoneNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public string EmergencyContact { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string PersonalEmail { get; set; } = string.Empty;

    // Privacy Settings
    public bool IsEmailPublic { get; set; } = false;

    // UI Preferences
    public bool IsDarkMode { get; set; } = true;
    public bool ChatPublicCollapsed { get; set; } = false;
    public bool ChatProjectsCollapsed { get; set; } = false;
    public bool ChatTeamsCollapsed { get; set; } = false;
    public bool ChatGroupChatsCollapsed { get; set; } = false;
    public bool ChatDirectCollapsed { get; set; } = false;
    public bool ChatArchiveCollapsed { get; set; } = false;
    public Dictionary<string, bool> ChatCustomCategoriesCollapsed { get; set; } = new();
    public bool PushNotificationsEnabled { get; set; } = true;
    public bool ChatNotificationsEnabled { get; set; } = true;
    public bool ReactionNotificationsEnabled { get; set; } = true;
    public bool ReplyNotificationsTreatAsMention { get; set; } = true;
    public bool EmailNotificationsEnabled { get; set; } = true;
    public bool ModerationNotificationsEnabled { get; set; } = true;
    public bool LimitConsecutiveNotificationSounds { get; set; } = false;
    public int ConsecutiveNotificationSoundLimit { get; set; } = 3;
    public string? LastSeenUpdateVersion { get; set; }


    // Custom Sidebar Ordering
    public bool UsesCustomSidebarOrder { get; set; } = false;
    public Dictionary<string, int> CustomCategoryOrder { get; set; } = new();
    public Dictionary<string, int> CustomChannelOrder { get; set; } = new();

    public string? AvatarBase64 { get; set; }

    public string? AvatarFile { get; set; }

    public uint AvatarVersion { get; set; } = 1;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomAvatar => !string.IsNullOrEmpty(AvatarFile) || !string.IsNullOrEmpty(AvatarBase64);

    public string? ProfileColor { get; set; }
    public List<string> FavoriteLinks { get; set; } = new();
    public List<string> RecentEmojis { get; set; } = new();
    public List<string> RecentGifs { get; set; } = new();

    // Notification Schedule — per-day active hours
    public bool NotificationScheduleEnabled { get; set; } = false;
    public List<DaySchedule> NotificationSchedule { get; set; } = Enumerable.Range(0, 7)
        .Select(i => new DaySchedule { Day = (DayOfWeek)i }).ToList();

    // Security
    public bool IsAdmin { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public bool IsSuspended { get; set; } = false;
    public bool IsBanned { get; set; } = false;

    // Secure Chat Keys
    public string? PublicKey { get; set; }
    public string? EncryptedPrivateKey { get; set; }
    public bool HasChatPassword => !string.IsNullOrEmpty(EncryptedPrivateKey);
    public bool DismissedChatWizardPermanently { get; set; } = false;
    public DateTimeOffset? ChatWizardRemindLaterDate { get; set; }

    // Email Credentials (Portable Encrypted)
    public string EncryptedEmailPassword { get; set; } = string.Empty;
    public string SignatureHtml { get; set; } = string.Empty;
    public string? SignatureImageBase64 { get; set; }

    // The Flexible Permission List
    public List<string> Permissions { get; set; } = new();
    
    // Dynamic Permission Group Link
    public string? PermissionGroupId { get; set; }
    
    [System.Text.Json.Serialization.JsonIgnore]
    public PermissionGroup? PermissionGroup { get; set; }

    // Blocked Users
    public List<string> BlockedUserIds { get; set; } = new();

    // The Logic: Admin can do anything. Others check the list.
    public bool HasPermission(string permission)
    {
        if (!IsActive || IsSuspended || IsBanned) return false;
        if (IsAdmin) return true;
        
        if (!string.IsNullOrEmpty(PermissionGroupId))
        {
            return PermissionGroup != null && PermissionGroup.Permissions.Contains(permission);
        }

        return Permissions.Contains(permission);
    }
}



