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
            mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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
        [InlineData(1.23, 15, "Unknown", 1.23)]
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

        [Theory]
        [InlineData("ts-appr-sub", "Approved")]
        [InlineData("ts-subm-sub", "Submitted")]
        public void SubmitTimesheet_WhenStatusIsNonDraft_ReturnsFailure(string timesheetId, string status)
        {
            var ts = new Timesheet { Id = timesheetId, Status = status };
            _db.Timesheets.Save(ts);

            var result = _service.SubmitTimesheet(timesheetId);
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

            var invalidOverflow = new Timesheet { Year = 99999, WeekNumber = 999 };
            Assert.False(_service.IsTimesheetLocked(invalidOverflow, settings));
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
        public void CanOverrideTimesheetLock_ValidatesAdminAndTeamLeaderAuthority()
        {
            var admin = new Employee { Id = "emp-admin-lock", IsAdmin = true };
            var leader = new Employee { Id = "emp-leader-lock", IsAdmin = false };
            var member = new Employee { Id = "emp-member-lock", IsAdmin = false, TeamId = "team-lock-1" };
            var outsider = new Employee { Id = "emp-outsider-lock", IsAdmin = false };
            var team = new Team { Id = "team-lock-1", LeaderId = "emp-leader-lock" };

            _db.Employees.Save(admin);
            _db.Employees.Save(leader);
            _db.Employees.Save(member);
            _db.Employees.Save(outsider);
            _db.Teams.Save(team);

            // Admin can override anyone, including self
            Assert.True(_service.CanOverrideTimesheetLock(admin, admin.Id));
            Assert.True(_service.CanOverrideTimesheetLock(admin, member.Id));
            Assert.True(_service.CanOverrideTimesheetLock(admin, outsider.Id));

            // Team leader can override managed team member, but NOT self or outsider
            Assert.True(_service.CanOverrideTimesheetLock(leader, member.Id));
            Assert.False(_service.CanOverrideTimesheetLock(leader, leader.Id));
            Assert.False(_service.CanOverrideTimesheetLock(leader, outsider.Id));

            // Regular employee cannot override anyone
            Assert.False(_service.CanOverrideTimesheetLock(member, member.Id));
            Assert.False(_service.CanOverrideTimesheetLock(null, member.Id));
        }

        [Fact]
        public void BulkUpdateTimeEntries_UpdatesAcrossApprovedWeeks_PreservesStatusAndIds_AndSkipsExcluded()
        {
            var admin = new Employee { Id = "bulk-admin", IsAdmin = true };
            var emp = new Employee { Id = "bulk-emp", IsAdmin = false };
            _db.Employees.Save(admin);
            _db.Employees.Save(emp);

            var entry1 = new TimeEntry
            {
                Id = "entry-w1-1",
                Date = new DateTime(2025, 3, 3),
                ProjectId = "proj-wrong",
                WorkTypeId = "",
                Hours = 8m,
                Description = "Legacy work",
                IsBillable = false
            };
            var entry2Billed = new TimeEntry
            {
                Id = "entry-w1-billed",
                Date = new DateTime(2025, 3, 4),
                ProjectId = "proj-wrong",
                WorkTypeId = "",
                Hours = 4m,
                Description = "Invoiced work",
                IsBillable = true
            };
            var entry3Week2 = new TimeEntry
            {
                Id = "entry-w2-1",
                Date = new DateTime(2025, 3, 10),
                ProjectId = "proj-wrong",
                WorkTypeId = "wt-old",
                Hours = 6.5m,
                Description = "",
                IsBillable = false
            };

            var sheet1 = new Timesheet
            {
                Id = "sheet-bulk-1",
                EmployeeId = emp.Id,
                Year = 2025,
                WeekNumber = 10,
                Status = TimesheetStatus.Approved,
                Entries = [entry1, entry2Billed]
            };
            var sheet2 = new Timesheet
            {
                Id = "sheet-bulk-2",
                EmployeeId = emp.Id,
                Year = 2025,
                WeekNumber = 11,
                Status = TimesheetStatus.Approved,
                Entries = [entry3Week2]
            };
            _db.Timesheets.Save(sheet1);
            _db.Timesheets.Save(sheet2);

            var request = new BulkTimesheetEditRequest
            {
                EntryIds = ["entry-w1-1", "entry-w1-billed", "entry-w2-1"],
                ExcludedEntryIds = new HashSet<string> { "entry-w1-billed" },
                UpdateProject = true,
                ProjectId = "proj-correct",
                UpdateTask = true,
                WorkTypeId = "wt-eng",
                SubTaskId = "sub-1",
                UpdateBillable = true,
                IsBillable = true,
                NoteMode = BulkNoteUpdateMode.Append,
                NoteText = "[Reassigned]"
            };

            var result = _service.BulkUpdateTimeEntries(request, admin.Id);
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.Equal(2, result.Value!.UpdatedEntriesCount);
            Assert.Equal(2, result.Value.UpdatedTimesheetsCount);
            Assert.Equal(1, result.Value.SkippedExcludedCount);

            var reloaded1 = _db.Timesheets.GetById("sheet-bulk-1")!;
            var reloaded2 = _db.Timesheets.GetById("sheet-bulk-2")!;

            // Both timesheets remain Approved
            Assert.Equal(TimesheetStatus.Approved, reloaded1.Status);
            Assert.Equal(TimesheetStatus.Approved, reloaded2.Status);

            // entry-w1-1 was updated in-place preserving its ID
            var updated1 = reloaded1.Entries.Single(e => e.Id == "entry-w1-1");
            Assert.Equal("proj-correct", updated1.ProjectId);
            Assert.Equal("wt-eng", updated1.WorkTypeId);
            Assert.Equal("sub-1", updated1.SubTaskId);
            Assert.True(updated1.IsBillable);
            Assert.Equal("Legacy work; [Reassigned]", updated1.Description);

            // entry-w1-billed was excluded and remains untouched
            var skippedBilled = reloaded1.Entries.Single(e => e.Id == "entry-w1-billed");
            Assert.Equal("proj-wrong", skippedBilled.ProjectId);
            Assert.Equal("Invoiced work", skippedBilled.Description);

            // entry-w2-1 was updated in-place
            var updated3 = reloaded2.Entries.Single(e => e.Id == "entry-w2-1");
            Assert.Equal("proj-correct", updated3.ProjectId);
            Assert.Equal("wt-eng", updated3.WorkTypeId);
            Assert.Equal("sub-1", updated3.SubTaskId);
            Assert.True(updated3.IsBillable);
            Assert.Equal("[Reassigned]", updated3.Description);
        }

        [Fact]
        public void BulkUpdateTimeEntries_WhenUnauthorized_ReturnsForbidden()
        {
            var regularUser = new Employee { Id = "bulk-reg", IsAdmin = false };
            var otherEmp = new Employee { Id = "bulk-other", IsAdmin = false };
            _db.Employees.Save(regularUser);
            _db.Employees.Save(otherEmp);

            var sheet = new Timesheet
            {
                Id = "sheet-unauth",
                EmployeeId = otherEmp.Id,
                Year = 2025,
                WeekNumber = 12,
                Status = TimesheetStatus.Approved,
                Entries = [new TimeEntry { Id = "entry-unauth-1", ProjectId = "proj-1", Hours = 5m }]
            };
            _db.Timesheets.Save(sheet);

            var result = _service.BulkUpdateTimeEntries(new BulkTimesheetEditRequest
            {
                EntryIds = ["entry-unauth-1"],
                UpdateProject = true,
                ProjectId = "proj-2"
            }, regularUser.Id);

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
        }

        [Fact]
        public void GetManagedEmployeeIds_WhenManagerLeadsDifferentTeamAndIncludeSelfTrue_AlwaysIncludesSelf()
        {
            var manager = new Employee
            {
                Id = "mgr-self-1",
                FirstName = "Alex",
                LastName = "Leader",
                IsActive = true,
                TeamId = "team-home"
            };
            var member = new Employee
            {
                Id = "dev-sub-1",
                FirstName = "Pat",
                LastName = "Worker",
                IsActive = true,
                TeamId = "team-led"
            };
            _db.Employees.Save(manager);
            _db.Employees.Save(member);
            _db.Teams.Save(new Team
            {
                Id = "team-led",
                Name = "Led Team",
                LeaderId = manager.Id
            });

            var managedWithSelf = _service.GetManagedEmployeeIds(manager, includeSelf: true);
            Assert.Contains(manager.Id, managedWithSelf);
            Assert.Contains(member.Id, managedWithSelf);

            var managedWithoutSelf = _service.GetManagedEmployeeIds(manager, includeSelf: false);
            Assert.DoesNotContain(manager.Id, managedWithoutSelf);
            Assert.Contains(member.Id, managedWithoutSelf);
        }
    }
