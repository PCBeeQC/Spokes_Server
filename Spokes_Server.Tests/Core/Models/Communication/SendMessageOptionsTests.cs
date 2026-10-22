using System.Text.Json;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Communication;

public class SendMessageOptionsTests
{
    [Fact]
    public void SendMessageOptions_Properties_WorkCorrectly()
    {
        var opts = new SendMessageOptions
        {
            SenderId = "sender_1",
            ChannelId = "channel_1",
            Content = "Test message content",
            ReplyToId = "msg_0",
            PrivateKey = "key_secret"
        };

        Assert.Equal("sender_1", opts.SenderId);
        Assert.Equal("channel_1", opts.ChannelId);
        Assert.Equal("Test message content", opts.Content);
        Assert.Equal("msg_0", opts.ReplyToId);
        Assert.Equal("key_secret", opts.PrivateKey);

        var json = JsonSerializer.Serialize(opts);
        var copy = JsonSerializer.Deserialize<SendMessageOptions>(json);
        Assert.NotNull(copy);
        Assert.Equal("sender_1", copy.SenderId);
        Assert.Equal("Test message content", copy.Content);
    }
}
