using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.HR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.HR
{
    public class HRServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;
        private readonly OvertimeService _overtimeService;
        private readonly HRService _service;

        public HRServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_HR_" + Guid.NewGuid().ToString());
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
            _overtimeService = new OvertimeService(_db.Employees, _db.Timesheets);
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
                Entries = new List<TimeEntry>
                {
                    new TimeEntry { Hours = 4.0m, Description = "" }
                }
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
    }
}
