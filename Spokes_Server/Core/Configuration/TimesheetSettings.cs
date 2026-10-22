namespace Spokes_Server.Core.Configuration;

public class TimesheetSettings
{
    // Features
    public bool EnableWeeklyPlan { get; set; } = true;
    
    // Approval Workflow
    public bool RequireApproval { get; set; }
    
    // Timesheet Locking
    public bool EnableTimesheetLocking { get; set; }
    public int LockTimesheetsOlderThanDays { get; set; } = 30;
    
    // Required Fields
    public bool RequireProject { get; set; } = true;
    public bool RequireTask { get; set; } = true;
    public bool RequireNote { get; set; }
    
    // Rounding (applied to reports/exports only)
    public bool EnableRounding { get; set; }
    public int RoundingIntervalMinutes { get; set; } = 15;     // 5, 6, 15, or 30
    public string RoundingDirection { get; set; } = "Nearest"; // "Up", "Down", "Nearest"
}
