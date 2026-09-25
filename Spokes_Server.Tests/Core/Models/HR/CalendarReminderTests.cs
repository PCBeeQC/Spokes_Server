using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class CalendarReminderTests
{
    [Fact]
    public void CalendarReminder_Initialization_SetsSensibleDefaults()
    {
        var reminder = new CalendarReminder();

        Assert.False(string.IsNullOrWhiteSpace(reminder.Id));
        Assert.True(Guid.TryParse(reminder.Id, out _));
        Assert.Equal(10, reminder.Value);
        Assert.Equal(CalendarReminderUnit.Minutes, reminder.Unit);
        Assert.Equal(CalendarReminderType.Notification, reminder.Type);
    }

    [Theory]
    [InlineData(10, CalendarReminderUnit.Minutes, 10 * 60)]
    [InlineData(2, CalendarReminderUnit.Hours, 2 * 3600)]
    [InlineData(3, CalendarReminderUnit.Days, 3 * 86400)]
    [InlineData(1, CalendarReminderUnit.Weeks, 7 * 86400)]
    [InlineData(0, CalendarReminderUnit.Minutes, 0)]
    [InlineData(-5, CalendarReminderUnit.Minutes, 0)]
    public void CalendarReminder_ToTimeSpan_CalculatesCorrectOffsets(int value, CalendarReminderUnit unit, double expectedTotalSeconds)
    {
        var reminder = new CalendarReminder
        {
            Value = value,
            Unit = unit
        };

        var span = reminder.ToTimeSpan();

        Assert.Equal(TimeSpan.FromSeconds(expectedTotalSeconds), span);
    }

    [Theory]
    [InlineData(1, CalendarReminderUnit.Minutes, "1 minute before")]
    [InlineData(15, CalendarReminderUnit.Minutes, "15 minutes before")]
    [InlineData(1, CalendarReminderUnit.Hours, "1 hour before")]
    [InlineData(2, CalendarReminderUnit.Hours, "2 hours before")]
    [InlineData(1, CalendarReminderUnit.Days, "1 day before")]
    [InlineData(4, CalendarReminderUnit.Days, "4 days before")]
    [InlineData(1, CalendarReminderUnit.Weeks, "1 week before")]
    [InlineData(2, CalendarReminderUnit.Weeks, "2 weeks before")]
    public void CalendarReminder_ToDisplayString_FormatsPluralsCorrectly(int value, CalendarReminderUnit unit, string expected)
    {
        var reminder = new CalendarReminder
        {
            Value = value,
            Unit = unit
        };

        Assert.Equal(expected, reminder.ToDisplayString());
    }

    [Theory]
    [InlineData(CalendarReminderUnit.Minutes, 1, true, true, "Minute before")]
    [InlineData(CalendarReminderUnit.Minutes, 10, true, true, "Minutes before")]
    [InlineData(CalendarReminderUnit.Hours, 1, true, true, "Hour before")]
    [InlineData(CalendarReminderUnit.Hours, 2, true, true, "Hours before")]
    [InlineData(CalendarReminderUnit.Days, 1, true, true, "Day before")]
    [InlineData(CalendarReminderUnit.Days, 3, true, true, "Days before")]
    [InlineData(CalendarReminderUnit.Weeks, 1, true, true, "Week before")]
    [InlineData(CalendarReminderUnit.Weeks, 2, true, true, "Weeks before")]
    [InlineData(CalendarReminderUnit.Minutes, 1, false, false, "minute")]
    [InlineData(CalendarReminderUnit.Minutes, 15, false, false, "minutes")]
    [InlineData(CalendarReminderUnit.Hours, 1, true, false, "hour before")]
    [InlineData(CalendarReminderUnit.Hours, 5, true, false, "hours before")]
    public void CalendarReminder_FormatUnitDisplay_FormatsCorrectly(
        CalendarReminderUnit unit, int value, bool includeBefore, bool capitalize, string expected)
    {
        var result = CalendarReminder.FormatUnitDisplay(unit, value, includeBefore, capitalize);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalendarReminder_Clone_CreatesDeepIndependentCopy()
    {
        var original = new CalendarReminder
        {
            Id = "rem-123",
            Value = 30,
            Unit = CalendarReminderUnit.Minutes,
            Type = CalendarReminderType.Notification
        };

        var copy = original.Clone();

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Value, copy.Value);
        Assert.Equal(original.Unit, copy.Unit);
        Assert.Equal(original.Type, copy.Type);

        copy.Value = 45;
        copy.Unit = CalendarReminderUnit.Hours;
        Assert.Equal(30, original.Value);
        Assert.Equal(CalendarReminderUnit.Minutes, original.Unit);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_ReturnsEventReminders_WhenNoUserPreference()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes }]
        };

        var attendeeReminders = evt.GetEffectiveRemindersForUser("attendee-1");

        Assert.Single(attendeeReminders);
        Assert.Equal(15, attendeeReminders[0].Value);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_ReturnsEmpty_WhenUserMuted()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["attendee-1"] = new CalendarUserReminderPreference { IsMuted = true }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("attendee-1");

        Assert.Empty(reminders);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_ReturnsCustomReminders_WhenUserCustomized()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["attendee-1"] = new CalendarUserReminderPreference
                {
                    ReceiveEventReminders = false,
                    PersonalReminders =
                    [
                        new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Hours },
                        new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Days }
                    ]
                }
            }
        };

        var organizerReminders = evt.GetEffectiveRemindersForUser("organizer-1");
        var attendeeReminders = evt.GetEffectiveRemindersForUser("attendee-1");

        // Organizer gets default shared
        Assert.Single(organizerReminders);
        Assert.Equal(10, organizerReminders[0].Value);

        // Attendee gets customized personal reminders without shared
        Assert.Equal(2, attendeeReminders.Count);
        Assert.Equal(1, attendeeReminders[0].Value);
        Assert.Equal(CalendarReminderUnit.Hours, attendeeReminders[0].Unit);
        Assert.Equal(1, attendeeReminders[1].Value);
        Assert.Equal(CalendarReminderUnit.Days, attendeeReminders[1].Unit);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_AttendeeCombinesSharedAndPersonalReminders()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["attendee-1"] = new CalendarUserReminderPreference
                {
                    ReceiveEventReminders = true,
                    PersonalReminders = [new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Hours }]
                }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("attendee-1");

        Assert.Equal(2, reminders.Count);
        Assert.Contains(reminders, r => r.Value == 10 && r.Unit == CalendarReminderUnit.Minutes);
        Assert.Contains(reminders, r => r.Value == 1 && r.Unit == CalendarReminderUnit.Hours);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_AttendeeCanOptOutOfSharedReminders()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["attendee-1"] = new CalendarUserReminderPreference
                {
                    ReceiveEventReminders = false,
                    PersonalReminders = [new CalendarReminder { Value = 30, Unit = CalendarReminderUnit.Minutes }]
                }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("attendee-1");

        Assert.Single(reminders);
        Assert.Equal(30, reminders[0].Value);
        Assert.Equal(CalendarReminderUnit.Minutes, reminders[0].Unit);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_CreatorCombinesSharedAndPersonalPrepReminders_WithoutLeakingToAttendees()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["organizer-1"] = new CalendarUserReminderPreference
                {
                    ReceiveEventReminders = true,
                    PersonalReminders =
                    [
                        new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Days },
                        new CalendarReminder { Value = 2, Unit = CalendarReminderUnit.Hours }
                    ]
                }
            }
        };

        var creatorReminders = evt.GetEffectiveRemindersForUser("organizer-1");
        var attendeeReminders = evt.GetEffectiveRemindersForUser("attendee-1");

        // Creator gets shared (10m) + prep (1d, 2h) = 3 reminders
        Assert.Equal(3, creatorReminders.Count);
        Assert.Contains(creatorReminders, r => r.Value == 10 && r.Unit == CalendarReminderUnit.Minutes);
        Assert.Contains(creatorReminders, r => r.Value == 1 && r.Unit == CalendarReminderUnit.Days);
        Assert.Contains(creatorReminders, r => r.Value == 2 && r.Unit == CalendarReminderUnit.Hours);

        // Attendee only gets the shared reminder (10m) — zero leakage!
        Assert.Single(attendeeReminders);
        Assert.Equal(10, attendeeReminders[0].Value);
        Assert.Equal(CalendarReminderUnit.Minutes, attendeeReminders[0].Unit);
    }

    [Fact]
    public void CalendarEvent_GetEffectiveReminders_DeduplicatesIdenticalOffsets()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "organizer-1",
            Reminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences = new Dictionary<string, CalendarUserReminderPreference>
            {
                ["attendee-1"] = new CalendarUserReminderPreference
                {
                    ReceiveEventReminders = true,
                    PersonalReminders = [new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes }]
                }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("attendee-1");

        // Both shared and personal have 10 minutes offset -> deduplicated to 1 reminder
        Assert.Single(reminders);
        Assert.Equal(10, reminders[0].Value);
    }

    [Fact]
    public void CalendarReminderBackgroundService_CalculateTriggerTime_StandardEvent()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 9, 20, 14, 0, 0),
            IsAllDay = false
        };
        var reminder = new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        Assert.Equal(new DateTime(2026, 9, 20, 13, 45, 0), triggerTime);
    }

    [Fact]
    public void CalendarReminderBackgroundService_CalculateTriggerTime_AllDayEvent()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 9, 20, 0, 0, 0),
            IsAllDay = true
        };
        var reminder = new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Days };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        // 1 day before 9:00 AM on Sept 20 is Sept 19 at 9:00 AM
        Assert.Equal(new DateTime(2026, 9, 19, 9, 0, 0), triggerTime);
    }

    [Fact]
    public void CalendarReminderStateService_Deduplication_OnlyTriggersOnce()
    {
        var service = new CalendarReminderStateService();
        var eventId = "evt-test-1";
        var reminderId = "rem-test-1";
        var userId = "user-test-1";
        var ticks = 123456789L;

        Assert.False(service.HasBeenDispatched(eventId, reminderId, userId, ticks));

        // First attempt succeeds
        var first = service.TryMarkDispatched(eventId, reminderId, userId, ticks);
        Assert.True(first);
        Assert.True(service.HasBeenDispatched(eventId, reminderId, userId, ticks));

        // Second attempt fails
        var second = service.TryMarkDispatched(eventId, reminderId, userId, ticks);
        Assert.False(second);
    }

    [Fact]
    public void CalendarReminder_NotificationSoundResolution_UsesUserSelectedSound()
    {
        var employee = new Employee { Id = "emp-1" };
        employee.SetNotificationSound(NotificationCategories.Calendar, "mixkit_long_pop_2358");

        var userSoundChoice = employee.GetNotificationSound(NotificationCategories.Calendar);
        var sound = NotificationSoundCatalog.GetEffectivePushSound(NotificationCategories.Calendar, userSoundChoice);

        Assert.Equal("mixkit_long_pop_2358.wav", sound);
    }

    [Fact]
    public void CalendarReminder_NotificationSoundResolution_UsesSystemDefault_WhenConfigured()
    {
        var employee = new Employee { Id = "emp-1" };
        employee.SetNotificationSound(NotificationCategories.Calendar, NotificationSoundIds.SystemDefault);

        var userSoundChoice = employee.GetNotificationSound(NotificationCategories.Calendar);
        var sound = NotificationSoundCatalog.GetEffectivePushSound(NotificationCategories.Calendar, userSoundChoice);

        Assert.Equal("default", sound);
    }

    [Fact]
    public void CalendarReminder_NotificationSoundResolution_FallsBackToDefaultChime_WhenNotConfigured()
    {
        var employee = new Employee { Id = "emp-1" };

        var userSoundChoice = employee.GetNotificationSound(NotificationCategories.Calendar);
        var sound = NotificationSoundCatalog.GetEffectivePushSound(NotificationCategories.Calendar, userSoundChoice);

        Assert.Equal("mixkit_alert_quick_chime_766.wav", sound);
    }
}

