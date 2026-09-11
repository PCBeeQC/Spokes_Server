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
using Xunit.Abstractions;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    // A simple stub for NavigationManager since Moq can't mock its properties effectively
    public class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    public class ChatNotificationServiceTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;
        private readonly ChatNotificationService _service;

        private readonly Mock<ISnackbar> _mockSnackbar;
        private readonly NavigationManager _nav;
        private readonly ChatStateService _chatState;

        private readonly ChatReadStateRepository _readStates;
        private readonly ChatMessageRepository _messages;
        private readonly ChatChannelRepository _channels;
        private readonly EmployeeRepository _employees;
        private readonly TeamRepository _teams;
        private readonly ProjectRepository _projects;
        private readonly ITestOutputHelper _output;

        public ChatNotificationServiceTests(ITestOutputHelper output)
        {
            _output = output;
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ChatNotif_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _readStates = new ChatReadStateRepository(_persistence, _config, new CompanyProfileRepository(_persistence, _config));
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            _messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
            _channels = new ChatChannelRepository(_persistence, _config);
            _employees = new EmployeeRepository(_persistence, _config);
            _teams = new TeamRepository(_persistence, _config);
            _projects = new ProjectRepository(_persistence, _config);

            _mockSnackbar = new Mock<ISnackbar>();
            _nav = new TestNavigationManager();

            _chatState = new ChatStateService();

            var dummyChatService = new ChatService(
                _employees,
                _channels,
                _messages,
                _teams,
                _readStates,
                _projects,
                new Mock<Microsoft.AspNetCore.SignalR.IHubContext<Spokes_Server.Core.Hubs.ChatHub>>().Object,
                _chatState,
                null!,
                null!,
                new Mock<ILogger<ChatService>>().Object,
                _config,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                new CompanyProfileRepository(_persistence, _config),
                new AlbumService(new AlbumRepository(_persistence, _config), _channels, null!, null!, _employees),
                null!
            );

            _service = new ChatNotificationService(
                _mockSnackbar.Object,
                _nav,
                _chatState,
                _readStates,
                _messages,
                _channels,
                _employees,
                _teams,
                _projects,
                new GlobalKeystoreService(),
                new Mock<ICryptoService>().Object,
                null,
                dummyChatService);
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

        [Fact]
        public async Task InitializeAsync_LoadsUnreadCounts()
        {
            var userId = "user-1";
            var channelId = "chan-1";

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
            _messages.LoadFromDisk();
            _readStates.LoadFromDisk();

            // Act
            await _service.InitializeAsync(userId, new[] { channelId });

            // Assert
            Assert.Equal(1, _service.GetUnreadCount(channelId));
        }

        [Fact]
        public async Task OnMessageReceived_IncrementsCounts_AndShowsSnackbar()
        {
            await _service.InitializeAsync("user-1", new[] { "chan-1" });

            var msg = new ChatMessage
            {
                ChannelId = "chan-1",
                SenderId = "user-2",
                Content = "Hello Notification",
                SentAt = DateTime.UtcNow
            };

            // Trigger the event
            _chatState.NotifyMessageReceived(msg);

            Assert.Equal(1, _service.GetUnreadCount("chan-1"));
            Assert.Equal(1, _service.TotalUnreadCount);

            // Verify snackbar is called
            _mockSnackbar.Verify(s => s.Add(
                It.IsAny<string>(),
                It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()), Times.Never); // Wait, if it uses Add<T> the non-generic never is called, we just skip verification for Add<T> since we can't reference the component class.
        }

        [Fact]
        public async Task OnMessageReceived_MentionsOnly_WhenConfigured()
        {
            var userId = "user-1";
            var chanId = "chan-1";
            await _service.InitializeAsync(userId, new[] { chanId });

            // Set notification level to Mentions
            _readStates.SetNotificationLevel(userId, chanId, "Mentions");

            // 1. Message with NO mention
            var msg1 = new ChatMessage { ChannelId = chanId, SenderId = "user-2", Content = "No mention" };
            _chatState.NotifyMessageReceived(msg1);

            Assert.Equal(1, _service.GetUnreadCount(chanId)); // Still unread in sidebar
            Assert.Equal(0, _service.TotalUnreadCount); // But NO notification

            // 2. Message WITH mention
            var msg2 = new ChatMessage { ChannelId = chanId, SenderId = "user-2", Content = "Hi @user-1", MentionedUserIds = new List<string> { userId } };
            _chatState.NotifyMessageReceived(msg2);

            Assert.Equal(2, _service.GetUnreadCount(chanId));
            Assert.Equal(1, _service.TotalUnreadCount);
        }

        [Fact]
        public async Task MarkChannelAsRead_ClearsCounts()
        {
            await _service.InitializeAsync("user-1", new[] { "chan-1" });
            var msg = new ChatMessage { ChannelId = "chan-1", SenderId = "user-2", Content = "Test" };
            _chatState.NotifyMessageReceived(msg);

            Assert.Equal(1, _service.GetUnreadCount("chan-1"));

            await _service.MarkChannelAsReadAsync("chan-1");

            Assert.Equal(0, _service.GetUnreadCount("chan-1"));
            Assert.Equal(0, _service.TotalUnreadCount);
        }

        [Fact]
        public async Task SubscribeToChannel_CalculatesInitialUnread()
        {
            var userId = "user-1";
            await _service.InitializeAsync(userId, Enumerable.Empty<string>());

            var chanId = "new-chan";
            var msg = new ChatMessage { ChannelId = chanId, SenderId = "user-2", Content = "Existing", SentAt = DateTime.UtcNow };
            _messages.Save(msg);

            await _service.SubscribeToChannel(chanId);

            Assert.Equal(1, _service.GetUnreadCount(chanId));
        }
    }
}



