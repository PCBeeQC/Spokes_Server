using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class WeeklyPlanRepository : JsonRepository<WeeklyPlan>
{
    public WeeklyPlanRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "WeeklyPlans"), "plan_*.json")
    {
    }

    protected override string GetFilePath(WeeklyPlan item) =>
        // Path: /Data/WeeklyPlans/{Year}/plan_{EmpId}_week_{Week}.json
        Path.Combine(_basePath, item.Year.ToString(), $"plan_{item.EmployeeId}_week_{item.WeekNumber}.json");

    public WeeklyPlan GetByEmployeeAndWeek(string employeeId, int year, int week) =>
        _cache.Values.FirstOrDefault(p =>
            p.EmployeeId == employeeId &&
            p.Year == year &&
            p.WeekNumber == week)
        ?? new WeeklyPlan
        {
            EmployeeId = employeeId,
            Year = year,
            WeekNumber = week
        };
}
