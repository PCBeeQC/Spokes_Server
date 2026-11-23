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
