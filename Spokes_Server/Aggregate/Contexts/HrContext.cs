namespace Spokes_Server.Aggregate.Contexts;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;

public class HrContext
{
    private readonly CompanyProfileRepository? _companyProfile;

    public virtual EmployeeRepository Employees { get; }
    public virtual TimesheetRepository Timesheets { get; }
    public virtual HourBankAdjustmentRepository HourBankAdjustments { get; }
    public virtual WeeklyPlanRepository WeeklyPlans { get; }
    public virtual TeamRepository Teams { get; }
    public virtual CalendarEventRepository CalendarEvents { get; }

    protected HrContext()
    {
        Employees = null!;
        Timesheets = null!;
        HourBankAdjustments = null!;
        WeeklyPlans = null!;
        Teams = null!;
        CalendarEvents = null!;
    }

    [ActivatorUtilitiesConstructor]
    public HrContext(
        EmployeeRepository employees,
        TimesheetRepository timesheets,
        HourBankAdjustmentRepository hourBankAdjustments,
        WeeklyPlanRepository weeklyPlans,
        TeamRepository teams,
        CalendarEventRepository calendarEvents,
        CompanyProfileRepository? companyProfile = null)
    {
        Employees = employees;
        Timesheets = timesheets;
        HourBankAdjustments = hourBankAdjustments;
        WeeklyPlans = weeklyPlans;
        Teams = teams;
        CalendarEvents = calendarEvents;
        _companyProfile = companyProfile;
    }

    /// <summary>
    /// Exposes domain-owned HR general settings decomposed from the global company profile.
    /// </summary>
    public virtual HRSettings GetHRSettings() => _companyProfile?.GetHRSettings() ?? new HRSettings();

    /// <summary>
    /// Exposes domain-owned timesheet settings decomposed from the global company profile.
    /// </summary>
    public virtual TimesheetSettings GetTimesheetSettings() => _companyProfile?.GetTimesheetSettings() ?? new TimesheetSettings();

    public virtual HRSettings HRConfig => GetHRSettings();
    public virtual TimesheetSettings TimesheetConfig => GetTimesheetSettings();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => Employees.LoadFromDisk(),
            () => Timesheets.LoadFromDisk(),
            () => HourBankAdjustments.LoadFromDisk(),
            () => WeeklyPlans.LoadFromDisk(),
            () => Teams.LoadFromDisk(),
            () => CalendarEvents.LoadFromDisk()
        );
    }
}
