using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

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

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new EmailMessageRepository(_persistenceService, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void Delete_WithSingleArgument_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => _repository.Delete("any-id"));
    }

    [Fact]
    public void RenameFolder_UpdatesMessageFolderPathsAndIndex()
    {
        var msg = new EmailMessage
        {
            Id = "rename-msg",
            EmployeeId = "emp-rename",
            FolderPath = "OldFolder",
            UniqueId = 101,
            Subject = "Folder rename test"
        };
        _repository.Save(msg);

        Assert.Equal(1, _repository.GetLocalMessageCount("emp-rename", "OldFolder"));

        _repository.RenameFolder("emp-rename", "OldFolder", "NewFolder");

        Assert.Equal(0, _repository.GetLocalMessageCount("emp-rename", "OldFolder"));
        Assert.Equal(1, _repository.GetLocalMessageCount("emp-rename", "NewFolder"));

        var moved = _repository.GetByFolder("emp-rename", "NewFolder");
        Assert.Single(moved);
        Assert.Equal("NewFolder", moved[0].FolderPath);
    }

    [Fact]
    public void GetAllByGlobalMessageId_ReturnsMatchingMessages()
    {
        var msg1 = new EmailMessage
        {
            Id = "g-msg1",
            EmployeeId = "emp-global",
            FolderPath = "Inbox",
            UniqueId = 201,
            GlobalMessageId = "GLOBAL-XYZ"
        };
        var msg2 = new EmailMessage
        {
            Id = "g-msg2",
            EmployeeId = "emp-global",
            FolderPath = "Archive",
            UniqueId = 202,
            GlobalMessageId = "GLOBAL-XYZ"
        };
        _repository.Save(msg1);
        _repository.Save(msg2);

        var matches = _repository.GetAllByGlobalMessageId("emp-global", "GLOBAL-XYZ");
        Assert.Equal(2, matches.Count);

        var empty = _repository.GetAllByGlobalMessageId("emp-global", string.Empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void GetByFolder_And_GetUidsByFolder_ReturnsMessages()
    {
        var msg1 = new EmailMessage { Id = "u1", EmployeeId = "emp-uids", FolderPath = "FolderA", UniqueId = 10 };
        var msg2 = new EmailMessage { Id = "u2", EmployeeId = "emp-uids", FolderPath = "FolderA", UniqueId = 20 };
        _repository.Save(msg1);
        _repository.Save(msg2);

        var uids = _repository.GetUidsByFolder("emp-uids", "FolderA");
        Assert.Equal(2, uids.Count);
        Assert.Contains((uint)10, uids);
        Assert.Contains((uint)20, uids);

        var msgs = _repository.GetByFolder("emp-uids", "FolderA");
        Assert.Equal(2, msgs.Count);

        var nonExistent = _repository.GetByFolder("emp-uids", "NonExistentFolder");
        Assert.Empty(nonExistent);
    }

    [Fact]
    public void GetByFolderRecent_And_GetByFolderBeforeDate_ReturnsOrderedMessages()
    {
        var date = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var m1 = new EmailMessage { Id = "m-old", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 1, Date = date.AddHours(-5) };
        var m2 = new EmailMessage { Id = "m-mid", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 2, Date = date };
        var m3 = new EmailMessage { Id = "m-new", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 3, Date = date.AddHours(5) };

        _repository.Save(m1);
        _repository.Save(m2);
        _repository.Save(m3);

        var recent = _repository.GetByFolderRecent("emp-dates", "Inbox", 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("m-new", recent[0].Id);
        Assert.Equal("m-mid", recent[1].Id);

        var before = _repository.GetByFolderBeforeDate("emp-dates", "Inbox", date, 10);
        Assert.Single(before);
        Assert.Equal("m-old", before[0].Id);
    }

    [Fact]
    public void GetIdByUid_ReturnsCorrectMessageId()
    {
        var msg = new EmailMessage { Id = "target-id", EmployeeId = "emp-uid-find", FolderPath = "Inbox", UniqueId = 999 };
        _repository.Save(msg);

        var foundId = _repository.GetIdByUid("emp-uid-find", "Inbox", 999);
        Assert.Equal("target-id", foundId);

        var notFound = _repository.GetIdByUid("emp-uid-find", "Inbox", 888);
        Assert.Null(notFound);
    }

    [Fact]
    public void HasMoreMessages_ReturnsTrueWhenOver50()
    {
        for (uint i = 1; i <= 51; i++)
        {
            _repository.TryClaimUid("emp-many", "BulkFolder", i, $"bulk-{i}");
        }

        Assert.True(_repository.HasMoreMessages("emp-many", "BulkFolder"));
        Assert.False(_repository.HasMoreMessages("emp-many", "EmptyFolder"));
    }

    [Fact]
    public void TryUpdateFlags_ReturnsFalseWhenFlagsUnchangedOrNotFound()
    {
        var msg = new EmailMessage { Id = "flag-msg", EmployeeId = "emp-flags", FolderPath = "Inbox", UniqueId = 333, IsRead = true, IsFlagged = false };
        _repository.Save(msg);

        // Attempting to set identical flags should return false
        var unchanged = _repository.TryUpdateFlags("emp-flags", "Inbox", 333, isRead: true, isFlagged: false);
        Assert.False(unchanged);

        // Non-existent UID should return false
        var notFound = _repository.TryUpdateFlags("emp-flags", "Inbox", 444, isRead: true, isFlagged: true);
        Assert.False(notFound);
    }
}


