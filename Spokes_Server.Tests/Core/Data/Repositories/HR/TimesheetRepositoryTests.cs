using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class TimesheetRepositoryTests : RepositoryTestFixture<TimesheetRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsTimesheet()
    {
        var sheet = new Timesheet
        {
            Id = "ts-1",
            EmployeeId = "emp-101",
            EmployeeName = "Jane Doe",
            Year = 2026,
            WeekNumber = 20,
            Status = "Submitted"
        };

        _repo.Save(sheet);

        var found = _repo.GetById("ts-1");
        Assert.NotNull(found);
        Assert.Equal("Jane Doe", found.EmployeeName);
        Assert.Equal("Submitted", found.Status);
    }

    [Fact]
    public void GetFilePath_SavesInNestedEmployeeTimesheetsFolder()
    {
        var sheet = new Timesheet
        {
            Id = "ts-path",
            EmployeeId = "emp-202",
            Year = 2026,
            WeekNumber = 15
        };

        _repo.Save(sheet);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp-202", "Timesheets", "2026", "week_15.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetByWeek_WhenExists_ReturnsCachedTimesheet()
    {
        var sheet = new Timesheet
        {
            Id = "ts-cached",
            EmployeeId = "emp-303",
            Year = 2026,
            WeekNumber = 12,
            Status = "Approved"
        };

        _repo.Save(sheet);

        var result = _repo.GetByWeek("emp-303", 2026, 12);
        Assert.NotNull(result);
        Assert.Equal("ts-cached", result.Id);
        Assert.Equal("Approved", result.Status);
    }

    [Fact]
    public void GetByWeek_WhenNotExists_ReturnsNull()
    {
        var result = _repo.GetByWeek("emp-404", 2026, 40);

        Assert.Null(result);
    }

    [Fact]
    public void GetOrStubByWeek_WhenNotExists_ReturnsNewDraftTimesheet()
    {
        var result = _repo.GetOrStubByWeek("emp-404", 2026, 40);

        Assert.NotNull(result);
        Assert.Equal("emp-404", result.EmployeeId);
        Assert.Equal(2026, result.Year);
        Assert.Equal(40, result.WeekNumber);
        Assert.Equal("Draft", result.Status);
    }

    [Fact]
    public void Delete_RemovesTimesheetFromCacheAndDisk()
    {
        var sheet = new Timesheet
        {
            Id = "ts-del",
            EmployeeId = "emp-505",
            Year = 2026,
            WeekNumber = 8
        };

        _repo.Save(sheet);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "Employees", "emp-505", "Timesheets", "2026", "week_8.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("ts-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("ts-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsNestedWeekFiles()
    {
        var targetDir = Path.Combine(_testDataDir, "Employees", "emp-load", "Timesheets", "2026");
        Directory.CreateDirectory(targetDir);

        var sheet = new Timesheet
        {
            Id = "ts-loaded",
            EmployeeId = "emp-load",
            Year = 2026,
            WeekNumber = 5,
            Status = "Approved"
        };

        File.WriteAllText(Path.Combine(targetDir, "week_5.json"), System.Text.Json.JsonSerializer.Serialize(sheet));

        var newRepo = new TimesheetRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("ts-loaded");
        Assert.NotNull(loaded);
        Assert.Equal("Approved", loaded.Status);
    }
}
