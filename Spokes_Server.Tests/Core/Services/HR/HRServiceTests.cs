using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.HR;

namespace Spokes_Server.Tests.Core.Services.HR;

public class HRServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly OvertimeService _overtimeService;
        private readonly HRService _service;

        public HRServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_HR_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDataDir);

            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();
            _overtimeService = new OvertimeService(_db.Employees, _db.Timesheets, _db.HourBankAdjustments, _db.CompanyProfile);
            _service = new HRService(_db, _overtimeService);
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
        public void SubmitTimesheet_TransitionsStatus_AndSetsMetadata()
        {
            var ts = new Timesheet { Id = "ts-1", EmployeeId = "emp-1", Status = "Draft", RejectionNote = "Old rejection" };
            _db.Timesheets.Save(ts);

            var success = _service.SubmitTimesheet("ts-1");
            Assert.True(success);

            var updated = _db.Timesheets.GetById("ts-1");
            Assert.Equal("Submitted", updated.Status);
            Assert.NotNull(updated.SubmittedAt);
            Assert.Null(updated.RejectionNote);
        }

        [Fact]
        public void SubmitTimesheet_Fails_WhenRequireNoteEnabledAndNoteMissing()
        {
            var ts = new Timesheet
            {
                Id = "ts-note-check",
                EmployeeId = "emp-1",
                Status = "Draft",
                Entries =
                [
                    new TimeEntry { Hours = 4.0m, Description = "" }
                ]
            };
            _db.Timesheets.Save(ts);

            var settings = new TimesheetSettings { RequireNote = true };
            var success = _service.SubmitTimesheet("ts-note-check", settings);
            Assert.False(success);

            var unchanged = _db.Timesheets.GetById("ts-note-check");
            Assert.Equal("Draft", unchanged.Status);
        }

        [Fact]
        public void ApproveTimesheet_TransitionsToApproved_AndSetsApprover()
        {
            var emp = new Employee { Id = "emp-sub", FirstName = "Sub", LastName = "User" };
            var manager = new Employee { Id = "mgr-1", FirstName = "Manager", LastName = "User" };
            _db.Employees.Save(emp);
            _db.Employees.Save(manager);

            var ts = new Timesheet { Id = "ts-appr", EmployeeId = "emp-sub", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            _service.ApproveTimesheet("ts-appr", "mgr-1");

            var approved = _db.Timesheets.GetById("ts-appr");
            Assert.Equal("Approved", approved.Status);
            Assert.Equal("mgr-1", approved.ApprovedBy);
            Assert.NotNull(approved.ApprovedAt);
        }

        [Fact]
        public void ApproveTimesheet_Throws_OnSelfApprovalByNonAdmin()
        {
            var emp = new Employee { Id = "emp-self", FirstName = "Self", LastName = "User", IsAdmin = false };
            _db.Employees.Save(emp);

            var ts = new Timesheet { Id = "ts-self", EmployeeId = "emp-self", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            Assert.Throws<InvalidOperationException>(() => _service.ApproveTimesheet("ts-self", "emp-self"));
        }

        [Fact]
        public void ApproveTimesheet_AllowsSelfApprovalByAdmin()
        {
            var admin = new Employee { Id = "emp-admin", FirstName = "Admin", LastName = "User", IsAdmin = true };
            _db.Employees.Save(admin);

            var ts = new Timesheet { Id = "ts-admin", EmployeeId = "emp-admin", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            _service.ApproveTimesheet("ts-admin", "emp-admin");

            var approved = _db.Timesheets.GetById("ts-admin");
            Assert.Equal("Approved", approved.Status);
            Assert.Equal("emp-admin", approved.ApprovedBy);
        }

        [Fact]
        public void RejectTimesheet_SetsStatusRejected_AndCapturesReason()
        {
            var ts = new Timesheet { Id = "ts-rej", EmployeeId = "emp-1", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            _service.RejectTimesheet("ts-rej", "Missing task description", "mgr-1");

            var rejected = _db.Timesheets.GetById("ts-rej");
            Assert.Equal("Rejected", rejected.Status);
            Assert.Equal("Missing task description", rejected.RejectionNote);
            Assert.Equal("mgr-1", rejected.ApprovedBy);
            Assert.Null(rejected.ApprovedAt);
        }

        [Fact]
        public void IsTimesheetLocked_EvaluatesCutoffProperly()
        {
            var oldTs = new Timesheet { Year = 2020, WeekNumber = 1 };
            var currentWeek = ISOWeek.GetWeekOfYear(DateTime.Today);
            var currentYear = ISOWeek.GetYear(DateTime.Today);
            var currentTs = new Timesheet { Year = currentYear, WeekNumber = currentWeek };

            var disabledSettings = new TimesheetSettings { EnableTimesheetLocking = false, LockTimesheetsOlderThanDays = 30 };
            var enabledSettings = new TimesheetSettings { EnableTimesheetLocking = true, LockTimesheetsOlderThanDays = 30 };

            Assert.False(_service.IsTimesheetLocked(oldTs, disabledSettings));
            Assert.True(_service.IsTimesheetLocked(oldTs, enabledSettings));
            Assert.False(_service.IsTimesheetLocked(currentTs, enabledSettings));
        }

        [Theory]
        [InlineData(1.10, 15, "Up", 1.25)]
        [InlineData(1.10, 15, "Down", 1.00)]
        [InlineData(1.12, 15, "Nearest", 1.00)]
        [InlineData(1.13, 15, "Nearest", 1.25)]
        [InlineData(0.00, 15, "Up", 0.00)]
        [InlineData(1.33, 30, "Nearest", 1.50)]
        public void RoundHours_CalculatesCorrectRounding(double hours, int interval, string direction, double expected)
        {
            var actual = HRService.RoundHours((decimal)hours, interval, direction);
            Assert.Equal((decimal)expected, actual);
        }

        [Fact]
        public void ManagementHierarchy_ResolvesDirectAndIndirectSubordinates()
        {
            var manager = new Employee { Id = "mgr-lead", FirstName = "Leader", LastName = "One" };
            var subLeader = new Employee { Id = "sub-lead", FirstName = "Sub", LastName = "Leader", TeamId = "team-main" };
            var member = new Employee { Id = "emp-leaf", FirstName = "Leaf", LastName = "Member", TeamId = "team-sub" };
            var outsider = new Employee { Id = "emp-out", FirstName = "Out", LastName = "Sider" };

            _db.Employees.Save(manager);
            _db.Employees.Save(subLeader);
            _db.Employees.Save(member);
            _db.Employees.Save(outsider);

            _db.Teams.Save(new Team { Id = "team-main", LeaderId = "mgr-lead" });
            _db.Teams.Save(new Team { Id = "team-sub", LeaderId = "sub-lead" });

            var managedIds = _service.GetManagedEmployeeIds(manager, includeSelf: false);
            Assert.Contains("sub-lead", managedIds);
            Assert.Contains("emp-leaf", managedIds);
            Assert.DoesNotContain("emp-out", managedIds);
            Assert.DoesNotContain("mgr-lead", managedIds);

            Assert.True(_service.CanManageEmployee(manager, "sub-lead"));
            Assert.True(_service.CanManageEmployee(manager, "emp-leaf"));
            Assert.False(_service.CanManageEmployee(manager, "emp-out"));
        }

        [Fact]
        public void CalculateHourBank_DelegatesToOvertimeService()
        {
            var emp = new Employee
            {
                Id = "emp-hb",
                DateOfJoining = DateOnly.FromDateTime(DateTime.Today.AddDays(-21)),
                WeeklyHours = 40m
            };
            _db.Employees.Save(emp);

            var lastWeekDate = DateTime.Today.AddDays(-14);
            var year = ISOWeek.GetYear(lastWeekDate);
            var week = ISOWeek.GetWeekOfYear(lastWeekDate);
            var ts = new Timesheet
            {
                Id = "ts-hb",
                EmployeeId = "emp-hb",
                Year = year,
                WeekNumber = week,
                Entries = [new() { Hours = 45m }]
            };
            _db.Timesheets.Save(ts);

            var result = _service.CalculateHourBank("emp-hb");
            Assert.NotNull(result);
            Assert.Equal(emp.DateOfJoining, result.StartDate);
            Assert.True(result.WeeksTracked > 0);
        }

        [Fact]
        public void CalculateWeeklyOvertime_DelegatesToOvertimeService()
        {
            var emp = new Employee { Id = "emp-wo", WeeklyHours = 40m };
            _db.Employees.Save(emp);

            var ts = new Timesheet
            {
                Id = "ts-wo",
                EmployeeId = "emp-wo",
                Year = 2024,
                WeekNumber = 10,
                Entries = [new() { Hours = 45m }]
            };
            _db.Timesheets.Save(ts);

            var result = _service.CalculateWeeklyOvertime("emp-wo", 2024, 10);
            Assert.NotNull(result);
            Assert.Equal(45m, result.WorkedHours);
            Assert.Equal(40m, result.ExpectedHours);
            Assert.Equal(5m, result.Difference);
            Assert.True(result.IsOvertime);
        }

        [Fact]
        public void SubmitTimesheet_WhenTimesheetNotFound_ReturnsFalse()
        {
            var success = _service.SubmitTimesheet("non-existent-ts");
            Assert.False(success);
        }

        [Fact]
        public void SubmitTimesheet_WhenStatusIsApproved_ReturnsFalse()
        {
            var ts = new Timesheet { Id = "ts-appr-sub", Status = "Approved" };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet("ts-appr-sub");
            Assert.False(result);
        }

        [Fact]
        public void SubmitTimesheet_WhenStatusIsSubmitted_ReturnsFalse()
        {
            var ts = new Timesheet { Id = "ts-subm-sub", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet("ts-subm-sub");
            Assert.False(result);
        }

        [Fact]
        public void SubmitTimesheet_WhenTimesheetLocked_ReturnsFalse()
        {
            var ts = new Timesheet { Id = "ts-locked", Status = "Draft", Year = 2020, WeekNumber = 1 };
            _db.Timesheets.Save(ts);

            var settings = new TimesheetSettings { EnableTimesheetLocking = true, LockTimesheetsOlderThanDays = 30 };
            var result = _service.SubmitTimesheet("ts-locked", settings);
            Assert.False(result);
        }

        [Fact]
        public void ApproveTimesheet_WhenTimesheetNotFound_DoesNotThrow()
        {
            var ex = Record.Exception(() => _service.ApproveTimesheet("non-existent-ts", "mgr-1"));
            Assert.Null(ex);
        }

        [Fact]
        public void ApproveTimesheet_WhenStatusNotSubmitted_DoesNotChangeTimesheet()
        {
            var ts = new Timesheet { Id = "ts-draft-app", Status = "Draft" };
            _db.Timesheets.Save(ts);

            _service.ApproveTimesheet("ts-draft-app", "mgr-1");

            var updated = _db.Timesheets.GetById("ts-draft-app");
            Assert.Equal("Draft", updated.Status);
            Assert.Null(updated.ApprovedBy);
            Assert.Null(updated.ApprovedAt);
        }

        [Fact]
        public void RejectTimesheet_WhenTimesheetNotFound_DoesNotThrow()
        {
            var ex = Record.Exception(() => _service.RejectTimesheet("non-existent-ts", "rejection note", "mgr-1"));
            Assert.Null(ex);
        }

        [Fact]
        public void RejectTimesheet_WhenStatusNotSubmitted_DoesNotChangeTimesheet()
        {
            var ts = new Timesheet { Id = "ts-draft-rej", Status = "Draft" };
            _db.Timesheets.Save(ts);

            _service.RejectTimesheet("ts-draft-rej", "rejection note", "mgr-1");

            var updated = _db.Timesheets.GetById("ts-draft-rej");
            Assert.Equal("Draft", updated.Status);
            Assert.Null(updated.RejectionNote);
            Assert.Null(updated.ApprovedBy);
        }

        [Fact]
        public void CanManageEmployee_WhenAdmin_ReturnsTrueForAnyTarget()
        {
            var admin = new Employee { Id = "admin-user", IsAdmin = true };
            Assert.True(_service.CanManageEmployee(admin, "any-target-emp"));
            Assert.True(_service.CanManageEmployee(admin, "unknown-emp-999"));
        }

        [Fact]
        public void CanManageEmployee_WhenTargetIsSelf_ReturnsTrue()
        {
            var emp = new Employee { Id = "emp-self-mgr", IsAdmin = false };
            Assert.True(_service.CanManageEmployee(emp, "emp-self-mgr"));
        }

        [Fact]
        public void GetManagedEmployeeIds_WhenAdmin_WithIncludeSelf()
        {
            var admin = new Employee { Id = "admin-inc", IsAdmin = true };
            var other = new Employee { Id = "emp-other", IsAdmin = false };
            _db.Employees.Save(admin);
            _db.Employees.Save(other);

            var idsWithSelf = _service.GetManagedEmployeeIds(admin, includeSelf: true);
            Assert.Contains("admin-inc", idsWithSelf);
            Assert.Contains("emp-other", idsWithSelf);

            var idsWithoutSelf = _service.GetManagedEmployeeIds(admin, includeSelf: false);
            Assert.DoesNotContain("admin-inc", idsWithoutSelf);
            Assert.Contains("emp-other", idsWithoutSelf);
        }

        [Fact]
        public void RoundHours_UnknownDirection_ReturnsOriginalHours()
        {
            var result = HRService.RoundHours(1.23m, 15, "Unknown");
            Assert.Equal(1.23m, result);
        }

        [Fact]
        public void IsTimesheetLocked_WhenInvalidYearOrWeek_CatchesExceptionAndReturnsFalse()
        {
            var settings = new TimesheetSettings { EnableTimesheetLocking = true, LockTimesheetsOlderThanDays = 30 };
            var ts = new Timesheet { Year = 99999, WeekNumber = 999 };

            var result = _service.IsTimesheetLocked(ts, settings);
            Assert.False(result);
        }
    }
