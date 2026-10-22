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
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Microsoft.AspNetCore.DataProtection;
using Spokes_Server.Core.Services.Security;

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
    private readonly Mock<IChatChannelAccessService> _chatAccessMock;

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

        _chatAccessMock = new Mock<IChatChannelAccessService>();

        _service = CreateChatNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(_snackbarMock.Object, _nav, new Mock<ISoundService>().Object),
            new NotificationRecipientFilter(_readStates, _messages),
            new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
            new Mock<ISoundService>().Object);
    }

    private ChatNotificationService CreateChatNotificationService(
        IUiNotificationPresenter presenter,
        NotificationRecipientFilter recipientFilter,
        NotificationContentDecryptor contentDecryptor,
        ISoundService soundService,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IInAppNotificationDispatcher? inAppDispatcher = null,
        ScopedKeystoreService? scopedKeystore = null)
    {
        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ChatChannels).Returns(_channels);
        commContext.Setup(c => c.ChatMessages).Returns(_messages);
        commContext.Setup(c => c.ChatReadStates).Returns(_readStates);

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);

        return new ChatNotificationService(
            commContext.Object,
            hrContext.Object,
            presenter,
            _chatState,
            recipientFilter,
            contentDecryptor,
            _webPushMock.Object,
            _chatAccessMock.Object,
            soundService,
            circuitContext,
            presenceState,
            inAppDispatcher,
            scopedKeystore);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
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

            _chatAccessMock.Setup(c => c.GetChannelsForUser("user2")).Returns([channel]);

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
            
            _readStates.Save(new ChatReadState { Id = ChatReadState.CreateId("user1", channelId), UserId = "user1", ChannelId = channelId, LastReadAt = DateTime.UtcNow.AddMinutes(-5), NotificationLevel = "None" });
            
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
            
            _readStates.Save(new ChatReadState { Id = ChatReadState.CreateId("user1", channelId), UserId = "user1", ChannelId = channelId, LastReadAt = DateTime.UtcNow.AddMinutes(-5), NotificationLevel = "Mentions" });
            
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
            
            _readStates.Save(new ChatReadState { Id = ChatReadState.CreateId("user1", channelId), UserId = "user1", ChannelId = channelId, LastReadAt = DateTime.UtcNow.AddMinutes(-5), NotificationLevel = "Mentions" });
            
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
        public async Task SubscribeToChannelAsync_AddsToSubscribedChannels()
        {
            // Arrange
            await _service.InitializeAsync("user1", []);
            
            // Act
            await _service.SubscribeToChannelAsync("ch_new");
            await _service.MarkAsReadAsync();

            // Assert
            var state = _readStates.GetLastReadAt("user1", "ch_new");
            Assert.True(state > DateTime.MinValue);
        }

        [Fact]
        public async Task SubscribeToChannelAsync_CalculatesUnreadCountForNewChannel()
        {
            // Arrange
            await _service.InitializeAsync("user1", []);
            
            var msg = new ChatMessage { Id = "msg5", ChannelId = "ch_new", SenderId = "other", Content = "Hi", SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            // Act
            await _service.SubscribeToChannelAsync("ch_new");

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
            
            // Wait for push task to complete deterministically without arbitrary delays or races
            if (_service.LastBackgroundTask != null)
            {
                await _service.LastBackgroundTask;
            }

            // Assert
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", channelId), Times.Once);
        }

        [Fact]
        public async Task MarkChannelAsReadAsync_ConsecutiveCallsWithoutNewMessages_SendsSilentPushOnlyOnce()
        {
            // Arrange
            var channelId = "ch_dedup_push";
            _service.BackgroundTaskRunner = work => work();
            await _service.InitializeAsync("user1", [channelId]);

            // Act: Call MarkChannelAsReadAsync three times in a row with no new messages
            await _service.MarkChannelAsReadAsync(channelId);
            await _service.MarkChannelAsReadAsync(channelId);
            await _service.MarkChannelAsReadAsync(channelId);

            // Assert: SendClearNotificationAsync was only invoked once
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", channelId), Times.Once);

            // Act 2: Receive a new message in that channel and mark as read again
            _chatState.NotifyMessageReceived(new ChatMessage
            {
                Id = "msg_new",
                ChannelId = channelId,
                SenderId = "other",
                Content = "New message",
                SentAt = DateTime.UtcNow.AddSeconds(1)
            });
            await _service.MarkChannelAsReadAsync(channelId);

            // Assert 2: SendClearNotificationAsync is invoked a second time for the new message
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", channelId), Times.Exactly(2));
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
            
            // Wait for background tasks to complete deterministically without countdown timeouts
            if (_service.LastBackgroundTask != null)
            {
                await _service.LastBackgroundTask;
            }

            // Assert
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", "chA"), Times.Once);
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", "chB"), Times.Once);
        }

        [Fact]
        public async Task MarkAsReadAsync_WithCustomBackgroundTaskRunner_ExecutesInlineSynchronously()
        {
            // Arrange
            var customRunnerInvoked = false;
            _service.BackgroundTaskRunner = async work =>
            {
                await work();
                customRunnerInvoked = true;
            };
            await _service.InitializeAsync("user1", ["chCustom"]);

            // Act
            await _service.MarkAsReadAsync();

            // Assert
            Assert.True(customRunnerInvoked);
            _webPushMock.Verify(x => x.SendClearNotificationAsync("user1", "chCustom"), Times.Once);
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
            presenceState.RegisterConnection(new ConnectionRegistrationOptions
            {
                UserId = "user1",
                SubscriptionId = "sub_mob_1",
                DeviceType = "Mobile",
                IsFocused = false
            });

            var soundMock = new Mock<ISoundService>();
            var snackbarMock = new Mock<ISnackbar>();

            var service = CreateChatNotificationService(
                new Spokes_Server.Components.UI.UiNotificationPresenter(snackbarMock.Object, _nav, soundMock.Object),
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
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
            presenceState.RegisterConnection(new ConnectionRegistrationOptions
            {
                UserId = "user1",
                SubscriptionId = "sub_mob_2",
                DeviceType = "Mobile",
                IsFocused = true
            });

            var soundMock = new Mock<ISoundService>();
            var snackbarMock = new Mock<ISnackbar>();
            var inAppDispatcher = new InAppNotificationDispatcher();

            var service = CreateChatNotificationService(
                new Spokes_Server.Components.UI.UiNotificationPresenter(snackbarMock.Object, _nav, soundMock.Object),
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                soundMock.Object,
                circuitContext,
                presenceState,
                inAppDispatcher);

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
            inAppDispatcher.Dispatch(new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "other_user",
                Title = "other_user",
                Content = "Hello focused mobile",
                Initials = "OU",
                ChannelId = channelId,
                ChannelName = "General",
                Category = NotificationCategories.Chat
            });

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

            var service = CreateChatNotificationService(
                new Spokes_Server.Components.UI.UiNotificationPresenter(snackbarMock.Object, _nav, soundMock.Object),
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
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

        [Fact]
        public async Task InitializeAsync_LoadsUnreadCounts()
        {
            var userId = "user-1";
            var channelId = "chan-1";
            _channels.Save(new ChatChannel { Id = channelId, ChannelType = ChatChannelType.General });

            // 1. Setup persistent unread state (Yesterday)
            var yesterday = DateTime.UtcNow.AddDays(-1);
            var stateId = ChatReadState.CreateId(userId, channelId);
            var state = new ChatReadState
            {
                Id = stateId,
                UserId = userId,
                ChannelId = channelId,
                LastReadAt = yesterday
            };
            _readStates.Save(state);

            // 2. Add a message TODAY (different sender)
            var msg = new ChatMessage
            {
                ChannelId = channelId,
                SenderId = "user-2",
                Content = "Hi",
                SentAt = DateTime.UtcNow
            };
            _messages.Save(msg);

            // Re-initialize to ensure fresh state
            await _persistence.FlushAllAsync();
            _messages.LoadFromDisk();
            _readStates.LoadFromDisk();

            // Act
            await _service.InitializeAsync(userId, [channelId]);

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
        }

        [Fact]
        public async Task OnMessageReceived_MentionsOnly_WhenConfigured()
        {
            var userId = "user-1";
            var chanId = "chan-1";
            _channels.Save(new ChatChannel { Id = chanId, ChannelType = ChatChannelType.General });
            await _service.InitializeAsync(userId, [chanId]);

            // Set notification level to Mentions
            _readStates.SetNotificationLevel(userId, chanId, "Mentions");

            // 1. Message with NO mention
            var msg1 = new ChatMessage { ChannelId = chanId, SenderId = "user-2", Content = "No mention" };
            _chatState.NotifyMessageReceived(msg1);

            Assert.Equal(1, _service.GetUnreadCount(chanId)); // Still unread in sidebar
            Assert.Equal(0, _service.TotalUnreadCount); // But NO notification

            // 2. Message WITH mention
            var msg2 = new ChatMessage { ChannelId = chanId, SenderId = "user-2", Content = "Hi @user-1", MentionedUserIds = [userId] };
            _chatState.NotifyMessageReceived(msg2);

            Assert.Equal(2, _service.GetUnreadCount(chanId));
            Assert.Equal(1, _service.TotalUnreadCount);
        }

        [Fact]
        public async Task ChatNotificationService_PrimaryConstructor_UsesInjectedFilterAndDecryptor()
        {
            var mockFilter = new Mock<NotificationRecipientFilter>();
            var mockDecryptor = new Mock<NotificationContentDecryptor>();
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            var soundMock = new Mock<ISoundService>();

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                mockFilter.Object,
                mockDecryptor.Object,
                soundMock.Object);

            var userId = "user_primary";
            var chanId = "chan_primary";
            _channels.Save(new ChatChannel { Id = chanId, ChannelType = ChatChannelType.General });
            _readStates.SetNotificationLevel(userId, chanId, "Mentions");
            await service.InitializeAsync(userId, [chanId]);

            var msg = new ChatMessage { Id = "m_prim", ChannelId = chanId, SenderId = "sender1", Content = "Test content" };

            // Setup mock filter to return true
            mockFilter.Setup(f => f.IsUserMentioned(userId, It.IsAny<Employee?>(), msg, null))
                .Returns(true);

            _chatState.NotifyMessageReceived(msg);

            // Verify filter was consulted for notified unread count
            mockFilter.Verify(f => f.IsUserMentioned(userId, It.IsAny<Employee?>(), msg, null), Times.Once);
            Assert.Equal(1, service.TotalUnreadCount);

            // Verify presenter is NOT called on NotifyMessageReceived because in-app alerts are unified via NotificationRoutingService
            mockPresenter.Verify(p => p.ShowChatNotification(It.IsAny<InAppNotification>()), Times.Never);
        }

        [Fact]
        public async Task ChatNotificationService_BoundedContextConstructor_InitializesCorrectly()
        {
            var mockCommContext = new Mock<CommunicationContext>();
            mockCommContext.Setup(c => c.ChatReadStates).Returns(_readStates);
            mockCommContext.Setup(c => c.ChatMessages).Returns(_messages);
            mockCommContext.Setup(c => c.ChatChannels).Returns(_channels);

            var mockHrContext = new Mock<HrContext>();
            mockHrContext.Setup(h => h.Employees).Returns(_employees);

            var mockPresenter = new Mock<IUiNotificationPresenter>();
            var mockFilter = new Mock<NotificationRecipientFilter>();
            var mockDecryptor = new Mock<NotificationContentDecryptor>();
            var soundMock = new Mock<ISoundService>();

            var service = new ChatNotificationService(
                mockCommContext.Object,
                mockHrContext.Object,
                mockPresenter.Object,
                _chatState,
                mockFilter.Object,
                mockDecryptor.Object,
                _webPushMock.Object,
                _chatAccessMock.Object,
                soundMock.Object);

            var userId = "user_bounded";
            var chanId = "chan_bounded";
            _channels.Save(new ChatChannel { Id = chanId, ChannelType = ChatChannelType.General });
            await service.InitializeAsync(userId, [chanId]);

            Assert.Equal(0, service.TotalUnreadCount);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenDifferentUser_DoesNotShowSnackbar()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user2",
                SenderName = "Bob",
                Title = "Bob reacted 👍 to:",
                Content = "\"Hello\"",
                Initials = "BA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsReaction = true,
                IsGroupChat = false
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert
            mockPresenter.Verify(p => p.ShowChatNotification(It.IsAny<InAppNotification>()), Times.Never);
            mockSound.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenOnSameChannelAndActive_SuppressesSnackbar()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(true);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();
            var circuitContext = new UserCircuitContext { IsConnected = true, IsFocused = true };

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                circuitContext: circuitContext,
                inAppDispatcher: inAppDispatcher);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Bob",
                Title = "Bob reacted 👍 to:",
                Content = "\"Hello\"",
                Initials = "BA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsReaction = true
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert
            mockPresenter.Verify(p => p.ShowChatNotification(It.IsAny<InAppNotification>()), Times.Never);
            mockSound.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenOnDifferentPage_ShowsSnackbarAndPlaysSound()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(false);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Bob",
                Title = "Bob reacted 👍 to:",
                Content = "\"Hello world\"",
                Initials = "BA",
                ChannelId = "ch1",
                ChannelName = "General",
                SenderAvatarUrl = "/avatars/bob.png",
                IsReaction = true,
                IsGroupChat = true,
                ServerName = "HQ",
                Category = NotificationCategories.Chat
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert
            mockPresenter.Verify(p => p.ShowChatNotification(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user1" &&
                n.Title == "Bob reacted 👍 to:" &&
                n.Content == "\"Hello world\"" &&
                n.SenderAvatarUrl == "/avatars/bob.png" &&
                n.ServerName == "HQ" &&
                n.IsReaction &&
                n.IsGroupChat)), Times.Once);
            mockSound.Verify(s => s.PlayNotificationSoundAsync(NotificationCategories.Chat, null, false), Times.Once);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenSilent_DoesNotPlaySound()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(false);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Bob",
                Title = "Bob reacted 👍 to:",
                Content = "\"Hello\"",
                Initials = "BA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsSilent = true,
                Category = NotificationCategories.Chat
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert
            mockPresenter.Verify(p => p.ShowChatNotification(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user1" &&
                n.Title == "Bob reacted 👍 to:" &&
                n.IsSilent)), Times.Once);
            mockSound.Verify(s => s.PlayNotificationSoundAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task DisposeAsync_UnsubscribesFromInAppNotificationDispatcher()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher);

            await service.InitializeAsync("user1", ["ch1"]);

            // Act - Dispose
            await service.DisposeAsync();

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Bob",
                Title = "Bob reacted 👍 to:",
                Content = "\"Hello\"",
                Initials = "BA",
                ChannelId = "ch1",
                ChannelName = "General"
            };

            inAppDispatcher.Dispatch(notification);

            // Assert - nothing received after dispose
            mockPresenter.Verify(p => p.ShowChatNotification(It.IsAny<InAppNotification>()), Times.Never);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenMessageIsEncryptedAndKeystoreIsLocked_DisplaysMaskedContent()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(false);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();
            var scopedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
            Assert.False(scopedKeystore.IsUnlocked); // Locked by default

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher,
                scopedKeystore: scopedKeystore);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Alice",
                Title = "Alice",
                Content = "Top Secret Plaintext",
                Initials = "AA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsEncrypted = true,
                MaskedContent = "🔒 New Secure Message"
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert: Must display MaskedContent instead of decrypted plaintext
            mockPresenter.Verify(p => p.ShowChatNotification(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user1" &&
                n.Content == "🔒 New Secure Message")), Times.Once);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenMessageIsEncryptedAndKeystoreIsUnlocked_DisplaysDecryptedContent()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(false);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();
            var scopedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
            scopedKeystore.Unlock("<RSAKeyValue>fake</RSAKeyValue>");
            Assert.True(scopedKeystore.IsUnlocked);

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher,
                scopedKeystore: scopedKeystore);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Alice",
                Title = "Alice",
                Content = "Top Secret Plaintext",
                Initials = "AA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsEncrypted = true,
                MaskedContent = "🔒 New Secure Message"
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert: Must display decrypted content when keystore is unlocked
            mockPresenter.Verify(p => p.ShowChatNotification(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user1" &&
                n.Content == "Top Secret Plaintext")), Times.Once);
        }

        [Fact]
        public async Task HandleInAppNotification_WhenMessageIsNotEncrypted_DisplaysOriginalContentEvenIfKeystoreLocked()
        {
            // Arrange
            var mockPresenter = new Mock<IUiNotificationPresenter>();
            mockPresenter.Setup(p => p.IsOnChatChannel("ch1")).Returns(false);

            var mockSound = new Mock<ISoundService>();
            var inAppDispatcher = new InAppNotificationDispatcher();
            var scopedKeystore = new ScopedKeystoreService(new EphemeralDataProtectionProvider());
            Assert.False(scopedKeystore.IsUnlocked);

            var service = CreateChatNotificationService(
                mockPresenter.Object,
                new NotificationRecipientFilter(_readStates, _messages),
                new NotificationContentDecryptor(_keystoreMock.Object, _cryptoMock.Object),
                mockSound.Object,
                inAppDispatcher: inAppDispatcher,
                scopedKeystore: scopedKeystore);

            await service.InitializeAsync("user1", ["ch1"]);

            var notification = new InAppNotification
            {
                RecipientUserId = "user1",
                SenderName = "Alice",
                Title = "Alice",
                Content = "Regular unencrypted message",
                Initials = "AA",
                ChannelId = "ch1",
                ChannelName = "General",
                IsEncrypted = false
            };

            // Act
            inAppDispatcher.Dispatch(notification);

            // Assert: Must display original content
            mockPresenter.Verify(p => p.ShowChatNotification(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user1" &&
                n.Content == "Regular unencrypted message")), Times.Once);
        }
    }


