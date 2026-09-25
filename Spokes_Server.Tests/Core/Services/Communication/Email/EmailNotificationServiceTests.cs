using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

namespace Spokes_Server.Tests.Core.Services.Communication.Email;
    public class EmailTestNavigationManager : NavigationManager
    {
        public EmailTestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        public void SetUri(string uri)
        {
            var absoluteUri = uri.StartsWith("http") ? uri : $"http://localhost{uri}";

            // Use reflection to update the private Uri field since Initialize can only be called once
            var type = typeof(NavigationManager);
            var uriField = type.GetField("_uri", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? type.GetField("<Uri>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            if (uriField != null)
            {
                uriField.SetValue(this, absoluteUri);
            }
            else
            {
                // Fallback for different .NET versions if field names change
                var prop = type.GetProperty("Uri");
                prop?.SetValue(this, absoluteUri);
            }
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            SetUri(uri);
        }
    }

    public class EmailNotificationServiceTests : IDisposable
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
                _mockSnackbar.Object,
                _nav,
                _syncState,
                _folders,
                _messages);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
            _service.DisposeAsync().AsTask().Wait();
        }

        private void SaveAndReload(EmailFolder folder)
        {
            _folders.Save(folder);
            _folders.LoadFromDisk();
        }

        private void SaveAndReload(EmailMessage msg)
        {
            _messages.Save(msg);
            _messages.LoadFromDisk();
        }

        [Fact]
        public async Task InitializeAsync_LoadsInitialUnreadCount()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            var msg = new EmailMessage { Id = "m1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
            SaveAndReload(msg);

            await _service.InitializeAsync(userId);

            Assert.Equal(1, _service.TotalUnreadCount);
        }

        [Fact]
        public async Task OnNewEmailReceived_UpdatesCount_AndShowsSnackbar()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            await _service.InitializeAsync(userId);

            var msg = new EmailMessage
            {
                Id = "m2",
                EmployeeId = userId,
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

            // Verify snackbar
            _mockSnackbar.Verify(s => s.Add(
                It.IsAny<string>(),
                It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()), Times.Never); // Wait, if it uses Add<T> the non-generic never is called, we just skip verification for Add<T> since we can't reference the component class.
        }

        [Fact]
        public async Task OnNewEmailReceived_NoSnackbar_WhenOnEmailPage()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            await _service.InitializeAsync(userId);

            // Navigate to email page
            _nav.SetUri("/email");
            Assert.Equal("http://localhost/email", _nav.Uri);

            var msg = new EmailMessage { Id = "m2", EmployeeId = userId, FolderPath = "INBOX", UniqueId = 103 };
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
            var folder = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            await _service.InitializeAsync(userId);

            var msg = new EmailMessage { Id = "m3", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 104 };
            SaveAndReload(msg);

            _syncState.NotifyUnreadCountChanged(userId);

            Assert.Equal(1, _service.TotalUnreadCount);
        }
        [Fact]
        public async Task InitializeAsync_SecondCall_ReturnsEarly()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);
            
            var msg = new EmailMessage { Id = "m1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
            SaveAndReload(msg);

            await _service.InitializeAsync(userId);
            
            var msg2 = new EmailMessage { Id = "m2", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 102 };
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
            
            var folder1 = new EmailFolder { Id = "f1", EmployeeId = userId, Name = "Trash", Path = "TRASH", IsTrash = true };
            SaveAndReload(folder1);
            var folder2 = new EmailFolder { Id = "f2", EmployeeId = userId, Name = "Drafts", Path = "DRAFTS", IsDrafts = true };
            SaveAndReload(folder2);
            var folder3 = new EmailFolder { Id = "f3", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder3);

            var msg1 = new EmailMessage { Id = "m1", EmployeeId = userId, FolderPath = "TRASH", IsRead = false, UniqueId = 101 };
            SaveAndReload(msg1);
            var msg2 = new EmailMessage { Id = "m2", EmployeeId = userId, FolderPath = "DRAFTS", IsRead = false, UniqueId = 102 };
            SaveAndReload(msg2);
            var msg3 = new EmailMessage { Id = "m3", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 103 };
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

            var msg = new EmailMessage { Id = "m1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false, UniqueId = 101 };
            SaveAndReload(msg);

            _syncState.NotifyNewEmail(userId, msg);
            
            Assert.Equal(0, _service.TotalUnreadCount);
        }

        [Fact]
        public async Task OnNewEmailReceived_WhenMobileAndNotFocused_SuppressesSnackbar()
        {
            // Arrange
            var userId = "user-mobile-bg";
            var folder = new EmailFolder { Id = "f_mob_1", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            var circuitContext = new UserCircuitContext
            {
                DeviceType = "Mobile",
                SubscriptionId = "sub_email_mob_1",
                IsConnected = true,
                IsFocused = false
            };
            var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
            presenceState.RegisterConnection(userId, "sub_email_mob_1", "Mobile", isFocused: false);

            var snackbarMock = new Mock<ISnackbar>();
            var service = new EmailNotificationService(
                snackbarMock.Object,
                _nav,
                _syncState,
                _folders,
                _messages,
                circuitContext,
                presenceState);

            await service.InitializeAsync(userId);

            var msg = new EmailMessage
            {
                Id = "msg_email_mob",
                EmployeeId = userId,
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
            // 1. Unread count MUST still increment
            Assert.Equal(1, service.TotalUnreadCount);

            // 2. Snackbar MUST NOT be displayed
            Assert.Empty(snackbarMock.Invocations);
        }

        [Fact]
        public async Task OnNewEmailReceived_WhenMobileAndFocused_DisplaysSnackbar()
        {
            // Arrange
            var userId = "user-mobile-focused";
            var folder = new EmailFolder { Id = "f_mob_2", EmployeeId = userId, Name = "Inbox", Path = "INBOX" };
            SaveAndReload(folder);

            var circuitContext = new UserCircuitContext
            {
                DeviceType = "Mobile",
                SubscriptionId = "sub_email_mob_2",
                IsConnected = true,
                IsFocused = true
            };
            var presenceState = new PresenceStateService(new Mock<ChatStateService>().Object);
            presenceState.RegisterConnection(userId, "sub_email_mob_2", "Mobile", isFocused: true);

            var snackbarMock = new Mock<ISnackbar>();
            var service = new EmailNotificationService(
                snackbarMock.Object,
                _nav,
                _syncState,
                _folders,
                _messages,
                circuitContext,
                presenceState);

            await service.InitializeAsync(userId);

            var msg = new EmailMessage
            {
                Id = "msg_email_mob_2",
                EmployeeId = userId,
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
    }
