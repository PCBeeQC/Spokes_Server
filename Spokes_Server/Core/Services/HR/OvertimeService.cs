using System.Globalization;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.HR;

/// <summary>
/// Calculates overtime and hour bank for employees.
/// The hour bank is the cumulative difference between worked hours 
/// and expected hours, taking into account schedules and manual adjustments.
/// </summary>
public class OvertimeService
{
    private readonly EmployeeRepository _employees;
    private readonly TimesheetRepository _timesheets;
    private readonly HourBankAdjustmentRepository _adjustments;
    private readonly Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository _companyProfiles;

    public OvertimeService(
        EmployeeRepository employees, 
        TimesheetRepository timesheets,
        HourBankAdjustmentRepository adjustments,
        Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository companyProfiles)
    {
        _employees = employees;
        _timesheets = timesheets;
        _adjustments = adjustments;
        _companyProfiles = companyProfiles;
    }

    /// <summary>
    /// Calculate the hour bank for an employee.
    /// Positive = overtime banked, Negative = hours owed.
    /// </summary>
    public HourBankResult CalculateHourBank(string employeeId)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return new HourBankResult();

        var hrConfig = _companyProfiles.Get()?.HRConfig ?? new Spokes_Server.Core.Models.Core.HRSettings();

        if (!hrConfig.EnableHourBank)
        {
            return new HourBankResult
            {
                TotalWorkedHours = 0,
                TotalExpectedHours = 0,
                HourBank = 0,
                WeeksTracked = 0,
                StartDate = employee.DateOfJoining,
                EndDate = employee.EmploymentEndDate
            };
        }

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
        decimal calculatedBank = 0;
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

            var timesheet = allTimesheets.FirstOrDefault(t => t.Year == year && t.WeekNumber == week);
            var weeklyWorked = timesheet?.TotalHours ?? 0;
            var weeklyExpected = GetExpectedWeeklyHours(employee, current);

            totalWorked += weeklyWorked;
            totalExpected += weeklyExpected;
            weekCount++;

            if (hrConfig.OvertimeCalculationMode == "Daily" && timesheet != null)
            {
                // Daily calculation: Overtime is banked per day exceeding the threshold
                decimal dailyThreshold = hrConfig.DailyOvertimeThreshold;
                var dailyHours = timesheet.Entries
                    .GroupBy(e => e.Date.Date)
                    .Select(g => g.Sum(e => e.Hours));

                decimal weekBank = 0;
                foreach (var dh in dailyHours)
                {
                    if (dh > dailyThreshold)
                    {
                        weekBank += (dh - dailyThreshold);
                    }
                }
                calculatedBank += weekBank;

                // Subtract hours if they didn't meet the weekly minimum?
                // For a true daily overtime setup, usually missing the weekly target deducts from the bank.
                if (weeklyWorked < weeklyExpected)
                {
                    calculatedBank -= (weeklyExpected - weeklyWorked);
                }
            }
            else if (hrConfig.OvertimeCalculationMode == "Weekly")
            {
                calculatedBank += (weeklyWorked - weeklyExpected);
            }

