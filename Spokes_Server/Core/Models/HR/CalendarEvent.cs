namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;

public class CalendarEvent : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is CalendarEvent other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty; // Optional link to a project

    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool IsAllDay { get; set; }

    public string Location { get; set; } = string.Empty;

    public string ExternalParticipant { get; set; } = string.Empty;

    // Future-proofing
    public List<string> Attendees { get; set; } = [];
    public List<string> InvitedTeamIds { get; set; } = [];
    public string Category { get; set; } = string.Empty; // Category Name
    public string CategoryId { get; set; } = string.Empty; // Creator's category ID
    public string CategoryColor { get; set; } = "Info"; // Snapshot of category color

    public bool IsCompanyWide { get; set; }

    /// <summary>
    /// Event-level reminders set by the organizer / creator.
    /// </summary>
    public List<CalendarReminder> Reminders { get; set; } = [];

    /// <summary>
    /// Per-user reminder preferences for this event (keyed by EmployeeId).
    /// Allows attendees to customize their reminders or mute alerts for this event.
    /// </summary>
    public Dictionary<string, CalendarUserReminderPreference> UserReminderPreferences { get; set; } = new();

    /// <summary>
    /// Resolves the effective reminders for a given user.
    /// Combines shared event reminders (unless the user opted out) and personal reminders.
    /// If the user muted the event, returns empty.
    /// Deduplicates reminders with identical trigger offsets.
    /// </summary>
    public List<CalendarReminder> GetEffectiveRemindersForUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return Reminders ?? [];

        if (UserReminderPreferences != null && UserReminderPreferences.TryGetValue(userId, out var pref))
        {
            if (pref.IsMuted) return [];

            var list = new List<CalendarReminder>();
            if (pref.ReceiveEventReminders && Reminders != null)
            {
                list.AddRange(Reminders);
            }

            if (pref.PersonalReminders != null && pref.PersonalReminders.Count > 0)
            {
                list.AddRange(pref.PersonalReminders);
            }

            // Deduplicate reminders that have identical offset spans
            var seenOffsets = new HashSet<TimeSpan>();
            var deduplicated = new List<CalendarReminder>();
            foreach (var r in list)
            {
                if (seenOffsets.Add(r.ToTimeSpan()))
                {
                    deduplicated.Add(r);
                }
            }
            return deduplicated;
        }

        return Reminders ?? [];
    }
}



