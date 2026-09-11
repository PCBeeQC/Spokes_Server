using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class EmailFolderRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly EmailFolderRepository _repo;

        public EmailFolderRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_EmailFolders_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _repo = new EmailFolderRepository(_writer, mockConfig.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
            _writer.Dispose();
        }

        [Fact]
        public void Save_AddsToCache()
        {
            var folder = new EmailFolder { Id = "f1", EmployeeId = "emp1", Name = "Inbox", Path = "INBOX" };
            _repo.Save(folder);

            var folders = _repo.GetByEmployee("emp1");
            Assert.Single(folders);
            Assert.Equal("Inbox", folders.First().Name);
        }

        [Fact]
        public void LoadFromDisk_LoadsCorrectly()
        {
            // Set up directory structure: Data/Employees/{EmployeeId}/Email/Folders/{Id}.json
            var empDir = Path.Combine(_testDataDir, "Employees", "emp2", "Email", "Folders");
            Directory.CreateDirectory(empDir);

            var folder1 = new EmailFolder { Id = "f1", EmployeeId = "emp2", Name = "Sent" };
            var folder2 = new EmailFolder { Id = "f2", EmployeeId = "emp2", Name = "Trash" };

            File.WriteAllText(Path.Combine(empDir, "f1.json"), JsonSerializer.Serialize(folder1));
            File.WriteAllText(Path.Combine(empDir, "f2.json"), JsonSerializer.Serialize(folder2));

            // Also create some corrupted file to test the try-catch block
            File.WriteAllText(Path.Combine(empDir, "corrupted.json"), "{ invalid json");

            _repo.LoadFromDisk();

            var loaded = _repo.GetByEmployee("emp2");
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, f => f.Id == "f1");
            Assert.Contains(loaded, f => f.Id == "f2");
        }

        [Fact]
        public void LoadFromDisk_IgnoresMissingSubdirectories()
        {
            // Just the Employees folder, no Email/Folders inside
            var empDir = Path.Combine(_testDataDir, "Employees", "emp3");
            Directory.CreateDirectory(empDir);

            _repo.LoadFromDisk();

            var loaded = _repo.GetByEmployee("emp3");
            Assert.Empty(loaded);
        }
    }
}




