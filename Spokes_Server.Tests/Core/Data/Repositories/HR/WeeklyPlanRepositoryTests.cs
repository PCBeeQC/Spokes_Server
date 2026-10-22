using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.HR;

public class WeeklyPlanRepositoryTests : RepositoryTestFixture<WeeklyPlanRepository>
{
    [Fact]
    public void Save_And_GetById_ReturnsWeeklyPlan()
    {
        var plan = new WeeklyPlan
        {
            Id = "plan-1",
            EmployeeId = "emp-1",
            EmployeeName = "Alice",
            Year = 2026,
            WeekNumber = 10
        };

        _repo.Save(plan);

        var found = _repo.GetById("plan-1");
        Assert.NotNull(found);
        Assert.Equal("Alice", found.EmployeeName);
        Assert.Equal(10, found.WeekNumber);
    }

    [Fact]
    public void GetFilePath_SavesInWeeklyPlansYearFolder()
    {
        var plan = new WeeklyPlan
        {
            Id = "plan-path",
            EmployeeId = "emp-2",
            Year = 2026,
            WeekNumber = 18
        };

        _repo.Save(plan);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "WeeklyPlans", "2026", "plan_emp-2_week_18.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void GetByEmployeeAndWeek_WhenExists_ReturnsCachedPlan()
    {
        var plan = new WeeklyPlan
        {
            Id = "plan-cached",
            EmployeeId = "emp-3",
            Year = 2026,
            WeekNumber = 22,
            Entries = { new PlanEntry { ProjectId = "p1", Hours = 8.5m } }
        };

        _repo.Save(plan);

        var result = _repo.GetByEmployeeAndWeek("emp-3", 2026, 22);
        Assert.NotNull(result);
        Assert.Equal("plan-cached", result.Id);
        Assert.Single(result.Entries);
        Assert.Equal(8.5m, result.TotalHours);
    }

    [Fact]
    public void GetByEmployeeAndWeek_WhenNotExists_ReturnsNull()
    {
        var result = _repo.GetByEmployeeAndWeek("emp-99", 2026, 30);

        Assert.Null(result);
    }

    [Fact]
    public void GetOrStubByEmployeeAndWeek_WhenNotExists_ReturnsNewPlan()
    {
        var result = _repo.GetOrStubByEmployeeAndWeek("emp-99", 2026, 30);

        Assert.NotNull(result);
        Assert.Equal("emp-99", result.EmployeeId);
        Assert.Equal(2026, result.Year);
        Assert.Equal(30, result.WeekNumber);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Delete_RemovesPlanFromCacheAndDisk()
    {
        var plan = new WeeklyPlan
        {
            Id = "plan-del",
            EmployeeId = "emp-4",
            Year = 2026,
            WeekNumber = 12
        };

        _repo.Save(plan);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "WeeklyPlans", "2026", "plan_emp-4_week_12.json");
        Assert.True(File.Exists(expectedPath));

        _repo.Delete("plan-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("plan-del"));
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public void LoadFromDisk_LoadsWeeklyPlansFromDisk()
    {
        var targetDir = Path.Combine(_testDataDir, "WeeklyPlans", "2026");
        Directory.CreateDirectory(targetDir);

        var plan = new WeeklyPlan
        {
            Id = "plan-loaded",
            EmployeeId = "emp-disk",
            Year = 2026,
            WeekNumber = 25
        };

        File.WriteAllText(Path.Combine(targetDir, "plan_emp-disk_week_25.json"), System.Text.Json.JsonSerializer.Serialize(plan));

        var newRepo = new WeeklyPlanRepository(_writer, Config);
        newRepo.LoadFromDisk();

        var loaded = newRepo.GetById("plan-loaded");
        Assert.NotNull(loaded);
        Assert.Equal("emp-disk", loaded.EmployeeId);
    }
}
