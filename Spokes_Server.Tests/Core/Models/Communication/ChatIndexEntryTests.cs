using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChatIndexEntryTests
{
    [Fact]
    public void ChatIndexEntry_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var entry = new ChatIndexEntry
        {
            Id = "entry_1",
            ChannelId = "c_general",
            SenderId = "user_admin",
            SentAt = now,
            IsDeleted = false,
            IsEncrypted = false,
            SearchText = "meeting tomorrow",
            ReplyToId = "entry_0",
            PreviewText = "meeting tomorrow at 9am"
        };

        Assert.Equal("entry_1", entry.Id);
        Assert.Equal("c_general", entry.ChannelId);
        Assert.Equal("user_admin", entry.SenderId);
        Assert.Equal(now, entry.SentAt);
        Assert.False(entry.IsDeleted);
        Assert.False(entry.IsEncrypted);
        Assert.Equal("meeting tomorrow", entry.SearchText);
        Assert.Equal("entry_0", entry.ReplyToId);
        Assert.Equal("meeting tomorrow at 9am", entry.PreviewText);

        var json = JsonSerializer.Serialize(entry);
        var copy = JsonSerializer.Deserialize<ChatIndexEntry>(json);
        Assert.NotNull(copy);
        Assert.Equal(entry.Id, copy.Id);
        Assert.Equal(entry.SearchText, copy.SearchText);
    }
}
