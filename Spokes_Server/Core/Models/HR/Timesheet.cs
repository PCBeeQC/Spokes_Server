namespace Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;

public class Timesheet : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Timesheet)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty; // Store name for easier reporting later

    public int Year { get; set; }
    public int WeekNumber { get; set; }

    // Status: "Draft", "Submitted", "Approved", "Rejected"
    public string Status { get; set; } = "Draft";

    // Approval metadata
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }      // Employee ID of the approver
    public string? RejectionNote { get; set; }    // Reason for rejection

    public List<TimeEntry> Entries { get; set; } = new();

    // Helper to get total hours
    public decimal TotalHours => Entries.Sum(e => e.Hours);
}

public class TimeEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectId { get; set; } = string.Empty;

    public string WorkTypeId { get; set; } = string.Empty;
    public string? SubTaskId { get; set; }

    // We store the specific date to avoid "Year change" edge case bugs
    public DateTime Date { get; set; }

    public decimal Hours { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsBillable { get; set; } = true;
}



