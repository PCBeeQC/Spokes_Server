using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class ChatReadReceiptHelperTests
{
    private readonly ChatChannel _dmChannel = new()
    {
        Id = "dm_alice_bob",
        ChannelType = ChatChannelType.Direct,
        ParticipantIds = new List<string> { "user-alice", "user-bob" }
    };

    private readonly ChatChannel _groupChannel = new()
    {
        Id = "group_team",
        ChannelType = ChatChannelType.Team,
        ParticipantIds = new List<string> { "user-alice", "user-bob", "user-charlie" }
    };

    [Fact]
    public void DM_UnreadMessage_ShowsNoAvatars()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-alice" }
        };

        var (latest, cumulative) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1 });

        Assert.False(latest.ContainsKey("m1"));
        Assert.False(cumulative.ContainsKey("m1"));
    }

    [Fact]
    public void DM_ReadMessage_ShowsRecipientAvatarOnSenderMessage()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-alice", "user-bob" }
        };

        var (latest, cumulative) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1 });

        Assert.True(latest.ContainsKey("m1"));
        Assert.Contains("user-bob", latest["m1"]);
        Assert.DoesNotContain("user-alice", latest["m1"]);

        Assert.True(cumulative.ContainsKey("m1"));
        Assert.Contains("user-bob", cumulative["m1"]);
    }

    [Fact]
    public void DM_WhenRecipientReplies_ReadReceiptOnOriginalMessagePersists()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-alice", "user-bob" }
        };

        // Bob replies with m2 (unread by Alice)
        var m2 = new ChatMessage
        {
            Id = "m2",
            SenderId = "user-bob",
            SentAt = DateTime.UtcNow.AddMinutes(-4),
            ReadBy = new List<string> { "user-bob" }
        };

        var (latest, cumulative) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1, m2 });

        // Bob's avatar should remain on Alice's message m1
        Assert.True(latest.ContainsKey("m1"));
        Assert.Contains("user-bob", latest["m1"]);

        // Alice hasn't read m2 yet, so m2 should have no avatars
        Assert.False(latest.ContainsKey("m2"));
    }

    [Fact]
    public void DM_WhenBothUsersReadEachOthersMessages_BothShowAppropriateAvatars()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-alice", "user-bob" }
        };

        var m2 = new ChatMessage
        {
            Id = "m2",
            SenderId = "user-bob",
            SentAt = DateTime.UtcNow.AddMinutes(-4),
            ReadBy = new List<string> { "user-bob", "user-alice" }
        };

        var (latest, cumulative) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1, m2 });

        // m1 (sent by Alice) should show Bob's avatar
        Assert.True(latest.ContainsKey("m1"));
        Assert.Contains("user-bob", latest["m1"]);

        // m2 (sent by Bob) should show Alice's avatar
        Assert.True(latest.ContainsKey("m2"));
        Assert.Contains("user-alice", latest["m2"]);
    }

    [Fact]
    public void DM_WhenAliceSendsNewMessage_BobAvatarDoesNotDisappearFromM1UntilM3Read()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-10),
            ReadBy = new List<string> { "user-alice", "user-bob" }
        };

        var m2 = new ChatMessage
        {
            Id = "m2",
            SenderId = "user-bob",
            SentAt = DateTime.UtcNow.AddMinutes(-8),
            ReadBy = new List<string> { "user-bob", "user-alice" }
        };

        // Alice sends m3, which Bob has NOT read yet
        var m3 = new ChatMessage
        {
            Id = "m3",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-2),
            ReadBy = new List<string> { "user-alice" }
        };

        var (latest, _) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1, m2, m3 });

        // Bob's avatar should remain on m1 (the latest sent by Alice that Bob has read)
        Assert.True(latest.ContainsKey("m1"));
        Assert.Contains("user-bob", latest["m1"]);

        // m3 has NOT been seen by Bob, so m3 has no avatars
        Assert.False(latest.ContainsKey("m3"));

        // Alice's avatar is still on m2
        Assert.True(latest.ContainsKey("m2"));
        Assert.Contains("user-alice", latest["m2"]);

        // NOW Bob reads m3
        m3.ReadBy.Add("user-bob");

        var (latestAfterBobReadsM3, _) = ChatReadReceiptHelper.CalculateReadReceipts(_dmChannel, new[] { m1, m2, m3 });

        // Bob's avatar moves from m1 to m3!
        Assert.False(latestAfterBobReadsM3.ContainsKey("m1"));
        Assert.True(latestAfterBobReadsM3.ContainsKey("m3"));
        Assert.Contains("user-bob", latestAfterBobReadsM3["m3"]);
    }

    [Fact]
    public void Group_CursorModel_SenderAdvancesCursorAndSuppressesAvatarOnOwnMessage()
    {
        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-10),
            ReadBy = new List<string> { "user-alice", "user-bob", "user-charlie" }
        };

        // Bob sends m2
        var m2 = new ChatMessage
        {
            Id = "m2",
            SenderId = "user-bob",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-bob" }
        };

        var (latest, _) = ChatReadReceiptHelper.CalculateReadReceipts(_groupChannel, new[] { m1, m2 });

        // Charlie is still on m1
        Assert.True(latest.ContainsKey("m1"));
        Assert.Contains("user-charlie", latest["m1"]);

        // Bob's cursor moved to m2, but Bob is suppressed on his own message
        Assert.DoesNotContain("user-bob", latest["m1"]);
        Assert.False(latest.ContainsKey("m2"));
    }

    [Fact]
    public void SelfDM_ShowsNoAvatars()
    {
        var selfChannel = new ChatChannel
        {
            Id = "self_alice",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user-alice" }
        };

        var m1 = new ChatMessage
        {
            Id = "m1",
            SenderId = "user-alice",
            SentAt = DateTime.UtcNow.AddMinutes(-5),
            ReadBy = new List<string> { "user-alice" }
        };

        var (latest, cumulative) = ChatReadReceiptHelper.CalculateReadReceipts(selfChannel, new[] { m1 });

        Assert.False(latest.ContainsKey("m1"));
        Assert.False(cumulative.ContainsKey("m1"));
    }
}
