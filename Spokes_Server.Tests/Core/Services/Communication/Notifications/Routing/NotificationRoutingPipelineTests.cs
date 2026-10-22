using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Routing;

public class NotificationRoutingPipelineTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ChatReadStateRepository _readStates;
    private readonly TeamRepository _teams;
    private readonly ChatMessageRepository _chatMessages;

    public NotificationRoutingPipelineTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_NotifPipeline_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfiles);
        _teams = new TeamRepository(_persistence, _config);
        _chatMessages = new ChatMessageRepository(_persistence, _config, _companyProfiles);
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
    public void RecipientFilter_ShouldNotifyChat_WhenSenderBlocked_ReturnsFalse()
    {
        var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
        var employee = new Employee
        {
            Id = "emp1",
            ChatNotificationsEnabled = true,
            BlockedUserIds = ["blocked_user"]
        };
        var sender = new Employee { Id = "blocked_user" };
        var channel = new ChatChannel { Id = "ch1", ChannelType = ChatChannelType.General };
        var message = new ChatMessage { Id = "msg1", Content = "Hello" };

        var shouldNotify = filter.ShouldNotifyChat(employee, sender, channel, message);

        Assert.False(shouldNotify);
    }

    [Fact]
    public void RecipientFilter_ShouldNotifyChat_WhenMentionsOnlyAndNotMentioned_ReturnsFalse()
    {
        var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
        var employee = new Employee
        {
            Id = "emp1",
            ChatNotificationsEnabled = true
        };
        var sender = new Employee { Id = "sender1" };
        var channel = new ChatChannel { Id = "ch1", ChannelType = ChatChannelType.General };
        var message = new ChatMessage
        {
            Id = "msg1",
            Content = "Hello",
            MentionedUserIds = ["other_user"]
        };

        // Set channel notification preference to Mentions
        var state = new ChatReadState
        {
            UserId = "emp1",
            ChannelId = "ch1",
            NotificationLevel = "Mentions"
        };
        _readStates.Save(state);

        var shouldNotify = filter.ShouldNotifyChat(employee, sender, channel, message);

        Assert.False(shouldNotify);
    }

    [Fact]
    public void RecipientFilter_IsUserMentioned_WhenTeamMentionedButUserNotOnMentionedTeam_ReturnsFalse()
    {
        var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
        var employee = new Employee
        {
            Id = "emp1",
            TeamId = "other_team",
            ChatNotificationsEnabled = true
        };
        var message = new ChatMessage
        {
            Id = "msg1",
            Content = "Hello @[Dev Team]",
            MentionsTeam = true,
            MentionedUserIds = ["emp2"]
        };

        var isMentioned = filter.IsUserMentioned("emp1", employee, message);

        Assert.False(isMentioned);
    }

    [Fact]
    public void RecipientFilter_IsUserMentioned_WhenUserInMentionedUserIds_ReturnsTrue()
    {
        var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
        var employee = new Employee
        {
            Id = "emp2",
            TeamId = "dev_team",
            ChatNotificationsEnabled = true
        };
        var message = new ChatMessage
        {
            Id = "msg1",
            Content = "Hello @[Dev Team]",
            MentionsTeam = true,
            MentionedUserIds = ["emp2"]
        };

        var isMentioned = filter.IsUserMentioned("emp2", employee, message);

        Assert.True(isMentioned);
    }

    [Fact]
    public void RecipientFilter_ShouldNotifyReaction_WhenReactionNotificationsDisabled_ReturnsFalse()
    {
        var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
        var originalSender = new Employee
        {
            Id = "orig1",
            PushNotificationsEnabled = true,
            ReactionNotificationsEnabled = false
        };
        var reactionSender = new Employee { Id = "react1" };

        var shouldNotify = filter.ShouldNotifyReaction(originalSender, reactionSender);

        Assert.False(shouldNotify);
    }

    [Fact]
    public void ContentDecryptor_DecryptAndFormatContent_UnencryptedMessage_FormatsAndTruncates()
    {
        var keystore = new GlobalKeystoreService();
        var cryptoMock = new Mock<ICryptoService>();
        var decryptor = new NotificationContentDecryptor(keystore, cryptoMock.Object);

        var channel = new ChatChannel { Id = "ch1" };
        var message = new ChatMessage
        {
            Id = "msg1",
            Content = "This is a plain message text that is long enough to verify truncation logic properly works.",
            IsEncrypted = false
        };

        var result = decryptor.DecryptAndFormatContent(message, channel, "user1", 30);

        Assert.True(result.Length <= 30);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void RateLimiter_ShouldSuppressSound_WhenConsecutiveLimitExceeded_ReturnsTrue()
    {
        var rateLimiter = new NotificationRateLimiter(_readStates, _chatMessages);
        var employee = new Employee
        {
            Id = "emp1",
            LimitConsecutiveNotificationSounds = true,
            ConsecutiveNotificationSoundLimit = 2
        };

        var lastRead = DateTime.UtcNow.AddMinutes(-10);
        _readStates.Save(new ChatReadState
        {
            Id = ChatReadState.CreateId("emp1", "ch1"),
            UserId = "emp1",
            ChannelId = "ch1",
            LastReadAt = lastRead
        });

        // Add 3 unread messages
        for (int i = 0; i < 3; i++)
        {
            _chatMessages.Save(new ChatMessage
            {
                Id = $"msg_{i}",
                ChannelId = "ch1",
                SenderId = "other",
                SentAt = DateTime.UtcNow.AddMinutes(-5 + i)
            });
        }

        var shouldSuppress = rateLimiter.ShouldSuppressSound(employee, "ch1");

        Assert.True(shouldSuppress);
    }
}
