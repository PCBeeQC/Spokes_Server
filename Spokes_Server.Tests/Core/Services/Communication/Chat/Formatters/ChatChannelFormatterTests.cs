using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Communication.Chat.Formatters;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat.Formatters;

public class ChatChannelFormatterTests
{
    private readonly Employee _currentUser = new()
    {
        Id = "user1",
        FirstName = "Alex",
        LastName = "Larsson"
    };

    private readonly Employee _otherUser = new()
    {
        Id = "user2",
        FirstName = "Jane",
        LastName = "Doe"
    };

    private readonly Employee _thirdUser = new()
    {
        Id = "user3",
        FirstName = "Bob",
        LastName = "Smith"
    };

    private readonly Employee _fourthUser = new()
    {
        Id = "user4",
        FirstName = "Alice",
        LastName = "Jones"
    };

    private readonly Employee _fifthUser = new()
    {
        Id = "user5",
        FirstName = "Charlie",
        LastName = "Brown"
    };

    private Employee? LookupEmployee(string id) => id switch
    {
        "user1" => _currentUser,
        "user2" => _otherUser,
        "user3" => _thirdUser,
        "user4" => _fourthUser,
        "user5" => _fifthUser,
        _ => null
    };

    [Theory]
    [InlineData(ChatChannelType.Direct, new[] { "user1", "user1" }, true)]
    [InlineData(ChatChannelType.Direct, new[] { "user1" }, true)]
    [InlineData(ChatChannelType.Direct, new[] { "user1", "user2" }, false)]
    [InlineData(ChatChannelType.General, new[] { "user1", "user1" }, false)]
    public void IsSelfDirectMessage_EvaluatesChannelTypeAndParticipants(string channelType, string[] participantIds, bool expected)
    {
        var channel = new ChatChannel
        {
            ChannelType = channelType,
            ParticipantIds = participantIds.ToList()
        };

        Assert.Equal(expected, ChatChannelFormatter.IsSelfDirectMessage(channel, "user1"));
    }

