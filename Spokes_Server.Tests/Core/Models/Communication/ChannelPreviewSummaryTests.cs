using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class ChannelPreviewSummaryTests
{
    [Fact]
    public void ChannelPreviewSummary_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var summary = new ChannelPreviewSummary
        {
            MessageId = "msg_1",
            ChannelId = "c1",
            SenderId = "user_1",
            SentAt = now,
            PreviewText = "Hello world",
            IsEncrypted = false,
            EncryptedContent = null
        };

        Assert.Equal("msg_1", summary.MessageId);
        Assert.Equal("c1", summary.ChannelId);
        Assert.Equal("user_1", summary.SenderId);
        Assert.Equal(now, summary.SentAt);
        Assert.Equal("Hello world", summary.PreviewText);
        Assert.False(summary.IsEncrypted);

        var json = JsonSerializer.Serialize(summary);
        var roundtripped = JsonSerializer.Deserialize<ChannelPreviewSummary>(json);
        Assert.NotNull(roundtripped);
        Assert.Equal("c1", roundtripped.ChannelId);
        Assert.Equal("Hello world", roundtripped.PreviewText);
    }
}
