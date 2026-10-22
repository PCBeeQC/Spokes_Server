using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR;

public class EventRsvpTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly CalendarEventRepository _calendarEvents;
    private readonly CalendarService _calendarService;

    public EventRsvpTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_EventRsvp_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _calendarEvents = new CalendarEventRepository(_persistence, config);
        _calendarService = new CalendarService(_calendarEvents, null!, null!, null);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void RsvpToEvent_AddsUserAndNotifiesEventUpdated()
    {
        var evt = new CalendarEvent
        {
            Id = "event-1",
            Title = "Company Picnic",
            Start = DateTime.Today.AddDays(1),
            End = DateTime.Today.AddDays(1).AddHours(2),
            Attendees = []
        };
        _calendarEvents.Save(evt);

        CalendarEvent? broadcastedEvent = null;
        _calendarService.EventUpdated += e => broadcastedEvent = e;

        var result = _calendarService.RsvpToEvent("event-1", "user-123");

        Assert.True(result.IsSuccess);
        var saved = _calendarEvents.GetById("event-1");
        Assert.NotNull(saved);
        Assert.Contains("user-123", saved.Attendees);
        Assert.NotNull(broadcastedEvent);
        Assert.Equal("event-1", broadcastedEvent.Id);
        Assert.Contains("user-123", broadcastedEvent.Attendees);
    }

    [Fact]
    public void RsvpToEvent_IsIdempotent_WhenUserAlreadyAttending()
    {
        var evt = new CalendarEvent
        {
            Id = "event-2",
            Title = "Sprint Planning",
            Start = DateTime.Today.AddDays(2),
            End = DateTime.Today.AddDays(2).AddHours(1),
            Attendees = ["user-123"]
        };
        _calendarEvents.Save(evt);

        bool notificationFired = false;
        _calendarService.EventUpdated += _ => notificationFired = true;

        var result = _calendarService.RsvpToEvent("event-2", "user-123");

        Assert.False(result.IsSuccess);
        Assert.False(notificationFired);
        var saved = _calendarEvents.GetById("event-2");
        Assert.Single(saved!.Attendees);
    }

    [Fact]
    public void CancelRsvpEvent_RemovesUserAndNotifiesEventUpdated()
    {
        var evt = new CalendarEvent
        {
            Id = "event-3",
            Title = "All Hands",
            Start = DateTime.Today.AddDays(3),
            End = DateTime.Today.AddDays(3).AddHours(1),
            Attendees = ["user-123", "user-456"]
        };
        _calendarEvents.Save(evt);

        CalendarEvent? broadcastedEvent = null;
        _calendarService.EventUpdated += e => broadcastedEvent = e;

        var result = _calendarService.CancelRsvpEvent("event-3", "user-123");

        Assert.True(result.IsSuccess);
        var saved = _calendarEvents.GetById("event-3");
        Assert.NotNull(saved);
        Assert.DoesNotContain("user-123", saved.Attendees);
        Assert.Contains("user-456", saved.Attendees);
        Assert.NotNull(broadcastedEvent);
        Assert.DoesNotContain("user-123", broadcastedEvent.Attendees);
    }

    [Fact]
    public void CancelRsvpEvent_ReturnsFalse_WhenUserNotAttending()
    {
        var evt = new CalendarEvent
        {
            Id = "event-4",
            Title = "Design Review",
            Start = DateTime.Today.AddDays(4),
            End = DateTime.Today.AddDays(4).AddHours(1),
            Attendees = ["user-456"]
        };
        _calendarEvents.Save(evt);

        bool notificationFired = false;
        _calendarService.EventUpdated += _ => notificationFired = true;

        var result = _calendarService.CancelRsvpEvent("event-4", "user-123");

        Assert.False(result.IsSuccess);
        Assert.False(notificationFired);
    }

    [Fact]
    public void RsvpToEvent_ReturnsFalse_WhenEventNotFound()
    {
        var result = _calendarService.RsvpToEvent("non-existent", "user-123");
        Assert.False(result.IsSuccess);
    }
}
