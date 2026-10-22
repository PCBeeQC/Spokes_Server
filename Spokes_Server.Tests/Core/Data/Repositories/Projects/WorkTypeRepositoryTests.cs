using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class WorkTypeRepositoryTests : RepositoryTestFixture<WorkTypeRepository>
{
    [Fact]
    public void LoadFromDisk_NoWorkTypes_DoesNothing()
    {
        // Arrange
        // Disk is empty

        // Act
        _repo.LoadFromDisk();

        // Assert
        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void LoadFromDisk_WorkTypeWithNoSubTasks_LoadsWithoutModifying()
    {
        // Arrange
        var workType = new WorkType { Id = Guid.NewGuid().ToString(), Name = "Test WorkType" };
        workType.SubTasks.Clear(); // Ensure empty
        
        // Synchronously write to disk to bypass DiskPersistenceService queue latency
        string workTypesDir = Path.Combine(_testDataDir, "Settings", "WorkTypes");
        Directory.CreateDirectory(workTypesDir);
        string filePath = Path.Combine(workTypesDir, $"{workType.Id}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(workType));

        // Act
        _repo.LoadFromDisk();

        // Assert
        var loaded = _repo.GetById(workType.Id);
        Assert.NotNull(loaded);
        Assert.Empty(loaded.SubTasks);
    }

    [Fact]
    public void LoadFromDisk_WorkTypeWithExistingSubTasks_DoesNotModify()
    {
        // Arrange
        var workType = new WorkType { Id = Guid.NewGuid().ToString(), Name = "Test WorkType" };
        workType.SubTasks.Add(new SubTask { Name = "Custom Task" });
        
        // Synchronously write to disk
        string workTypesDir = Path.Combine(_testDataDir, "Settings", "WorkTypes");
        Directory.CreateDirectory(workTypesDir);
        string filePath = Path.Combine(workTypesDir, $"{workType.Id}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(workType));

        // Act
        _repo.LoadFromDisk();

        // Assert
        var loaded = _repo.GetById(workType.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded.SubTasks);
        Assert.Equal("Custom Task", loaded.SubTasks[0].Name);
    }

    [Fact]
    public async Task WorkTypeSubTaskMigrationStep_WorkTypeWithNoSubTasks_AddsGeneralSubTask()
    {
        // Arrange
        var workType = new WorkType { Id = Guid.NewGuid().ToString(), Name = "Migrated WorkType" };
        workType.SubTasks.Clear();
        _repo.Save(workType);

        var step = new WorkTypeSubTaskMigrationStep(_repo);
        Assert.Equal("20260106_WorkTypeDefaultSubTask", step.MigrationKey);
        Assert.Equal(60, step.Order);

        // Act
        await step.ExecuteAsync();

        // Assert
        var updated = _repo.GetById(workType.Id);
        Assert.NotNull(updated);
        Assert.Single(updated.SubTasks);
        Assert.Equal("General", updated.SubTasks[0].Name);
    }
}