            // Move to next week (add 7 days)
            current = current.AddDays(7);
        }

        // Add manual adjustments
        var adjustments = _adjustments.GetByEmployeeId(employeeId);
        decimal adjustmentsSum = adjustments.Sum(a => a.Hours);
        calculatedBank += adjustmentsSum;

        // Apply max bankable limit if configured
        if (hrConfig.MaxBankableHours > 0 && calculatedBank > hrConfig.MaxBankableHours)
        {
            calculatedBank = hrConfig.MaxBankableHours;
        }

        return new HourBankResult
        {
            TotalWorkedHours = totalWorked,
            TotalExpectedHours = totalExpected,
            HourBank = calculatedBank,
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
        
        var weekMonday = ISOWeek.ToDateTime(year, weekNumber, DayOfWeek.Monday);
        var expectedHours = GetExpectedWeeklyHours(employee, DateOnly.FromDateTime(weekMonday));

        return new WeeklyOvertimeResult
        {
            WorkedHours = workedHours,
            ExpectedHours = expectedHours,
            Difference = workedHours - expectedHours,
            IsOvertime = workedHours > expectedHours
        };
    }

    private decimal GetExpectedWeeklyHours(Employee employee, DateOnly weekMonday)
    {
        if (employee.WorkSchedules == null || !employee.WorkSchedules.Any())
            return employee.WeeklyHours;

        var activeSchedule = employee.WorkSchedules
            .Where(s => s.StartDate <= weekMonday && (s.EndDate == null || s.EndDate >= weekMonday))
            .OrderByDescending(s => s.StartDate)
            .FirstOrDefault();

        return activeSchedule?.WeeklyHours ?? employee.WeeklyHours;
    }

    /// <summary>
    /// Generates an audit trail of all hour bank changes (automatic weekly/daily logic and manual adjustments).
    /// </summary>
    public List<HourBankAuditRecord> GetHourBankAuditTrail(string employeeId)
    {
        var audit = new List<HourBankAuditRecord>();
        
        var employee = _employees.GetById(employeeId);
        if (employee == null) return audit;

        var hrConfig = _companyProfiles.Get()?.HRConfig ?? new Spokes_Server.Core.Models.Core.HRSettings();
        if (!hrConfig.EnableHourBank) return audit;

        var startDate = employee.DateOfJoining;
        var endDate = employee.EmploymentEndDate ?? DateOnly.FromDateTime(DateTime.Today);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (endDate > today) endDate = today;

        var allTimesheets = _timesheets.GetAll().Where(t => t.EmployeeId == employeeId).ToList();

        var current = GetMondayOfWeek(startDate);
        var endMonday = GetMondayOfWeek(endDate);
        var currentWeekMonday = GetMondayOfWeek(today);

        while (current <= endMonday && current < currentWeekMonday)
        {
            var year = current.Year;
            var week = ISOWeek.GetWeekOfYear(current.ToDateTime(TimeOnly.MinValue));

            var timesheet = allTimesheets.FirstOrDefault(t => t.Year == year && t.WeekNumber == week);
            var weeklyWorked = timesheet?.TotalHours ?? 0;
            var weeklyExpected = GetExpectedWeeklyHours(employee, current);

            if (hrConfig.OvertimeCalculationMode == "Daily" && timesheet != null)
            {
                decimal dailyThreshold = hrConfig.DailyOvertimeThreshold;
                var dailyHours = timesheet.Entries
                    .GroupBy(e => e.Date.Date)
                    .Select(g => new { Date = g.Key, Hours = g.Sum(e => e.Hours) });

                decimal weekBank = 0;
                foreach (var dh in dailyHours)
                {
                    if (dh.Hours > dailyThreshold)
                    {
                        decimal overtime = dh.Hours - dailyThreshold;
                        weekBank += overtime;
                        audit.Add(new HourBankAuditRecord
                        {
                            Date = dh.Date,
                            Hours = overtime,
                            Description = $"Daily Overtime ({dh.Hours}h > {dailyThreshold}h limit)",
                            Type = "System"
                        });
                    }
                }

                if (weeklyWorked < weeklyExpected)
                {
                    decimal missing = weeklyExpected - weeklyWorked;
                    audit.Add(new HourBankAuditRecord
                    {
                        Date = current.AddDays(6).ToDateTime(TimeOnly.MinValue), // End of week
                        Hours = -missing,
                        Description = $"Weekly Target Shortfall ({weeklyWorked}h / {weeklyExpected}h)",
                        Type = "System"
                    });
                }
            }
            else if (hrConfig.OvertimeCalculationMode == "Weekly")
            {
                decimal diff = weeklyWorked - weeklyExpected;
                if (diff != 0)
                {
                    audit.Add(new HourBankAuditRecord
                    {
                        Date = current.AddDays(6).ToDateTime(TimeOnly.MinValue), // End of week
                        Hours = diff,
                        Description = diff > 0 ? $"Weekly Overtime ({weeklyWorked}h / {weeklyExpected}h)" : $"Weekly Target Shortfall ({weeklyWorked}h / {weeklyExpected}h)",
                        Type = "System"
                    });
                }
            }

            current = current.AddDays(7);
        }

        // Add manual adjustments
        var adjustments = _adjustments.GetByEmployeeId(employeeId);
        foreach (var adj in adjustments)
        {
            audit.Add(new HourBankAuditRecord
            {
                Date = adj.Date,
                Hours = adj.Hours,
                Description = adj.Reason,
                Type = "Manual",
                CreatedById = adj.CreatedByEmployeeId
            });
        }

        return audit.OrderByDescending(a => a.Date).ToList();
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

public class HourBankAuditRecord
{
    public DateTime Date { get; set; }
    public decimal Hours { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "System", "Manual"
    public string? CreatedById { get; set; }
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
