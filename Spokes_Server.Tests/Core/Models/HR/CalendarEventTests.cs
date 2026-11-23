using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class CalendarEventTests
{
    [Fact]
    public void GetEffectiveRemindersForUser_Creator_AlwaysReceivesReminders()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Reminders = [new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes }]
        };

        var remindersOptOut = evt.GetEffectiveRemindersForUser("emp-creator", defaultMuteSharedAlerts: true);
        var remindersOptIn = evt.GetEffectiveRemindersForUser("emp-creator", defaultMuteSharedAlerts: false);

        Assert.Single(remindersOptOut);
        Assert.Single(remindersOptIn);
    }

    [Fact]
    public void GetEffectiveRemindersForUser_SharedUser_DefaultOptOut_ReturnsEmpty()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Reminders = [new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes }]
        };

        var reminders = evt.GetEffectiveRemindersForUser("emp-other", defaultMuteSharedAlerts: true);
        Assert.Empty(reminders);
    }

    [Fact]
    public void GetEffectiveRemindersForUser_SharedUser_ExplicitOptIn_OverridesDefaultOptOut()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Reminders = [new CalendarReminder { Value = 30, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences =
            {
                ["emp-other"] = new CalendarUserReminderPreference
                {
                    IsMuted = false,
                    PersonalReminders = [new CalendarReminder { Value = 30, Unit = CalendarReminderUnit.Minutes }]
                }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("emp-other", defaultMuteSharedAlerts: true);
        Assert.Single(reminders);
        Assert.Equal(30, reminders[0].Value);
    }

    [Fact]
    public void GetEffectiveRemindersForUser_SharedUser_ExplicitOptOut_OverridesDefaultOptIn()
    {
        var evt = new CalendarEvent
        {
            EmployeeId = "emp-creator",
            Reminders = [new CalendarReminder { Value = 30, Unit = CalendarReminderUnit.Minutes }],
            UserReminderPreferences =
            {
                ["emp-other"] = new CalendarUserReminderPreference { IsMuted = true }
            }
        };

        var reminders = evt.GetEffectiveRemindersForUser("emp-other", defaultMuteSharedAlerts: false);
        Assert.Empty(reminders);
    }

    [Theory]
    [InlineData(30, "min-height: 44px;")]
    [InlineData(44, "min-height: 44px;")]
    [InlineData(45, "min-height: 56px;")]
    [InlineData(60, "min-height: 56px;")]
    [InlineData(75, "min-height: 56px;")]
    [InlineData(90, "min-height: 84px;")]
    [InlineData(120, "min-height: 84px;")]
    [InlineData(150, "min-height: 112px;")]
    [InlineData(180, "min-height: 112px;")]
    [InlineData(240, "min-height: 140px;")]
    public void GetDurationTierMinHeight_CalculatesCorrectProportionalHeights(int durationMinutes, string expectedMinHeight)
    {
        var start = new DateTime(2026, 10, 8, 9, 0, 0);
        var evt = new CalendarEvent
        {
            Start = start,
            End = start.AddMinutes(durationMinutes)
        };

        var style = Spokes_Server.Components.Pages.Planning.MyCalendar.GetDurationTierMinHeight(evt);

        Assert.Equal(expectedMinHeight, style);
    }
}

