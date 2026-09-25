using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications;

public class CalendarNotificationServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly Mock<ISnackbar> _mockSnackbar;
    private readonly Mock<ISoundService> _mockSoundService;
    private readonly CalendarReminderStateService _reminderState;
    private readonly MockCalendarNavManager _nav;
    private readonly CalendarNotificationService _service;

    public CalendarNotificationServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_CalNotif_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);

        _mockSnackbar = new Mock<ISnackbar>();
        _mockSoundService = new Mock<ISoundService>();
        _reminderState = new CalendarReminderStateService();
        _nav = new MockCalendarNavManager();

        _service = new CalendarNotificationService(
            _mockSnackbar.Object,
            _nav,
            _mockSoundService.Object,
            _reminderState,
            _companyProfiles);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, recursive: true);
            }
        }
        catch { }
    }

    private class MockCalendarNavManager : NavigationManager
    {
        public MockCalendarNavManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { WasNavigated = true; LastNavigatedUri = uri; }
        public bool WasNavigated { get; set; }
        public string? LastNavigatedUri { get; set; }
    }

    #region FormatTimeDescription Tests

    [Fact]
    public void FormatTimeDescription_StartingNowNonAllDay_ReturnsStartsNowWithTimes()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 1, 10, 0, 0),
            End = new DateTime(2026, 10, 1, 11, 0, 0),
            IsAllDay = false
        };
        var reminder = new CalendarReminder { Value = 0, Unit = CalendarReminderUnit.Minutes };

        var result = CalendarNotificationService.FormatTimeDescription(evt, reminder);

        Assert.Equal($"Starts now ({evt.Start:t} – {evt.End:t})", result);
    }

    [Fact]
    public void FormatTimeDescription_StartingNowAllDay_ReturnsTodayAllDay()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 1, 0, 0, 0),
            End = new DateTime(2026, 10, 1, 23, 59, 59),
            IsAllDay = true
        };
        var reminder = new CalendarReminder { Value = 0, Unit = CalendarReminderUnit.Minutes };

        var result = CalendarNotificationService.FormatTimeDescription(evt, reminder);

        Assert.Equal("Today (All Day)", result);
    }

    [Fact]
    public void FormatTimeDescription_AdvanceReminderNonAllDay_ReturnsStartsInWithTimes()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 1, 14, 30, 0),
            End = new DateTime(2026, 10, 1, 15, 30, 0),
            IsAllDay = false
        };
        var reminder = new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes };

        var result = CalendarNotificationService.FormatTimeDescription(evt, reminder);

        Assert.Equal($"Starts in 15 minutes ({evt.Start:t} – {evt.End:t})", result);
    }

    [Fact]
    public void FormatTimeDescription_AdvanceReminderAllDay_ReturnsStartsInWithDate()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 5, 0, 0, 0),
            End = new DateTime(2026, 10, 5, 23, 59, 59),
            IsAllDay = true
        };
        var reminder = new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Days };

        var result = CalendarNotificationService.FormatTimeDescription(evt, reminder);

        Assert.Equal($"Starts in 1 day ({evt.Start:MMM d})", result);
    }

    #endregion

    #region CalendarReminderBackgroundService Format & Calculation Tests

    [Fact]
    public void BackgroundService_FormatReminderTimeDescription_WithLocation()
    {
        var evt = new CalendarEvent
        {
            Title = "Sprint Review",
            Start = new DateTime(2026, 10, 1, 14, 0, 0),
            End = new DateTime(2026, 10, 1, 15, 0, 0),
            IsAllDay = false,
            Location = "Meeting Room B"
        };
        var reminder = new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes };

        var result = CalendarReminderBackgroundService.FormatReminderTimeDescription(evt, reminder);

        Assert.Equal($"Starts in 10 minutes ({evt.Start:t} – {evt.End:t}) • 📍 Meeting Room B", result);
    }

    [Fact]
    public void BackgroundService_FormatReminderTimeDescription_WithoutLocation()
    {
        var evt = new CalendarEvent
        {
            Title = "Focus Time",
            Start = new DateTime(2026, 10, 1, 9, 0, 0),
            End = new DateTime(2026, 10, 1, 10, 0, 0),
            IsAllDay = false,
            Location = null
        };
        var reminder = new CalendarReminder { Value = 5, Unit = CalendarReminderUnit.Minutes };

        var result = CalendarReminderBackgroundService.FormatReminderTimeDescription(evt, reminder);

        Assert.Equal($"Starts in 5 minutes ({evt.Start:t} – {evt.End:t})", result);
    }

    [Fact]
    public void BackgroundService_CalculateTriggerTime_NonAllDay()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 1, 14, 0, 0),
            End = new DateTime(2026, 10, 1, 15, 0, 0),
            IsAllDay = false
        };
        var reminder = new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        Assert.Equal(new DateTime(2026, 10, 1, 13, 45, 0), triggerTime);
    }

    [Fact]
    public void BackgroundService_CalculateTriggerTime_AllDay_References9AM()
    {
        var evt = new CalendarEvent
        {
            Start = new DateTime(2026, 10, 1, 0, 0, 0),
            End = new DateTime(2026, 10, 1, 23, 59, 59),
            IsAllDay = true
        };
        var reminder = new CalendarReminder { Value = 1, Unit = CalendarReminderUnit.Days };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        // 9:00 AM on Oct 1 minus 1 day = 9:00 AM on Sept 30
        Assert.Equal(new DateTime(2026, 9, 30, 9, 0, 0), triggerTime);
    }

    #endregion

    #region HandleReminderTriggered Integration Tests

    [Fact]
    public async Task HandleReminderTriggered_DifferentUser_DoesNotTriggerNotification()
    {
        await _service.InitializeAsync("user-alice");

        var evt = new CalendarEvent { Id = "evt-1", Title = "Alice Event" };
        var reminder = new CalendarReminder { Id = "rem-1", Value = 10, Unit = CalendarReminderUnit.Minutes };

        bool unreadChangedFired = false;
        _service.OnUnreadCountChanged += () => unreadChangedFired = true;

        _reminderState.TriggerReminder("user-bob", evt, reminder);

        Assert.False(unreadChangedFired);
        _mockSoundService.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task HandleReminderTriggered_MatchingUser_PlaysSoundAndShowsSnackbarWithMetadata()
    {
        // Setup company profile
        _companyProfiles.Save(new CompanyProfile
        {
            CompanyName = "Poly Robotics",
            IconBase64 = "test-icon-data",
            IconVersion = 3
        });

        await _service.InitializeAsync("user-alice");

        Dictionary<string, object>? capturedParams = null;
        Action<SnackbarOptions>? capturedConfig = null;

        _mockSnackbar.Setup(s => s.Add<CalendarNotificationContent>(
                It.IsAny<Dictionary<string, object>>(),
                Severity.Normal,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback<Dictionary<string, object>?, Severity, Action<SnackbarOptions>?, string?>((p, s, c, k) =>
            {
                capturedParams = p;
                capturedConfig = c;
            })
            .Returns((Snackbar)null!);

        bool unreadChangedFired = false;
        _service.OnUnreadCountChanged += () => unreadChangedFired = true;

        var evt = new CalendarEvent
        {
            Id = "evt-99",
            Title = "Design Review",
            Location = "Room 101",
            Start = new DateTime(2026, 10, 1, 10, 0, 0),
            End = new DateTime(2026, 10, 1, 11, 0, 0)
        };
        var reminder = new CalendarReminder { Id = "rem-99", Value = 15, Unit = CalendarReminderUnit.Minutes };

        _reminderState.TriggerReminder("user-alice", evt, reminder);

        Assert.True(unreadChangedFired);

        // Sound played
        _mockSoundService.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Calendar, null, false), Times.Once);

        // Snackbar shown with proper content
        Assert.NotNull(capturedParams);
        Assert.Equal("Design Review", capturedParams["Title"]);
        Assert.Equal("Room 101", capturedParams["Location"]);
        Assert.Equal("evt-99", capturedParams["EventId"]);
        Assert.Equal("Poly Robotics", capturedParams["ServerName"]);
        Assert.Equal("/spokesapi/Media/Icon?v=3", capturedParams["ServerIconUrl"]);
        Assert.Contains("Starts in 15 minutes", (string)capturedParams["TimeDescription"]);

        // Config callback was registered
        Assert.NotNull(capturedConfig);
    }

    [Fact]
    public async Task TotalUnreadCount_AlwaysReturnsZero()
    {
        await _service.InitializeAsync("user-alice");
        Assert.Equal(0, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task DisposeAsync_UnhooksReminderTriggered()
    {
        await _service.InitializeAsync("user-alice");
        await _service.DisposeAsync();

        var evt = new CalendarEvent { Id = "evt-1", Title = "Post-dispose Event" };
        var reminder = new CalendarReminder { Id = "rem-1", Value = 0, Unit = CalendarReminderUnit.Minutes };

        _reminderState.TriggerReminder("user-alice", evt, reminder);

        _mockSoundService.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task HandleReminderTriggered_WhenMobileAndNotFocused_SuppressesSnackbarAndSound()
    {
        var userId = "user-cal-bg";
        var circuitContext = new UserCircuitContext
        {
            DeviceType = "Mobile",
            SubscriptionId = "sub_cal_mob_1",
            IsConnected = true,
            IsFocused = false
        };
        var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
        presenceState.RegisterConnection(userId, "sub_cal_mob_1", "Mobile", isFocused: false);

        var snackbarMock = new Mock<ISnackbar>();
        var soundMock = new Mock<ISoundService>();

        var service = new CalendarNotificationService(
            snackbarMock.Object,
            _nav,
            soundMock.Object,
            _reminderState,
            _companyProfiles,
            circuitContext,
            presenceState);

        await service.InitializeAsync(userId);

        var evt = new CalendarEvent { Id = "evt-bg", Title = "Background Event" };
        var reminder = new CalendarReminder { Id = "rem-bg", Value = 5, Unit = CalendarReminderUnit.Minutes };

        // Act
        _reminderState.TriggerReminder(userId, evt, reminder);

        // Assert
        // Sound and Snackbar MUST NOT fire
        soundMock.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        Assert.Empty(snackbarMock.Invocations);
    }

    [Fact]
    public async Task HandleReminderTriggered_WhenMobileAndFocused_ShowsSnackbarAndPlaysSound()
    {
        var userId = "user-cal-focused";
        var circuitContext = new UserCircuitContext
        {
            DeviceType = "Mobile",
            SubscriptionId = "sub_cal_mob_2",
            IsConnected = true,
            IsFocused = true
        };
        var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
        presenceState.RegisterConnection(userId, "sub_cal_mob_2", "Mobile", isFocused: true);

        var snackbarMock = new Mock<ISnackbar>();
        var soundMock = new Mock<ISoundService>();

        var service = new CalendarNotificationService(
            snackbarMock.Object,
            _nav,
            soundMock.Object,
            _reminderState,
            _companyProfiles,
            circuitContext,
            presenceState);

        await service.InitializeAsync(userId);

        var evt = new CalendarEvent { Id = "evt-focused", Title = "Focused Event" };
        var reminder = new CalendarReminder { Id = "rem-focused", Value = 5, Unit = CalendarReminderUnit.Minutes };

        // Act
        _reminderState.TriggerReminder(userId, evt, reminder);

        // Assert
        soundMock.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Calendar, null, false), Times.Once);
        Assert.NotEmpty(snackbarMock.Invocations);
    }

    #endregion
}
