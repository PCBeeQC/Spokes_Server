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
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class EmailMessageRepositoryTests : TestDataTestBase
    {
        private readonly Mock<ILogger<DiskPersistenceService>> _mockLogger;
        private readonly DiskPersistenceService _persistenceService;
        private readonly Mock<IConfiguration> _mockConfig;
        private readonly EmailMessageRepository _repository;

        public EmailMessageRepositoryTests()
        {
            _mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _persistenceService = new DiskPersistenceService(_mockLogger.Object);

            _mockConfig = new Mock<IConfiguration>();
            _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

            _repository = new EmailMessageRepository(_persistenceService, _mockConfig.Object);
        }

        [Fact]
        public void LoadFromDisk_CreatesEmptyState_WhenNoDirectoriesExist()
        {
            _repository.LoadFromDisk();
            Assert.Equal(0, _repository.GetLocalMessageCount("emp1", "Inbox"));
        }

        [Fact]
        public void Save_AddsItemToCacheAndQueuesWrite()
        {
            var msg = new EmailMessage
            {
                EmployeeId = "emp1",
                FolderPath = "Inbox",
                UniqueId = 123,
                Id = "msg1",
                IsRead = false
            };

            _repository.Save(msg);

            Assert.True(_repository.Exists("emp1", "Inbox", 123));
            Assert.Equal(1, _repository.GetUnreadCount("emp1", "Inbox"));
            Assert.Equal(1, _repository.GetLocalMessageCount("emp1", "Inbox"));
        }

        [Fact]
        public void Delete_RemovesItemFromCacheAndIndex()
        {
            var msg = new EmailMessage
            {
                EmployeeId = "emp2",
                FolderPath = "Sent",
                UniqueId = 456,
                Id = "msg2"
            };

            _repository.Save(msg);
            Assert.True(_repository.Exists("emp2", "Sent", 456));

            _repository.Delete("msg2", "emp2");

            Assert.False(_repository.Exists("emp2", "Sent", 456));
        }

        [Fact]
        public void TryClaimUid_ReturnsTrue_WhenUidIsAvailable()
        {
            var result = _repository.TryClaimUid("emp3", "Drafts", 789, "msg3");
            Assert.True(result);
            Assert.True(_repository.Exists("emp3", "Drafts", 789));

            // Claiming same UID should fail
            var result2 = _repository.TryClaimUid("emp3", "Drafts", 789, "msg4");
            Assert.False(result2);
        }

        [Fact]
        public void TryUpdateFlags_UpdatesFlagsAndSaves()
        {
            var msg = new EmailMessage
            {
                EmployeeId = "emp1",
                FolderPath = "Inbox",
                UniqueId = 123,
                Id = "msg1",
                IsRead = false,
                IsFlagged = false
            };

            _repository.Save(msg);

            var updated = _repository.TryUpdateFlags("emp1", "Inbox", 123, true, true);

            Assert.True(updated);
            var retrieved = _repository.GetByUniqueId("emp1", "Inbox", 123);
            Assert.NotNull(retrieved);
            Assert.True(retrieved.IsRead);
            Assert.True(retrieved.IsFlagged);
        }

        [Fact]
        public async Task SearchAsync_WithMatches_ReturnsResults()
        {
            var msg1 = new EmailMessage { EmployeeId = "emp1", FolderPath = "Inbox", UniqueId = 1, Id = "m1", Subject = "Hello World" };
            var msg2 = new EmailMessage { EmployeeId = "emp1", FolderPath = "Inbox", UniqueId = 2, Id = "m2", Subject = "Goodbye" };

            _repository.Save(msg1);
            _repository.Save(msg2);

            var results = await _repository.SearchAsync("emp1", "hello", _testDataPath);
            Assert.Single(results);
            Assert.Equal("m1", results[0].Id);
        }
    }
}


