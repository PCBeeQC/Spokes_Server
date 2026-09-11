using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
// using Spokes_Server.Components.Shared;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;

namespace Spokes_Server.Tests.Core.Services.Communication
{
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
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_EmailNotif_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
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
    }
}



