using System.Collections.Generic;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class ChatChannelFormatterTests
{
    private readonly Employee _currentUser = new()
    {
        Id = "user1",
        FirstName = "Mats",
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

    [Fact]
    public void IsSelfDirectMessage_ReturnsTrue_WhenTwoIdenticalParticipantIds()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user1" }
        };

        Assert.True(ChatChannelFormatter.IsSelfDirectMessage(channel, "user1"));
    }

    [Fact]
    public void IsSelfDirectMessage_ReturnsTrue_WhenSingleParticipantId()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1" }
        };

        Assert.True(ChatChannelFormatter.IsSelfDirectMessage(channel, "user1"));
    }

    [Fact]
    public void IsSelfDirectMessage_ReturnsFalse_WhenDirectMessageToOtherUser()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user1", "user2" }
        };

        Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channel, "user1"));
    }

    [Fact]
    public void IsSelfDirectMessage_ReturnsFalse_WhenNonDirectChannel()
    {
        var channel = new ChatChannel
        {
            ChannelType = ChatChannelType.General,
            ParticipantIds = new List<string> { "user1", "user1" }
        };

        Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channel, "user1"));
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

        Assert.Equal("Mats Larsson (you)", displayName);
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

        Assert.Equal("Mats Larsson (you)", displayName);
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

        Assert.Equal("Mats Larsson", displayName);
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

        Assert.Equal("Mats Larsson (you)", displayName);
    }
}
