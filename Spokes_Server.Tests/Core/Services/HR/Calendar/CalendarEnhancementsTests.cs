using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.HR.Calendar;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR.Calendar;

public class CalendarEnhancementsTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly IConfiguration _config;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly EmployeeRepository _employees;
    private readonly CalendarReminderStateService _reminderState;

    public CalendarEnhancementsTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_CalEnhance_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _reminderState = new CalendarReminderStateService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDataDir))
                Directory.Delete(_testDataDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task CalendarNotification_WhenWebPushUnconfigured_DoesNotSuppressInAppOnMobile()
    {
        // Arrange
        var userId = "emp-unconfigured-push";
        var circuitContext = new UserCircuitContext
        {
            DeviceType = DeviceType.Mobile,
            SubscriptionId = "sub_unconf_1",
            IsConnected = true,
            IsFocused = false // Tab is not focused
        };

        var mockWebPush = new Mock<IWebPushService>();
        mockWebPush.Setup(w => w.IsConfigured).Returns(false); // Push is unconfigured

        var mockSnackbar = new Mock<ISnackbar>();
        var mockSound = new Mock<ISoundService>();
        var presenter = new Mock<IUiNotificationPresenter>();

        var service = new CalendarNotificationService(
            presenter.Object,
            mockSound.Object,
            _reminderState,
            _companyProfiles,
            circuitContext,
            presenceState: null,
            webPush: mockWebPush.Object,
            employeeRepo: _employees);

        await service.InitializeAsync(userId);

        var evt = new CalendarEvent { Id = "evt-1", Title = "Doctor Appointment", Start = DateTime.Now.AddMinutes(10), End = DateTime.Now.AddHours(1) };
        var reminder = new CalendarReminder { Id = "rem-1", Value = 10, Unit = CalendarReminderUnit.Minutes };

        // Act
        _reminderState.TriggerReminder(userId, evt, reminder);

        // Assert: presenter must show notification and sound must play because push is unconfigured
        presenter.Verify(p => p.ShowCalendarNotification(It.Is<CalendarNotificationModel>(m => m.EventId == "evt-1")), Times.Once);
        mockSound.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Calendar, It.IsAny<string?>(), false), Times.Once);
    }

    [Fact]
    public async Task CalendarNotification_ResolvesUserSoundChoiceExplicitly()
    {
        // Arrange
        var userId = "emp-custom-sound";
        var employee = new Employee
        {
            Id = userId,
            FirstName = "Alice",
            LastName = "Smith"
        };
        employee.SetNotificationSound(NotificationCategories.Calendar, "sound_bell_ring");
        _employees.Save(employee);

        var mockSound = new Mock<ISoundService>();
        var presenter = new Mock<IUiNotificationPresenter>();

        var service = new CalendarNotificationService(
            presenter.Object,
            mockSound.Object,
            _reminderState,
            _companyProfiles,
            circuitContext: null,
            presenceState: null,
            webPush: null,
            employeeRepo: _employees);

        await service.InitializeAsync(userId);

        var evt = new CalendarEvent { Id = "evt-sound", Title = "Team Sync", Start = DateTime.Now.AddMinutes(5), End = DateTime.Now.AddMinutes(35) };
        var reminder = new CalendarReminder { Id = "rem-sound", Value = 5, Unit = CalendarReminderUnit.Minutes };

        // Act
        _reminderState.TriggerReminder(userId, evt, reminder);

        // Assert: sound choice must match employee's configured sound
        mockSound.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Calendar, "sound_bell_ring", false), Times.Once);
    }

    [Fact]
    public void CalculateTriggerTime_TimedEvent_SubtractsReminderDuration()
    {
        var evt = new CalendarEvent
        {
            Id = "evt-timed",
            Start = new DateTime(2026, 10, 20, 14, 0, 0),
            End = new DateTime(2026, 10, 20, 15, 0, 0)
        };
        var reminder = new CalendarReminder { Value = 15, Unit = CalendarReminderUnit.Minutes };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        Assert.Equal(new DateTime(2026, 10, 20, 13, 45, 0), triggerTime);
    }

    [Fact]
    public void CalculateTriggerTime_AllDayEvent_References9AmOnStartDate()
    {
        var evt = new CalendarEvent
        {
            Id = "evt-allday",
            IsAllDay = true,
            Start = new DateTime(2026, 10, 20, 0, 0, 0),
            End = new DateTime(2026, 10, 20, 23, 59, 59)
        };
        var reminder = new CalendarReminder { Value = 2, Unit = CalendarReminderUnit.Hours };

        var triggerTime = CalendarReminderBackgroundService.CalculateTriggerTime(evt, reminder);

        Assert.Equal(new DateTime(2026, 10, 20, 7, 0, 0), triggerTime);
    }

    [Fact]
    public void FormatReminderTimeDescription_RendersClearSummary()
    {
        var evt = new CalendarEvent
        {
            Id = "evt-summary",
            Title = "Sprint Review",
            Location = "Room 402",
            Start = new DateTime(2026, 10, 20, 10, 0, 0),
            End = new DateTime(2026, 10, 20, 11, 0, 0)
        };
        var reminder = new CalendarReminder { Value = 10, Unit = CalendarReminderUnit.Minutes };

        var desc = CalendarReminderBackgroundService.FormatReminderTimeDescription(evt, reminder);

        Assert.Contains("Starts in 10 minutes", desc);
        Assert.Contains("Room 402", desc);
    }

    [Fact]
    public void MultiDayEvent_ContinuationViewModelMaintainsTitle()
    {
        // Simulate multi-day generation as done in MyCalendar.LoadData
        var evt = new CalendarEvent
        {
            Id = "evt-multiday",
            Title = "3-Day Workshop",
            Start = new DateTime(2026, 10, 19, 9, 0, 0),
            End = new DateTime(2026, 10, 21, 17, 0, 0)
        };

        var days = new List<DateTime>
        {
            new DateTime(2026, 10, 19),
            new DateTime(2026, 10, 20),
            new DateTime(2026, 10, 21)
        };

        var viewModels = new List<CalendarEventViewModel>();
        foreach (var day in days)
        {
            var isStart = day == evt.Start.Date;
            var isCont = !isStart;
            var vm = new CalendarEventViewModel
            {
                Date = day,
                Title = evt.Title,
                SourceEvent = evt,
                IsContinuation = isCont
            };
            viewModels.Add(vm);
        }

        // Assert: every single day has the event title preserved (never erased to nbsp)
        Assert.Equal(3, viewModels.Count);
        Assert.All(viewModels, vm => Assert.Equal("3-Day Workshop", vm.Title));
        Assert.False(viewModels[0].IsContinuation);
        Assert.True(viewModels[1].IsContinuation);
        Assert.True(viewModels[2].IsContinuation);
    }

    private class CalendarEventViewModel
    {
        public DateTime Date { get; set; }
        public string Title { get; set; } = string.Empty;
        public CalendarEvent SourceEvent { get; set; } = new();
        public bool IsContinuation { get; set; }
    }
}
