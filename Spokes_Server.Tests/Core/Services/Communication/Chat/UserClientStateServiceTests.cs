using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class UserClientStateServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly UserClientStateRepository _clientStatesRepo;
    private readonly Mock<Database> _mockDb;
    private readonly ChatStateService _chatState;
    private readonly Mock<IHubContext<ChatHub>> _mockHubContext;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly UserClientStateService _service;

    public UserClientStateServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_UserClientStateService_{Guid.NewGuid()}");
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockLogger.Object);

        _clientStatesRepo = new UserClientStateRepository(_writer, mockConfig.Object);
        var sessionRepo = new DeviceSessionRepository(_writer, mockConfig.Object);
        var empRepo = new EmployeeRepository(_writer, mockConfig.Object);

        _mockDb = new Mock<Database>(sessionRepo, empRepo);
        _mockDb.Setup(d => d.ClientStates).Returns(_clientStatesRepo);

        _chatState = new ChatStateService();

        _mockHubContext = new Mock<IHubContext<ChatHub>>();
        var mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        var serviceLogger = Mock.Of<ILogger<UserClientStateService>>();
        _service = new UserClientStateService(_mockDb.Object, _chatState, _mockHubContext.Object, serviceLogger);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
        _writer.Dispose();
    }

    [Fact]
    public void UpdateDraftInMemory_DoesNotWriteToDisk_WhileTyping()
    {
        // Act: update draft as user types
        _service.UpdateDraftInMemory("user1", "chan1", "Draft in progress");

        // Assert: draft is available in service
        var draft = _service.GetDraft("user1", "chan1");
        Assert.Equal("Draft in progress", draft);

        // Assert: disk repository has ZERO writes (zero disk writes during typing requirement)
        var diskDraft = _clientStatesRepo.GetDraft("user1", "chan1");
        Assert.Null(diskDraft);
    }

    [Fact]
    public void GetActiveDraftChannel_WhenDraftExists_ReturnsChannel()
    {
        // Arrange
        _service.UpdateDraftInMemory("user1", "chan1", "Draft in progress");

        // Act
        var activeChannel = _service.GetActiveDraftChannel("user1");

        // Assert
        Assert.Equal("chan1", activeChannel);
    }

    [Fact]
    public void GetActiveDraftChannel_WhenNoDraftExists_ReturnsNull()
    {
        // Act (user has no drafts)
        var activeChannel = _service.GetActiveDraftChannel("user1");

        // Assert: returns null so mobile app opens the standard home screen (/)
        Assert.Null(activeChannel);
    }

    [Fact]
    public void SetTyping_And_StopActiveTyping_ClearsTyping_AndNotifies()
    {
        // Arrange
        bool stoppedEventFired = false;
        _chatState.UserStoppedTyping += (chan, user) =>
        {
            if (chan == "chan1" && user == "user1") stoppedEventFired = true;
        };

        _service.SetTyping("user1", "chan1", true);

        // Act
        _service.StopActiveTyping("user1");

        // Assert
        Assert.True(stoppedEventFired);
        _mockClientProxy.Verify(
            p => p.SendCoreAsync("UserStoppedTyping", It.IsAny<object[]>(), default),
            Times.Once);
    }

    [Fact]
    public void FlushToDiskIfUnsent_PersistsUnsentDraftToDisk_AndStopsTyping()
    {
        // Arrange
        bool stoppedEventFired = false;
        _chatState.UserStoppedTyping += (chan, user) =>
        {
            if (chan == "chan1" && user == "user1") stoppedEventFired = true;
        };

        _service.UpdateDraftInMemory("user1", "chan1", "Unsent message before backgrounding");
        _service.SetTyping("user1", "chan1", true);

        // Act: user backgrounds or disconnects before sending
        _service.FlushToDiskIfUnsent("user1");

        // Assert: draft is now persisted to disk
        var persistedDraft = _clientStatesRepo.GetDraft("user1", "chan1");
        Assert.Equal("Unsent message before backgrounding", persistedDraft);

        // Assert: typing was immediately stopped
        Assert.True(stoppedEventFired);
    }

    [Fact]
    public void ClearDraft_RemovesMemoryAndDisk_AndStopsTyping()
    {
        // Arrange: prepare draft and flush to disk
        _service.UpdateDraftInMemory("user1", "chan1", "Message to send");
        _service.FlushToDiskIfUnsent("user1");
        Assert.NotNull(_clientStatesRepo.GetDraft("user1", "chan1"));

        // Act: message is sent
        _service.ClearDraft("user1", "chan1");

        // Assert: cleared from both memory and disk
        Assert.Null(_service.GetDraft("user1", "chan1"));
        Assert.Null(_clientStatesRepo.GetDraft("user1", "chan1"));
        Assert.Null(_service.GetActiveDraftChannel("user1"));
    }

    [Fact]
    public void GetActiveDraftChannel_WhenChannelDoesNotExist_ReturnsNull()
    {
        // Arrange: Mock ChatChannels to simulate non-existent / deleted channel
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        var chatChannelRepo = new ChatChannelRepository(_writer, mockConfig.Object);
        _mockDb.Setup(d => d.ChatChannels).Returns(chatChannelRepo);

        _service.UpdateDraftInMemory("user1", "deleted-chan", "Draft in deleted channel");

        // Act
        var activeChannel = _service.GetActiveDraftChannel("user1");

        // Assert: Channel does not exist in repository, so returns null instead of stale channel
        Assert.Null(activeChannel);
    }
}
