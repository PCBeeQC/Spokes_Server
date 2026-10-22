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
            services.AddSingleton<EncryptionService>();
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();
            _service = new HRService(_db.HrContext);
        }

        public void Dispose()
        {
            _serviceProvider.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        [Fact]
        public void SubmitTimesheet_TransitionsStatus_AndSetsMetadata()
        {
            var ts = new Timesheet { Id = "ts-1", EmployeeId = "emp-1", Status = "Draft", RejectionNote = "Old rejection" };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet("ts-1");
            Assert.True(result.IsSuccess);

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
            var result = _service.SubmitTimesheet("ts-note-check", settings);
            Assert.False(result.IsSuccess);
            Assert.Equal("NOTE_REQUIRED", result.Code);

            var unchanged = _db.Timesheets.GetById("ts-note-check");
            Assert.Equal("Draft", unchanged.Status);
        }

        [Fact]
        public void ApproveTimesheet_TransitionsToApproved_AndSetsApprover()
        {
            var manager = new Employee { Id = "mgr-1", FirstName = "Manager", LastName = "User" };
            var emp = new Employee { Id = "emp-sub", FirstName = "Sub", LastName = "User", TeamId = "team-mgr" };
            var team = new Team { Id = "team-mgr", LeaderId = "mgr-1" };
            _db.Teams.Save(team);
            _db.Employees.Save(emp);
            _db.Employees.Save(manager);

            var ts = new Timesheet { Id = "ts-appr", EmployeeId = "emp-sub", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.ApproveTimesheet("ts-appr", "mgr-1");
            Assert.True(result.IsSuccess);

            var approved = _db.Timesheets.GetById("ts-appr");
            Assert.Equal("Approved", approved.Status);
            Assert.Equal("mgr-1", approved.ApprovedBy);
            Assert.NotNull(approved.ApprovedAt);
        }

        [Fact]
        public void ApproveTimesheet_Fails_WhenApproverLacksManagerialAuthority()
        {
            var outsider = new Employee { Id = "mgr-outsider", FirstName = "Outsider", LastName = "User", IsAdmin = false };
            var emp = new Employee { Id = "emp-sub-2", FirstName = "Sub", LastName = "User", TeamId = "team-other" };
            _db.Employees.Save(emp);
            _db.Employees.Save(outsider);

            var ts = new Timesheet { Id = "ts-unauth-appr", EmployeeId = "emp-sub-2", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.ApproveTimesheet("ts-unauth-appr", "mgr-outsider");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
        }

        [Fact]
        public void ApproveTimesheet_Fails_OnSelfApprovalByNonAdmin()
        {
            var emp = new Employee { Id = "emp-self", FirstName = "Self", LastName = "User", IsAdmin = false };
            _db.Employees.Save(emp);

            var ts = new Timesheet { Id = "ts-self", EmployeeId = "emp-self", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.ApproveTimesheet("ts-self", "emp-self");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
        }

        [Fact]
        public void ApproveTimesheet_AllowsSelfApprovalByAdmin()
        {
            var admin = new Employee { Id = "emp-admin", FirstName = "Admin", LastName = "User", IsAdmin = true };
            _db.Employees.Save(admin);

            var ts = new Timesheet { Id = "ts-admin", EmployeeId = "emp-admin", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.ApproveTimesheet("ts-admin", "emp-admin");
            Assert.True(result.IsSuccess);

            var approved = _db.Timesheets.GetById("ts-admin");
            Assert.Equal("Approved", approved.Status);
            Assert.Equal("emp-admin", approved.ApprovedBy);
        }

        [Fact]
        public void RejectTimesheet_SetsStatusRejected_AndCapturesReason()
        {
            var manager = new Employee { Id = "mgr-1", FirstName = "Manager", LastName = "User" };
            var emp = new Employee { Id = "emp-1", FirstName = "Employee", LastName = "One", TeamId = "team-mgr" };
            var team = new Team { Id = "team-mgr", LeaderId = "mgr-1" };
            _db.Teams.Save(team);
            _db.Employees.Save(emp);
            _db.Employees.Save(manager);

            var ts = new Timesheet { Id = "ts-rej", EmployeeId = "emp-1", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.RejectTimesheet("ts-rej", "mgr-1", "Missing task description");
            Assert.True(result.IsSuccess);

            var rejected = _db.Timesheets.GetById("ts-rej");
            Assert.Equal("Rejected", rejected.Status);
            Assert.Equal("Missing task description", rejected.RejectionNote);
            Assert.Equal("mgr-1", rejected.ApprovedBy);
            Assert.Null(rejected.ApprovedAt);
        }

        [Fact]
        public void RejectTimesheet_Fails_WhenRejectorLacksManagerialAuthority()
        {
            var outsider = new Employee { Id = "mgr-outsider", FirstName = "Outsider", LastName = "User", IsAdmin = false };
            var emp = new Employee { Id = "emp-2", FirstName = "Employee", LastName = "Two", TeamId = "team-other" };
            _db.Employees.Save(emp);
            _db.Employees.Save(outsider);

            var ts = new Timesheet { Id = "ts-unauth-rej", EmployeeId = "emp-2", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.RejectTimesheet("ts-unauth-rej", "mgr-outsider", "note");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
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
        public void SubmitTimesheet_WhenTimesheetNotFound_ReturnsFailure()
        {
            var result = _service.SubmitTimesheet("non-existent-ts");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public void SubmitTimesheet_WhenStatusIsApproved_ReturnsFailure()
        {
            var ts = new Timesheet { Id = "ts-appr-sub", Status = "Approved" };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet("ts-appr-sub");
            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_STATUS", result.Code);
        }

        [Fact]
        public void SubmitTimesheet_WhenStatusIsSubmitted_ReturnsFailure()
        {
            var ts = new Timesheet { Id = "ts-subm-sub", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet("ts-subm-sub");
            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_STATUS", result.Code);
        }

        [Fact]
        public void SubmitTimesheet_WhenTimesheetLocked_ReturnsFailure()
        {
            var ts = new Timesheet { Id = "ts-locked", Status = "Draft", Year = 2020, WeekNumber = 1 };
            _db.Timesheets.Save(ts);

            var settings = new TimesheetSettings { EnableTimesheetLocking = true, LockTimesheetsOlderThanDays = 30 };
            var result = _service.SubmitTimesheet("ts-locked", settings);
            Assert.False(result.IsSuccess);
            Assert.Equal("TIMESHEET_LOCKED", result.Code);
        }

        [Fact]
        public void ApproveTimesheet_WhenTimesheetNotFound_ReturnsFailure()
        {
            var result = _service.ApproveTimesheet("non-existent-ts", "mgr-1");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public void ApproveTimesheet_WhenStatusNotSubmitted_ReturnsFailure()
        {
            var ts = new Timesheet { Id = "ts-draft-app", Status = "Draft" };
            _db.Timesheets.Save(ts);

            var result = _service.ApproveTimesheet("ts-draft-app", "mgr-1");
            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_STATUS", result.Code);

            var updated = _db.Timesheets.GetById("ts-draft-app");
            Assert.Equal("Draft", updated.Status);
            Assert.Null(updated.ApprovedBy);
            Assert.Null(updated.ApprovedAt);
        }

        [Fact]
        public void RejectTimesheet_WhenTimesheetNotFound_ReturnsFailure()
        {
            var result = _service.RejectTimesheet("non-existent-ts", "mgr-1", "rejection note");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public void RejectTimesheet_WhenStatusNotSubmitted_ReturnsFailure()
        {
            var ts = new Timesheet { Id = "ts-draft-rej", Status = "Draft" };
            _db.Timesheets.Save(ts);

            var result = _service.RejectTimesheet("ts-draft-rej", "mgr-1", "rejection note");
            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_STATUS", result.Code);

            var updated = _db.Timesheets.GetById("ts-draft-rej");
            Assert.Equal("Draft", updated.Status);
            Assert.Null(updated.RejectionNote);
            Assert.Null(updated.ApprovedBy);
        }

        [Fact]
        public void RejectTimesheet_WhenRejectionNoteIsWhitespace_ReturnsFailure()
        {
            var manager = new Employee { Id = "mgr-note", FirstName = "Manager", LastName = "User" };
            var emp = new Employee { Id = "emp-note", FirstName = "Sub", LastName = "User", TeamId = "team-note" };
            var team = new Team { Id = "team-note", LeaderId = "mgr-note" };
            _db.Teams.Save(team);
            _db.Employees.Save(emp);
            _db.Employees.Save(manager);

            var ts = new Timesheet { Id = "ts-empty-note", EmployeeId = "emp-note", Status = "Submitted" };
            _db.Timesheets.Save(ts);

            var result = _service.RejectTimesheet("ts-empty-note", "mgr-note", "   ");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOTE_REQUIRED", result.Code);
        }

        [Fact]
        public void IsTimesheetLocked_WithInvalidYearOrWeek_ReturnsFalseWithoutThrowing()
        {
            var settings = new TimesheetSettings { EnableTimesheetLocking = true, LockTimesheetsOlderThanDays = 30 };

            var invalidYear = new Timesheet { Year = -1, WeekNumber = 1 };
            Assert.False(_service.IsTimesheetLocked(invalidYear, settings));

            var invalidWeekLow = new Timesheet { Year = 2024, WeekNumber = 0 };
            Assert.False(_service.IsTimesheetLocked(invalidWeekLow, settings));

            var invalidWeekHigh = new Timesheet { Year = 2024, WeekNumber = 55 };
            Assert.False(_service.IsTimesheetLocked(invalidWeekHigh, settings));
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
