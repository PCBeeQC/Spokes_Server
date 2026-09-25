using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using Xunit;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public class MockNavigationManager : NavigationManager
{
    public MockNavigationManager() => Initialize("http://localhost/", "http://localhost/");
    protected override void NavigateToCore(string uri, bool forceLoad) { WasNavigated = true; LastNavigatedUri = uri; }
    public bool WasNavigated { get; set; }
    public string? LastNavigatedUri { get; set; }
}

public class ChatNotificationServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    private readonly ChatReadStateRepository _readStates;
    private readonly ChatMessageRepository _messages;
    private readonly ChatChannelRepository _channels;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly CompanyProfileRepository _companyProfiles;

    private readonly Mock<ISnackbar> _snackbarMock;
    private readonly MockNavigationManager _nav;
    private readonly ChatStateService _chatState;
    private readonly Mock<GlobalKeystoreService> _keystoreMock;
    private readonly Mock<ICryptoService> _cryptoMock;
    private readonly Mock<IWebPushService> _webPushMock;
    private readonly Mock<ChatService> _chatServiceMock;

    private readonly ChatNotificationService _service;

    public ChatNotificationServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatNotif_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfiles);
        _messages = new ChatMessageRepository(_persistence, _config, _companyProfiles);
        _channels = new ChatChannelRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _projects = new ProjectRepository(_persistence, _config);

        _snackbarMock = new Mock<ISnackbar>();
        _nav = new MockNavigationManager();
        _chatState = new ChatStateService();

        _keystoreMock = new Mock<GlobalKeystoreService>();
        _cryptoMock = new Mock<ICryptoService>();
        _webPushMock = new Mock<IWebPushService>();

        _chatServiceMock = new Mock<ChatService>(
            null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null, null, null);

        _service = new ChatNotificationService(
            _snackbarMock.Object,
            _nav,
            _chatState,
            _readStates,
            _messages,
            _channels,
            _employees,
            _teams,
            _projects,
            _keystoreMock.Object,
            _cryptoMock.Object,
            _webPushMock.Object,
            _chatServiceMock.Object,
            new Mock<Spokes_Server.Core.Services.UI.ISoundService>().Object
        );
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_SetsUserIdAndSubscribesToChannels_WhenExplicitlyProvided()
        {
            // Arrange
            var userId = "user1";
            var channels = new List<string> { "ch1", "ch2" };

            // Act
            await _service.InitializeAsync(userId, channels);

            // Assert
            // Cannot directly verify private _subscribedChannels, but we can call MarkAsReadAsync and verify ReadStates
            await _service.MarkAsReadAsync();
            var ch1State = _readStates.GetLastReadAt(userId, "ch1");
            var ch2State = _readStates.GetLastReadAt(userId, "ch2");
            Assert.True(ch1State > DateTime.MinValue);
            Assert.True(ch2State > DateTime.MinValue);
        }

        [Fact]
        public async Task InitializeAsync_LoadsUserChannels_WhenChannelsNotProvided()
        {
            // Arrange
            var emp = new Employee { Id = "user2", IsAdmin = true };
            _employees.Save(emp);
            
            var channel = new ChatChannel { Id = "ch3", Name = "Gen", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            _chatServiceMock.Setup(c => c.GetChannelsForUser("user2")).Returns([channel]);

            // Act
            await _service.InitializeAsync("user2");
            await _service.MarkAsReadAsync();

            // Assert
            var ch3State = _readStates.GetLastReadAt("user2", "ch3");
            Assert.True(ch3State > DateTime.MinValue);
        }

        [Fact]
        public async Task InitializeAsync_IgnoresIfAlreadyInitialized()
        {
            // Arrange
            await _service.InitializeAsync("user1", ["ch1"]);
            
            // Act
            await _service.InitializeAsync("user2", ["ch2"]); // Should ignore this
            await _service.MarkAsReadAsync();

            // Assert
            var ch1State = _readStates.GetLastReadAt("user1", "ch1");
            var ch2State = _readStates.GetLastReadAt("user2", "ch2");
            Assert.True(ch1State > DateTime.MinValue);
            Assert.Equal(DateTime.MinValue, ch2State); // user2 state shouldn't exist
        }

        [Fact]
        public async Task InitializeAsync_LoadsUnreadCountsAndSubscribesToEvents()
        {
            // Arrange
            var channelId = "ch1";
            await _service.InitializeAsync("user1", [channelId]);

            bool eventFired = false;
            _service.OnUnreadCountChanged += () => eventFired = true;

            var msg = new ChatMessage { Id = "msg1", ChannelId = channelId, SenderId = "other", Content = "Hi" };
            
            // Act
            _chatState.NotifyMessageReceived(msg);

            // Assert
            Assert.True(eventFired);
            Assert.Equal(1, _service.GetUnreadCount(channelId));
        }

        [Fact]
        public async Task ReloadUnreadCounts_LoadsCountsFromPersistence()
        {
            // Arrange
            var channelId = "ch1";
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            await _service.InitializeAsync("user1", [channelId]);
            
            _readStates.Save(new ChatReadState { Id = "user1_ch1", UserId = "user1", ChannelId = channelId, LastReadAt = DateTime.UtcNow.AddMinutes(-5), NotificationLevel = "All" });
            
            var msg = new ChatMessage { Id = "msg1", ChannelId = channelId, SenderId = "other", Content = "Hi", SentAt = DateTime.UtcNow, ReadBy = [] };
            _messages.Save(msg);

            // Act
            _service.ReloadUnreadCounts();

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
            Assert.Equal(1, _service.GetNotifiedUnreadCount(channelId));
        }

        [Fact]
        public async Task ReloadUnreadCounts_HandlesMutedChannels_UpdatesTotalUnreadButNotNotified()
        {
            // Arrange
            var channelId = "ch2";
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            await _service.InitializeAsync("user1", [channelId]);
            
            _readStates.MarkAsRead("user1", channelId, "General", false);
            _readStates.SetNotificationLevel("user1", channelId, "None");
            
            await Task.Delay(100);
            var msg = new ChatMessage { Id = "msg2", ChannelId = channelId, SenderId = "other", Content = "Hi", SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            // Act
            _service.ReloadUnreadCounts();

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
            Assert.Equal(0, _service.GetNotifiedUnreadCount(channelId));
        }

        [Fact]
        public async Task ReloadUnreadCounts_HandlesMentionsLevel_WithMention()
        {
            // Arrange
            var channelId = "ch3";
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            await _service.InitializeAsync("user1", [channelId]);
            
            _readStates.SetNotificationLevel("user1", channelId, "Mentions");
            
            await Task.Delay(100);
            var msg = new ChatMessage { Id = "msg3", ChannelId = channelId, SenderId = "other", Content = "Hi", MentionedUserIds = ["user1"], SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            // Act
            _service.ReloadUnreadCounts();

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
            Assert.Equal(1, _service.GetNotifiedUnreadCount(channelId));
        }

        [Fact]
        public async Task ReloadUnreadCounts_HandlesMentionsLevel_NoMention()
        {
            // Arrange
            var channelId = "ch4";
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            await _service.InitializeAsync("user1", [channelId]);
            
            _readStates.SetNotificationLevel("user1", channelId, "Mentions");
            
            await Task.Delay(100);
            var msg = new ChatMessage { Id = "msg4", ChannelId = channelId, SenderId = "other", Content = "Hi", SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            // Act
            _service.ReloadUnreadCounts();

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
            Assert.Equal(0, _service.GetNotifiedUnreadCount(channelId));
        }

        [Fact]
        public void GetUnreadCount_ReturnsZero_WhenNotExists()
        {
            // Act
            var count = _service.GetUnreadCount("nonexistent");
            // Assert
            Assert.Equal(0, count);
        }

        [Fact]
        public void GetNotifiedUnreadCount_ReturnsZero_WhenNotExists()
        {
            // Act
            var count = _service.GetNotifiedUnreadCount("nonexistent");
            // Assert
            Assert.Equal(0, count);
        }

        [Fact]
        public async Task SubscribeToChannel_AddsToSubscribedChannels()
        {
            // Arrange
            await _service.InitializeAsync("user1", []);
            
            // Act
            await _service.SubscribeToChannel("ch_new");
            await _service.MarkAsReadAsync();

            // Assert
            var state = _readStates.GetLastReadAt("user1", "ch_new");
            Assert.True(state > DateTime.MinValue);
        }

        [Fact]
        public async Task SubscribeToChannel_CalculatesUnreadCountForNewChannel()
        {
            // Arrange
            await _service.InitializeAsync("user1", []);
            
            var msg = new ChatMessage { Id = "msg5", ChannelId = "ch_new", SenderId = "other", Content = "Hi", SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            // Act
            await _service.SubscribeToChannel("ch_new");

            // Assert
            Assert.Equal(1, _service.GetUnreadCount("ch_new"));
        }

        [Fact]
        public async Task MarkChannelAsReadAsync_PersistsStateAndClearsCache()
        {
            // Arrange
            var channelId = "ch_read";
            await _service.InitializeAsync("user1", [channelId]);
            _chatState.NotifyMessageReceived(new ChatMessage { ChannelId = channelId, SenderId = "other", Content = "hi" });
            
            Assert.Equal(1, _service.GetUnreadCount(channelId));

            // Act
            await _service.MarkChannelAsReadAsync(channelId);

            // Assert
            Assert.Equal(0, _service.GetUnreadCount(channelId));
            var state = _readStates.GetLastReadAt("user1", channelId);
            Assert.True(state > DateTime.MinValue);
        }

        [Fact]
        public async Task MarkChannelAsReadAsync_SendsSilentPushNotification()
        {
            // Arrange
            var channelId = "ch_read2";
            await _service.InitializeAsync("user1", [channelId]);

            // Act
            await _service.MarkChannelAsReadAsync(channelId);
            
            // Give async background task a moment
            await Task.Delay(50);

            // Assert
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", channelId), Times.Once);
        }

        [Fact]
        public async Task MarkAsReadAsync_PersistsStateForAllChannels()
        {
            // Arrange
            await _service.InitializeAsync("user1", ["chA", "chB"]);
            _chatState.NotifyMessageReceived(new ChatMessage { ChannelId = "chA", SenderId = "other" });
            _chatState.NotifyMessageReceived(new ChatMessage { ChannelId = "chB", SenderId = "other" });
            
            Assert.Equal(1, _service.GetUnreadCount("chA"));
            Assert.Equal(1, _service.GetUnreadCount("chB"));

            // Act
            await _service.MarkAsReadAsync();

            // Assert
            Assert.Equal(0, _service.GetUnreadCount("chA"));
            Assert.Equal(0, _service.GetUnreadCount("chB"));
            
            var stateA = _readStates.GetLastReadAt("user1", "chA");
            var stateB = _readStates.GetLastReadAt("user1", "chB");
            Assert.True(stateA > DateTime.MinValue);
            Assert.True(stateB > DateTime.MinValue);
        }

        [Fact]
        public async Task MarkAsReadAsync_SendsSilentPushForAllChannels()
        {
            // Arrange
            await _service.InitializeAsync("user1", ["chA", "chB"]);

            // Act
            await _service.MarkAsReadAsync();
            
            // Give async background task a moment
            await Task.Delay(50);

            // Assert
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", "chA"), Times.Once);
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", "chB"), Times.Once);
        }

        [Fact]
        public async Task DisposeAsync_CompletesSuccessfully()
        {
            // Arrange
            await _service.InitializeAsync("user1", []);

            // Act
            await _service.DisposeAsync();

            // Assert
            Assert.True(true); // Verifies it doesn't throw and cleans up
        }

        [Fact]
        public async Task OnMessageReceived_WhenMobileAndNotFocused_IncrementsUnread_DoesNotShowSnackbarOrPlaySound()
        {
            // Arrange
            var channelId = "ch_mob";
            var circuitContext = new UserCircuitContext
            {
                DeviceType = "Mobile",
                SubscriptionId = "sub_mob_1",
                IsConnected = true,
                IsFocused = false
            };
            var presenceState = new PresenceStateService(_chatState);
            presenceState.RegisterConnection("user1", "sub_mob_1", "Mobile", isFocused: false);

            var soundMock = new Mock<ISoundService>();
            var snackbarMock = new Mock<ISnackbar>();

            var service = new ChatNotificationService(
                snackbarMock.Object,
                _nav,
                _chatState,
                _readStates,
                _messages,
                _channels,
                _employees,
                _teams,
                _projects,
                _keystoreMock.Object,
                _cryptoMock.Object,
                _webPushMock.Object,
                _chatServiceMock.Object,
                soundMock.Object,
                circuitContext,
                presenceState);

            await service.InitializeAsync("user1", [channelId]);
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            _readStates.SetNotificationLevel("user1", channelId, "All");

            var msg = new ChatMessage
            {
                Id = "msg_mob_1",
                ChannelId = channelId,
                SenderId = "other_user",
                Content = "Hello background mobile",
                SentAt = DateTime.UtcNow
            };

            // Act
            _chatState.NotifyMessageReceived(msg);

            // Assert
            // 1. Unread counts MUST still increment
            Assert.Equal(1, service.GetUnreadCount(channelId));
            Assert.Equal(1, service.GetNotifiedUnreadCount(channelId));
            Assert.Equal(1, service.TotalUnreadCount);

            // 2. Snackbar MUST NOT be shown
            Assert.Empty(snackbarMock.Invocations);

            // 3. Audio MUST NOT be played in the webview
            soundMock.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task OnMessageReceived_WhenMobileAndFocused_ShowsSnackbarAndPlaysSound()
        {
            // Arrange
            var channelId = "ch_mob_focused";
            var circuitContext = new UserCircuitContext
            {
                DeviceType = "Mobile",
                SubscriptionId = "sub_mob_2",
                IsConnected = true,
                IsFocused = true
            };
            var presenceState = new PresenceStateService(_chatState);
            presenceState.RegisterConnection("user1", "sub_mob_2", "Mobile", isFocused: true);

            var soundMock = new Mock<ISoundService>();
            var snackbarMock = new Mock<ISnackbar>();

            var service = new ChatNotificationService(
                snackbarMock.Object,
                _nav,
                _chatState,
                _readStates,
                _messages,
                _channels,
                _employees,
                _teams,
                _projects,
                _keystoreMock.Object,
                _cryptoMock.Object,
                _webPushMock.Object,
                _chatServiceMock.Object,
                soundMock.Object,
                circuitContext,
                presenceState);

            await service.InitializeAsync("user1", [channelId]);
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            _readStates.SetNotificationLevel("user1", channelId, "All");

            var msg = new ChatMessage
            {
                Id = "msg_mob_2",
                ChannelId = channelId,
                SenderId = "other_user",
                Content = "Hello focused mobile",
                SentAt = DateTime.UtcNow
            };

            // Act
            _chatState.NotifyMessageReceived(msg);

            // Assert
            Assert.Equal(1, service.GetUnreadCount(channelId));

            // Snackbar MUST be shown
            Assert.NotEmpty(snackbarMock.Invocations);

            // Audio MUST be played
            soundMock.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Chat, null, false), Times.Once);
        }

        [Fact]
        public async Task OnMessageReceived_WhenDisconnected_SuppressesSnackbar()
        {
            // Arrange
            var channelId = "ch_disconnected";
            var circuitContext = new UserCircuitContext
            {
                DeviceType = "Mobile",
                SubscriptionId = "sub_disc",
                IsConnected = false,
                IsFocused = false
            };
            var presenceState = new PresenceStateService(_chatState);

            var soundMock = new Mock<ISoundService>();
            var snackbarMock = new Mock<ISnackbar>();

            var service = new ChatNotificationService(
                snackbarMock.Object,
                _nav,
                _chatState,
                _readStates,
                _messages,
                _channels,
                _employees,
                _teams,
                _projects,
                _keystoreMock.Object,
                _cryptoMock.Object,
                _webPushMock.Object,
                _chatServiceMock.Object,
                soundMock.Object,
                circuitContext,
                presenceState);

            await service.InitializeAsync("user1", [channelId]);
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });
            _readStates.SetNotificationLevel("user1", channelId, "All");

            var msg = new ChatMessage
            {
                Id = "msg_disc",
                ChannelId = channelId,
                SenderId = "other_user",
                Content = "Hello while disconnected",
                SentAt = DateTime.UtcNow
            };

            // Act
            _chatState.NotifyMessageReceived(msg);

            // Assert
            Assert.Equal(1, service.GetUnreadCount(channelId));
            Assert.Empty(snackbarMock.Invocations);
            soundMock.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }
    }
