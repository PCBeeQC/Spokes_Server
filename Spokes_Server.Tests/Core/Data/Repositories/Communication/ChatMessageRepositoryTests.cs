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
using System.Linq;
using System.Text.Json;
using System;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class ChatMessageRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly ChatMessageRepository _repo;

        public ChatMessageRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ChatMsgs_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            var companyProfiles = new CompanyProfileRepository(_writer, mockConfig.Object);
            _repo = new ChatMessageRepository(_writer, mockConfig.Object, companyProfiles);
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
            var recentMsg = new ChatMessage { Id = "recent", ChannelId = "c1", Content = "Recent", SentAt = DateTime.UtcNow.AddDays(-1) };
            File.WriteAllText(Path.Combine(channelDir, "recent.json"), JsonSerializer.Serialize(recentMsg));

            // Old message (older than 7 days)
            var oldMsg = new ChatMessage { Id = "old", ChannelId = "c1", Content = "Old", SentAt = DateTime.UtcNow.AddDays(-10) };
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
            var filteredPreview = _repo.GetChannelPreviewSummary("chan_blocked", new List<string> { "spammer" });
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
    }
}
