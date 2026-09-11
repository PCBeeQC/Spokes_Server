using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using System.IO;
using System.Linq;
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

        protected override string GetFilePath(TestEntity item)
        {
            return Path.Combine(_basePath, $"{item.Id}.json");
        }
    }

    public class JsonRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly TestEntityRepository _repo;

        public JsonRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_JsonRepo_" + Guid.NewGuid().ToString());
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
            Assert.Single(items);
            Assert.Equal("123", items.First().Id);

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
    }
}


