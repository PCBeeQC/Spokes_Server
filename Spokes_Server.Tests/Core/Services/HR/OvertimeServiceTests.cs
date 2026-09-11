using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.HR;
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

namespace Spokes_Server.Tests.Core.Services.HR
{
    public class OvertimeServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly EmployeeRepository _employees;
        private readonly TimesheetRepository _timesheets;
        private readonly OvertimeService _service;

        public OvertimeServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Overtime_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _timesheets = new TimesheetRepository(_persistence, _config);

            _service = new OvertimeService(_employees, _timesheets);
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
        public void CalculateHourBank_CalculatesCorrectCumulativeDifference()
        {
            var userId = "emp-1";
            var startDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-21)); // 3 weeks ago
            var employee = new Employee
            {
                Id = userId,
                DateOfJoining = startDate,
                WeeklyHours = 40
            };
            _employees.Save(employee);

            // Week 1: 45 hours (5 overtime)
            var mon1 = GetMonday(startDate);
            var ts1 = new Timesheet { EmployeeId = userId, Year = mon1.Year, WeekNumber = GetWeek(mon1) };
            ts1.Entries.Add(new TimeEntry { Hours = 45 });
            _timesheets.Save(ts1);

            // Week 2: 38 hours (-2 under)
            var mon2 = mon1.AddDays(7);
            var ts2 = new Timesheet { EmployeeId = userId, Year = mon2.Year, WeekNumber = GetWeek(mon2) };
            ts2.Entries.Add(new TimeEntry { Hours = 38 });
            _timesheets.Save(ts2);

            // Week 3 (last completed week): 0 hours (0)
            // Note: The service skips the CURRENT week (today's week)

            var result = _service.CalculateHourBank(userId);

            // Expected: (45+38+0) - (40*3) = 83 - 120 = -37
            // Wait, previous test said 3, but 45+38+0 = 83. 40*3 = 120. 83-120 = -37.
            // If I want 3, I need 123 hours. 45 + 38 + 40 = 123.

            // Let's just assert the math matches the logic
            Assert.Equal(83 - 120, result.HourBank);
            Assert.Equal(3, result.WeeksTracked);
        }

        [Fact]
        public void CalculateWeeklyOvertime_ReturnsCorrectDifference()
        {
            var userId = "emp-2";
            var employee = new Employee { Id = userId, WeeklyHours = 37.5m };
            _employees.Save(employee);

            var result = _service.CalculateWeeklyOvertime(userId, 2024, 10);
            Assert.Equal(-37.5m, result.Difference); // 0 worked - 37.5 expected

            var ts = new Timesheet { EmployeeId = userId, Year = 2024, WeekNumber = 10 };
            ts.Entries.Add(new TimeEntry { Hours = 42 });
            _timesheets.Save(ts);

            result = _service.CalculateWeeklyOvertime(userId, 2024, 10);

            Assert.Equal(4.5m, result.Difference);
            Assert.True(result.IsOvertime);
        }

        private DateOnly GetMonday(DateOnly date)
        {
            var dt = date.ToDateTime(TimeOnly.MinValue);
            var diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
            return DateOnly.FromDateTime(dt.AddDays(-diff));
        }

        private int GetWeek(DateOnly date)
        {
            return System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue));
        }
    }
}



