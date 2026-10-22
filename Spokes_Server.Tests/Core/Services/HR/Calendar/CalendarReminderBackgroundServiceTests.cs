using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.HR.Calendar;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR.Calendar;

public class CalendarReminderBackgroundServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<Database> _mockDb;
    private readonly EmployeeRepository _employeesRepo;
    private readonly CalendarEventRepository _calendarEventsRepo;
    private readonly CompanyProfileRepository _companyProfileRepo;
    private readonly CalendarReminderStateService _reminderState;
    private readonly Mock<ILogger<CalendarReminderBackgroundService>> _loggerMock;
    private readonly CalendarReminderBackgroundService _service;

    public CalendarReminderBackgroundServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_CalRemBg_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfileRepo = new CompanyProfileRepository(persistence, config);
        _employeesRepo = new EmployeeRepository(persistence, config);
        _calendarEventsRepo = new CalendarEventRepository(persistence, config);

        _mockDb = new Mock<Database>();
        _mockDb.Setup(d => d.CompanyProfile).Returns(_companyProfileRepo);
        _mockDb.Setup(d => d.Employees).Returns(_employeesRepo);
        _mockDb.Setup(d => d.CalendarEvents).Returns(_calendarEventsRepo);

        var services = new ServiceCollection();
        services.AddSingleton(_companyProfileRepo);
        services.AddSingleton(_employeesRepo);
        services.AddSingleton(_calendarEventsRepo);
        services.AddSingleton(new HrContext(_employeesRepo, null!, null!, null!, null!, _calendarEventsRepo, _companyProfileRepo));
        services.AddSingleton(_mockDb.Object);
        services.AddSingleton<IWebPushService>(new Mock<IWebPushService>().Object);

        _serviceProvider = services.BuildServiceProvider();
        _reminderState = new CalendarReminderStateService();
        _loggerMock = new Mock<ILogger<CalendarReminderBackgroundService>>();

        _service = new CalendarReminderBackgroundService(_serviceProvider, _reminderState, _loggerMock.Object);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, true);
            }
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    [Fact]
    public async Task CheckDueRemindersAsync_WhenNoEvents_CompletesWithoutErrors()
    {
        await _service.CheckDueRemindersAsync();

        Assert.NotNull(_service);
        Assert.NotNull(_reminderState);
        Assert.Empty(_calendarEventsRepo.GetAll());
    }

    [Fact]
    public async Task CheckDueRemindersAsync_WithDueEventReminder_DispatchesReminder()
    {
        var employee = new Employee
        {
            Id = "emp1",
            FirstName = "Alice",
            LastName = "Smith",
            IsActive = true
        };
        _employeesRepo.Save(employee);

        var now = DateTime.Now;
        var evt = new CalendarEvent
        {
            Id = "evt1",
            Title = "Team Standup",
            EmployeeId = "emp1",
            Start = now.AddMinutes(5),
            End = now.AddMinutes(35),
            Reminders = [new CalendarReminder { Id = "rem1", Value = 5, Unit = CalendarReminderUnit.Minutes }]
        };
        _calendarEventsRepo.Save(evt);

        bool reminderTriggered = false;
        _reminderState.OnReminderTriggered += (userId, e, rem) =>
        {
            if (userId == "emp1" && e.Id == "evt1" && rem.Id == "rem1")
            {
                reminderTriggered = true;
            }
        };

        await _service.CheckDueRemindersAsync();

        Assert.NotNull(evt);
        Assert.Equal("Team Standup", evt.Title);
        Assert.True(reminderTriggered);
        Assert.Single(_employeesRepo.GetAll());
    }

    [Fact]
    public async Task CheckDueRemindersAsync_WhenAlreadyDispatched_DoesNotDispatchAgain()
    {
        var employee = new Employee
        {
            Id = "emp2",
            FirstName = "Bob",
            LastName = "Jones",
            IsActive = true
        };
        _employeesRepo.Save(employee);

        var now = DateTime.Now;
        var evt = new CalendarEvent
        {
            Id = "evt2",
            Title = "One on One",
            EmployeeId = "emp2",
            Start = now.AddMinutes(5),
            End = now.AddMinutes(35),
            Reminders = [new CalendarReminder { Id = "rem2", Value = 5, Unit = CalendarReminderUnit.Minutes }]
        };
        _calendarEventsRepo.Save(evt);

        int triggerCount = 0;
        _reminderState.OnReminderTriggered += (_, _, _) => triggerCount++;

        await _service.CheckDueRemindersAsync();
        await _service.CheckDueRemindersAsync();

        Assert.NotNull(evt);
        Assert.Equal(1, triggerCount);
        Assert.Single(_calendarEventsRepo.GetAll());
    }

    [Fact]
    public async Task SafeDispatchNotificationAsync_WhenActionThrows_LogsWarningAndDoesNotCrash()
    {
        var task = _service.SafeDispatchNotificationAsync(() => throw new InvalidOperationException("Push dispatch failure"), "test push");
        await task;

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to dispatch test push in background")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
