using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects;

public class WorkTypeRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly WorkTypeRepository _repository;

    public WorkTypeRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_WorkTypeRepo_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _repository = new WorkTypeRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void LoadFromDisk_NoWorkTypes_DoesNothing()
    {
        // Arrange
        // Disk is empty

        // Act
        _repository.LoadFromDisk();

        // Assert
        Assert.Empty(_repository.GetAll());
    }

    [Fact]
    public void LoadFromDisk_WorkTypeWithNoSubTasks_AddsGeneralSubTaskAndSaves()
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
        _repository.LoadFromDisk();

        // Assert
        var loaded = _repository.GetById(workType.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded.SubTasks);
        Assert.Equal("General", loaded.SubTasks[0].Name);
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
        _repository.LoadFromDisk();

        // Assert
        var loaded = _repository.GetById(workType.Id);
        Assert.NotNull(loaded);
        Assert.Single(loaded.SubTasks);
        Assert.Equal("Custom Task", loaded.SubTasks[0].Name);
    }
}
