namespace Spokes_Server.Core.Models.HR;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data;

public class Employee : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Employee other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    [JsonPropertyName("OidcSub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MigratedOidcSub { get; set; }

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

    [JsonIgnore]
    public string DisplaySubtitle => string.IsNullOrEmpty(Position) ? (IsEmailPublic ? Email : "") : Position;

    public string TeamId { get; set; } = string.Empty;
    public decimal WeeklyHours { get; set; } = 40.0m; // Default to 40 hours
    public List<EmployeeWorkSchedule> WorkSchedules { get; set; } = [];
    public decimal HourlyRate { get; set; }
    public EmployeePayrollProfile Payroll { get; set; } = new();

    // Personal Info
    public string SocialInsuranceNumber { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateOnly? DateOfBirth { get; set; }
    public string EmergencyContact { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string PersonalEmail { get; set; } = string.Empty;

    // Privacy Settings
    public bool IsEmailPublic { get; set; }
    public HashSet<string> ContactUserIds { get; set; } = [];

    // UI Preferences
    public bool IsDarkMode { get; set; } = true;
    public string PreferredLanguage { get; set; } = "en-US";
    public bool ChatPublicCollapsed { get; set; }
    public bool ChatProjectsCollapsed { get; set; }
    public bool ChatTeamsCollapsed { get; set; }
    public bool ChatGroupChatsCollapsed { get; set; }
    public bool ChatDirectCollapsed { get; set; }
    public bool ChatArchiveCollapsed { get; set; }
    public Dictionary<string, bool> ChatCustomCategoriesCollapsed { get; set; } = [];
    public bool PushNotificationsEnabled { get; set; } = true;
    public bool ChatNotificationsEnabled { get; set; } = true;
    public bool ReactionNotificationsEnabled { get; set; } = true;
    public bool ReplyNotificationsTreatAsMention { get; set; } = true;
    public bool EmailNotificationsEnabled { get; set; } = true;
    public bool ModerationNotificationsEnabled { get; set; } = true;
    public bool LimitConsecutiveNotificationSounds { get; set; }
    public int ConsecutiveNotificationSoundLimit { get; set; } = 3;
    public bool MuteSharedCalendarAlertsByDefault { get; set; }
    public Dictionary<string, string> NotificationSounds { get; set; } = [];

    public string GetNotificationSound(string categoryId) =>
        NotificationSounds.TryGetValue(categoryId, out var soundId) && !string.IsNullOrWhiteSpace(soundId)
            ? soundId
            : "spokes_default";

    public void SetNotificationSound(string categoryId, string soundId) =>
        NotificationSounds[categoryId] = soundId;

    public string? LastSeenUpdateVersion { get; set; }
    public DateTimeOffset? LastEmailVisitedAt { get; set; }

    // Custom Sidebar Ordering
    public bool UsesCustomSidebarOrder { get; set; }
    public Dictionary<string, int> CustomCategoryOrder { get; set; } = [];
    public Dictionary<string, int> CustomChannelOrder { get; set; } = [];

    public string? AvatarBase64 { get; set; }
    public string? AvatarFile { get; set; }
    public uint AvatarVersion { get; set; } = 1;

    [JsonIgnore]
    public bool HasCustomAvatar => !string.IsNullOrEmpty(AvatarFile) || !string.IsNullOrEmpty(AvatarBase64);

    public string? ProfileColor { get; set; }
    public string? LandingPage { get; set; }
    public List<string> FavoriteLinks { get; set; } = [];
    public List<string> RecentEmojis { get; set; } = [];
    public List<string> RecentGifs { get; set; } = [];

    // Personal Calendar Categories
    public List<CalendarCategory> CalendarCategories { get; set; } = [];

    public List<CalendarCategory> EnsureCalendarCategories(CompanyProfile? serverProfile = null)
    {
        if (CalendarCategories == null || CalendarCategories.Count == 0)
        {
            CalendarCategories = CalendarCategoryDefaults.CloneList(serverProfile?.CalendarCategories);
        }
        return CalendarCategories;
    }

    // Notification Schedule — per-day active hours
    public bool NotificationScheduleEnabled { get; set; }
    public List<DaySchedule> NotificationSchedule { get; set; } =
    [
        .. Enumerable.Range(0, 7).Select(i => new DaySchedule { Day = (DayOfWeek)i })
    ];

    // Security
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSuspended { get; set; }
    public bool IsBanned { get; set; }
    public bool IsSystem { get; set; }

    [JsonIgnore]
    public bool IsSelectable => IsActive && !IsSuspended && !IsBanned && !IsSystem;

    [JsonIgnore]
    public bool IsActiveUnsuspended => IsActive && !IsSuspended && !IsBanned;

    [JsonIgnore]
    public bool IsEffectiveAdmin => IsActiveUnsuspended && IsAdmin;

    // Secure Chat / Vault Keys
    public string? PublicKey { get; set; }
    public string? EncryptedPrivateKey { get; set; }
    public bool HasVaultPassword => !string.IsNullOrEmpty(EncryptedPrivateKey);

    public bool DismissedChatWizardPermanently { get; set; }
    public DateTimeOffset? ChatWizardRemindLaterDate { get; set; }

    // License Banner Dismissal (Matching Secure Chat Wizard Pattern)
    public bool DismissedLicenseBannerPermanently { get; set; }
    public DateTimeOffset? LicenseBannerRemindLaterDate { get; set; }

    // Handwritten Document Signature
    public string? SignatureImageBase64 { get; set; }

    // The Flexible Permission List
    public List<string> Permissions { get; set; } = [];
    
    // Dynamic Permission Group Links (Multi-role support)
    public List<string> PermissionGroupIds { get; set; } = [];

    // Backward-compatibility property for single PermissionGroupId in JSON
    public string? PermissionGroupId
    {
        get => PermissionGroupIds.FirstOrDefault();
        set
        {
            if (string.IsNullOrEmpty(value))
            {
                PermissionGroupIds.Clear();
                PermissionGroups.Clear();
            }
            else if (!PermissionGroupIds.Contains(value))
            {
                PermissionGroupIds = [value];
            }
        }
    }
    
    [JsonIgnore]
    public List<PermissionGroup> PermissionGroups { get; set; } = [];

    // Blocked Users
    public List<string> BlockedUserIds { get; set; } = [];

    // The Logic: Admin can do anything. Others check assigned PermissionGroups and granular Permissions.
    public bool HasPermission(string permission) => HasEffectivePermission(permission, allowSuspended: false);

    /// <summary>
    /// Evaluates whether this employee currently holds <paramref name="permission"/>,
    /// enforcing active, non-banned, and (unless <paramref name="allowSuspended"/> is true)
    /// non-suspended account status, and checking multi-group assignments consistently.
    /// </summary>
    public bool HasEffectivePermission(string permission, bool allowSuspended = false)
    {
        if (!IsActive || IsBanned) return false;
        if (IsSuspended && !allowSuspended) return false;
        if (IsAdmin) return !IsSuspended || allowSuspended;

        if (PermissionGroupIds.Count > 0 && PermissionGroups.Any(g => g != null && g.Permissions.Contains(permission)))
        {
            return true;
        }

        return Permissions.Contains(permission);
    }

    /// <summary>
    /// Returns the union of all permissions granted via assigned <see cref="PermissionGroups"/>
    /// and direct <see cref="Permissions"/> when the employee is active and not banned.
    /// </summary>
    public HashSet<string> GetEffectivePermissionSet(bool allowSuspended = false)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsActive || IsBanned) return result;
        if (IsSuspended && !allowSuspended) return result;

        if (PermissionGroupIds.Count > 0 && PermissionGroups.Count > 0)
        {
            foreach (var group in PermissionGroups)
            {
                if (group?.Permissions == null) continue;
                foreach (var perm in group.Permissions)
                {
                    result.Add(perm);
                }
            }
        }

        if (Permissions != null)
        {
            foreach (var perm in Permissions)
            {
                result.Add(perm);
            }
        }

        return result;
    }

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(AvatarFile))
        {
            yield return AvatarFile;
        }

        if (!string.IsNullOrWhiteSpace(SignatureImageBase64) &&
            SignatureImageBase64.StartsWith("/spokesapi/files/", StringComparison.OrdinalIgnoreCase))
        {
            yield return SignatureImageBase64;
        }
    }
}
