using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class AlbumRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly Mock<IConfiguration> _mockConfig;

        public AlbumRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Albums_" + Guid.NewGuid().ToString());

            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch { }
            }
            _writer.Dispose();
        }

        [Fact]
        public void LoadFromDisk_WithAsteriskPattern_LoadsAlbums()
        {
            // 1. Create first repo and write data
            var repo1 = new AlbumRepository(_writer, _mockConfig.Object);
            var album1 = new Album { Id = "album1", Title = "Test Album 1" };
            var album2 = new Album { Id = "album2", Title = "Test Album 2" };

            repo1.Save(album1);
            repo1.Save(album2);

            // Give the background writer a tiny bit of time to flush (DiskPersistenceService is synchronous in tests if we wait or we can just bypass it by saving directly, but let's just use it)
            System.Threading.Thread.Sleep(50); // DiskPersistenceService flushes quickly in test if we don't start the timer

            // Wait, DiskPersistenceService uses a background timer. In tests we might need to manually serialize if it doesn't write instantly.
            // Let's write the files manually to simulate existing data on disk for LoadFromDisk to read, or rely on repo1.Save if it's synchronous in tests.
            // Actually, looking at the repo base, Save() enqueues. Let's write directly to disk to test LoadFromDisk cleanly.
            
            var albumsDir = Path.Combine(_testDataDir, "Albums");
            Directory.CreateDirectory(albumsDir);
            File.WriteAllText(Path.Combine(albumsDir, "album3.json"), "{\"Id\":\"album3\",\"Title\":\"Loaded Album\"}");
            File.WriteAllText(Path.Combine(albumsDir, "album4.json"), "{\"Id\":\"album4\",\"Title\":\"Another Album\"}");

            // 2. Create second repo which will trigger LoadFromDisk
            var repo2 = new AlbumRepository(_writer, _mockConfig.Object);
            repo2.LoadFromDisk();
            
            // 3. Verify it loaded the manually written files
            var loadedAlbums = repo2.GetAll();
            Assert.True(loadedAlbums.Count >= 2, "Should load at least the manually written albums");
            Assert.Contains(loadedAlbums, a => a.Id == "album3");
            Assert.Contains(loadedAlbums, a => a.Id == "album4");
        }
    }
}
