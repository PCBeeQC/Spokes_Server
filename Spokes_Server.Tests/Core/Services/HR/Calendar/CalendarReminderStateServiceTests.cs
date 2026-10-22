using System;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.HR.Calendar;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR.Calendar;

public class CalendarReminderStateServiceTests
{
    private readonly CalendarReminderStateService _service = new();

    [Fact]
    public void BuildKey_ReturnsFormattedKey()
    {
        var key = CalendarReminderStateService.BuildKey("evt1", "rem1", "user1", 123456789L);
        Assert.Equal("evt1_rem1_user1_123456789", key);
    }

    [Fact]
    public void HasBeenDispatched_WhenNotMarked_ReturnsFalse()
    {
        var dispatched = _service.HasBeenDispatched("evt1", "rem1", "user1", 1000L);
        Assert.False(dispatched);
    }

    [Fact]
    public void TryMarkDispatched_WhenFirstTime_ReturnsTrueAndUpdatesState()
    {
        var marked = _service.TryMarkDispatched("evt1", "rem1", "user1", 1000L);
        Assert.True(marked);

        var hasBeen = _service.HasBeenDispatched("evt1", "rem1", "user1", 1000L);
        Assert.True(hasBeen);

        var markedAgain = _service.TryMarkDispatched("evt1", "rem1", "user1", 1000L);
        Assert.False(markedAgain);
    }

    [Fact]
    public void TriggerReminder_FiresOnReminderTriggeredEvent()
    {
        string? receivedUser = null;
        CalendarEvent? receivedEvt = null;
        CalendarReminder? receivedRem = null;

        _service.OnReminderTriggered += (u, e, r) =>
        {
            receivedUser = u;
            receivedEvt = e;
            receivedRem = r;
        };

        var evt = new CalendarEvent { Id = "evt_abc", Title = "Sync" };
        var rem = new CalendarReminder { Id = "rem_xyz", Value = 15 };

        _service.TriggerReminder("user_42", evt, rem);

        Assert.Equal("user_42", receivedUser);
        Assert.NotNull(receivedEvt);
        Assert.Equal("evt_abc", receivedEvt.Id);
        Assert.NotNull(receivedRem);
        Assert.Equal(15, receivedRem.Value);
    }

    [Fact]
    public void CleanupOldEntries_RemovesStaleEntries()
    {
        _service.TryMarkDispatched("evt_old", "rem_old", "user_old", 500L);
        Assert.True(_service.HasBeenDispatched("evt_old", "rem_old", "user_old", 500L));

        _service.CleanupOldEntries(TimeSpan.Zero);

        Assert.False(_service.HasBeenDispatched("evt_old", "rem_old", "user_old", 500L));
    }
}
