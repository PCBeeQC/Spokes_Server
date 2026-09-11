using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Spokes_Server.Tests.Core.Services.Core
{
    public class TimerServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly EmployeeRepository _employees;
        private readonly TimesheetRepository _timesheets;
        private readonly TimerService _service;

        public TimerServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Timer_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _timesheets = new TimesheetRepository(_persistence, _config);

            _service = new TimerService(_timesheets, _employees, _config);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public void Start_SetsStateCorrectly()
        {
            var userId = "user-1";
            _service.Start(userId, "proj-1", "work-1", "sub-1");

            var state = _service.GetState(userId);
            Assert.True(state.IsRunning);
            Assert.Equal("proj-1", state.ProjectId);
            Assert.Equal("work-1", state.WorkTypeId);
            Assert.Equal("sub-1", state.SubTaskId);
            Assert.NotNull(state.StartedAt);
        }

        [Fact]
        public void Stop_LogsTime_WhenMinimumElapsed()
        {
            var userId = "user-1";
            var employee = new Employee { Id = userId, FirstName = "Test", LastName = "User" };
            _employees.Save(employee);

            _service.Start(userId, "proj-1", "work-1", null);

            // Manually set start time back 10 minutes to bypass real-time wait
            var state = _service.GetState(userId);
            state.StartedAt = DateTime.UtcNow.AddMinutes(-10);

            var elapsed = _service.Stop(userId);

            Assert.False(state.IsRunning);
            Assert.True(elapsed.TotalMinutes >= 10);

            // Verify timesheet entry
            var timesheets = _timesheets.GetAll().Where(t => t.EmployeeId == userId).ToList();
            Assert.Single(timesheets);
            Assert.Single(timesheets[0].Entries);
            Assert.Equal(0.17m, timesheets[0].Entries[0].Hours); // 10/60 = 0.166... rounded to 0.17
        }

        [Fact]
        public void Cancel_ResetsState_WithoutLogging()
        {
            var userId = "user-1";
            _service.Start(userId, "proj-1", "work-1", null);
            _service.Cancel(userId);

            var state = _service.GetState(userId);
            Assert.False(state.IsRunning);
            Assert.Null(state.StartedAt);

            Assert.Empty(_timesheets.GetAll());
        }

        [Fact]
        public void OnTimerChanged_InvokedOnStateChanges()
        {
            int callCount = 0;
            _service.OnTimerChanged += () => callCount++;

            _service.Start("u1", "p1", "w1", null);
            _service.Stop("u1");
            _service.Cancel("u1");

            Assert.Equal(3, callCount);
        }

        [Fact]
        public void TimerService_PersistsAndRestoresTimerState_AcrossInstances()
        {
            var userId = "emp-restore-1";
            _service.Start(userId, "proj-1", "work-1", "sub-1", "Working on tests");

            var stateFile = Path.Combine(_testDataDir, "Employees", userId, "timer_state.json");
            Assert.True(File.Exists(stateFile));

            // Instantiate a new service instance to simulate server restart
            var newService = new TimerService(_timesheets, _employees, _config);
            var restoredState = newService.GetState(userId);

            Assert.True(restoredState.IsRunning);
            Assert.Equal("proj-1", restoredState.ProjectId);
            Assert.Equal("Working on tests", restoredState.Note);

            newService.Stop(userId);
            Assert.False(File.Exists(stateFile));
        }

        [Fact]
        public void Stop_LogsCustomNote_OrFallsBackToDefaultDescription()
        {
            var userId = "emp-notes";
            _employees.Save(new Employee { Id = userId, FirstName = "Notes", LastName = "User" });

            _service.Start(userId, "proj-1", "work-1", null, "Custom work note");
            var state = _service.GetState(userId);
            state.StartedAt = DateTime.UtcNow.AddMinutes(-10);
            _service.Stop(userId);

            var entry = _timesheets.GetAll().First(t => t.EmployeeId == userId).Entries[0];
            Assert.Equal("Custom work note", entry.Description);
            Assert.Equal(string.Empty, _service.GetState(userId).Note);
        }

        [Theory]
        [InlineData("Submitted")]
        [InlineData("Approved")]
        public void Stop_DoesNotLogTime_WhenTimesheetIsSubmittedOrApproved(string status)
        {
            var userId = "emp-locked-" + status;
            _employees.Save(new Employee { Id = userId, FirstName = "Locked", LastName = "User" });
            var today = DateTime.Today;
            var weekNum = System.Globalization.ISOWeek.GetWeekOfYear(today);
            var year = System.Globalization.ISOWeek.GetYear(today);

            _timesheets.Save(new Timesheet
            {
                EmployeeId = userId,
                Year = year,
                WeekNumber = weekNum,
                Status = status
            });

            _service.Start(userId, "proj-1", "work-1", null);
            _service.GetState(userId).StartedAt = DateTime.UtcNow.AddMinutes(-30);
            _service.Stop(userId);

            var sheet = _timesheets.GetAll().First(t => t.EmployeeId == userId);
            Assert.Empty(sheet.Entries);
            Assert.False(_service.GetState(userId).IsRunning);
        }
    }
}



