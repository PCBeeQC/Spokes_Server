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
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailTestNavigationManager : NavigationManager
{
    public EmailTestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

    public void SetUri(string uri)
    {
        var absoluteUri = uri.StartsWith("http") ? uri : $"http://localhost{uri}";
        Uri = absoluteUri;
    }

    protected override void NavigateToCore(string uri, bool forceLoad)
    {
        SetUri(uri);
    }
}

public class EmailNotificationServiceTests : IAsyncDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmailNotificationService _service;

    private readonly Mock<ISnackbar> _mockSnackbar;
    private readonly EmailTestNavigationManager _nav;
    private readonly EmailSyncStateService _syncState;

    private readonly EmailFolderRepository _folders;
    private readonly EmailMessageRepository _messages;

    public EmailNotificationServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailNotif_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _folders = new EmailFolderRepository(_persistence, _config);
        _messages = new EmailMessageRepository(_persistence, _config);

        _mockSnackbar = new Mock<ISnackbar>();
        _nav = new EmailTestNavigationManager();

        _syncState = new EmailSyncStateService();

        _service = new EmailNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(_mockSnackbar.Object, _nav, new Mock<ISoundService>().Object),
            _syncState,
            _folders,
            _messages);
    }

    public async ValueTask DisposeAsync()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
        await _service.DisposeAsync();
    }

    private void SaveAndReload(EmailFolder folder)
    {
        _folders.Save(folder);
        _persistence.FlushAll();
        _folders.LoadFromDisk();
    }

    private void SaveAndReload(EmailMessage msg)
    {
        _messages.Save(msg);
        _persistence.FlushAll();
        _messages.LoadFromDisk();
    }

    [Fact]
    public async Task InitializeAsync_LoadsInitialUnreadCount()
    {
        var userId = "user-1";
        var folder = new EmailFolder { Id = "f1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        var msg = new EmailMessage { Id = "m1", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
        SaveAndReload(msg);

        await _service.InitializeAsync(userId);

        Assert.Equal(1, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task OnNewEmailReceived_UpdatesCount_AndShowsSnackbar()
    {
        var userId = "user-1";
        var folder = new EmailFolder { Id = "f1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        await _service.InitializeAsync(userId);

        var msg = new EmailMessage
        {
            Id = "m2",
            AccountId = userId,
            FolderPath = "INBOX",
            FromName = "Sender",
            Subject = "Hello",
            Snippet = "World",
            IsRead = false,
            UniqueId = 102
        };

        SaveAndReload(msg);

        _syncState.NotifyNewEmail(userId, msg);

        Assert.Equal(1, _service.TotalUnreadCount);

        _mockSnackbar.Verify(s => s.Add(
            It.IsAny<string>(),
            It.IsAny<Severity>(),
            It.IsAny<Action<SnackbarOptions>>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OnNewEmailReceived_NoSnackbar_WhenOnEmailPage()
    {
        var userId = "user-1";
        var folder = new EmailFolder { Id = "f1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        await _service.InitializeAsync(userId);

        // Navigate to email page
        _nav.SetUri("/email");
        Assert.Equal("http://localhost/email", _nav.Uri);

        var msg = new EmailMessage { Id = "m2", AccountId = userId, FolderPath = "INBOX", UniqueId = 103 };
        SaveAndReload(msg);

        _syncState.NotifyNewEmail(userId, msg);

        _mockSnackbar.Verify(s => s.Add(
            It.IsAny<string>(),
            It.IsAny<Severity>(),
            It.IsAny<Action<SnackbarOptions>>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnreadCountChanged_UpdatesTotal()
    {
        var userId = "user-1";
        var folder = new EmailFolder { Id = "f1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        await _service.InitializeAsync(userId);

        var msg = new EmailMessage { Id = "m3", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 104 };
        SaveAndReload(msg);

        _syncState.NotifyUnreadCountChanged(userId);

        Assert.Equal(1, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task InitializeAsync_SecondCall_ReturnsEarly()
    {
        var userId = "user-1";
        var folder = new EmailFolder { Id = "f1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        var msg = new EmailMessage { Id = "m1", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
        SaveAndReload(msg);

        await _service.InitializeAsync(userId);

        var msg2 = new EmailMessage { Id = "m2", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 102 };
        SaveAndReload(msg2);

        await _service.InitializeAsync("user-2");

        Assert.Equal(1, _service.TotalUnreadCount);
    }

    [Fact]
    public void LoadUnreadCount_NullUserId_DoesNothing()
    {
        _service.LoadUnreadCount();
        Assert.Equal(0, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task LoadUnreadCount_SkipsTrashAndDraftsFolders()
    {
        var userId = "user-1";

        var folder1 = new EmailFolder { Id = "f1", AccountId = userId, Name = "Trash", Path = "TRASH", IsTrash = true };
        SaveAndReload(folder1);
        var folder2 = new EmailFolder { Id = "f2", AccountId = userId, Name = "Drafts", Path = "DRAFTS", IsDrafts = true };
        SaveAndReload(folder2);
        var folder3 = new EmailFolder { Id = "f3", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder3);

        var msg1 = new EmailMessage { Id = "m1", AccountId = userId, FolderPath = "TRASH", IsRead = false, UniqueId = 101 };
        SaveAndReload(msg1);
        var msg2 = new EmailMessage { Id = "m2", AccountId = userId, FolderPath = "DRAFTS", IsRead = false, UniqueId = 102 };
        SaveAndReload(msg2);
        var msg3 = new EmailMessage { Id = "m3", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 103 };
        SaveAndReload(msg3);

        await _service.InitializeAsync(userId);

        Assert.Equal(1, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task LoadUnreadCount_TriggersOnUnreadCountChangedEvent()
    {
        var userId = "user-1";
        bool eventFired = false;
        _service.OnUnreadCountChanged += () => eventFired = true;

        await _service.InitializeAsync(userId);

        eventFired = false;

        _service.LoadUnreadCount();

        Assert.True(eventFired);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromEvents()
    {
        var userId = "user-1";
        await _service.InitializeAsync(userId);

        await _service.DisposeAsync();

        var msg = new EmailMessage { Id = "m1", AccountId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
        SaveAndReload(msg);

        _syncState.NotifyNewEmail(userId, msg);

        Assert.Equal(0, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task OnNewEmailReceived_WhenMobileAndNotFocused_SuppressesSnackbar()
    {
        // Arrange
        var userId = "user-mobile-bg";
        var folder = new EmailFolder { Id = "f_mob_1", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        var circuitContext = new UserCircuitContext
        {
            DeviceType = "Mobile",
            SubscriptionId = "sub_email_mob_1",
            IsConnected = true,
            IsFocused = false
        };
        var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
        presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = userId,
            SubscriptionId = "sub_email_mob_1",
            DeviceType = "Mobile",
            IsFocused = false
        });

        var snackbarMock = new Mock<ISnackbar>();
        var service = new EmailNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(snackbarMock.Object, _nav, new Mock<ISoundService>().Object),
            _syncState,
            _folders,
            _messages,
            inboxService: null,
            circuitContext: circuitContext,
            presenceState: presenceState);

        await service.InitializeAsync(userId);

        var msg = new EmailMessage
        {
            Id = "msg_email_mob",
            AccountId = userId,
            FolderPath = "INBOX",
            FromName = "Boss",
            Subject = "Important",
            Snippet = "Please read",
            IsRead = false,
            UniqueId = 999
        };
        SaveAndReload(msg);

        // Act
        _syncState.NotifyNewEmail(userId, msg);

        // Assert
        Assert.Equal(1, service.TotalUnreadCount);
        Assert.Empty(snackbarMock.Invocations);
    }

    [Fact]
    public async Task OnNewEmailReceived_WhenMobileAndFocused_DisplaysSnackbar()
    {
        // Arrange
        var userId = "user-mobile-focused";
        var folder = new EmailFolder { Id = "f_mob_2", AccountId = userId, Name = "Inbox", Path = "INBOX" };
        SaveAndReload(folder);

        var circuitContext = new UserCircuitContext
        {
            DeviceType = "Mobile",
            SubscriptionId = "sub_email_mob_2",
            IsConnected = true,
            IsFocused = true
        };
        var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
        presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = userId,
            SubscriptionId = "sub_email_mob_2",
            DeviceType = "Mobile",
            IsFocused = true
        });

        var snackbarMock = new Mock<ISnackbar>();
        var service = new EmailNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(snackbarMock.Object, _nav, new Mock<ISoundService>().Object),
            _syncState,
            _folders,
            _messages,
            inboxService: null,
            circuitContext: circuitContext,
            presenceState: presenceState);

        await service.InitializeAsync(userId);

        var msg = new EmailMessage
        {
            Id = "msg_email_mob_2",
            AccountId = userId,
            FolderPath = "INBOX",
            FromName = "Boss",
            Subject = "Important",
            Snippet = "Please read",
            IsRead = false,
            UniqueId = 1000
        };
        SaveAndReload(msg);

        // Act
        _syncState.NotifyNewEmail(userId, msg);

        // Assert
        Assert.Equal(1, service.TotalUnreadCount);
        Assert.NotEmpty(snackbarMock.Invocations);
    }

    [Fact]
    public async Task MarkEmailPageVisited_ClearsBadgeAndCountsOnlySubsequentNewUnreadEmails()
    {
        var userId = "user-visit-badge";
        var folder = new EmailFolder { Id = "f_visit_1", AccountId = userId, Name = "Inbox", Path = "INBOX", IsInbox = true };
        SaveAndReload(folder);

        var baseTime = DateTimeOffset.UtcNow;
        SaveAndReload(new EmailMessage
        {
            Id = "m_old_1",
            AccountId = userId,
            FolderPath = "INBOX",
            IsRead = false,
            UniqueId = 1,
            Date = baseTime.AddHours(-3)
        });
        SaveAndReload(new EmailMessage
        {
            Id = "m_old_2",
            AccountId = userId,
            FolderPath = "INBOX",
            IsRead = false,
            UniqueId = 2,
            Date = baseTime.AddHours(-1)
        });

        await _service.InitializeAsync(userId);
        Assert.Equal(2, _service.TotalUnreadCount);

        // User opens the email page -> badge clears to 0
        _service.MarkEmailPageVisited();
        Assert.Equal(0, _service.TotalUnreadCount);

        // User is away from /email when 1 new unread email arrives -> badge shows 1 (not 3)
        _nav.SetUri("/chat");
        var newMsg = new EmailMessage
        {
            Id = "m_new_3",
            AccountId = userId,
            FolderPath = "INBOX",
            IsRead = false,
            UniqueId = 3,
            Date = DateTimeOffset.UtcNow.AddSeconds(5)
        };
        SaveAndReload(newMsg);
        _syncState.NotifyNewEmail(userId, newMsg);
        Assert.Equal(1, _service.TotalUnreadCount);

        // User navigates back to /email and another email arrives while viewing /email -> badge stays 0
        _nav.SetUri("/email");
        var whileOpenMsg = new EmailMessage
        {
            Id = "m_new_4",
            AccountId = userId,
            FolderPath = "INBOX",
            IsRead = false,
            UniqueId = 4,
            Date = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        SaveAndReload(whileOpenMsg);
        _syncState.NotifyNewEmail(userId, whileOpenMsg);
        Assert.Equal(0, _service.TotalUnreadCount);
    }

    [Fact]
    public async Task OnNewEmailReceived_PlaysNotificationSound_WhenNotOnEmailPage()
    {
        var userId = "user-sound-test";
        var mockSoundService = new Mock<ISoundService>();
        var service = new EmailNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(_mockSnackbar.Object, _nav, mockSoundService.Object),
            _syncState,
            _folders,
            _messages,
            soundService: mockSoundService.Object);

        await service.InitializeAsync(userId);

        _nav.SetUri("/chat");

        var msg = new EmailMessage
        {
            Id = "msg-sound-1",
            AccountId = userId,
            FolderPath = "INBOX",
            FromName = "Boss",
            Subject = "Important Update",
            Snippet = "Please read",
            IsRead = false,
            UniqueId = 501
        };
        SaveAndReload(msg);

        _syncState.NotifyNewEmail(userId, msg);

        mockSoundService.Verify(s => s.PlayNotificationSoundAsync(
            Spokes_Server.Core.Models.Communication.Notifications.NotificationCategories.Email, null, false), Times.Once);

        // When navigating to /email, incoming emails do not play notification sound
        mockSoundService.Invocations.Clear();
        _nav.SetUri("/email");

        var msg2 = new EmailMessage
        {
            Id = "msg-sound-2",
            AccountId = userId,
            FolderPath = "INBOX",
            FromName = "Boss",
            Subject = "Another Update",
            Snippet = "Please read",
            IsRead = false,
            UniqueId = 502
        };
        SaveAndReload(msg2);

        _syncState.NotifyNewEmail(userId, msg2);

        mockSoundService.Verify(s => s.PlayNotificationSoundAsync(
            Spokes_Server.Core.Models.Communication.Notifications.NotificationCategories.Email, null, false), Times.Never);

        await service.DisposeAsync();
    }

    [Fact]
    public async Task MarkEmailPageVisited_WhenUnseenEmailsExist_DispatchesClearNotificationToOtherDevices()
    {
        var userId = "user-email-clear";
        var folder = new EmailFolder { Id = "f_clear_1", AccountId = userId, Name = "Inbox", Path = "INBOX", IsInbox = true };
        SaveAndReload(folder);

        SaveAndReload(new EmailMessage
        {
            Id = "m_clear_1",
            AccountId = userId,
            FolderPath = "INBOX",
            IsRead = false,
            UniqueId = 1,
            Date = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        var mockWebPush = new Mock<Spokes_Server.Core.Services.Communication.Notifications.WebPush.IWebPushService>();
        var service = new EmailNotificationService(
            new Spokes_Server.Components.UI.UiNotificationPresenter(_mockSnackbar.Object, _nav, new Mock<ISoundService>().Object),
            _syncState,
            _folders,
            _messages,
            webPush: mockWebPush.Object,
            backgroundTaskRunner: work => work());

        await service.InitializeAsync(userId);
        Assert.Equal(1, service.TotalUnreadCount);

        service.MarkEmailPageVisited();
        if (service.LastBackgroundTask != null)
        {
            await service.LastBackgroundTask;
        }

        mockWebPush.Verify(w => w.SendClearNotificationAsync(userId, $"email-{userId}"), Times.Once);

        // Visiting again with 0 unseen emails should not send redundant clear pushes
        service.MarkEmailPageVisited();
        mockWebPush.Verify(w => w.SendClearNotificationAsync(userId, $"email-{userId}"), Times.Once);

        await service.DisposeAsync();
    }
}
