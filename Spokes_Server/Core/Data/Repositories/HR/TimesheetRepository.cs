// Core/Data/Repositories/TimesheetRepository.cs
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class TimesheetRepository : JsonRepository<Timesheet>
{
    private readonly string _rootDataPath;

    public TimesheetRepository(DiskPersistenceService writer, IConfiguration config)
        // Note: Base path is generic here because timesheets are nested deep inside Employees
        : base(writer, config["DataPath"] ?? "Data", "week_*.json")
    {
        _rootDataPath = config["DataPath"] ?? "Data";
    }

    protected override string GetFilePath(Timesheet item)
    {
        // /Data/Employees/{EmpId}/Timesheets/{Year}/week_{Week}.json
        return Path.Combine(_rootDataPath, "Employees", item.EmployeeId, "Timesheets", item.Year.ToString(), $"week_{item.WeekNumber}.json");
    }

    public Timesheet GetByWeek(string empId, int year, int week)
    {
        // 1. Search Memory Cache
        var existing = _cache.Values.FirstOrDefault(t =>
            t.EmployeeId == empId &&
            t.Year == year &&
            t.WeekNumber == week);

        if (existing != null) return existing;

        // 2. If not found, return a NEW Draft (User will save it later)
        return new Timesheet
        {
            EmployeeId = empId,
            Year = year,
            WeekNumber = week,
            Status = "Draft"
        };
    }

    // Override LoadFromDisk because we only want to load RELEVANT timesheets, 
    // or scan specific folders. For now, let's keep it simple and scan everything.
    // In the future, you would override this to only load "Current Year".
}


