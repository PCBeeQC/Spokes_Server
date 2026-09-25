using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models.HR;

public class TimesheetTests
{
    [Fact]
    public void Timesheet_Defaults_InitializedCorrectly()
    {
        var sheet = new Timesheet();

        Assert.False(string.IsNullOrWhiteSpace(sheet.Id));
        Assert.True(Guid.TryParse(sheet.Id, out _));
        Assert.Equal(string.Empty, sheet.EmployeeId);
        Assert.Equal(string.Empty, sheet.EmployeeName);
        Assert.Equal(0, sheet.Year);
        Assert.Equal(0, sheet.WeekNumber);
        Assert.Equal("Draft", sheet.Status);
        Assert.Null(sheet.SubmittedAt);
        Assert.Null(sheet.ApprovedAt);
        Assert.Null(sheet.ApprovedBy);
        Assert.Null(sheet.RejectionNote);
        Assert.NotNull(sheet.Entries);
        Assert.Empty(sheet.Entries);
        Assert.Equal(0m, sheet.TotalHours);
    }

    [Fact]
    public void TimeEntry_Defaults_InitializedCorrectly()
    {
        var entry = new TimeEntry();

        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.True(Guid.TryParse(entry.Id, out _));
        Assert.Equal(string.Empty, entry.ProjectId);
        Assert.Equal(string.Empty, entry.WorkTypeId);
        Assert.Null(entry.SubTaskId);
        Assert.Equal(default, entry.Date);
        Assert.Equal(0m, entry.Hours);
        Assert.Equal(string.Empty, entry.Description);
        Assert.True(entry.IsBillable);
    }

    [Fact]
    public void TotalHours_CalculatesSumOfEntryHours()
    {
        var sheet = new Timesheet();
        Assert.Equal(0m, sheet.TotalHours);

        sheet.Entries.Add(new TimeEntry { Hours = 4.5m });
        sheet.Entries.Add(new TimeEntry { Hours = 3.25m });
        sheet.Entries.Add(new TimeEntry { Hours = 1.75m });

        Assert.Equal(9.5m, sheet.TotalHours);
    }

    [Fact]
    public void Timesheet_PropertyMutations_UpdatesAndReadsProperties()
    {
        var submittedDate = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        var approvedDate = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        List<TimeEntry> entries =
        [
            new TimeEntry { Hours = 8m }
        ];

        var sheet = new Timesheet
        {
            Id = "ts-custom-id",
            EmployeeId = "emp-001",
            EmployeeName = "Jane Doe",
            Year = 2026,
            WeekNumber = 37,
            Status = "Approved",
            SubmittedAt = submittedDate,
            ApprovedAt = approvedDate,
            ApprovedBy = "approver-002",
            RejectionNote = "None",
            Entries = entries
        };

        Assert.Equal("ts-custom-id", sheet.Id);
        Assert.Equal("emp-001", sheet.EmployeeId);
        Assert.Equal("Jane Doe", sheet.EmployeeName);
        Assert.Equal(2026, sheet.Year);
        Assert.Equal(37, sheet.WeekNumber);
        Assert.Equal("Approved", sheet.Status);
        Assert.Equal(submittedDate, sheet.SubmittedAt);
        Assert.Equal(approvedDate, sheet.ApprovedAt);
        Assert.Equal("approver-002", sheet.ApprovedBy);
        Assert.Equal("None", sheet.RejectionNote);
        Assert.Same(entries, sheet.Entries);
        Assert.Equal(8m, sheet.TotalHours);
    }

    [Fact]
    public void TimeEntry_PropertyMutations_UpdatesAndReadsProperties()
    {
        var entryDate = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

        var entry = new TimeEntry
        {
            Id = "entry-custom-id",
            ProjectId = "proj-123",
            WorkTypeId = "work-456",
            SubTaskId = "task-789",
            Date = entryDate,
            Hours = 7.5m,
            Description = "Implemented feature X",
            IsBillable = false
        };

        Assert.Equal("entry-custom-id", entry.Id);
        Assert.Equal("proj-123", entry.ProjectId);
        Assert.Equal("work-456", entry.WorkTypeId);
        Assert.Equal("task-789", entry.SubTaskId);
        Assert.Equal(entryDate, entry.Date);
        Assert.Equal(7.5m, entry.Hours);
        Assert.Equal("Implemented feature X", entry.Description);
        Assert.False(entry.IsBillable);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var id1 = Guid.NewGuid().ToString();
        var id2 = Guid.NewGuid().ToString();

        var sheet1 = new Timesheet { Id = id1, EmployeeName = "Alice" };
        var sheet2 = new Timesheet { Id = id1, EmployeeName = "Bob" };
        var sheet3 = new Timesheet { Id = id2, EmployeeName = "Alice" };

        // Reference equality
        Assert.True(sheet1.Equals(sheet1));

        // Same Id equality
        Assert.True(sheet1.Equals(sheet2));
        Assert.True(sheet2.Equals(sheet1));
        Assert.Equal(sheet1.GetHashCode(), sheet2.GetHashCode());

        // Different Id inequality
        Assert.False(sheet1.Equals(sheet3));
        Assert.False(sheet3.Equals(sheet1));

        // Null and different type inequality
        Assert.False(sheet1.Equals(null));
        Assert.False(sheet1.Equals("some string"));
        Assert.False(sheet1.Equals(new object()));
    }

    [Fact]
    public void GetHashCode_NullId_FallsBackToBaseHashCodeWithoutThrowing()
    {
        var sheet = new Timesheet { Id = null! };

        var exception = Record.Exception(() => sheet.GetHashCode());
        Assert.Null(exception);
    }
}
