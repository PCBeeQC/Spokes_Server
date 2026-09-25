using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Data
{
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
        public async Task SaveAsync_AddsToCache_QueuesWrite_AndTriggersOnSaved()
        {
            var entity = new TestEntity { Id = "async-1", Name = "Async Test" };
            TestEntity? eventEntity = null;
            _repo.OnSaved += e => eventEntity = e;

            await _repo.SaveAsync(entity);

            var items = await _repo.GetAllAsync();
            var item = Assert.Single(items);
            Assert.Equal("async-1", item.Id);

            var byId = await _repo.GetByIdAsync("async-1");
            Assert.NotNull(byId);
            Assert.Equal("Async Test", byId.Name);

            Assert.NotNull(eventEntity);
            Assert.Same(entity, eventEntity);
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
        public async Task DeleteAsync_RemovesFromCacheAndQueuesDelete()
        {
            var entity = new TestEntity { Id = "async-del", Name = "Async Delete" };
            await _repo.SaveAsync(entity);

            Assert.NotNull(await _repo.GetByIdAsync("async-del"));

            await _repo.DeleteAsync("async-del");

            Assert.Null(await _repo.GetByIdAsync("async-del"));
            Assert.Empty(await _repo.GetAllAsync());
        }

        [Fact]
        public async Task GetAllAsync_ReturnsAllCachedEntities()
        {
            var entity1 = new TestEntity { Id = "g1", Name = "Entity 1" };
            var entity2 = new TestEntity { Id = "g2", Name = "Entity 2" };

            await _repo.SaveAsync(entity1);
            await _repo.SaveAsync(entity2);

            var all = await _repo.GetAllAsync();

            Assert.Equal(2, all.Count);
            Assert.Contains(all, e => e.Id == "g1");
            Assert.Contains(all, e => e.Id == "g2");
        }

        [Fact]
        public async Task GetByIdAsync_ReturnsEntityWhenPresent_AndNullWhenMissing()
        {
            var entity = new TestEntity { Id = "present-id", Name = "Present" };
            await _repo.SaveAsync(entity);

            var present = await _repo.GetByIdAsync("present-id");
            var missing = await _repo.GetByIdAsync("missing-id");

            Assert.NotNull(present);
            Assert.Equal("present-id", present.Id);
            Assert.Null(missing);
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
        public async Task DeleteAsync_WhenIdDoesNotExist_DoesNotThrowOrQueue()
        {
            var exception = await Record.ExceptionAsync(() => _repo.DeleteAsync("non-existent-id"));
            Assert.Null(exception);
            Assert.Empty(await _repo.GetAllAsync());
        }
    }
}
