using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class CalendarEventTests
{
    [Fact]
    public void CalendarEvent_Initialization_SetsDefaults()
    {
        // Act
        var calendarEvent = new CalendarEvent();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(calendarEvent.Id));
        Assert.True(Guid.TryParse(calendarEvent.Id, out _));
        Assert.Equal(string.Empty, calendarEvent.EmployeeId);
        Assert.Equal(string.Empty, calendarEvent.Title);
        Assert.Equal(string.Empty, calendarEvent.Description);
        Assert.Equal(string.Empty, calendarEvent.ProjectId);
        Assert.Equal(default, calendarEvent.Start);
        Assert.Equal(default, calendarEvent.End);
        Assert.False(calendarEvent.IsAllDay);
        Assert.Equal(string.Empty, calendarEvent.Location);
        Assert.Equal(string.Empty, calendarEvent.ExternalParticipant);
        Assert.NotNull(calendarEvent.Attendees);
        Assert.Empty(calendarEvent.Attendees);
        Assert.NotNull(calendarEvent.InvitedTeamIds);
        Assert.Empty(calendarEvent.InvitedTeamIds);
        Assert.Equal(string.Empty, calendarEvent.Category);
        Assert.Equal(string.Empty, calendarEvent.CategoryId);
        Assert.Equal("Info", calendarEvent.CategoryColor);
        Assert.False(calendarEvent.IsCompanyWide);
        Assert.NotNull(calendarEvent.Reminders);
        Assert.Empty(calendarEvent.Reminders);
        Assert.NotNull(calendarEvent.UserReminderPreferences);
        Assert.Empty(calendarEvent.UserReminderPreferences);
    }

    [Fact]
    public void CalendarEvent_PropertyMutations_UpdatesAndReadsCorrectly()
    {
        // Arrange
        var calendarEvent = new CalendarEvent();
        var start = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        List<string> attendees = ["emp-1", "emp-2"];
        List<string> teams = ["team-a", "team-b"];

        // Act
        calendarEvent.Id = "evt-123";
        calendarEvent.EmployeeId = "emp-admin";
        calendarEvent.Title = "Sprint Planning";
        calendarEvent.Description = "Bi-weekly sprint planning meeting";
        calendarEvent.ProjectId = "proj-456";
        calendarEvent.Start = start;
        calendarEvent.End = end;
        calendarEvent.IsAllDay = true;
        calendarEvent.Location = "Conference Room B";
        calendarEvent.ExternalParticipant = "client@example.com";
        calendarEvent.Attendees = attendees;
        calendarEvent.InvitedTeamIds = teams;
        calendarEvent.Category = "Meeting";
        calendarEvent.CategoryId = "cat-custom-123";
        calendarEvent.CategoryColor = "Warning";
        calendarEvent.IsCompanyWide = true;

        // Assert
        Assert.Equal("evt-123", calendarEvent.Id);
        Assert.Equal("emp-admin", calendarEvent.EmployeeId);
        Assert.Equal("Sprint Planning", calendarEvent.Title);
        Assert.Equal("Bi-weekly sprint planning meeting", calendarEvent.Description);
        Assert.Equal("proj-456", calendarEvent.ProjectId);
        Assert.Equal(start, calendarEvent.Start);
        Assert.Equal(end, calendarEvent.End);
        Assert.True(calendarEvent.IsAllDay);
        Assert.Equal("Conference Room B", calendarEvent.Location);
        Assert.Equal("client@example.com", calendarEvent.ExternalParticipant);
        Assert.Same(attendees, calendarEvent.Attendees);
        Assert.Equal(2, calendarEvent.Attendees.Count);
        Assert.Same(teams, calendarEvent.InvitedTeamIds);
        Assert.Equal(2, calendarEvent.InvitedTeamIds.Count);
        Assert.Equal("Meeting", calendarEvent.Category);
        Assert.Equal("cat-custom-123", calendarEvent.CategoryId);
        Assert.Equal("Warning", calendarEvent.CategoryColor);
        Assert.True(calendarEvent.IsCompanyWide);
    }

    [Fact]
    public void CalendarEvent_Equals_ReferenceEquality()
    {
        // Arrange
        var calendarEvent = new CalendarEvent { Id = "evt-1" };

        // Assert
        Assert.True(calendarEvent.Equals(calendarEvent));
    }

    [Fact]
    public void CalendarEvent_Equals_SameId_ReturnsTrue()
    {
        // Arrange
        var id = "evt-shared-id";
        var event1 = new CalendarEvent
        {
            Id = id,
            Title = "Event One",
            Category = "Meeting"
        };
        var event2 = new CalendarEvent
        {
            Id = id,
            Title = "Event Two",
            Category = "Holiday"
        };

        // Assert
        Assert.True(event1.Equals(event2));
        Assert.True(event2.Equals(event1));
    }

    [Fact]
    public void CalendarEvent_Equals_DifferentId_ReturnsFalse()
    {
        // Arrange
        var event1 = new CalendarEvent { Id = "evt-1" };
        var event2 = new CalendarEvent { Id = "evt-2" };

        // Assert
        Assert.False(event1.Equals(event2));
        Assert.False(event2.Equals(event1));
    }

    [Fact]
    public void CalendarEvent_Equals_NullOrDifferentType_ReturnsFalse()
    {
        // Arrange
        var calendarEvent = new CalendarEvent { Id = "evt-1" };

        // Assert
        Assert.False(calendarEvent.Equals(null));
        Assert.False(calendarEvent.Equals("evt-1"));
        Assert.False(calendarEvent.Equals(new object()));
    }

    [Fact]
    public void CalendarEvent_GetHashCode_SameId_ReturnsSameHashCode()
    {
        // Arrange
        var id = "evt-hash-test";
        var event1 = new CalendarEvent { Id = id, Title = "Event 1" };
        var event2 = new CalendarEvent { Id = id, Title = "Event 2" };

        // Assert
        Assert.Equal(event1.GetHashCode(), event2.GetHashCode());
    }

    [Fact]
    public void CalendarEvent_GetHashCode_NullId_FallsBackWithoutThrowing()
    {
        // Arrange
        var calendarEvent = new CalendarEvent { Id = null! };

        // Act & Assert
        var exception = Record.Exception(() => calendarEvent.GetHashCode());
        Assert.Null(exception);
        Assert.IsType<int>(calendarEvent.GetHashCode());
    }

    [Fact]
    public void CalendarEvent_CloneAndIsolation_CollectionsAreIndependent()
    {
        // Arrange
        var original = new CalendarEvent
        {
            Id = "evt-1",
            Title = "Initial Meeting",
            Attendees = new List<string> { "emp-1" },
            InvitedTeamIds = new List<string> { "team-1" },
            ProjectId = "proj-1"
        };

        // Act - simulate working copy clone pattern
        var copy = new CalendarEvent
        {
            Id = original.Id,
            EmployeeId = original.EmployeeId,
            Title = original.Title,
            Description = original.Description,
            ProjectId = original.ProjectId,
            Start = original.Start,
            End = original.End,
            IsAllDay = original.IsAllDay,
            Location = original.Location,
            ExternalParticipant = original.ExternalParticipant,
            Attendees = new List<string>(original.Attendees ?? []),
            InvitedTeamIds = new List<string>(original.InvitedTeamIds ?? []),
            Category = original.Category,
            CategoryId = original.CategoryId,
            CategoryColor = original.CategoryColor,
            IsCompanyWide = original.IsCompanyWide
        };

        copy.Title = "Edited Meeting";
        copy.Attendees.Add("emp-2");
        copy.InvitedTeamIds.Add("team-2");

        // Assert - original must be completely untouched
        Assert.Equal("Initial Meeting", original.Title);
        Assert.Single(original.Attendees);
        Assert.Single(original.InvitedTeamIds);
        Assert.Equal(2, copy.Attendees.Count);
        Assert.Equal(2, copy.InvitedTeamIds.Count);
    }
}

