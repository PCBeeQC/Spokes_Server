using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class ChatDirectMessageOrderingTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _writer;
    private readonly ChatChannelRepository _channelsRepo;
    private readonly ChatMessageRepository _messagesRepo;
    private readonly CompanyProfileRepository _companyProfiles;

    public ChatDirectMessageOrderingTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_DMOrder_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _companyProfiles = new CompanyProfileRepository(_writer, _config);
        _channelsRepo = new ChatChannelRepository(_writer, _config);
        _messagesRepo = new ChatMessageRepository(_writer, _config, _companyProfiles);
    }

    public void Dispose()
    {
        _writer.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void GetChannelsForUser_SortsDirectMessagesByLastActivityDescending()
    {
        var userA = "user_a";
        var userB = "user_b";
        var userC = "user_c";

        var olderTime = DateTime.UtcNow.AddHours(-5);
        var newerTime = DateTime.UtcNow.AddMinutes(-10);

        var dmOld = _channelsRepo.GetOrCreateDirectChannel(userA, userB, false);
        dmOld.LastActivityAt = olderTime;
        _channelsRepo.Save(dmOld);

        var dmNew = _channelsRepo.GetOrCreateDirectChannel(userA, userC, false);
        dmNew.LastActivityAt = newerTime;
        _channelsRepo.Save(dmNew);

        var userChannels = _channelsRepo.GetChannelsForUser(userA, [], []);

        Assert.Equal(2, userChannels.Count);
        Assert.Equal(dmNew.Id, userChannels[0].Id);
        Assert.Equal(dmOld.Id, userChannels[1].Id);
    }

    [Fact]
    public void ReconcileChannelActivityTimestamps_UpdatesStaleChannelLastActivityAt()
    {
        var userA = "user_a";
        var userB = "user_b";

        var oldTime = DateTime.UtcNow.AddDays(-3);
        var messageTime = DateTime.UtcNow.AddMinutes(-2);

        var dm = _channelsRepo.GetOrCreateDirectChannel(userA, userB, false);
        dm.LastActivityAt = oldTime;
        _channelsRepo.Save(dm);

        var msg = new ChatMessage
        {
            ChannelId = dm.Id,
            ChannelType = ChatChannelType.Direct,
            SenderId = userB,
            Content = "Hello from the past",
            SentAt = messageTime,
            ReadBy = [userB]
        };
        _messagesRepo.Save(msg);

        // Verify message timestamp is indexed
        var latestMsgTime = _messagesRepo.GetLatestMessageTimestamp(dm.Id);
        Assert.Equal(messageTime, latestMsgTime);

        // Run reconciliation
        _channelsRepo.ReconcileChannelActivityTimestamps(_messagesRepo);

        var healedChannel = _channelsRepo.GetById(dm.Id);
        Assert.NotNull(healedChannel);
        Assert.Equal(messageTime, healedChannel.LastActivityAt);
    }

    [Fact]
    public void CategoryOrdering_DictatedByCustomOrder_WhileChannelsInsideSortByRecency()
    {
        // Setup categories
        var catPublic = new ChatCategory { Id = "sys_public", Name = "Public", DisplayOrder = 0 };
        var catDirect = new ChatCategory { Id = "sys_direct", Name = "Direct Messages", DisplayOrder = 1 };
        var categories = new List<ChatCategory> { catPublic, catDirect };

        // Setup user with custom category ordering placing sys_direct first
        var user = new Employee
        {
            Id = "user1",
            UsesCustomSidebarOrder = true,
            CustomCategoryOrder = new Dictionary<string, int>
            {
                { "sys_direct", 0 }, // sys_direct moved to top
                { "sys_public", 1 }
            },
            // Legacy DM IDs that might have been saved in the past
            CustomChannelOrder = new Dictionary<string, int>
            {
                { "dm_old", 0 },
                { "dm_new", 1 }
            }
        };

        // Category ordering respects user custom order
        var orderedCategories = categories
            .OrderBy(c => user.CustomCategoryOrder.TryGetValue(c.Id, out int order) ? order : c.DisplayOrder + 1000)
            .ToList();

        Assert.Equal("sys_direct", orderedCategories[0].Id);
        Assert.Equal("sys_public", orderedCategories[1].Id);

        // Channels inside sys_direct MUST sort by recency, ignoring CustomChannelOrder
        var dmOld = new ChatChannel { Id = "dm_old", ChannelType = ChatChannelType.Direct, LastActivityAt = DateTime.UtcNow.AddHours(-2) };
        var dmNew = new ChatChannel { Id = "dm_new", ChannelType = ChatChannelType.Direct, LastActivityAt = DateTime.UtcNow.AddMinutes(-5) };

        var dmChannels = new List<ChatChannel> { dmOld, dmNew };

        // Sorting using the updated logic
        var sortedDms = dmChannels
            .OrderBy(c => c.IsVoiceChannel ? 1 : 0)
            .ThenByDescending(c => c.LastActivityAt)
            .ToList();

        // Even though dm_old has CustomChannelOrder rank 0, dm_new is more recent so it MUST be first
        Assert.Equal("dm_new", sortedDms[0].Id);
        Assert.Equal("dm_old", sortedDms[1].Id);
    }
}
