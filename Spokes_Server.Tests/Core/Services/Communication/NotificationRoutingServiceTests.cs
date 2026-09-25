using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class NotificationRoutingServiceTests : IDisposable
{
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        private readonly EmployeeRepository _employees;
        private readonly ProjectRepository _projects;
        private readonly ChatReadStateRepository _readStates;
        private readonly TeamRepository _teams;
        private readonly Mock<IWebPushService> _mockWebPush;
        private readonly NotificationQueueService _queue;
        private readonly GlobalKeystoreService _keystore;
        private readonly Mock<ICryptoService> _mockCrypto;
        private readonly Mock<IDataProtectionProvider> _mockDataProtection;
        private readonly Mock<IDataProtector> _mockDataProtector;
        private readonly EmailFolderRepository _emailFolders;
        private readonly EmailMessageRepository _emailMessages;
        private readonly ChatChannelRepository _chatChannels;
        private readonly ChatMessageRepository _chatMessages;
        private readonly CompanyProfileRepository _companyProfile;
        private readonly Mock<ILogger<NotificationRoutingService>> _mockLogger;

        private readonly NotificationRoutingService _service;

        public NotificationRoutingServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_NotifRouting_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _companyProfile = new CompanyProfileRepository(_persistence, _config);
            _employees = new EmployeeRepository(_persistence, _config);
            _projects = new ProjectRepository(_persistence, _config);
            _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfile);
            _teams = new TeamRepository(_persistence, _config);
            
            _mockWebPush = new Mock<IWebPushService>();
            _queue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object);
            _keystore = new GlobalKeystoreService();
            _mockCrypto = new Mock<ICryptoService>();
            
            _mockDataProtector = new Mock<IDataProtector>();
            _mockDataProtector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
            _mockDataProtection = new Mock<IDataProtectionProvider>();
            _mockDataProtection.Setup(d => d.CreateProtector(It.IsAny<string>())).Returns(_mockDataProtector.Object);

            _emailFolders = new EmailFolderRepository(_persistence, _config);
            _emailMessages = new EmailMessageRepository(_persistence, _config);
            _chatChannels = new ChatChannelRepository(_persistence, _config);
            _chatMessages = new ChatMessageRepository(_persistence, _config, _companyProfile);
            
            _mockLogger = new Mock<ILogger<NotificationRoutingService>>();

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns((string userId) => _chatChannels.GetAll().ToList());
            mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => ["user-recipient", "user-1", "user-sender"]);

            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider.Setup(sp => sp.GetService(typeof(IChatChannelAccessService))).Returns(mockChatAccess.Object);

            _service = new NotificationRoutingService(
                _employees,
                _projects,
                _readStates,
                _teams,
                _mockWebPush.Object,
                _queue,
                _keystore,
                _mockCrypto.Object,
                _mockDataProtection.Object,
                _emailFolders,
                _emailMessages,
                _chatChannels,
                _chatMessages,
                _companyProfile,
                _mockLogger.Object,
                mockServiceProvider.Object
            );
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public async Task GetTotalBadgeCountAsync_CalculatesCountsCorrectly()
        {
            var userId = "user-1";
            
            // Setup an unread email folder
            var folder = new EmailFolder { Id = "fold-1", EmployeeId = userId, Path = "INBOX", UnreadCount = 1 };
            _emailFolders.Save(folder);

            var email = new EmailMessage { Id = "email-1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false };
            _emailMessages.Save(email);

            // Setup an unread chat channel
            var channel = new ChatChannel { Id = "chan-1", Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            // Add recipient employee
            var employee = new Employee { Id = userId, FirstName = "Test", LastName = "User", IsActive = true };
            _employees.Save(employee);

            // Set notification level to All
            _readStates.SetNotificationLevel(userId, "chan-1", "All");

            // Add unread message
            var msg = new ChatMessage { Id = "msg-1", ChannelId = "chan-1", SenderId = "user-2", Content = "Hi", SentAt = DateTime.UtcNow };
            _chatMessages.Save(msg);

            // Act
            var totalCount = await _service.GetTotalBadgeCountAsync(userId);

            // Assert
            Assert.Equal(2, totalCount); // 1 email + 1 chat
        }

        [Fact]
        public async Task RouteChatNotificationAsync_SendsDesktopNotification()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-1";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee { Id = recipientId, FirstName = "Bob", LastName = "Jones", IsActive = true, ChatNotificationsEnabled = true };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            var message = new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "Hello Desktop Only", SentAt = DateTime.UtcNow };
            
            _mockWebPush.Setup(w => w.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);
            _readStates.SetNotificationLevel(recipientId, channelId, "All");

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                recipientId,
                "Alice Smith",
                "Hello Desktop Only",
                $"/chat/{channelId}",
                It.IsAny<string>(),
                PresenceTier.DesktopOnly,
                $"chat-{channelId}",
                It.IsAny<object[]>(),
                "chat",
                channelId,
                It.IsAny<string>(),
                "General",
                true,
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()
            ), Times.Once);
        }

        [Fact]
        public async Task RouteChatNotificationAsync_SkipsIfBlocked()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-1";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee 
            { 
                Id = recipientId, 
                FirstName = "Bob", 
                LastName = "Jones", 
                IsActive = true, 
                ChatNotificationsEnabled = true,
                BlockedUserIds = [senderId]
            };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            var message = new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "Hello blocked", SentAt = DateTime.UtcNow };

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PresenceTier?>(),
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>()
            ), Times.Never);
        }

        [Fact]
        public async Task GetBadgeBreakdownAsync_ReturnsCorrectBreakdown()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "fold-1", EmployeeId = userId, Path = "INBOX", UnreadCount = 1 };
            _emailFolders.Save(folder);
            var email = new EmailMessage { Id = "email-1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false };
            _emailMessages.Save(email);
            
            var result = await _service.GetBadgeBreakdownAsync(userId);
            
            var type = result.GetType();
            var count = (int)type.GetProperty("count")!.GetValue(result)!;
            var breakdown = (Dictionary<string, int>)type.GetProperty("breakdown")!.GetValue(result)!;
            
            Assert.Equal(1, count);
            Assert.True(breakdown.ContainsKey("Email_INBOX"));
        }

        [Fact]
        public async Task RouteEmailNotificationAsync_SendsPush()
        {
            var employee = new Employee { Id = "user-1", PushNotificationsEnabled = true, EmailNotificationsEnabled = true };
            _employees.Save(employee);
            var email = new EmailMessage { Id = "email-1", EmployeeId = "user-1", FromName = "Test", Subject = "Subj", FolderPath = "INBOX" };
            
            _mockWebPush.Setup(w => w.DetermineNotificationTier("user-1")).Returns(PresenceTier.All);
            
            await _service.RouteEmailNotificationAsync(email);
            
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                "user-1", "New Email from Test", "Subj", It.IsAny<string>(), It.IsAny<string>(), PresenceTier.All, 
                "email-user-1", It.IsAny<object[]>(), "email", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 
                false, It.IsAny<int?>(), false, It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task RouteReactionNotificationAsync_SendsPush()
        {
            var originalSender = new Employee { Id = "user-1", PushNotificationsEnabled = true, ReactionNotificationsEnabled = true };
            var reactionSender = new Employee { Id = "user-2", FirstName = "Alice" };
            _employees.Save(originalSender);
            _employees.Save(reactionSender);
            
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.Direct };
            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "test" };
            
            _mockWebPush.Setup(w => w.DetermineNotificationTier("user-1")).Returns(PresenceTier.All);
            
            await _service.RouteReactionNotificationAsync(msg, channel, "👍", reactionSender);
            
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                "user-1", "Alice reacted 👍 to:", "test", "/chat/chan-1", It.IsAny<string>(), PresenceTier.All, 
                "chat-chan-1", It.IsAny<object[]>(), "chat", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 
                false, It.IsAny<int?>(), false, It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task RouteModerationNotificationAsync_SendsToMods()
        {
            var mod = new Employee { Id = "mod-1", IsActive = true, IsAdmin = true, PushNotificationsEnabled = true, ModerationNotificationsEnabled = true };
            _employees.Save(mod);
            var report = new ReportedMessage { Id = "rep-1", Reason = "spam" };
            
            _mockWebPush.Setup(w => w.DetermineNotificationTier("mod-1")).Returns(PresenceTier.All);
            
            await _service.RouteModerationNotificationAsync(report);
            
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                "mod-1", "New Moderation Report", "A message was reported for spam", "/admin/moderation", It.IsAny<string>(), PresenceTier.All, 
                "mod-rep-1", It.IsAny<object[]>(), "moderation", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), 
                false, It.IsAny<int?>(), false, It.IsAny<string?>()), Times.Once);
        }

        [Fact]
        public async Task RouteChatNotificationAsync_WhenLimitConsecutiveSoundsAndNeverRead_IsNotSilent()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-never-read";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee 
            { 
                Id = recipientId, 
                FirstName = "Bob", 
                LastName = "Jones", 
                IsActive = true, 
                ChatNotificationsEnabled = true,
                LimitConsecutiveNotificationSounds = true,
                ConsecutiveNotificationSoundLimit = 2
            };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            var message = new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "First message in unread channel", SentAt = DateTime.UtcNow };

            _mockWebPush.Setup(w => w.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);
            _readStates.SetNotificationLevel(recipientId, channelId, "All");

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert: isSilent must be false
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                recipientId,
                "Alice Smith",
                "First message in unread channel",
                $"/chat/{channelId}",
                It.IsAny<string>(),
                PresenceTier.DesktopOnly,
                $"chat-{channelId}",
                It.IsAny<object[]>(),
                "chat",
                channelId,
                It.IsAny<string>(),
                "General",
                true,
                It.IsAny<int?>(),
                false, // isSilent = false
                It.IsAny<string?>()
            ), Times.Once);
        }

        [Fact]
        public async Task RouteChatNotificationAsync_WhenLimitConsecutiveSoundsAndUnreadExceedsLimit_IsSilent()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-streak";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee 
            { 
                Id = recipientId, 
                FirstName = "Bob", 
                LastName = "Jones", 
                IsActive = true, 
                ChatNotificationsEnabled = true,
                LimitConsecutiveNotificationSounds = true,
                ConsecutiveNotificationSoundLimit = 2
            };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            // User read channel 10 minutes ago
            var readTime = DateTime.UtcNow.AddMinutes(-10);
            _readStates.Save(new ChatReadState
            {
                Id = ChatReadState.CreateId(recipientId, channelId),
                UserId = recipientId,
                ChannelId = channelId,
                LastReadAt = readTime,
                NotificationLevel = "All"
            });

            // 3 unread messages arrived since readTime (exceeding limit of 2)
            _chatMessages.Save(new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "1", SentAt = readTime.AddMinutes(1) });
            _chatMessages.Save(new ChatMessage { Id = "msg-2", ChannelId = channelId, SenderId = senderId, Content = "2", SentAt = readTime.AddMinutes(2) });
            _chatMessages.Save(new ChatMessage { Id = "msg-3", ChannelId = channelId, SenderId = senderId, Content = "3", SentAt = readTime.AddMinutes(3) });

            var newMessage = new ChatMessage { Id = "msg-4", ChannelId = channelId, SenderId = senderId, Content = "4", SentAt = readTime.AddMinutes(4) };

            _mockWebPush.Setup(w => w.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);

            // Act
            await _service.RouteChatNotificationAsync(newMessage, channel, sender);

            // Assert: isSilent must be true
            _mockWebPush.Verify(w => w.SendNotificationAsync(
                recipientId,
                "Alice Smith",
                "4",
                $"/chat/{channelId}",
                It.IsAny<string>(),
                PresenceTier.DesktopOnly,
                $"chat-{channelId}",
                It.IsAny<object[]>(),
                "chat",
                channelId,
                It.IsAny<string>(),
                "General",
                true,
                It.IsAny<int?>(),
                true, // isSilent = true
                It.IsAny<string?>()
            ), Times.Once);
        }
    }


