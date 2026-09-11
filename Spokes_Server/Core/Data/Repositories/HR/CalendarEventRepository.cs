using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.Extensions.Configuration;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class CalendarEventRepository : JsonRepository<CalendarEvent>
{
    private readonly string _rootDataPath;

    public CalendarEventRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "CalendarEvents"), "event_*.json")
    {
        _rootDataPath = Path.Combine(config["DataPath"] ?? "Data", "CalendarEvents");
    }

    protected override string GetFilePath(CalendarEvent item)
    {
        // Path: /Data/CalendarEvents/{Year}/event_{EmpId}_{Id}.json
        // Organize by Year of Start date to keep folders manageable
        return Path.Combine(_rootDataPath, item.Start.Year.ToString(), $"event_{item.EmployeeId}_{item.Id}.json");
    }

    public IEnumerable<CalendarEvent> GetByEmployeeAndDateRange(string employeeId, DateTime start, DateTime end)
    {
        // Simple scan for now, since we load all into memory (JsonRepository pattern in this app seems to load all or rely on _cache)
        // The base JsonRepository seems to have a _cache protected field based on WeeklyPlanRepository usage.

        return _cache.Values.Where(e =>
            e.EmployeeId == employeeId &&
            e.Start < end && e.End > start);
    }
}


