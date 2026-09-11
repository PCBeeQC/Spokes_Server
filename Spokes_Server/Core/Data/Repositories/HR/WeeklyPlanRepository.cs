using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class WeeklyPlanRepository : JsonRepository<WeeklyPlan>
{
    private readonly string _rootDataPath;

    public WeeklyPlanRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "WeeklyPlans"), "plan_*.json")
    {
        _rootDataPath = Path.Combine(config["DataPath"] ?? "Data", "WeeklyPlans");
    }

    protected override string GetFilePath(WeeklyPlan item)
    {
        // Path: /Data/WeeklyPlans/{Year}/plan_{EmpId}_week_{Week}.json
        return Path.Combine(_rootDataPath, item.Year.ToString(), $"plan_{item.EmployeeId}_week_{item.WeekNumber}.json");
    }

    public WeeklyPlan GetByEmployeeAndWeek(string employeeId, int year, int week)
    {
        // 1. Check Cache
        var existing = _cache.Values.FirstOrDefault(p =>
            p.EmployeeId == employeeId &&
            p.Year == year &&
            p.WeekNumber == week);

        if (existing != null) return existing;

        // 2. Return new empty plan if not found
        return new WeeklyPlan
        {
            EmployeeId = employeeId,
            Year = year,
            WeekNumber = week
        };
    }
}


