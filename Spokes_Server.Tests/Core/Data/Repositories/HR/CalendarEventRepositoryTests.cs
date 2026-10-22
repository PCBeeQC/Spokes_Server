using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class CalendarEventRepositoryTests : RepositoryTestFixture<CalendarEventRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsEvent()
    {
        var evt = new CalendarEvent
        {
            Id = "evt1",
            EmployeeId = "emp123",
            Title = "Team Standup",
            Start = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 1, 9, 30, 0, DateTimeKind.Utc)
        };

        _repo.Save(evt);

        var found = _repo.GetById("evt1");
        Assert.NotNull(found);
        Assert.Equal("Team Standup", found.Title);
        Assert.Equal("emp123", found.EmployeeId);
    }

    [Fact]
    public void GetFilePath_PartitionsByStartYear()
    {
        var evt = new CalendarEvent
        {
            Id = "evt-path",
            EmployeeId = "emp456",
            Start = new DateTime(2025, 11, 15, 10, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2025, 11, 15, 11, 0, 0, DateTimeKind.Utc)
        };

        _repo.Save(evt);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "CalendarEvents", "2025", "event_emp456_evt-path.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetByEmployeeAndDateRange_FiltersCorrectly()
    {
        var empId = "emp-filter";

        // Event 1: Inside range (June 10 9am - 10am)
        var ev1 = new CalendarEvent
        {
            Id = "ev1",
            EmployeeId = empId,
            Title = "Inside Range",
            Start = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc)
        };

        // Event 2: Overlaps start of range (June 10 7am - 8:30am, range starts 8:00am)
        var ev2 = new CalendarEvent
        {
            Id = "ev2",
            EmployeeId = empId,
            Title = "Overlap Start",
            Start = new DateTime(2026, 6, 10, 7, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 10, 8, 30, 0, DateTimeKind.Utc)
        };

        // Event 3: Ends before range (June 10 6am - 7:59am)
        var ev3 = new CalendarEvent
        {
            Id = "ev3",
            EmployeeId = empId,
            Title = "Before Range",
            Start = new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 10, 7, 59, 0, DateTimeKind.Utc)
        };

        // Event 4: Starts after range (June 10 18:00 - 19:00, range ends 17:00)
        var ev4 = new CalendarEvent
        {
            Id = "ev4",
            EmployeeId = empId,
            Title = "After Range",
            Start = new DateTime(2026, 6, 10, 18, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 10, 19, 0, 0, DateTimeKind.Utc)
        };

        // Event 5: Inside range but different employee
        var ev5 = new CalendarEvent
        {
            Id = "ev5",
            EmployeeId = "other-emp",
            Title = "Other Employee",
            Start = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc)
        };

        _repo.Save(ev1);
        _repo.Save(ev2);
        _repo.Save(ev3);
        _repo.Save(ev4);
        _repo.Save(ev5);

        var rangeStart = new DateTime(2026, 6, 10, 8, 0, 0, DateTimeKind.Utc);
        var rangeEnd = new DateTime(2026, 6, 10, 17, 0, 0, DateTimeKind.Utc);

        var results = _repo.GetByEmployeeAndDateRange(empId, rangeStart, rangeEnd).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.Id == "ev1");
        Assert.Contains(results, e => e.Id == "ev2");
        Assert.DoesNotContain(results, e => e.Id == "ev3");
        Assert.DoesNotContain(results, e => e.Id == "ev4");
        Assert.DoesNotContain(results, e => e.Id == "ev5");
    }

    [Fact]
    public void Delete_RemovesEventFromCacheAndDisk()
    {
        var evt = new CalendarEvent
        {
            Id = "evt-del",
            EmployeeId = "emp-del",
            Start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc)
        };

        _repo.Save(evt);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "CalendarEvents", "2026", "event_emp-del_evt-del.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("evt-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("evt-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsEventsFromYearSubdirectories()
    {
        var yearDir = Path.Combine(_testDataDir, "CalendarEvents", "2026");
        Directory.CreateDirectory(yearDir);

        var evt = new CalendarEvent
        {
            Id = "disk-evt",
            EmployeeId = "emp-loaded",
            Title = "Loaded Event",
            Start = new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 3, 15, 15, 0, 0, DateTimeKind.Utc)
        };

        var filePath = Path.Combine(yearDir, "event_emp-loaded_disk-evt.json");
        File.WriteAllText(filePath, System.Text.Json.JsonSerializer.Serialize(evt));

        var newRepo = new CalendarEventRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("disk-evt");
        Assert.NotNull(loaded);
        Assert.Equal("Loaded Event", loaded.Title);
        Assert.Equal("emp-loaded", loaded.EmployeeId);
    }
}
