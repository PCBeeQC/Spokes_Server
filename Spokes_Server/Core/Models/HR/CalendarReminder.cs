using System;
using System.Collections.Generic;

namespace Spokes_Server.Core.Models.HR;

public enum CalendarReminderUnit
{
    Minutes = 0,
    Hours = 1,
    Days = 2,
    Weeks = 3
}

public enum CalendarReminderType
{
    Notification = 0
}

public class CalendarReminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int Value { get; set; } = 10;
    public CalendarReminderUnit Unit { get; set; } = CalendarReminderUnit.Minutes;
    public CalendarReminderType Type { get; set; } = CalendarReminderType.Notification;

    /// <summary>
    /// Computes the offset as a TimeSpan.
    /// </summary>
    public TimeSpan ToTimeSpan()
    {
        var val = Math.Max(0, Value);
        return Unit switch
        {
            CalendarReminderUnit.Minutes => TimeSpan.FromMinutes(val),
            CalendarReminderUnit.Hours => TimeSpan.FromHours(val),
            CalendarReminderUnit.Days => TimeSpan.FromDays(val),
            CalendarReminderUnit.Weeks => TimeSpan.FromDays(val * 7),
            _ => TimeSpan.FromMinutes(val)
        };
    }

    /// <summary>
    /// Formats human-readable duration (e.g., "10 minutes", "1 hour", "1 day").
    /// </summary>
    public string ToDurationString()
    {
        return $"{Value} {FormatUnitDisplay(Unit, Value, includeBefore: false)}";
    }

    /// <summary>
    /// Formats human-readable summary of reminder (e.g., "10 minutes before").
    /// </summary>
    public string ToDisplayString()
    {
        return $"{Value} {FormatUnitDisplay(Unit, Value, includeBefore: true)}";
    }

    /// <summary>
    /// Formats the reminder unit with proper singular/plural grammar, optionally including the "before" preposition.
    /// E.g. "Minutes before", "Hour before", "minutes", "day".
    /// </summary>
    public static string FormatUnitDisplay(CalendarReminderUnit unit, int value, bool includeBefore = true, bool capitalize = false)
    {
        var unitStr = unit switch
        {
            CalendarReminderUnit.Minutes => value == 1 ? (capitalize ? "Minute" : "minute") : (capitalize ? "Minutes" : "minutes"),
            CalendarReminderUnit.Hours => value == 1 ? (capitalize ? "Hour" : "hour") : (capitalize ? "Hours" : "hours"),
            CalendarReminderUnit.Days => value == 1 ? (capitalize ? "Day" : "day") : (capitalize ? "Days" : "days"),
            CalendarReminderUnit.Weeks => value == 1 ? (capitalize ? "Week" : "week") : (capitalize ? "Weeks" : "weeks"),
            _ => capitalize ? "Minutes" : "minutes"
        };
        return includeBefore ? $"{unitStr} before" : unitStr;
    }

    public CalendarReminder Clone()
    {
        return new CalendarReminder
        {
            Id = Id,
            Value = Value,
            Unit = Unit,
            Type = Type
        };
    }
}

public class CalendarUserReminderPreference
{
    /// <summary>
    /// If true, mutes all notifications (both shared and personal) for this event.
    /// </summary>
    public bool IsMuted { get; set; }

    /// <summary>
    /// Controls whether the user receives shared event-level broadcast reminders. Default is true.
    /// </summary>
    public bool ReceiveEventReminders { get; set; } = true;

    /// <summary>
    /// Personal reminders configured exclusively for this user (never broadcast to others).
    /// </summary>
    public List<CalendarReminder> PersonalReminders { get; set; } = [];

    /// <summary>
    /// Backward-compatibility alias for PersonalReminders.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<CalendarReminder> Reminders
    {
        get => PersonalReminders;
        set => PersonalReminders = value ?? [];
    }

    /// <summary>
    /// Backward-compatibility helper for legacy custom reminder checks.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomReminders
    {
        get => !ReceiveEventReminders || (PersonalReminders != null && PersonalReminders.Count > 0);
        set
        {
            if (!value)
            {
                ReceiveEventReminders = true;
                PersonalReminders.Clear();
            }
        }
    }

    public CalendarUserReminderPreference Clone()
    {
        return new CalendarUserReminderPreference
        {
            IsMuted = IsMuted,
            ReceiveEventReminders = ReceiveEventReminders,
            PersonalReminders = PersonalReminders?.ConvertAll(r => r.Clone()) ?? []
        };
    }
}
