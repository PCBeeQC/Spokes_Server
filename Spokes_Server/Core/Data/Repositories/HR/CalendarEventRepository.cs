using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class CalendarEventRepository : JsonRepository<CalendarEvent>
{
    public CalendarEventRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "CalendarEvents"), "event_*.json")
    {
    }

    protected override string GetFilePath(CalendarEvent item) =>
        // Path: /Data/CalendarEvents/{Year}/event_{EmpId}_{Id}.json
        Path.Combine(_basePath, item.Start.Year.ToString(), $"event_{item.EmployeeId}_{item.Id}.json");

    public IEnumerable<CalendarEvent> GetByEmployeeAndDateRange(string employeeId, DateTime start, DateTime end) =>
        _cache.Values.Where(e => e.EmployeeId == employeeId && e.Start < end && e.End > start);
}
