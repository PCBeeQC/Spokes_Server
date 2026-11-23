using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR;

public class CalendarServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly CalendarService _service;
    private readonly ChatStateService _chatStateService;

    public CalendarServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Calendar_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<ChatStateService>();
        services.AddSingleton<CalendarService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _chatStateService = _serviceProvider.GetRequiredService<ChatStateService>();
        _service = _serviceProvider.GetRequiredService<CalendarService>();
        _db.Employees.Save(new Employee
        {
            Id = "test-admin",
            FirstName = "Test",
            LastName = "Admin",
            IsActive = true,
            IsAdmin = true
        });
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void GetEventsForUser_FiltersUserAndTeamEvents()
    {
        // Arrange
        _db.Employees.Save(new Employee { Id = "emp-1", IsActive = true, Permissions = [AppPermissions.Calendar.View] });
        _db.Employees.Save(new Employee { Id = "emp-99", IsActive = true, Permissions = [AppPermissions.Calendar.View] });
        var companyWide = new CalendarEvent { Id = "evt-1", Title = "All Hands", IsCompanyWide = true };
        var userEvent = new CalendarEvent { Id = "evt-2", Title = "User 1 Private", EmployeeId = "emp-1" };
        var attendeeEvent = new CalendarEvent { Id = "evt-3", Title = "Meeting", Attendees = ["emp-1", "emp-2"] };
        var teamEvent = new CalendarEvent { Id = "evt-4", Title = "Team Standup", InvitedTeamIds = ["team-dev"] };
        var otherUserEvent = new CalendarEvent { Id = "evt-5", Title = "Other Private", EmployeeId = "emp-99" };

        _service.SaveEvent(companyWide, "test-admin");
        _service.SaveEvent(userEvent, "test-admin");
        _service.SaveEvent(attendeeEvent, "test-admin");
        _service.SaveEvent(teamEvent, "test-admin");
        _service.SaveEvent(otherUserEvent, "test-admin");

        // Act
        var events = _service.GetEventsForUser("emp-1", ["team-dev"]);

        // Assert
        var ids = events.Select(e => e.Id).ToHashSet();
        Assert.Contains("evt-1", ids);
        Assert.Contains("evt-2", ids);
        Assert.Contains("evt-3", ids);
        Assert.Contains("evt-4", ids);
        Assert.DoesNotContain("evt-5", ids);
    }

    [Fact]
    public void SaveEvent_GeneratesId_WhenMissing()
    {
        _db.Employees.Save(new Employee { Id = "emp-1", IsActive = true, Permissions = [AppPermissions.Calendar.View] });
        var evt = new CalendarEvent { Title = "No Id Event", EmployeeId = "emp-1" };
        _service.SaveEvent(evt, "test-admin");

        Assert.False(string.IsNullOrWhiteSpace(evt.Id));
        var retrieved = _service.GetEventById(evt.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("No Id Event", retrieved.Title);
    }

    [Fact]
    public void DeleteEvent_RemovesEventSuccessfully()
    {
        var evt = new CalendarEvent { Id = "evt-del", Title = "To Delete" };
        _service.SaveEvent(evt, "test-admin");

        Assert.NotNull(_service.GetEventById("evt-del"));

        _service.DeleteEvent("evt-del", "test-admin");
        Assert.Null(_service.GetEventById("evt-del"));
    }

    [Fact]
    public void UpdateUserCategories_SavesValidCategories()
    {
        var emp = new Employee { Id = "emp-cat-1", FirstName = "John", LastName = "Doe", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(emp);

        var categories = new List<CalendarCategory>
        {
            new() { Id = "c1", Name = "  Design  ", Color = "#ff0000" },
            new() { Id = "c2", Name = "Development", Color = "#00ff00" }
        };

        _service.UpdateUserCategories("emp-cat-1", categories);

        var updated = _db.Employees.GetById("emp-cat-1");
        Assert.NotNull(updated);
        Assert.Equal(2, updated.CalendarCategories.Count);
        Assert.Equal("Design", updated.CalendarCategories[0].Name);
    }

    [Fact]
    public void UpdateUserCategories_Fails_WhenCategoryNameIsEmpty()
    {
        var emp = new Employee { Id = "emp-cat-2", FirstName = "Jane", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(emp);

        var categories = new List<CalendarCategory>
        {
            new() { Id = "c1", Name = "   ", Color = "#ff0000" }
        };

        var result = _service.UpdateUserCategories("emp-cat-2", categories);
        Assert.False(result.IsSuccess);
        Assert.Equal("All categories must have a name.", result.ErrorMessage);
    }

    [Fact]
    public void GetEffectiveCategories_ReturnsDefault_WhenEmpty()
    {
        var emp = new Employee { Id = "emp-cat-3" };
        _db.Employees.Save(emp);

        var categories = _service.GetEffectiveCategories("emp-cat-3");
        Assert.NotEmpty(categories);
    }

    [Fact]
    public void RsvpToEvent_AddsAttendeeAndRaisesEvent()
    {
        _db.Employees.Save(new Employee { Id = "emp-rsvp-user", IsActive = true, Permissions = [AppPermissions.Calendar.View] });
        var evt = new CalendarEvent
        {
            Id = "evt-rsvp-1",
            Title = "Tech Talk",
            IsCompanyWide = true,
            Attendees = ["emp-initial"]
        };
        _service.SaveEvent(evt, "test-admin");

        CalendarEvent? eventFired = null;
        _service.EventUpdated += updated => eventFired = updated;

        var success = _service.RsvpToEvent("evt-rsvp-1", "emp-rsvp-user");
        Assert.True(success.IsSuccess);

        var retrieved = _service.GetEventById("evt-rsvp-1");
        Assert.NotNull(retrieved);
        Assert.Contains("emp-rsvp-user", retrieved.Attendees);
        Assert.NotNull(eventFired);
        Assert.Equal("evt-rsvp-1", eventFired.Id);

        // Second RSVP returns false (idempotent)
        var duplicateRsvp = _service.RsvpToEvent("evt-rsvp-1", "emp-rsvp-user");
        Assert.False(duplicateRsvp.IsSuccess);
    }

    [Fact]
    public void CancelRsvpEvent_RemovesAttendeeAndRaisesEvent()
    {
        _db.Employees.Save(new Employee { Id = "emp-user-1", IsActive = true, Permissions = [AppPermissions.Calendar.View] });
        var evt = new CalendarEvent
        {
            Id = "evt-cancel-1",
            Title = "Workshop",
            IsCompanyWide = true,
            Attendees = ["emp-user-1", "emp-user-2"]
        };
        _service.SaveEvent(evt, "test-admin");

        CalendarEvent? eventFired = null;
        _service.EventUpdated += updated => eventFired = updated;

        var success = _service.CancelRsvpEvent("evt-cancel-1", "emp-user-1");
        Assert.True(success.IsSuccess);

        var retrieved = _service.GetEventById("evt-cancel-1");
        Assert.NotNull(retrieved);
        Assert.DoesNotContain("emp-user-1", retrieved.Attendees);
        Assert.Contains("emp-user-2", retrieved.Attendees);
        Assert.NotNull(eventFired);

        // Cancel again returns false
        var duplicateCancel = _service.CancelRsvpEvent("evt-cancel-1", "emp-user-1");
        Assert.False(duplicateCancel.IsSuccess);
    }

    [Fact]
    public void UpdateUserCategories_CascadesRenameToExistingEvents()
    {
        var emp = new Employee { Id = "emp-cat-cascade", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(emp);

        var originalCategory = new CalendarCategory { Id = "cat-custom-1", Name = "Work Sprint", Color = "Primary" };
        _service.UpdateUserCategories("emp-cat-cascade", [originalCategory]);

        var evt = new CalendarEvent
        {
            Id = "evt-cascade-1",
            EmployeeId = "emp-cat-cascade",
            Title = "Sprint Planning",
            CategoryId = "cat-custom-1",
            Category = "Work Sprint",
            CategoryColor = "Primary"
        };
        _service.SaveEvent(evt, "test-admin");

        CalendarEvent? eventUpdatedNotification = null;
        _chatStateService.CalendarEventUpdated += updated => eventUpdatedNotification = updated;

        // Act - Rename category and change color
        var updatedCategory = new CalendarCategory { Id = "cat-custom-1", Name = "Deep Work Focus", Color = "Warning" };
        var result = _service.UpdateUserCategories("emp-cat-cascade", [updatedCategory]);

        // Assert
        Assert.True(result.IsSuccess);
        var reloadedEvt = _service.GetEventById("evt-cascade-1");
        Assert.NotNull(reloadedEvt);
        Assert.Equal("Deep Work Focus", reloadedEvt.Category);
        Assert.Equal("Warning", reloadedEvt.CategoryColor);
        Assert.NotNull(eventUpdatedNotification);
        Assert.Equal("evt-cascade-1", eventUpdatedNotification.Id);
    }

    [Fact]
    public void SaveEvent_AndDeleteEvent_NotifyChatStateService()
    {
        CalendarEvent? updatedEvent = null;
        string? deletedEventId = null;

        _chatStateService.CalendarEventUpdated += e => updatedEvent = e;
        _chatStateService.CalendarEventDeleted += id => deletedEventId = id;

        var evt = new CalendarEvent { Id = "evt-notify-1", Title = "Live Sync" };
        _service.SaveEvent(evt, "test-admin");

        Assert.NotNull(updatedEvent);
        Assert.Equal("evt-notify-1", updatedEvent.Id);

        _service.DeleteEvent("evt-notify-1", "test-admin");
        Assert.Equal("evt-notify-1", deletedEventId);
    }

    [Fact]
    public void SaveEvent_AndDeleteEvent_ThrowUnauthorizedAccessException_WhenActingUserIdIsMissing()
    {
        var owner = new Employee { Id = "emp-req-actor", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(owner);

        var newEvt = new CalendarEvent { Id = "evt-req-1", EmployeeId = owner.Id, Title = "Unauth Create" };
        Assert.Throws<UnauthorizedAccessException>(() => _service.SaveEvent(newEvt));
        Assert.Throws<UnauthorizedAccessException>(() => _service.SaveEvent(newEvt, "   "));

        _service.SaveEvent(newEvt, owner.Id);
        Assert.Throws<UnauthorizedAccessException>(() => _service.DeleteEvent("evt-req-1"));
        Assert.Throws<UnauthorizedAccessException>(() => _service.DeleteEvent("evt-req-1", ""));
        Assert.NotNull(_service.GetEventById("evt-req-1"));
    }

    [Fact]
    public void RsvpToEvent_BlocksPrivateAndUnauthorizedTeamEvents_AllowsPublicAndInvitedTeamEvents()
    {
        var owner = new Employee { Id = "emp-owner", FirstName = "Alice", TeamId = "team-a", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        var teamMate = new Employee { Id = "emp-teammate", FirstName = "Bob", TeamId = "team-a", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        var outsider = new Employee { Id = "emp-outsider", FirstName = "Mallory", TeamId = "team-b", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(owner);
        _db.Employees.Save(teamMate);
        _db.Employees.Save(outsider);

        var privateEvt = new CalendarEvent { Id = "evt-priv", EmployeeId = owner.Id, Title = "Private 1:1", IsCompanyWide = false };
        var teamEvt = new CalendarEvent { Id = "evt-team", EmployeeId = owner.Id, Title = "Team A Sync", InvitedTeamIds = ["team-a"] };
        var publicEvt = new CalendarEvent { Id = "evt-pub", EmployeeId = owner.Id, Title = "Town Hall", IsCompanyWide = true };
        _service.SaveEvent(privateEvt, "test-admin");
        _service.SaveEvent(teamEvt, "test-admin");
        _service.SaveEvent(publicEvt, "test-admin");

        // Outsider cannot RSVP to private or Team A event
        Assert.False(_service.RsvpToEvent("evt-priv", outsider.Id).IsSuccess);
        Assert.False(_service.RsvpToEvent("evt-team", outsider.Id).IsSuccess);

        // Teammate can RSVP to Team A event, and outsider can RSVP to company-wide event
        Assert.True(_service.RsvpToEvent("evt-team", teamMate.Id).IsSuccess);
        Assert.True(_service.RsvpToEvent("evt-pub", outsider.Id).IsSuccess);
    }

    [Fact]
    public void UpdateUserEventNotificationPreference_AllowsViewerToUpdateOwnPreferences_RejectsUnauthorizedUser()
    {
        var owner = new Employee { Id = "emp-owner-pref", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        var attendee = new Employee { Id = "emp-att-pref", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        var outsider = new Employee { Id = "emp-out-pref", FirstName = "Mallory", IsActive = true, Permissions = [AppPermissions.Calendar.View] };
        _db.Employees.Save(owner);
        _db.Employees.Save(attendee);
        _db.Employees.Save(outsider);

        var evt = new CalendarEvent
        {
            Id = "evt-pref-1",
            EmployeeId = owner.Id,
            Title = "Project Review",
            Attendees = [attendee.Id]
        };
        _service.SaveEvent(evt, "test-admin");

        // Outsider cannot mutate notification preferences on a private event
        var unauthorizedResult = _service.UpdateUserEventNotificationPreference("evt-pref-1", outsider.Id, isMuted: true);
        Assert.False(unauthorizedResult.IsSuccess);

        // Attendee can update their own mute/reminder preferences without full edit rights
        var validResult = _service.UpdateUserEventNotificationPreference("evt-pref-1", attendee.Id, isMuted: true, receiveEventReminders: false);
        Assert.True(validResult.IsSuccess);

        var updated = _service.GetEventById("evt-pref-1");
        Assert.NotNull(updated);
        Assert.True(updated.UserReminderPreferences.TryGetValue(attendee.Id, out var pref));
        Assert.True(pref!.IsMuted);
        Assert.False(pref.ReceiveEventReminders);
    }
}