    [Fact]
    public void GetDisplayName_SelfDirectMessage_ReturnsFullNameWithYouSuffix()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user1" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Alex Larsson (you)", displayName);
    }

    [Fact]
    public void GetDisplayName_SelfDirectMessage_SingleParticipant_ReturnsFullNameWithYouSuffix()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Alex Larsson (you)", displayName);
    }

    [Fact]
    public void GetDisplayName_SelfDirectMessage_WhenEmployeeNotFound_ReturnsUnknownUserWithSuffix()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "missing_user", "missing_user" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "missing_user", _ => null);

        Assert.Equal("Unknown User (you)", displayName);
    }

    [Fact]
    public void GetDisplayName_StandardDM_ViewedByUser1_ReturnsOtherUserFullName()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Jane Doe", displayName);
    }

    [Fact]
    public void GetDisplayName_StandardDM_ViewedByUser2_ReturnsOtherUserFullName()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user2", LookupEmployee);

        Assert.Equal("Alex Larsson", displayName);
    }

    [Fact]
    public void GetDisplayName_GroupChat_WithCustomName_ReturnsCustomName()
    {
        var channel = new ChatChannel
        {
            Name = "Project Alpha Chat",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = new List<string> { "user1", "user2", "user3" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Project Alpha Chat", displayName);
    }

    [Fact]
    public void GetDisplayName_GroupChat_WithoutName_ReturnsCommaSeparatedFirstNames()
    {
        var channel = new ChatChannel
        {
            Name = "",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = new List<string> { "user1", "user2", "user3" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Jane, Bob", displayName);
    }

    [Fact]
    public void GetDisplayName_GroupChat_MoreThanThreeOthers_AppendsOverflowCount()
    {
        var channel = new ChatChannel
        {
            Name = "",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = new List<string> { "user1", "user2", "user3", "user4", "user5" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Jane, Bob, Alice +1", displayName);
    }

    [Fact]
    public void GetDisplayName_GeneralChannel_ReturnsChannelName()
    {
        var channel = new ChatChannel
        {
            Name = "announcements",
            ChannelType = ChatChannelType.General
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("announcements", displayName);
    }

    [Fact]
    public void GetDisplayName_NullChannel_ReturnsUnnamedChannel()
    {
        var displayName = ChatChannelFormatter.GetDisplayName(null, "user1", LookupEmployee);

        Assert.Equal("Unnamed Channel", displayName);
    }

    [Fact]
    public void GetDisplayName_SupportsEmployeesCollectionOverload()
    {
        var employees = new List<Employee> { _currentUser, _otherUser };
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user1" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", employees);

        Assert.Equal("Alex Larsson (you)", displayName);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(false, "user1")]
    public void IsSelfDirectMessage_ReturnsFalse_WhenChannelIsNull_OrUserIdIsEmpty(bool channelExists, string? userId)
    {
        var channel = channelExists
            ? new ChatChannel
            {
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user1" }
            }
            : null;

        Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channel, userId));
    }

    [Fact]
    public void IsSelfDirectMessage_ReturnsFalse_WhenParticipantIdsNullOrEmpty()
    {
        var channelWithNull = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = null!
        };
        var channelWithEmpty = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string>()
        };

        Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channelWithNull, "user1"));
        Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channelWithEmpty, "user1"));
    }

    [Fact]
    public void GetDisplayName_WhenEmployeesCollectionIsNull_FallsBackGracefully()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", (IEnumerable<Employee>?)null);

        Assert.Equal("Unknown User", displayName);
    }

    [Fact]
    public void GetDisplayName_LegacyGroupDirectMessage_MoreThanTwoParticipants()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2", "user3", "user4" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Jane, Bob, Alice", displayName);
    }

    [Fact]
    public void GetDisplayName_LegacyGroupDirectMessage_WithCustomName_ReturnsCustomName()
    {
        var channel = new ChatChannel
        {
            Name = "Legacy Team Chat",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2", "user3" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Legacy Team Chat", displayName);
    }

    [Fact]
    public void GetDisplayName_LegacyGroupDirectMessage_WhenNoEmployeesMatch_ReturnsDirectMessage()
    {
        var channel = new ChatChannel
        {
            Name = "",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "unknown1", "unknown2" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Direct Message", displayName);
    }

    [Fact]
    public void GetDisplayName_GroupChat_WithoutName_WhenNoEmployeesMatch_ReturnsGroupChat()
    {
        var channel = new ChatChannel
        {
            Name = "",
            ChannelType = ChatChannelType.Group,
            ParticipantIds = new List<string> { "user1", "unknown1" }
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Group Chat", displayName);
    }

    [Fact]
    public void GetDisplayName_GeneralChannel_WithoutName_ReturnsUnnamedChannel()
    {
        var channel = new ChatChannel
        {
            Name = "   ",
            ChannelType = ChatChannelType.General
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Unnamed Channel", displayName);
    }

    [Fact]
    public void GetDisplayName_DirectMessage_WithNoParticipants_ReturnsUnknownUser()
    {
        var channel = new ChatChannel
        {
            Name = "Direct Message",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string>()
        };

        var displayName = ChatChannelFormatter.GetDisplayName(channel, "user1", LookupEmployee);

        Assert.Equal("Unknown User", displayName);
    }

    [Fact]
    public void GetDirectMessageParticipant_StandardDM_ReturnsOtherEmployee()
    {
        var employees = new List<Employee> { _currentUser, _otherUser };
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        var participant = ChatChannelFormatter.GetDirectMessageParticipant(channel, "user1", employees);

        Assert.Same(_otherUser, participant);
    }

    [Fact]
    public void GetDirectMessageParticipant_SelfDM_ReturnsCurrentEmployee()
    {
        var employees = new List<Employee> { _currentUser, _otherUser };
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user1" }
        };

        var participant = ChatChannelFormatter.GetDirectMessageParticipant(channel, "user1", employees);

        Assert.Same(_currentUser, participant);
    }

    [Fact]
    public void GetDirectMessageParticipant_LegacyGroupDM_ReturnsNull()
    {
        var employees = new List<Employee> { _currentUser, _otherUser, _thirdUser };
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2", "user3" }
        };

        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipantId(channel, "user1"));
        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipant(channel, "user1", employees));
    }

    [Theory]
    [InlineData(ChatChannelType.General)]
    [InlineData(ChatChannelType.Group)]
    [InlineData(ChatChannelType.Team)]
    [InlineData(ChatChannelType.Project)]
    public void GetDirectMessageParticipantId_NonDirectChannel_ReturnsNull(string channelType)
    {
        var channel = new ChatChannel
        {
            ChannelType = channelType,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipantId(channel, "user1"));
    }

    [Fact]
    public void GetDirectMessageParticipant_NullOrEmptyInputs_ReturnsNull()
    {
        var dmChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };
        var emptyChannel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string>()
        };

        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipant(null, "user1", new[] { _currentUser }));
        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipant(emptyChannel, "user1", new[] { _currentUser }));
        Assert.Null(ChatChannelFormatter.GetDirectMessageParticipant(dmChannel, "user1", null));
    }
}
