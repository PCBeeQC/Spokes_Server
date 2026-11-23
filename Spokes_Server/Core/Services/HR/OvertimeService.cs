using System.Globalization;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Reports.Helpers;

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

        var hrConfig = _companyProfiles.GetHRSettings();

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
        var sim = SimulateCompletedWeeks(employee, employeeId, hrConfig, includeAuditRecords: false);
        decimal calculatedBank = sim.RawBank;

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
            TotalWorkedHours = sim.TotalWorked,
            TotalExpectedHours = sim.TotalExpected,
            HourBank = calculatedBank,
            WeeksTracked = sim.WeekCount,
            StartDate = startDate,
            EndDate = employee.EmploymentEndDate
        };
    }

    private (decimal TotalWorked, decimal TotalExpected, decimal RawBank, int WeekCount, List<HourBankAuditRecord> SystemAuditRecords) SimulateCompletedWeeks(
        Employee employee,
        string employeeId,
        HRSettings hrConfig,
        bool includeAuditRecords)
    {
        var startDate = employee.DateOfJoining;
        var endDate = employee.EmploymentEndDate ?? DateOnly.FromDateTime(DateTime.Today);
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (endDate > today)
        {
            endDate = today;
        }

        var allTimesheets = _timesheets.GetAll()
            .Where(t => t.EmployeeId == employeeId)
            .ToList();

        decimal totalWorked = 0;
        decimal totalExpected = 0;
        decimal calculatedBank = 0;
        int weekCount = 0;
        List<HourBankAuditRecord> audit = [];

        var current = TimesheetDateRangeHelper.GetMondayOfWeek(startDate);
        var endMonday = TimesheetDateRangeHelper.GetMondayOfWeek(endDate);
        var currentWeekMonday = TimesheetDateRangeHelper.GetMondayOfWeek(today);

        while (current <= endMonday && current < currentWeekMonday)
        {
            var year = ISOWeek.GetYear(current.ToDateTime(TimeOnly.MinValue));
            var week = ISOWeek.GetWeekOfYear(current.ToDateTime(TimeOnly.MinValue));

            var timesheet = allTimesheets.FirstOrDefault(t => t.Year == year && t.WeekNumber == week);
            var weeklyWorked = GetEffectiveTargetHours(timesheet);
            var weeklyExpected = GetExpectedWeeklyHours(employee, current);

            totalWorked += weeklyWorked;
            totalExpected += weeklyExpected;
            weekCount++;

            if (hrConfig.OvertimeCalculationMode == "Daily" && timesheet != null)
            {
                decimal dailyThreshold = hrConfig.DailyOvertimeThreshold;
                var dailyHours = timesheet.Entries
                    .Where(e => TimeEntryTypes.CountsTowardWeeklyExpectedTarget(e.EntryType, e.ProjectId))
                    .GroupBy(e => e.Date.Date)
                    .Select(g => new { Date = g.Key, Hours = g.Sum(e => e.Hours) });

                foreach (var dh in dailyHours)
                {
                    if (dh.Hours > dailyThreshold)
                    {
                        decimal overtime = dh.Hours - dailyThreshold;
                        calculatedBank += overtime;
                        if (includeAuditRecords)
                        {
                            audit.Add(new HourBankAuditRecord
                            {
                                Date = dh.Date,
                                Hours = overtime,
                                Description = $"Daily Overtime ({dh.Hours}h > {dailyThreshold}h limit)",
                                Type = "System"
                            });
                        }
                    }
                }

                if (weeklyWorked < weeklyExpected)
                {
                    decimal missing = weeklyExpected - weeklyWorked;
                    calculatedBank -= missing;
                    if (includeAuditRecords)
                    {
                        var bankedRedeemed = GetBankedTimeOffHours(timesheet);
                        var desc = bankedRedeemed > 0
                            ? $"Banked Time Off Redeemed ({bankedRedeemed}h) / Weekly Target ({weeklyWorked}h / {weeklyExpected}h)"
                            : $"Weekly Target Shortfall ({weeklyWorked}h / {weeklyExpected}h)";
                        audit.Add(new HourBankAuditRecord
                        {
                            Date = current.AddDays(6).ToDateTime(TimeOnly.MinValue),
                            Hours = -missing,
                            Description = desc,
                            Type = "System"
                        });
                    }
                }
            }
            else if (hrConfig.OvertimeCalculationMode == "Weekly")
            {
                decimal diff = weeklyWorked - weeklyExpected;
                calculatedBank += diff;
                if (includeAuditRecords && diff != 0)
                {
                    var bankedRedeemed = GetBankedTimeOffHours(timesheet);
                    var desc = diff > 0
                        ? $"Weekly Overtime ({weeklyWorked}h / {weeklyExpected}h)"
                        : (bankedRedeemed > 0
                            ? $"Banked Time Off Redeemed ({bankedRedeemed}h) / Weekly Target ({weeklyWorked}h / {weeklyExpected}h)"
                            : $"Weekly Target Shortfall ({weeklyWorked}h / {weeklyExpected}h)");
                    audit.Add(new HourBankAuditRecord
                    {
                        Date = current.AddDays(6).ToDateTime(TimeOnly.MinValue),
                        Hours = diff,
                        Description = desc,
                        Type = "System"
                    });
                }
            }

            current = current.AddDays(7);
        }

        return (totalWorked, totalExpected, calculatedBank, weekCount, audit);
    }

    /// <summary>
    /// Calculate the hour difference for a specific week.
    /// </summary>
    public WeeklyOvertimeResult CalculateWeeklyOvertime(string employeeId, int year, int weekNumber)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return new WeeklyOvertimeResult();

        var timesheet = _timesheets.GetByWeek(employeeId, year, weekNumber);
        var workedHours = GetEffectiveTargetHours(timesheet);
        
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

    private static decimal GetEffectiveTargetHours(Timesheet? timesheet)
    {
        if (timesheet?.Entries == null || timesheet.Entries.Count == 0)
            return 0m;

        return timesheet.Entries
            .Where(e => e != null && TimeEntryTypes.CountsTowardWeeklyExpectedTarget(e.EntryType, e.ProjectId))
            .Sum(e => e.Hours);
    }

    private static decimal GetBankedTimeOffHours(Timesheet? timesheet)
    {
        if (timesheet?.Entries == null || timesheet.Entries.Count == 0)
            return 0m;

        return timesheet.Entries
            .Where(e => e != null && !TimeEntryTypes.CountsTowardWeeklyExpectedTarget(e.EntryType, e.ProjectId))
            .Sum(e => e.Hours);
    }

    private static decimal GetExpectedWeeklyHours(Employee employee, DateOnly weekMonday)
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
        var employee = _employees.GetById(employeeId);
        if (employee == null) return [];

        var hrConfig = _companyProfiles.GetHRSettings();
        if (!hrConfig.EnableHourBank) return [];

        var sim = SimulateCompletedWeeks(employee, employeeId, hrConfig, includeAuditRecords: true);
        var audit = sim.SystemAuditRecords;

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
