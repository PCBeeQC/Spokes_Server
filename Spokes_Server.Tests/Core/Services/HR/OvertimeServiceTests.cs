using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.HR;

namespace Spokes_Server.Tests.Core.Services.HR;

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
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Overtime_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDataDir);

            Dictionary<string, string?> configDict = new() { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _timesheets = new TimesheetRepository(_persistence, _config);
            var adjustments = new HourBankAdjustmentRepository(_persistence, _config);
            var profiles = new Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository(_persistence, _config);

            _service = new OvertimeService(_employees, _timesheets, adjustments, profiles);
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

        [Fact]
        public void CalculateHourBank_WhenEmployeeNotFound_ReturnsEmptyResult()
        {
            var result = _service.CalculateHourBank("non-existent-employee");

            Assert.NotNull(result);
            Assert.Equal(0, result.HourBank);
            Assert.Equal(0, result.WeeksTracked);
            Assert.Equal(0, result.TotalWorkedHours);
            Assert.Equal(0, result.TotalExpectedHours);
            Assert.Equal(default, result.StartDate);
            Assert.Null(result.EndDate);
        }

        [Fact]
        public void CalculateWeeklyOvertime_WhenEmployeeNotFound_ReturnsEmptyResult()
        {
            var result = _service.CalculateWeeklyOvertime("non-existent-employee", 2024, 1);

            Assert.NotNull(result);
            Assert.Equal(0, result.WorkedHours);
            Assert.Equal(0, result.ExpectedHours);
            Assert.Equal(0, result.Difference);
            Assert.False(result.IsOvertime);
        }

        [Fact]
        public void CalculateHourBank_WithFutureEmploymentEndDate_ClampsEndDateToToday()
        {
            var userId = "emp-future";
            var today = DateOnly.FromDateTime(DateTime.Today);
            var futureEndDate = today.AddDays(30);
            var employee = new Employee
            {
                Id = userId,
                DateOfJoining = today.AddDays(-21),
                EmploymentEndDate = futureEndDate,
                WeeklyHours = 40
            };
            _employees.Save(employee);

            var result = _service.CalculateHourBank(userId);

            // Clamped to today, so it only tracks past completed weeks (3 weeks), not future weeks
            Assert.Equal(3, result.WeeksTracked);
            Assert.Equal(futureEndDate, result.EndDate);
            Assert.Equal(120, result.TotalExpectedHours);
        }

        [Fact]
        public void CalculateHourBank_WithPastEmploymentEndDate_RespectsEndDate()
        {
            var userId = "emp-past";
            var today = DateOnly.FromDateTime(DateTime.Today);
            var pastEndDate = today.AddDays(-14);
            var employee = new Employee
            {
                Id = userId,
                DateOfJoining = today.AddDays(-21),
                EmploymentEndDate = pastEndDate,
                WeeklyHours = 40
            };
            _employees.Save(employee);

            var result = _service.CalculateHourBank(userId);

            // Only completed weeks up to pastEndDate are tracked (2 weeks instead of 3)
            Assert.Equal(2, result.WeeksTracked);
            Assert.Equal(pastEndDate, result.EndDate);
            Assert.Equal(80, result.TotalExpectedHours);
        }

        [Fact]
        public void CalculateHourBank_WhenEmployeeJoinedThisWeek_TracksZeroWeeks()
        {
            var userId = "emp-new";
            var today = DateOnly.FromDateTime(DateTime.Today);
            var employee = new Employee
            {
                Id = userId,
                DateOfJoining = today,
                WeeklyHours = 40
            };
            _employees.Save(employee);

            var result = _service.CalculateHourBank(userId);

            Assert.Equal(0, result.WeeksTracked);
            Assert.Equal(0, result.HourBank);
            Assert.Equal(0, result.TotalWorkedHours);
            Assert.Equal(0, result.TotalExpectedHours);
            Assert.Equal(today, result.StartDate);
        }

        [Fact]
        public void HourBankResult_And_WeeklyOvertimeResult_Properties_CanGetAndSet()
        {
            var startDate = new DateOnly(2025, 1, 6);
            var endDate = new DateOnly(2025, 6, 30);
            var hourBankResult = new HourBankResult
            {
                TotalWorkedHours = 165.5m,
                TotalExpectedHours = 160m,
                HourBank = 5.5m,
                WeeksTracked = 4,
                StartDate = startDate,
                EndDate = endDate
            };

            Assert.Equal(165.5m, hourBankResult.TotalWorkedHours);
            Assert.Equal(160m, hourBankResult.TotalExpectedHours);
            Assert.Equal(5.5m, hourBankResult.HourBank);
            Assert.Equal(4, hourBankResult.WeeksTracked);
            Assert.Equal(startDate, hourBankResult.StartDate);
            Assert.Equal(endDate, hourBankResult.EndDate);

            var weeklyResult = new WeeklyOvertimeResult
            {
                WorkedHours = 45m,
                ExpectedHours = 40m,
                Difference = 5m,
                IsOvertime = true
            };

            Assert.Equal(45m, weeklyResult.WorkedHours);
            Assert.Equal(40m, weeklyResult.ExpectedHours);
            Assert.Equal(5m, weeklyResult.Difference);
            Assert.True(weeklyResult.IsOvertime);
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
