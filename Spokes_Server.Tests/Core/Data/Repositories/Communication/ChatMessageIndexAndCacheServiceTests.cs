using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Data.Repositories.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication;

public class ChatMessageIndexAndCacheServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;

    public ChatMessageIndexAndCacheServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_IndexCacheTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void ChatMessageIndexService_UpdateAndRetrieveIndex_WorksCorrectly()
    {
        var indexService = new ChatMessageIndexService(_persistence, _testDataDir);
        var msg = new ChatMessage
        {
            Id = "msg_101",
            ChannelId = "chan_A",
            SenderId = "user_1",
            Content = "Testing preview and search text",
            SentAt = DateTime.UtcNow
        };

        indexService.UpdateIndex(msg);

        var entries = indexService.GetIndexForChannel("chan_A");
        Assert.Single(entries);
        Assert.Equal("msg_101", entries[0].Id);
        Assert.Contains("testing preview", entries[0].SearchText);

        var (chanId, entry) = indexService.FindIndexEntry("msg_101");
        Assert.Equal("chan_A", chanId);
        Assert.NotNull(entry);

        var preview = indexService.GetChannelPreviewSummary("chan_A");
        Assert.NotNull(preview);
        Assert.Equal("msg_101", preview.MessageId);
        Assert.Equal("user_1", preview.SenderId);
    }

    [Fact]
    public void ChatMessageIndexService_BlockedUserHandling_ReturnsFallbackOrNull()
    {
        var indexService = new ChatMessageIndexService(_persistence, _testDataDir);
        var msg1 = new ChatMessage { Id = "msg_1", ChannelId = "c1", SenderId = "blocked_user", Content = "Spam", SentAt = DateTime.UtcNow.AddMinutes(-5) };
        var msg2 = new ChatMessage { Id = "msg_2", ChannelId = "c1", SenderId = "good_user", Content = "Hello", SentAt = DateTime.UtcNow.AddMinutes(-10) };

        indexService.UpdateIndex(msg2);
        indexService.UpdateIndex(msg1);

        // When not blocking, latest is msg_1
        var p1 = indexService.GetChannelPreviewSummary("c1");
        Assert.Equal("msg_1", p1?.MessageId);

        // When blocked_user is blocked, fallback should pick msg_2
        var p2 = indexService.GetChannelPreviewSummary("c1", new[] { "blocked_user" });
        Assert.Equal("msg_2", p2?.MessageId);
    }

    [Fact]
    public void ChatMessageCacheService_TrackAccessAndLRUPruning_EvictsOldestMessages()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        var companyProfileRepo = new CompanyProfileRepository(_persistence, mockConfig.Object);

        // Set max cache size to 1 MB
        companyProfileRepo.Save(new CompanyProfile { Id = "1", MaxChatCacheSizeMb = 1 });

        var cacheService = new ChatMessageCacheService(companyProfileRepo);
        var cache = new ConcurrentDictionary<string, ChatMessage>();

        // Add 3 large messages (each ~500 KB to exceed 1 MB)
        var largeContent = new string('A', 250_000); // 250,000 * 2 bytes = 500,000 bytes
        var msg1 = new ChatMessage { Id = "m1", ChannelId = "c", Content = largeContent };
        var msg2 = new ChatMessage { Id = "m2", ChannelId = "c", Content = largeContent };
        var msg3 = new ChatMessage { Id = "m3", ChannelId = "c", Content = largeContent };

        cache["m1"] = msg1;
        cacheService.TrackAccess(msg1, DateTime.UtcNow.AddMinutes(-10));

        cache["m2"] = msg2;
        cacheService.TrackAccess(msg2, DateTime.UtcNow.AddMinutes(-5));

        cache["m3"] = msg3;
        cacheService.TrackAccess(msg3, DateTime.UtcNow);

        // Enforce limit should evict m1 first
        cacheService.EnforceGlobalCacheSizeLimit(cache);

        Assert.False(cache.ContainsKey("m1"), "m1 should have been evicted as the oldest message");
        Assert.True(cache.ContainsKey("m3"), "m3 should remain in cache as the most recently accessed");
    }

    [Fact]
    public async Task ChatMessageCacheService_ConcurrentTrackAccess_DoesNotCorruptTotalCacheSizeBytes()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        var companyProfileRepo = new CompanyProfileRepository(_persistence, mockConfig.Object);
        var cacheService = new ChatMessageCacheService(companyProfileRepo);

        var msg = new ChatMessage { Id = "m_concurrent", ChannelId = "c", Content = "Test message content" };
        int expectedSize = (msg.Content.Length * 2) + 500;

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                cacheService.TrackAccess(msg);
            }
        })).ToList();

        await Task.WhenAll(tasks);

        Assert.Equal(expectedSize, cacheService.TotalCacheSizeBytes);
    }

    [Fact]
    public async Task ChatMessageIndexService_ConcurrentReadsAndWrites_DoesNotThrowCollectionModifiedException()
    {
        var indexService = new ChatMessageIndexService(_persistence, _testDataDir);

        var tasks = new List<Task>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Writers updating index
        for (int i = 0; i < 4; i++)
        {
            int writerId = i;
            tasks.Add(Task.Run(() =>
            {
                int counter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    indexService.UpdateIndex(new ChatMessage
                    {
                        Id = $"msg_{writerId}_{counter++}",
                        ChannelId = $"chan_{writerId % 2}",
                        SenderId = $"user_{writerId}",
                        Content = $"Hello world {counter}",
                        SentAt = DateTime.UtcNow
                    });
                }
            }));
        }

        // Readers calling GetIndexForChannel, SaveMasterIndex, GetChannelIndexesSnapshot
        for (int i = 0; i < 4; i++)
        {
            int readerId = i;
            tasks.Add(Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var channelId = $"chan_{readerId % 2}";
                    var entries = indexService.GetIndexForChannel(channelId);
                    foreach (var e in entries)
                    {
                        _ = e.Id;
                    }
                    indexService.SaveMasterIndex();
                    var snapshot = indexService.GetChannelIndexesSnapshot();
                    _ = snapshot.Count;
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void ChatMessageIndexService_RebuildMasterIndexFromDisk_PopulatesIndexesAndReturnsRecentMessages()
    {
        var indexService = new ChatMessageIndexService(_persistence, _testDataDir);
        var chanDir = Path.Combine(_testDataDir, "channel1", "messages");
        Directory.CreateDirectory(chanDir);

        var recent = new ChatMessage
        {
            Id = "msg_recent",
            ChannelId = "channel1",
            SenderId = "alice",
            Content = "Recent message",
            SentAt = DateTime.UtcNow.AddDays(-1)
        };
        var old = new ChatMessage
        {
            Id = "msg_old",
            ChannelId = "channel1",
            SenderId = "bob",
            Content = "Old message",
            SentAt = DateTime.UtcNow.AddDays(-14)
        };

        File.WriteAllText(Path.Combine(chanDir, "msg_recent.json"), System.Text.Json.JsonSerializer.Serialize(recent));
        File.WriteAllText(Path.Combine(chanDir, "msg_old.json"), System.Text.Json.JsonSerializer.Serialize(old));

        var loaded = indexService.RebuildMasterIndexFromDisk(55);

        // Only recent message is returned for cache seeding
        Assert.Single(loaded);
        Assert.Equal("msg_recent", loaded[0].Id);

        // Both messages are indexed
        var index = indexService.GetIndexForChannel("channel1");
        Assert.Equal(2, index.Count);
        Assert.Contains(index, e => e.Id == "msg_recent");
        Assert.Contains(index, e => e.Id == "msg_old");

        // Preview is refreshed
        var preview = indexService.GetChannelPreviewSummary("channel1");
        Assert.NotNull(preview);
        Assert.Equal("msg_recent", preview.MessageId);
    }

    [Fact]
    public void ChatMessageIndexService_EnsureIndexBuilt_And_EnsureAll_BuildsDiskChannels()
    {
        var indexService = new ChatMessageIndexService(_persistence, _testDataDir);
        var chanADir = Path.Combine(_testDataDir, "chanA", "messages");
        var chanBDir = Path.Combine(_testDataDir, "chanB", "messages");
        var orphanDir = Path.Combine(_testDataDir, "orphaned");
        Directory.CreateDirectory(chanADir);
        Directory.CreateDirectory(chanBDir);
        Directory.CreateDirectory(orphanDir);

        var msgA = new ChatMessage { Id = "mA", ChannelId = "chanA", SenderId = "u1", Content = "Alpha", SentAt = DateTime.UtcNow };
        var msgB = new ChatMessage { Id = "mB", ChannelId = "chanB", SenderId = "u2", Content = "Beta", SentAt = DateTime.UtcNow };
        var msgOrphan = new ChatMessage { Id = "mO", ChannelId = "", SenderId = "u3", Content = "Orphan", SentAt = DateTime.UtcNow };

        File.WriteAllText(Path.Combine(chanADir, "mA.json"), System.Text.Json.JsonSerializer.Serialize(msgA));
        File.WriteAllText(Path.Combine(chanBDir, "mB.json"), System.Text.Json.JsonSerializer.Serialize(msgB));
        File.WriteAllText(Path.Combine(orphanDir, "mO.json"), System.Text.Json.JsonSerializer.Serialize(msgOrphan));

        // Ensure single
        indexService.EnsureIndexBuilt("chanA");
        Assert.Single(indexService.GetIndexForChannel("chanA"));
        Assert.Empty(indexService.GetIndexForChannel("chanB"));

        // Ensure all
        indexService.EnsureAllChannelIndexesBuilt();
        Assert.Single(indexService.GetIndexForChannel("chanA"));
        Assert.Single(indexService.GetIndexForChannel("chanB"));
        Assert.Empty(indexService.GetIndexForChannel("orphaned"));
    }
}

