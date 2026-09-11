using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class EventRsvpTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;
        private readonly CalendarEventRepository _calendarEvents;
        private readonly ChatStateService _chatState;
        private readonly ChatService _chatService;

        public EventRsvpTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_EventRsvp_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            _calendarEvents = new CalendarEventRepository(_persistence, _config);
            _chatState = new ChatStateService();

            var employees = new EmployeeRepository(_persistence, _config);
            var channels = new ChatChannelRepository(_persistence, _config);
            var companyProfile = new CompanyProfileRepository(_persistence, _config);
            var messages = new ChatMessageRepository(_persistence, _config, companyProfile);
            var teams = new TeamRepository(_persistence, _config);
            var readStates = new ChatReadStateRepository(_persistence, _config, companyProfile);
            var projects = new ProjectRepository(_persistence, _config);
            var albums = new AlbumRepository(_persistence, _config);
            var systemConfigs = new SystemConfigRepository(_persistence, _config);

            var mockHubContext = new Mock<IHubContext<ChatHub>>();
            var mockClients = new Mock<IHubClients>();
            var mockClientProxy = new Mock<IClientProxy>();
            mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);

            var mockPresence = new Mock<PresenceStateService>(_chatState);
            var mockFileService = new Mock<IFileService>();
            var mockModeration = new Mock<IContentModerationService>();

            var mockServiceProvider = new Mock<IServiceProvider>();
            var mockNotificationRouting = new NotificationRoutingService(
                employees, projects, readStates, teams,
                new Mock<IWebPushService>().Object,
                new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object),
                new GlobalKeystoreService(),
                new Mock<ICryptoService>().Object,
                new Mock<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().Object,
                new EmailFolderRepository(_persistence, _config),
                new EmailMessageRepository(_persistence, _config),
                channels, messages, companyProfile,
                new Mock<ILogger<NotificationRoutingService>>().Object,
                mockServiceProvider.Object);

            _chatService = new ChatService(
                employees,
                channels,
                messages,
                teams,
                readStates,
                projects,
                mockHubContext.Object,
                _chatState,
                mockNotificationRouting,
                mockPresence.Object,
                new Mock<ILogger<ChatService>>().Object,
                _config,
                mockFileService.Object,
                new Mock<ICryptoService>().Object,
                new GlobalKeystoreService(),
                null!,
                mockModeration.Object,
                systemConfigs,
                companyProfile,
                new AlbumService(albums, channels, new Mock<ICryptoService>().Object, null!, employees),
                new MarkdownSanitizerService(),
                _calendarEvents);
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
        public async Task RsvpToEventAsync_AddsUserAndNotifiesState()
        {
            var evt = new CalendarEvent
            {
                Id = "event-1",
                Title = "Company Picnic",
                Start = DateTime.Today.AddDays(1),
                End = DateTime.Today.AddDays(1).AddHours(2),
                Attendees = new List<string>()
            };
            _calendarEvents.Save(evt);

            CalendarEvent? broadcastedEvent = null;
            _chatState.CalendarEventUpdated += e => broadcastedEvent = e;

            var result = await _chatService.RsvpToEventAsync("event-1", "user-123");

            Assert.True(result);
            var saved = _calendarEvents.GetById("event-1");
            Assert.NotNull(saved);
            Assert.Contains("user-123", saved.Attendees);
            Assert.NotNull(broadcastedEvent);
            Assert.Equal("event-1", broadcastedEvent.Id);
            Assert.Contains("user-123", broadcastedEvent.Attendees);
        }

        [Fact]
        public async Task RsvpToEventAsync_IsIdempotent_WhenUserAlreadyAttending()
        {
            var evt = new CalendarEvent
            {
                Id = "event-2",
                Title = "Sprint Planning",
                Start = DateTime.Today.AddDays(2),
                End = DateTime.Today.AddDays(2).AddHours(1),
                Attendees = new List<string> { "user-123" }
            };
            _calendarEvents.Save(evt);

            bool notificationFired = false;
            _chatState.CalendarEventUpdated += _ => notificationFired = true;

            var result = await _chatService.RsvpToEventAsync("event-2", "user-123");

            Assert.False(result);
            Assert.False(notificationFired);
            var saved = _calendarEvents.GetById("event-2");
            Assert.Single(saved!.Attendees);
        }

        [Fact]
        public async Task CancelRsvpEventAsync_RemovesUserAndNotifiesState()
        {
            var evt = new CalendarEvent
            {
                Id = "event-3",
                Title = "All Hands",
                Start = DateTime.Today.AddDays(3),
                End = DateTime.Today.AddDays(3).AddHours(1),
                Attendees = new List<string> { "user-123", "user-456" }
            };
            _calendarEvents.Save(evt);

            CalendarEvent? broadcastedEvent = null;
            _chatState.CalendarEventUpdated += e => broadcastedEvent = e;

            var result = await _chatService.CancelRsvpEventAsync("event-3", "user-123");

            Assert.True(result);
            var saved = _calendarEvents.GetById("event-3");
            Assert.NotNull(saved);
            Assert.DoesNotContain("user-123", saved.Attendees);
            Assert.Contains("user-456", saved.Attendees);
            Assert.NotNull(broadcastedEvent);
            Assert.DoesNotContain("user-123", broadcastedEvent.Attendees);
        }

        [Fact]
        public async Task CancelRsvpEventAsync_ReturnsFalse_WhenUserNotAttending()
        {
            var evt = new CalendarEvent
            {
                Id = "event-4",
                Title = "Design Review",
                Start = DateTime.Today.AddDays(4),
                End = DateTime.Today.AddDays(4).AddHours(1),
                Attendees = new List<string> { "user-456" }
            };
            _calendarEvents.Save(evt);

            bool notificationFired = false;
            _chatState.CalendarEventUpdated += _ => notificationFired = true;

            var result = await _chatService.CancelRsvpEventAsync("event-4", "user-123");

            Assert.False(result);
            Assert.False(notificationFired);
        }

        [Fact]
        public async Task RsvpToEventAsync_ReturnsFalse_WhenEventNotFound()
        {
            var result = await _chatService.RsvpToEventAsync("non-existent", "user-123");
            Assert.False(result);
        }
    }
}
