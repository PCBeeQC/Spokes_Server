using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class EmailMessageRepositoryTests : RepositoryTestFixture<EmailMessageRepository>
{
    [Fact]
    public void LoadFromDisk_CreatesEmptyState_WhenNoDirectoriesExist()
    {
        _repo.LoadFromDisk();
        Assert.Equal(0, _repo.GetLocalMessageCount("emp1", "Inbox"));
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

        _repo.Save(msg);

        Assert.True(_repo.Exists("emp1", "Inbox", 123));
        Assert.Equal(1, _repo.GetUnreadCount("emp1", "Inbox"));
        Assert.Equal(1, _repo.GetLocalMessageCount("emp1", "Inbox"));
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

        _repo.Save(msg);
        Assert.True(_repo.Exists("emp2", "Sent", 456));

        _repo.Delete("emp2", "msg2");

        Assert.False(_repo.Exists("emp2", "Sent", 456));
    }

    [Fact]
    public void TryClaimUid_ReturnsTrue_WhenUidIsAvailable()
    {
        var result = _repo.TryClaimUid("emp3", "Drafts", 789, "msg3");
        Assert.True(result);
        Assert.True(_repo.Exists("emp3", "Drafts", 789));

        // Claiming same UID should fail
        var result2 = _repo.TryClaimUid("emp3", "Drafts", 789, "msg4");
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

        _repo.Save(msg);

        var updated = _repo.TryUpdateFlags("emp1", "Inbox", 123, true, true);

        Assert.True(updated);
        var retrieved = _repo.GetByUid("emp1", "Inbox", 123);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsRead);
        Assert.True(retrieved.IsFlagged);
    }

    [Fact]
    public async Task SearchAsync_WithMatches_ReturnsResults()
    {
        var msg1 = new EmailMessage { EmployeeId = "emp1", FolderPath = "Inbox", UniqueId = 1, Id = "m1", Subject = "Hello World" };
        var msg2 = new EmailMessage { EmployeeId = "emp1", FolderPath = "Inbox", UniqueId = 2, Id = "m2", Subject = "Goodbye" };

        _repo.Save(msg1);
        _repo.Save(msg2);

        var results = await _repo.SearchAsync("emp1", "hello");
        Assert.Single(results);
        Assert.Equal("m1", results[0].Id);

        var streamingResults = new List<EmailMessage>();
        await foreach (var msg in _repo.SearchStreamingAsync("emp1", "hello"))
        {
            streamingResults.Add(msg);
        }
        Assert.Single(streamingResults);
        Assert.Equal("m1", streamingResults[0].Id);
    }

    [Fact]
    public void Delete_WithSingleArgument_ResolvesFromCacheOrIndexAndDeletes()
    {
        var msg = new EmailMessage
        {
            Id = "single-del",
            EmployeeId = "emp-del",
            FolderPath = "Inbox",
            UniqueId = 999,
            Subject = "Test single arg delete"
        };
        _repo.Save(msg);
        _writer.FlushAll();

        Assert.NotNull(_repo.GetById("emp-del", "single-del"));

        // Single-argument Delete should resolve employeeId from cache or index and delete
        _repo.Delete("single-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("emp-del", "single-del"));
        Assert.Null(_repo.GetById("single-del"));
        Assert.False(_repo.Exists("emp-del", "Inbox", 999));
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
        _repo.Save(msg);

        Assert.Equal(1, _repo.GetLocalMessageCount("emp-rename", "OldFolder"));

        _repo.RenameFolder("emp-rename", "OldFolder", "NewFolder");

        Assert.Equal(0, _repo.GetLocalMessageCount("emp-rename", "OldFolder"));
        Assert.Equal(1, _repo.GetLocalMessageCount("emp-rename", "NewFolder"));

        var moved = _repo.GetByFolder("emp-rename", "NewFolder");
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
        _repo.Save(msg1);
        _repo.Save(msg2);

        var matches = _repo.GetAllByGlobalMessageId("emp-global", "GLOBAL-XYZ");
        Assert.Equal(2, matches.Count);

        var empty = _repo.GetAllByGlobalMessageId("emp-global", string.Empty);
        Assert.Empty(empty);
    }

    [Fact]
    public void GetByFolder_And_GetUidsByFolder_ReturnsMessages()
    {
        var msg1 = new EmailMessage { Id = "u1", EmployeeId = "emp-uids", FolderPath = "FolderA", UniqueId = 10 };
        var msg2 = new EmailMessage { Id = "u2", EmployeeId = "emp-uids", FolderPath = "FolderA", UniqueId = 20 };
        _repo.Save(msg1);
        _repo.Save(msg2);

        var uids = _repo.GetUidsByFolder("emp-uids", "FolderA");
        Assert.Equal(2, uids.Count);
        Assert.Contains((uint)10, uids);
        Assert.Contains((uint)20, uids);

        var msgs = _repo.GetByFolder("emp-uids", "FolderA");
        Assert.Equal(2, msgs.Count);

        var nonExistent = _repo.GetByFolder("emp-uids", "NonExistentFolder");
        Assert.Empty(nonExistent);
    }

    [Fact]
    public void GetByFolderRecent_And_GetByFolderBeforeDate_ReturnsOrderedMessages()
    {
        var date = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var m1 = new EmailMessage { Id = "m-old", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 1, Date = date.AddHours(-5) };
        var m2 = new EmailMessage { Id = "m-mid", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 2, Date = date };
        var m3 = new EmailMessage { Id = "m-new", EmployeeId = "emp-dates", FolderPath = "Inbox", UniqueId = 3, Date = date.AddHours(5) };

        _repo.Save(m1);
        _repo.Save(m2);
        _repo.Save(m3);

        var recent = _repo.GetByFolderRecent("emp-dates", "Inbox", 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("m-new", recent[0].Id);
        Assert.Equal("m-mid", recent[1].Id);

        var before = _repo.GetByFolderBeforeDate("emp-dates", "Inbox", date, 10);
        Assert.Single(before);
        Assert.Equal("m-old", before[0].Id);
    }

    [Fact]
    public void GetIdByUid_ReturnsCorrectMessageId()
    {
        var msg = new EmailMessage { Id = "target-id", EmployeeId = "emp-uid-find", FolderPath = "Inbox", UniqueId = 999 };
        _repo.Save(msg);

        var foundId = _repo.GetIdByUid("emp-uid-find", "Inbox", 999);
        Assert.Equal("target-id", foundId);

        var notFound = _repo.GetIdByUid("emp-uid-find", "Inbox", 888);
        Assert.Null(notFound);
    }

    [Fact]
    public void HasMoreMessages_ReturnsTrueWhenOver50()
    {
        for (uint i = 1; i <= 51; i++)
        {
            _repo.TryClaimUid("emp-many", "BulkFolder", i, $"bulk-{i}");
        }

        Assert.True(_repo.HasMoreMessages("emp-many", "BulkFolder"));
        Assert.False(_repo.HasMoreMessages("emp-many", "EmptyFolder"));
    }

    [Fact]
    public void TryUpdateFlags_ReturnsFalseWhenFlagsUnchangedOrNotFound()
    {
        var msg = new EmailMessage { Id = "flag-msg", EmployeeId = "emp-flags", FolderPath = "Inbox", UniqueId = 333, IsRead = true, IsFlagged = false };
        _repo.Save(msg);

        // Attempting to set identical flags should return false
        var unchanged = _repo.TryUpdateFlags("emp-flags", "Inbox", 333, isRead: true, isFlagged: false);
        Assert.False(unchanged);

        // Non-existent UID should return false
        var notFound = _repo.TryUpdateFlags("emp-flags", "Inbox", 444, isRead: true, isFlagged: true);
        Assert.False(notFound);
    }

    [Fact]
    public void GetById_WithSingleArgument_WhenNotInCache_LoadsFromDiskUsingIndex()
    {
        var msg = new EmailMessage
        {
            Id = "uncached-msg",
            EmployeeId = "emp-uncached",
            FolderPath = "Inbox",
            UniqueId = 555,
            Subject = "Uncached load test"
        };
        _repo.Save(msg);
        _writer.FlushAll();

        // Clear cache and reload from disk to build index without cache
        _repo.Clear();
        _repo.LoadFromDisk();

        // GetById without employeeId should locate via index and load from disk
        var loaded = _repo.GetById("uncached-msg");
        Assert.NotNull(loaded);
        Assert.Equal("uncached-msg", loaded!.Id);
        Assert.Equal("Uncached load test", loaded.Subject);
        Assert.Equal("emp-uncached", loaded.EmployeeId);
    }

    [Fact]
    public void GetById_WithEmployeeId_LoadsFromDisk()
    {
        var msg = new EmailMessage
        {
            Id = "emp-scoped-msg",
            EmployeeId = "emp-scoped",
            FolderPath = "Sent",
            UniqueId = 777,
            Subject = "Scoped load test"
        };
        _repo.Save(msg);
        _writer.FlushAll();

        _repo.Clear();

        var loaded = _repo.GetById("emp-scoped", "emp-scoped-msg");
        Assert.NotNull(loaded);
        Assert.Equal("emp-scoped-msg", loaded!.Id);
        Assert.Equal("Scoped load test", loaded.Subject);
    }

    [Fact]
    public void GetAllForEmployee_ReturnsAllMessagesForEmployee()
    {
        var msg1 = new EmailMessage { Id = "all-1", EmployeeId = "emp-all", FolderPath = "Inbox", UniqueId = 1, Subject = "Sub1" };
        var msg2 = new EmailMessage { Id = "all-2", EmployeeId = "emp-all", FolderPath = "Sent", UniqueId = 2, Subject = "Sub2" };
        var otherMsg = new EmailMessage { Id = "other-1", EmployeeId = "emp-other", FolderPath = "Inbox", UniqueId = 1, Subject = "SubOther" };

        _repo.Save(msg1);
        _repo.Save(msg2);
        _repo.Save(otherMsg);
        _writer.FlushAll();

        var employeeMessages = _repo.GetAllForEmployee("emp-all");
        Assert.Equal(2, employeeMessages.Count);
        Assert.Contains(employeeMessages, m => m.Id == "all-1");
        Assert.Contains(employeeMessages, m => m.Id == "all-2");
        Assert.DoesNotContain(employeeMessages, m => m.Id == "other-1");
    }

    [Fact]
    public void Delete_WithSingleArgument_WhenNotInCache_ResolvesFromIndexAndDeletes()
    {
        var msg = new EmailMessage
        {
            Id = "uncached-del",
            EmployeeId = "emp-del-index",
            FolderPath = "Inbox",
            UniqueId = 888,
            Subject = "Uncached delete test"
        };
        _repo.Save(msg);
        _writer.FlushAll();

        _repo.Clear();
        _repo.LoadFromDisk();

        // Delete without employeeId should resolve from index and remove file
        _repo.Delete("uncached-del");
        _writer.FlushAll();

        Assert.Null(_repo.GetById("uncached-del"));
        Assert.False(_repo.Exists("emp-del-index", "Inbox", 888));
    }

    [Fact]
    public void GetAll_ReturnsAllMessagesAcrossEmployees_EvenWhenCacheCleared()
    {
        var msg1 = new EmailMessage { Id = "getall-1", EmployeeId = "emp-getall-1", FolderPath = "Inbox", UniqueId = 1, Subject = "Sub1" };
        var msg2 = new EmailMessage { Id = "getall-2", EmployeeId = "emp-getall-2", FolderPath = "Inbox", UniqueId = 2, Subject = "Sub2" };

        _repo.Save(msg1);
        _repo.Save(msg2);
        _writer.FlushAll();

        _repo.Clear();

        var allMessages = _repo.GetAll();
        Assert.Contains(allMessages, m => m.Id == "getall-1");
        Assert.Contains(allMessages, m => m.Id == "getall-2");
    }


    [Fact]
    public void GetNextLocalUid_EmptyFolder_ReturnsOne()
    {
        var uid = _repo.GetNextLocalUid("emp_seq", "Trash");
        Assert.Equal(1u, uid);
    }

    [Fact]
    public void GetNextLocalUid_WithExistingMessages_ReturnsMaxPlusOne()
    {
        _repo.Save(new EmailMessage { EmployeeId = "emp_seq", FolderPath = "Trash", Id = "m1", UniqueId = 10 });
        _repo.Save(new EmailMessage { EmployeeId = "emp_seq", FolderPath = "Trash", Id = "m2", UniqueId = 25 });

        var uid = _repo.GetNextLocalUid("emp_seq", "Trash");
        Assert.Equal(26u, uid);
    }

    [Fact]
    public void GetNextLocalUid_IgnoresLegacySentinelUids()
    {
        _repo.Save(new EmailMessage { EmployeeId = "emp_seq", FolderPath = "Trash", Id = "m1", UniqueId = 15 });
        // Legacy sentinel UID
        _repo.Save(new EmailMessage { EmployeeId = "emp_seq", FolderPath = "Trash", Id = "m_legacy", UniqueId = uint.MaxValue - 500 });

        var uid = _repo.GetNextLocalUid("emp_seq", "Trash");
        Assert.Equal(16u, uid);
    }

    [Fact]
    public void GetUnreadByFolderRecent_ReturnsOnlyUnreadMessages_OrderedByDateDescending()
    {
        var now = DateTimeOffset.UtcNow;
        _repo.Save(new EmailMessage { EmployeeId = "emp_unread", FolderPath = "INBOX", Id = "msg_read", UniqueId = 1, IsRead = true, Date = now.AddMinutes(-10), Subject = "Read Email" });
        _repo.Save(new EmailMessage { EmployeeId = "emp_unread", FolderPath = "INBOX", Id = "msg_unread1", UniqueId = 2, IsRead = false, Date = now.AddMinutes(-5), Subject = "Older Unread" });
        _repo.Save(new EmailMessage { EmployeeId = "emp_unread", FolderPath = "INBOX", Id = "msg_unread2", UniqueId = 3, IsRead = false, Date = now, Subject = "Newer Unread" });

        var results = _repo.GetUnreadByFolderRecent("emp_unread", "INBOX", 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("msg_unread2", results[0].Id);
        Assert.Equal("msg_unread1", results[1].Id);
    }

    [Fact]
    public void GetUnreadByFolderRecent_ReturnsEmpty_WhenAllMessagesAreRead()
    {
        _repo.Save(new EmailMessage { EmployeeId = "emp_allread", FolderPath = "INBOX", Id = "m1", UniqueId = 1, IsRead = true, Subject = "Read 1" });
        _repo.Save(new EmailMessage { EmployeeId = "emp_allread", FolderPath = "INBOX", Id = "m2", UniqueId = 2, IsRead = true, Subject = "Read 2" });

        var results = _repo.GetUnreadByFolderRecent("emp_allread", "INBOX", 10);

        Assert.Empty(results);
    }

    [Fact]
    public void GetByUniqueId_ReturnsCorrectMessage_WithoutScanningOtherEmployees()
    {
        _repo.Save(new EmailMessage { EmployeeId = "emp_a", FolderPath = "INBOX", Id = "msg_a", UniqueId = 777, Subject = "Emp A Message" });
        _repo.Save(new EmailMessage { EmployeeId = "emp_b", FolderPath = "INBOX", Id = "msg_b", UniqueId = 777, Subject = "Emp B Message" });

        var msgA = _repo.GetByUniqueId("emp_a", 777);
        Assert.NotNull(msgA);
        Assert.Equal("msg_a", msgA.Id);
        Assert.Equal("emp_a", msgA.EmployeeId);

        var msgB = _repo.GetByUniqueId("emp_b", 777);
        Assert.NotNull(msgB);
        Assert.Equal("msg_b", msgB.Id);
        Assert.Equal("emp_b", msgB.EmployeeId);

        var msgC = _repo.GetByUniqueId("emp_c", 777);
        Assert.Null(msgC);
    }

    [Fact]
    public void GetAllForEmployee_WhenEmployeeHasNoMessages_ReturnsEmptyImmediately()
    {
        _repo.Save(new EmailMessage { EmployeeId = "emp_other", FolderPath = "INBOX", Id = "m_other", UniqueId = 123, Subject = "Other" });

        var results = _repo.GetAllForEmployee("system_company_bills");
        Assert.Empty(results);
    }
}

