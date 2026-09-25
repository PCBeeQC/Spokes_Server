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

    public Timesheet GetByWeek(string empId, int year, int week) =>
        _cache.Values.FirstOrDefault(t =>
            t.EmployeeId == empId &&
            t.Year == year &&
            t.WeekNumber == week)
        ?? new Timesheet
        {
            EmployeeId = empId,
            Year = year,
            WeekNumber = week,
            Status = "Draft"
        };
}
