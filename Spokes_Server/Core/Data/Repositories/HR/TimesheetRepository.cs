// Core/Data/Repositories/TimesheetRepository.cs
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class TimesheetRepository : JsonRepository<Timesheet>
{
    public TimesheetRepository(DiskPersistenceService writer, IConfiguration config)
        // Note: Base path is generic here because timesheets are nested deep inside Employees
        : base(writer, config["DataPath"] ?? "Data", "week_*.json")
    {
    }

    protected override string GetFilePath(Timesheet item) =>
        // /Data/Employees/{EmpId}/Timesheets/{Year}/week_{Week}.json
        Path.Combine(_basePath, "Employees", item.EmployeeId, "Timesheets", item.Year.ToString(), $"week_{item.WeekNumber}.json");

    /// <summary>
    /// Gets the persisted timesheet for the specified employee and week, or null if no timesheet exists.
    /// </summary>
    public Timesheet? GetByWeek(string employeeId, int year, int week)
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault(t =>
            t.EmployeeId == employeeId &&
            t.Year == year &&
            t.WeekNumber == week);
    }

    /// <summary>
    /// Gets the persisted timesheet for the specified employee and week, or returns an unpersisted draft stub.
    /// </summary>
    public Timesheet GetOrStubByWeek(string employeeId, int year, int week) =>
        GetByWeek(employeeId, year, week)
        ?? new Timesheet
        {
            EmployeeId = employeeId,
            Year = year,
            WeekNumber = week,
            Status = TimesheetStatus.Draft
        };
}
