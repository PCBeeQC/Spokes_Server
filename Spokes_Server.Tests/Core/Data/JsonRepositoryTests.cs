using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Data;

public class TestEntity : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
}

public class TestEntityRepository : JsonRepository<TestEntity>
{
    public TestEntityRepository(DiskPersistenceService writer, string basePath)
        : base(writer, basePath, "*.json")
    {
    }

    protected override string GetFilePath(TestEntity item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public TestEntity? FindByNameDirectCache(string name) =>
        _cache.Values.FirstOrDefault(e => e.Name == name);

    public TestEntity? FindByNamePropertyCache(string name) =>
        Cache.Values.FirstOrDefault(e => e.Name == name);
}

public class JsonRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly TestEntityRepository _repo;

    public JsonRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_JsonRepo_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _repo = new TestEntityRepository(_writer, _testDataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try 
            { 
                Directory.Delete(_testDataDir, true); 
            } 
            catch (Exception ex) 
            { 
                System.Diagnostics.Debug.WriteLine($"Failed to delete test directory: {ex.Message}"); 
            }
        }
        _writer.Dispose();
    }

    [Fact]
    public void Save_AddsToCacheAndQueuesWrite()
    {
        var entity = new TestEntity { Id = "123", Name = "Test" };
        _repo.Save(entity);

        var items = _repo.GetAll();
        var item = Assert.Single(items);
        Assert.Equal("123", item.Id);

        var byId = _repo.GetById("123");
        Assert.NotNull(byId);
    }

    [Fact]
    public void Delete_RemovesFromCacheAndQueuesDelete()
    {
        var entity = new TestEntity { Id = "456", Name = "To Delete" };
        _repo.Save(entity);

        Assert.NotNull(_repo.GetById("456"));

        _repo.Delete("456");

        Assert.Null(_repo.GetById("456"));
        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void Delete_RaisesOnDeleted()
    {
        var entity = new TestEntity { Id = "del-1", Name = "Event Test" };
        _repo.Save(entity);

        TestEntity? deletedEntity = null;
        _repo.OnDeleted += e => deletedEntity = e;

        _repo.Delete("del-1");

        Assert.NotNull(deletedEntity);
        Assert.Equal("del-1", deletedEntity.Id);
        Assert.Equal("Event Test", deletedEntity.Name);
    }

    [Fact]
    public void Clear_EmptiesCache()
    {
        var entity = new TestEntity { Id = "789" };
        _repo.Save(entity);

        _repo.Clear();

        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void LoadFromDisk_LoadsFilesIntoCache()
    {
        var entity1 = new TestEntity { Id = "e1", Name = "File 1" };
        var entity2 = new TestEntity { Id = "e2", Name = "File 2" };

        File.WriteAllText(Path.Combine(_testDataDir, "e1.json"), JsonSerializer.Serialize(entity1));
        File.WriteAllText(Path.Combine(_testDataDir, "e2.json"), JsonSerializer.Serialize(entity2));

        _repo.LoadFromDisk();

        var items = _repo.GetAll();
        Assert.Equal(2, items.Count);
        Assert.NotNull(_repo.GetById("e1"));
        Assert.NotNull(_repo.GetById("e2"));
    }

    [Fact]
    public void LoadFromDisk_HandlesInvalidJsonGracefully()
    {
        var entity1 = new TestEntity { Id = "e1", Name = "File 1" };
        File.WriteAllText(Path.Combine(_testDataDir, "e1.json"), JsonSerializer.Serialize(entity1));
        File.WriteAllText(Path.Combine(_testDataDir, "bad.json"), "}");

        _repo.LoadFromDisk(); // Should not throw

        var items = _repo.GetAll();
        Assert.Single(items);
        Assert.NotNull(_repo.GetById("e1"));
    }

    [Fact]
    public void Save_TriggersOnSaved_Event()
    {
        var entity = new TestEntity { Id = "save-event", Name = "Event Test" };
        TestEntity? eventEntity = null;
        _repo.OnSaved += e => eventEntity = e;

        _repo.Save(entity);

        Assert.NotNull(eventEntity);
        Assert.Same(entity, eventEntity);
    }

    [Fact]
    public void LoadFromDisk_WhenDirectoryDoesNotExist_ReturnsGracefully()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_NonExistent_" + Guid.NewGuid());
        var repo = new TestEntityRepository(_writer, nonExistentDir);

        repo.LoadFromDisk(); // Should not throw

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Delete_WhenIdDoesNotExist_DoesNotThrowOrQueue()
    {
        var exception = Record.Exception(() => _repo.Delete("non-existent-id"));
        Assert.Null(exception);
        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void GetAll_WithoutExplicitLoadFromDisk_LazilyPreloadsFilesFromDisk()
    {
        var entity = new TestEntity { Id = "lazy1", Name = "Lazy Loaded Entity" };
        File.WriteAllText(Path.Combine(_testDataDir, "lazy1.json"), JsonSerializer.Serialize(entity));

        // Create a new repository instance pointing to the directory, without calling LoadFromDisk()
        var freshRepo = new TestEntityRepository(_writer, _testDataDir);

        // First read operation should lazily load the file
        var items = freshRepo.GetAll();
        var item = Assert.Single(items);
        Assert.Equal("lazy1", item.Id);
        Assert.Equal("Lazy Loaded Entity", item.Name);

        // GetById should also work directly
        var byId = freshRepo.GetById("lazy1");
        Assert.NotNull(byId);
        Assert.Equal("lazy1", byId.Id);
    }

    [Fact]
    public void Save_WithoutExplicitLoadFromDisk_PreservesExistingDiskFiles()
    {
        var existing = new TestEntity { Id = "existing1", Name = "Existing File" };
        File.WriteAllText(Path.Combine(_testDataDir, "existing1.json"), JsonSerializer.Serialize(existing));

        // Create a new repository without calling LoadFromDisk()
        var freshRepo = new TestEntityRepository(_writer, _testDataDir);

        // Save a new entity
        var newEntity = new TestEntity { Id = "new2", Name = "New Entity" };
        freshRepo.Save(newEntity);

        // Both existing and new should be in cache
        var all = freshRepo.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, e => e.Id == "existing1");
        Assert.Contains(all, e => e.Id == "new2");
    }

    [Fact]
    public void EnsureLoaded_CalledMultipleTimes_IsIdempotent()
    {
        var entity = new TestEntity { Id = "idem1", Name = "Idempotent Entity" };
        File.WriteAllText(Path.Combine(_testDataDir, "idem1.json"), JsonSerializer.Serialize(entity));

        var freshRepo = new TestEntityRepository(_writer, _testDataDir);
        freshRepo.EnsureLoaded();
        freshRepo.EnsureLoaded();

        var items = freshRepo.GetAll();
        Assert.Single(items);
        Assert.Equal("idem1", items[0].Id);
    }

    [Fact]
    public void CustomQueryOnDerivedRepository_UsingCacheField_LazilyLoadsFromDisk()
    {
        var entity = new TestEntity { Id = "query1", Name = "Special Name" };
        File.WriteAllText(Path.Combine(_testDataDir, "query1.json"), JsonSerializer.Serialize(entity));

        var freshRepo = new TestEntityRepository(_writer, _testDataDir);

        // Directly invoke custom query without calling GetAll, GetById, or LoadFromDisk
        var result = freshRepo.FindByNameDirectCache("Special Name");

        Assert.NotNull(result);
        Assert.Equal("query1", result.Id);
        Assert.Equal("Special Name", result.Name);
    }

    [Fact]
    public void CustomQueryOnDerivedRepository_UsingCacheProperty_LazilyLoadsFromDisk()
    {
        var entity = new TestEntity { Id = "query2", Name = "Another Special" };
        File.WriteAllText(Path.Combine(_testDataDir, "query2.json"), JsonSerializer.Serialize(entity));

        var freshRepo = new TestEntityRepository(_writer, _testDataDir);

        var result = freshRepo.FindByNamePropertyCache("Another Special");

        Assert.NotNull(result);
        Assert.Equal("query2", result.Id);
        Assert.Equal("Another Special", result.Name);
    }

    [Fact]
    public async Task ConcurrentAccess_DuringEnsureLoaded_NeverObservesPartialCache()
    {
        for (int i = 0; i < 20; i++)
        {
            var entity = new TestEntity { Id = $"concurrent_{i}", Name = $"Name_{i}" };
            File.WriteAllText(Path.Combine(_testDataDir, $"concurrent_{i}.json"), JsonSerializer.Serialize(entity));
        }

        var freshRepo = new TestEntityRepository(_writer, _testDataDir);
        var readCounts = new System.Collections.Concurrent.ConcurrentBag<int>();

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            var count = freshRepo.GetAll().Count;
            readCounts.Add(count);
        }));

        await Task.WhenAll(tasks);

        // Every single concurrent thread should observe exactly 20 items, never 0 or partial
        Assert.All(readCounts, c => Assert.Equal(20, c));
    }
}

