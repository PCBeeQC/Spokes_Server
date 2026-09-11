using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.Globalization;
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

namespace Spokes_Server.Core.Services.HR;

/// <summary>
/// Calculates overtime and hour bank for employees.
/// The hour bank is the cumulative difference between worked hours 
/// and default weekly hours since the employee's start date.
/// </summary>
public class OvertimeService
{
    private readonly EmployeeRepository _employees;
    private readonly TimesheetRepository _timesheets;

    public OvertimeService(EmployeeRepository employees, TimesheetRepository timesheets)
    {
        _employees = employees;
        _timesheets = timesheets;
    }

    /// <summary>
    /// Calculate the hour bank for an employee.
    /// Positive = overtime banked, Negative = hours owed.
    /// </summary>
    public HourBankResult CalculateHourBank(string employeeId)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return new HourBankResult();

        var startDate = employee.DateOfJoining;
        var endDate = employee.EmploymentEndDate ?? DateOnly.FromDateTime(DateTime.Today);
        var today = DateOnly.FromDateTime(DateTime.Today);

        // Don't calculate future weeks
        if (endDate > today)
        {
            endDate = today;
        }

        // Get all timesheets for this employee
        var allTimesheets = _timesheets.GetAll()
            .Where(t => t.EmployeeId == employeeId)
            .ToList();

        decimal totalWorked = 0;
        decimal totalExpected = 0;
        int weekCount = 0;

        // Start from the Monday of the start week
        var current = GetMondayOfWeek(startDate);
        var endMonday = GetMondayOfWeek(endDate);

        // Don't include partial current week - only completed weeks
        var currentWeekMonday = GetMondayOfWeek(today);

        while (current <= endMonday && current < currentWeekMonday)
        {
            var year = current.Year;
            var week = ISOWeek.GetWeekOfYear(current.ToDateTime(TimeOnly.MinValue));

            // Find timesheet for this week
            var timesheet = allTimesheets.FirstOrDefault(t =>
                t.Year == year && t.WeekNumber == week);

            totalWorked += timesheet?.TotalHours ?? 0;
            totalExpected += employee.WeeklyHours;
            weekCount++;

            // Move to next week (add 7 days)
            current = current.AddDays(7);
        }

        return new HourBankResult
        {
            TotalWorkedHours = totalWorked,
            TotalExpectedHours = totalExpected,
            HourBank = totalWorked - totalExpected,
            WeeksTracked = weekCount,
            StartDate = startDate,
            EndDate = employee.EmploymentEndDate
        };
    }

    /// <summary>
    /// Calculate the hour difference for a specific week.
    /// </summary>
    public WeeklyOvertimeResult CalculateWeeklyOvertime(string employeeId, int year, int weekNumber)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return new WeeklyOvertimeResult();

        var timesheet = _timesheets.GetByWeek(employeeId, year, weekNumber);
        var workedHours = timesheet?.TotalHours ?? 0;
        var expectedHours = employee.WeeklyHours;

        return new WeeklyOvertimeResult
        {
            WorkedHours = workedHours,
            ExpectedHours = expectedHours,
            Difference = workedHours - expectedHours,
            IsOvertime = workedHours > expectedHours
        };
    }

    /// <summary>
    /// Get the Monday of the week containing the given date.
    /// </summary>
    private static DateOnly GetMondayOfWeek(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        var diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
        return DateOnly.FromDateTime(dt.AddDays(-diff));
    }
}

public class HourBankResult
{
    public decimal TotalWorkedHours { get; set; }
    public decimal TotalExpectedHours { get; set; }
    public decimal HourBank { get; set; }
    public int WeeksTracked { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public class WeeklyOvertimeResult
{
    public decimal WorkedHours { get; set; }
    public decimal ExpectedHours { get; set; }
    public decimal Difference { get; set; }
    public bool IsOvertime { get; set; }
}



