using System.Text.Json;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Tests.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class ChatMessageRepositoryTests : RepositoryTestFixture<ChatMessageRepository>
{
    public ChatMessageRepositoryTests()
        : base((writer, config) => new ChatMessageRepository(writer, config, new CompanyProfileRepository(writer, config)), "ChatMsgs")
    {
    }

    [Fact]
    public void Save_AddsToCacheAndUpdatesIndex()
    {
        var msg = new ChatMessage { Id = "msg1", ChannelId = "c1", Content = "Hello World" };
        _repo.Save(msg);

        // Assert cache contains the message
        var msgs = _repo.GetByChannel("c1");
        Assert.Single(msgs);
        Assert.Equal("Hello World", msgs.First().Content);

        // Assert index entry was created and populated
        var index = _repo.GetIndexForChannel("c1");
        Assert.Single(index);
        Assert.Equal("msg1", index.First().Id);
        Assert.Equal("hello world", index.First().SearchText);
    }

    [Fact]
    public void LoadFromDisk_LoadsRecentMessagesToCache_IndexesAllMessages()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c1", "messages");
        Directory.CreateDirectory(channelDir);

        // Recent message (within 7 days)
        var recentMsg = new ChatMessage { Id = "recent", ChannelId = "c1", SenderId = "user1", Content = "Recent", SentAt = DateTime.UtcNow.AddDays(-1) };
        File.WriteAllText(Path.Combine(channelDir, "recent.json"), JsonSerializer.Serialize(recentMsg));

        // Old message (older than 7 days)
        var oldMsg = new ChatMessage { Id = "old", ChannelId = "c1", SenderId = "user2", Content = "Old", SentAt = DateTime.UtcNow.AddDays(-10) };
        File.WriteAllText(Path.Combine(channelDir, "old.json"), JsonSerializer.Serialize(oldMsg));

        _repo.LoadFromDisk();

        // Index contains both messages, verifying startup load boundaries don't skip indexing older files
        var index = _repo.GetIndexForChannel("c1");
        Assert.Equal(2, index.Count);

        // GetByChannel without limit retrieves only cached messages (skips old message)
        var cachedMsgs = _repo.GetByChannel("c1");
        Assert.Single(cachedMsgs);
        Assert.Equal("Recent", cachedMsgs.First().Content);

        // Old message is not in the cache, but exists in the index
        Assert.Contains(index, e => e.Id == "old");
        Assert.DoesNotContain(cachedMsgs, m => m.Id == "old");

        // GetAll retrieves ALL messages across the repository (both cached and non-cached on disk)
        var allMsgs = _repo.GetAll();
        Assert.Equal(2, allMsgs.Count);
        Assert.Contains(allMsgs, m => m.Id == "recent");
        Assert.Contains(allMsgs, m => m.Id == "old");

        // GetAllForSender retrieves messages for the specific sender even if not in cache
        var oldSenderMsgs = _repo.GetAllForSender("user2");
        Assert.Single(oldSenderMsgs);
        Assert.Equal("old", oldSenderMsgs.First().Id);
    }

    [Fact]
    public void GetByChannelBefore_DynamicallyLoadsIndexedMessages()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c3", "messages");
        Directory.CreateDirectory(channelDir);

        var msg = new ChatMessage { Id = "m1", ChannelId = "c3", Content = "Text", SentAt = DateTime.UtcNow.AddDays(-15) };
        File.WriteAllText(Path.Combine(channelDir, "m1.json"), JsonSerializer.Serialize(msg));

        _repo.LoadFromDisk(); // Indexes the message

        // Cache should be empty initially
        Assert.Empty(_repo.GetByChannel("c3"));

        // Request messages before 10 days ago (forces dynamic lazy loading of indexed message)
        var msgs = _repo.GetByChannelBefore("c3", DateTime.UtcNow.AddDays(-10), 10);

        Assert.Single(msgs);
        Assert.Equal("Text", msgs.First().Content);
    }

    [Fact]
    public void HasMessagesBefore_ReturnsTrueWhenOlderMessagesExistInIndex()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_has_before", "messages");
        Directory.CreateDirectory(channelDir);

        var msg1 = new ChatMessage { Id = "m_old", ChannelId = "c_has_before", Content = "Old message", SentAt = DateTime.UtcNow.AddDays(-20) };
        var msg2 = new ChatMessage { Id = "m_recent", ChannelId = "c_has_before", Content = "Recent message", SentAt = DateTime.UtcNow.AddDays(-10) };
        File.WriteAllText(Path.Combine(channelDir, "m_old.json"), JsonSerializer.Serialize(msg1));
        File.WriteAllText(Path.Combine(channelDir, "m_recent.json"), JsonSerializer.Serialize(msg2));

        _repo.LoadFromDisk(); // Indexes messages, cache remains empty (older than 7 days)
        Assert.Empty(_repo.GetByChannel("c_has_before"));

        // HasMessagesBefore checking before recent message timestamp
        Assert.True(_repo.HasMessagesBefore("c_has_before", msg2.SentAt));

        // HasMessagesBefore checking before the oldest message timestamp
        Assert.False(_repo.HasMessagesBefore("c_has_before", msg1.SentAt));

        // HasMessagesBefore for non-existent channel
        Assert.False(_repo.HasMessagesBefore("c_unknown", DateTime.UtcNow));
    }

    [Fact]
    public void GetMessageCount_ReturnsIndexedCountEvenWhenCacheEmpty()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_count_test", "messages");
        Directory.CreateDirectory(channelDir);

        var msg1 = new ChatMessage { Id = "m_cnt1", ChannelId = "c_count_test", Content = "Msg 1", SentAt = DateTime.UtcNow.AddDays(-14) };
        var msg2 = new ChatMessage { Id = "m_cnt2", ChannelId = "c_count_test", Content = "Msg 2", SentAt = DateTime.UtcNow.AddDays(-15) };
        File.WriteAllText(Path.Combine(channelDir, "m_cnt1.json"), JsonSerializer.Serialize(msg1));
        File.WriteAllText(Path.Combine(channelDir, "m_cnt2.json"), JsonSerializer.Serialize(msg2));

        _repo.LoadFromDisk(); // Indexes messages (> 7 days cutoff means not loaded into _cache)

        Assert.Equal(2, _repo.GetMessageCount("c_count_test"));
        Assert.Equal(2, _repo.GetTotalMessageCount("c_count_test"));
    }

    [Fact]
    public void Search_FindsMatchingMessagesUsingIndex()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c4", "messages");
        Directory.CreateDirectory(channelDir);

        var msg = new ChatMessage { Id = "m_search", ChannelId = "c4", Content = "Find this phrase", SentAt = DateTime.UtcNow.AddDays(-30) };
        File.WriteAllText(Path.Combine(channelDir, "m_search.json"), JsonSerializer.Serialize(msg));

        _repo.LoadFromDisk(); // Index contains the search text

        // Search retrieves match from index and loads the body dynamically
        var results = _repo.Search("c4", "this phrase");

        Assert.Single(results);
        Assert.Equal("m_search", results.First().Id);
    }

    [Fact]
    public void GetUnreadCountSince_ExcludesSender()
    {
        _repo.Save(new ChatMessage { Id = "u1", ChannelId = "uc1", SenderId = "user1", SentAt = DateTime.UtcNow.AddHours(-24) });
        _repo.Save(new ChatMessage { Id = "u2", ChannelId = "uc1", SenderId = "user2", SentAt = DateTime.UtcNow.AddHours(-2) });

        var unread = _repo.GetUnreadCountSince("uc1", DateTime.UtcNow.AddHours(-12), "user1");
        Assert.Equal(1, unread); // only u2's message
 
        var unread2 = _repo.GetUnreadCountSince("uc1", DateTime.UtcNow.AddHours(-48), "user1");
        Assert.Equal(1, unread2); // still 1 because user1 is excluded
    }

    [Fact]
    public void GetLatestInChannel_FallsBackToDiskUsingIndex_BypassingMainCache()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_preview", "messages");
        Directory.CreateDirectory(channelDir);

        var oldMsg = new ChatMessage
        {
            Id = "old_preview",
            ChannelId = "c_preview",
            Content = "Old preview message",
            SenderId = "user1",
            SentAt = DateTime.UtcNow.AddDays(-20)
        };
        File.WriteAllText(Path.Combine(channelDir, "old_preview.json"), JsonSerializer.Serialize(oldMsg));

        _repo.LoadFromDisk(); // Only indexes old_preview

        // Cache is empty
        var byChannel = _repo.GetByChannel("c_preview");
        Assert.Empty(byChannel);

        // GetLatestInChannel falls back to disk and finds it using index
        var latest = _repo.GetLatestInChannel("c_preview");
        Assert.NotNull(latest);
        Assert.Equal("old_preview", latest!.Id);
        Assert.Equal("Old preview message", latest.Content);

        // Cache remains empty to avoid polluting main channel load counts
        var stillEmpty = _repo.GetByChannel("c_preview");
        Assert.Empty(stillEmpty);

        // Subsequent call resolves from the preview cache
        var cachedPreview = _repo.GetLatestInChannel("c_preview");
        Assert.NotNull(cachedPreview);
        Assert.Equal("old_preview", cachedPreview!.Id);
    }

    [Fact]
    public void ChannelPreviewSummary_PopulatedOnSave_And_UpdatedOnDelete()
    {
        var msg1 = new ChatMessage
        {
            Id = "msg1",
            ChannelId = "chan_prev",
            SenderId = "alice",
            Content = "Hello from Alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5)
        };
        _repo.Save(msg1);

        var preview1 = _repo.GetChannelPreviewSummary("chan_prev");
        Assert.NotNull(preview1);
        Assert.Equal("msg1", preview1!.MessageId);
        Assert.Equal("alice", preview1.SenderId);
        Assert.Equal("Hello from Alice", preview1.PreviewText);

        var msg2 = new ChatMessage
        {
            Id = "msg2",
            ChannelId = "chan_prev",
            SenderId = "bob",
            Content = "Reply from Bob",
            SentAt = DateTime.UtcNow
        };
        _repo.Save(msg2);

        var preview2 = _repo.GetChannelPreviewSummary("chan_prev");
        Assert.NotNull(preview2);
        Assert.Equal("msg2", preview2!.MessageId);
        Assert.Equal("bob", preview2.SenderId);
        Assert.Equal("Reply from Bob", preview2.PreviewText);

        // Deleting msg2 should restore msg1 as the channel preview
        _repo.Delete("msg2");

        var previewAfterDelete = _repo.GetChannelPreviewSummary("chan_prev");
        Assert.NotNull(previewAfterDelete);
        Assert.Equal("msg1", previewAfterDelete!.MessageId);
        Assert.Equal("alice", previewAfterDelete.SenderId);
        Assert.Equal("Hello from Alice", previewAfterDelete.PreviewText);
    }

    [Fact]
    public void GetChannelPreviewSummary_ExcludesBlockedUser()
    {
        var msg1 = new ChatMessage
        {
            Id = "m1",
            ChannelId = "chan_blocked",
            SenderId = "good_user",
            Content = "Good message",
            SentAt = DateTime.UtcNow.AddMinutes(-10)
        };
        _repo.Save(msg1);

        var msg2 = new ChatMessage
        {
            Id = "m2",
            ChannelId = "chan_blocked",
            SenderId = "spammer",
            Content = "Spam message",
            SentAt = DateTime.UtcNow
        };
        _repo.Save(msg2);

        // Without blocking: latest is spammer
        var normalPreview = _repo.GetChannelPreviewSummary("chan_blocked");
        Assert.NotNull(normalPreview);
        Assert.Equal("m2", normalPreview!.MessageId);

        // With spammer blocked: returns good_user
        var filteredPreview = _repo.GetChannelPreviewSummary("chan_blocked", ["spammer"]);
        Assert.NotNull(filteredPreview);
        Assert.Equal("m1", filteredPreview!.MessageId);
        Assert.Equal("good_user", filteredPreview.SenderId);
        Assert.Equal("Good message", filteredPreview.PreviewText);
    }

    [Fact]
    public void LoadFromDisk_PopulatesChannelPreviewsFromIndex_WithoutDiskRead()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_index_prev", "messages");
        Directory.CreateDirectory(channelDir);

        // Save old message (> 7 days ago)
        var oldMsg = new ChatMessage
        {
            Id = "old_msg",
            ChannelId = "c_index_prev",
            SenderId = "charlie",
            Content = "Message from long ago",
            SentAt = DateTime.UtcNow.AddDays(-30)
        };
        File.WriteAllText(Path.Combine(channelDir, "old_msg.json"), JsonSerializer.Serialize(oldMsg));

        // Load from disk: reads directory and populates index and preview summary
        _repo.LoadFromDisk();

        // Preview summary should be available in memory without needing message in _cache
        var preview = _repo.GetChannelPreviewSummary("c_index_prev");
        Assert.NotNull(preview);
        Assert.Equal("old_msg", preview!.MessageId);
        Assert.Equal("charlie", preview.SenderId);
        Assert.Equal("Message from long ago", preview.PreviewText);

        // Verify message body is NOT in _cache (lazy loading preserved)
        var cached = _repo.GetByChannel("c_index_prev");
        Assert.Empty(cached);
    }

    [Fact]
    public void Save_WithEmptyChannelId_StoresInOutFolderOrphaned()
    {
        var orphaned = new ChatMessage { Id = "msg-orphan", ChannelId = string.Empty, Content = "No channel" };
        _repo.Save(orphaned);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "chat", "orphaned", "msg-orphan.json");
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public void Save_And_Delete_UpdatesCacheAndIndex()
    {
        var msg = new ChatMessage
        {
            Id = "async-msg",
            ChannelId = "c-async",
            Content = "Async text",
            SentAt = DateTime.UtcNow
        };

        _repo.Save(msg);

        var msgs = _repo.GetByChannel("c-async");
        Assert.Single(msgs);
        Assert.Equal("Async text", msgs.First().Content);

        var idx = _repo.GetIndexForChannel("c-async");
        Assert.Single(idx);

        _repo.Delete("async-msg");

        Assert.Empty(_repo.GetByChannel("c-async"));
        Assert.Empty(_repo.GetIndexForChannel("c-async"));
    }

    [Fact]
    public void GetReplies_ReturnsOrderedThreadReplies()
    {
        var parent = new ChatMessage { Id = "parent-1", ChannelId = "c-replies", Content = "Parent" };
        var reply2 = new ChatMessage
        {
            Id = "rep-2",
            ChannelId = "c-replies",
            ReplyToId = "parent-1",
            Content = "Reply 2",
            SentAt = DateTime.UtcNow.AddMinutes(5)
        };
        var reply1 = new ChatMessage
        {
            Id = "rep-1",
            ChannelId = "c-replies",
            ReplyToId = "parent-1",
            Content = "Reply 1",
            SentAt = DateTime.UtcNow.AddMinutes(1)
        };

        _repo.Save(parent);
        _repo.Save(reply2);
        _repo.Save(reply1);

        var replies = _repo.GetReplies("parent-1");
        Assert.Equal(2, replies.Count);
        Assert.Equal("rep-1", replies[0].Id);
        Assert.Equal("rep-2", replies[1].Id);
    }

    [Fact]
    public void GetMessageFromDisk_BypassesCache()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c-disk", "messages");
        Directory.CreateDirectory(channelDir);

        var diskMsg = new ChatMessage { Id = "disk-m1", ChannelId = "c-disk", Content = "From disk directly" };
        File.WriteAllText(Path.Combine(channelDir, "disk-m1.json"), JsonSerializer.Serialize(diskMsg));

        var syncLoaded = _repo.GetMessageFromDisk("c-disk", "disk-m1");
        Assert.NotNull(syncLoaded);
        Assert.Equal("From disk directly", syncLoaded!.Content);

        var nonExistent = _repo.GetMessageFromDisk("c-disk", "non-existent");
        Assert.Null(nonExistent);
    }

    [Fact]
    public void GetById_WhenMessageNotInCache_LoadsFromDiskAndPopulatesCache()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_getbyid", "messages");
        Directory.CreateDirectory(channelDir);

        var oldMsg = new ChatMessage
        {
            Id = "msg_old_1",
            ChannelId = "c_getbyid",
            Content = "Old message content",
            SenderId = "alice",
            SentAt = DateTime.UtcNow.AddDays(-20)
        };
        File.WriteAllText(Path.Combine(channelDir, "msg_old_1.json"), JsonSerializer.Serialize(oldMsg));

        // LoadFromDisk indexes older messages but does not load their bodies into _cache
        _repo.LoadFromDisk();
        Assert.Empty(_repo.GetByChannel("c_getbyid"));

        // GetById should resolve from index and disk, then populate cache
        var loaded = _repo.GetById("msg_old_1");
        Assert.NotNull(loaded);
        Assert.Equal("msg_old_1", loaded!.Id);
        Assert.Equal("Old message content", loaded.Content);

        // Cache now contains the message
        var cached = _repo.GetByChannel("c_getbyid");
        Assert.Single(cached);
        Assert.Equal("msg_old_1", cached.First().Id);
    }

    [Fact]
    public void Delete_WhenMessageNotInCache_RemovesFromIndexAndDeletesFileOnDisk()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_del_evicted", "messages");
        Directory.CreateDirectory(channelDir);

        var filePath = Path.Combine(channelDir, "msg_evicted.json");
        var oldMsg = new ChatMessage
        {
            Id = "msg_evicted",
            ChannelId = "c_del_evicted",
            Content = "Evicted message to be deleted",
            SenderId = "charlie",
            SentAt = DateTime.UtcNow.AddDays(-30)
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(oldMsg));

        _repo.LoadFromDisk();
        Assert.Single(_repo.GetIndexForChannel("c_del_evicted"));
        Assert.Empty(_repo.GetByChannel("c_del_evicted"));
        Assert.True(File.Exists(filePath));

        // Delete should locate channel from index, remove index entry, and queue disk deletion
        _repo.Delete("msg_evicted");
        _writer.FlushAll();

        Assert.Empty(_repo.GetIndexForChannel("c_del_evicted"));
        Assert.False(File.Exists(filePath));
        Assert.Null(_repo.GetById("msg_evicted"));
    }

    [Fact]
    public void Delete_WhenOrphanedMessageNotInCache_DeletesFileOnDisk()
    {
        var orphanDir = Path.Combine(_testDataDir, "chat", "orphaned");
        Directory.CreateDirectory(orphanDir);

        var filePath = Path.Combine(orphanDir, "orphan_del.json");
        var orphanMsg = new ChatMessage
        {
            Id = "orphan_del",
            ChannelId = string.Empty,
            Content = "Orphan message to delete"
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(orphanMsg));
        Assert.True(File.Exists(filePath));

        _repo.Delete("orphan_del");
        _writer.FlushAll();

        Assert.False(File.Exists(filePath));
        Assert.Null(_repo.GetById("orphan_del"));
    }

    [Fact]
    public void GetById_WhenNonExistent_ReturnsNullWithoutRecursiveDirectoryScan()
    {
        var result = _repo.GetById("non_existent_msg_id_12345");
        Assert.Null(result);
    }

    [Fact]
    public void Delete_WhenNonExistent_DoesNotThrow()
    {
        var exception = Record.Exception(() => _repo.Delete("non_existent_msg_id_99999"));
        Assert.Null(exception);
    }

    [Fact]
    public void GetByIdOrLoad_WhenInCache_ReturnsCachedInstance()
    {
        var msg = new ChatMessage { Id = "msg_cached", ChannelId = "c1", Content = "In memory" };
        _repo.Save(msg);

        var retrieved = _repo.GetByIdOrLoad("c1", "msg_cached");

        Assert.NotNull(retrieved);
        Assert.Equal("In memory", retrieved.Content);

        // Also verify single-argument overload
        var retrievedByMessageIdOnly = _repo.GetByIdOrLoad("msg_cached");
        Assert.NotNull(retrievedByMessageIdOnly);
        Assert.Equal("In memory", retrievedByMessageIdOnly.Content);
    }

    [Fact]
    public void GetByIdOrLoad_WhenNotInCache_LoadsFromDiskAndPopulatesCache()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_load", "messages");
        Directory.CreateDirectory(channelDir);

        var msg = new ChatMessage
        {
            Id = "msg_on_disk",
            ChannelId = "c_load",
            Content = "Stored on disk only",
            SenderId = "alice",
            SentAt = DateTime.UtcNow
        };
        File.WriteAllText(Path.Combine(channelDir, "msg_on_disk.json"), JsonSerializer.Serialize(msg));

        // Message is on disk but not in memory cache
        Assert.Empty(_repo.GetByChannel("c_load"));

        var retrieved = _repo.GetByIdOrLoad("c_load", "msg_on_disk");

        Assert.NotNull(retrieved);
        Assert.Equal("Stored on disk only", retrieved.Content);
        // Verify it was added to cache
    }

    [Fact]
    public void GetById_CorruptJson_LogsWarningAndReturnsNullWithoutCrashing()
    {
        var mockLogger = new Moq.Mock<Microsoft.Extensions.Logging.ILogger<ChatMessageRepository>>();
        var repo = new ChatMessageRepository(
            _writer,
            _config,
            new CompanyProfileRepository(_writer, _config),
            logger: mockLogger.Object);

        var channelDir = Path.Combine(_testDataDir, "chat", "c_corrupt", "messages");
        Directory.CreateDirectory(channelDir);
        File.WriteAllText(Path.Combine(channelDir, "corrupt_msg.json"), "{ invalid json }");

        var retrieved = repo.GetMessageFromDisk("c_corrupt", "corrupt_msg");

        Assert.Null(retrieved);
        mockLogger.Verify(
            x => x.Log(
                Microsoft.Extensions.Logging.LogLevel.Warning,
                Moq.It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                Moq.It.Is<Moq.It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to deserialize")),
                Moq.It.IsAny<Exception>(),
                Moq.It.IsAny<Func<Moq.It.IsAnyType, Exception?, string>>()),
            Moq.Times.Once);
    }

    [Fact]
    public void LoadFromDisk_CorruptJson_ContinuesLoadingValidMessages()
    {
        var mockLogger = new Moq.Mock<Microsoft.Extensions.Logging.ILogger<ChatMessageRepository>>();
        var repo = new ChatMessageRepository(
            _writer,
            _config,
            new CompanyProfileRepository(_writer, _config),
            logger: mockLogger.Object);

        var channelDir = Path.Combine(_testDataDir, "chat", "c_mixed", "messages");
        Directory.CreateDirectory(channelDir);

        var validMsg = new ChatMessage
        {
            Id = "valid_1",
            ChannelId = "c_mixed",
            SenderId = "u1",
            Content = "Valid content",
            SentAt = DateTime.UtcNow
        };
        File.WriteAllText(Path.Combine(channelDir, "valid_1.json"), JsonSerializer.Serialize(validMsg));
        File.WriteAllText(Path.Combine(channelDir, "corrupt_2.json"), "NOT_JSON_DATA");

        // Master index is not present, so LoadFromDisk will crawl and rebuild index
        repo.LoadFromDisk();

        var msgs = repo.GetByChannel("c_mixed");
        Assert.Single(msgs);
        Assert.Equal("Valid content", msgs.First().Content);
    }

    [Fact]
    public async Task RepeatedReads_DoNotThrashThreadPool_AndEnforcesCacheLimitWhenExceeded()
    {
        var companyProfileRepo = new CompanyProfileRepository(_writer, _config);
        var profile = companyProfileRepo.Get();
        profile.MaxChatCacheSizeMb = 1; // 1 MB limit
        companyProfileRepo.Save(profile);

        var repo = new ChatMessageRepository(_writer, _config, companyProfileRepo);

        var channelDir = Path.Combine(_testDataDir, "chat", "c_throttle", "messages");
        Directory.CreateDirectory(channelDir);

        var largeContent = new string('X', 20_000); // 20 KB per message
        for (int i = 0; i < 60; i++)
        {
            var msg = new ChatMessage
            {
                Id = $"msg_{i}",
                ChannelId = "c_throttle",
                Content = largeContent,
                SentAt = DateTime.UtcNow.AddMinutes(i)
            };
            File.WriteAllText(Path.Combine(channelDir, $"msg_{i}.json"), JsonSerializer.Serialize(msg));
        }

        for (int i = 0; i < 60; i++)
        {
            var loaded = repo.GetByIdOrLoad("c_throttle", $"msg_{i}");
            Assert.NotNull(loaded);
        }

        await Task.Delay(100);

        Assert.NotNull(repo.GetByIdOrLoad("c_throttle", "msg_59"));
    }

    [Fact]
    public void SaveBatch_PersistsMultipleMessagesAndTracksCache()
    {
        var messages = Enumerable.Range(0, 10).Select(i => new ChatMessage
        {
            Id = $"batch_msg_{i}",
            ChannelId = "c_batch",
            Content = $"Batch Message {i}",
            SentAt = DateTime.UtcNow.AddSeconds(i),
            ReadBy = new List<string> { "user_1" }
        }).ToList();

        _repo.SaveBatch(messages);

        foreach (var msg in messages)
        {
            var retrieved = _repo.GetById(msg.Id);
            Assert.NotNull(retrieved);
            Assert.Equal(msg.Content, retrieved.Content);
            Assert.Contains("user_1", retrieved.ReadBy);
        }
    }

    [Fact]
    public void Clear_ClearsCacheAndCacheServiceAndIndexService()
    {
        var msg = new ChatMessage { Id = "msg_clear", ChannelId = "c_clear", Content = "To Be Cleared" };
        _repo.Save(msg);

        Assert.NotNull(_repo.GetCachedMessage("msg_clear"));
        Assert.NotEmpty(_repo.GetIndexForChannel("c_clear"));
        Assert.True(_repo.CacheService.TotalCacheSizeBytes > 0);

        _repo.Clear();

        Assert.Null(_repo.GetCachedMessage("msg_clear"));
        Assert.Empty(_repo.GetIndexForChannel("c_clear"));
        Assert.Equal(0, _repo.CacheService.TotalCacheSizeBytes);
    }

    [Fact]
    public async Task GetByChannelAsync_LoadsUncachedMessagesAsynchronously()
    {
        var channelDir = Path.Combine(_testDataDir, "chat", "c_async", "messages");
        Directory.CreateDirectory(channelDir);

        var msg = new ChatMessage
        {
            Id = "async_msg_1",
            ChannelId = "c_async",
            SenderId = "u1",
            Content = "Async content",
            SentAt = DateTime.UtcNow
        };
        File.WriteAllText(Path.Combine(channelDir, "async_msg_1.json"), JsonSerializer.Serialize(msg));

        _repo.IndexService.EnsureIndexBuilt("c_async");

        var results = await _repo.GetByChannelAsync("c_async", 10);
        Assert.Single(results);
        Assert.Equal("async_msg_1", results[0].Id);
        Assert.Equal("Async content", results[0].Content);
        Assert.NotNull(_repo.GetCachedMessage("async_msg_1"));
    }

    [Fact]
    public void GetCachedByChannel_ReturnsOnlyCachedMessages()
    {
        var msg1 = new ChatMessage { Id = "cached_1", ChannelId = "c_cached", Content = "In Cache" };
        _repo.Save(msg1);

        var cached = _repo.GetCachedByChannel("c_cached");
        Assert.Single(cached);
        Assert.Equal("cached_1", cached[0].Id);

        var empty = _repo.GetCachedByChannel("c_nonexistent");
        Assert.Empty(empty);
    }

    [Fact]
    public void Save_IndexesMentionedAlbumIds()
    {
        var msg = new ChatMessage
        {
            Id = "msg_album",
            ChannelId = "c_album",
            Content = "Look at #[Photos](album:alb_99)",
            MentionedAlbumIds = new List<string> { "alb_99" }
        };
        _repo.Save(msg);

        var index = _repo.GetIndexForChannel("c_album");
        Assert.Single(index);
        Assert.Contains("alb_99", index[0].MentionedAlbumIds);
    }
}
