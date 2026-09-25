namespace Spokes_Server.Core.Models.HR;

using System;

public class EmployeeWorkSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public decimal WeeklyHours { get; set; }
    public string Note { get; set; } = string.Empty;
}
