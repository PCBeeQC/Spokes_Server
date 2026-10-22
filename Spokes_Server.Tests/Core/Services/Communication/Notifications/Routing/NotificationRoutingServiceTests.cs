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
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Routing;

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
        private readonly Mock<IPushDecisionService> _mockPushDecision;
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
        private readonly Mock<IInAppNotificationDispatcher> _mockInAppDispatcher;

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
            _mockPushDecision = new Mock<IPushDecisionService>();
            _queue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _persistence, _config, _companyProfile);
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

            var commContext = new Mock<CommunicationContext>();
            commContext.Setup(c => c.ChatChannels).Returns(_chatChannels);
            commContext.Setup(c => c.ChatMessages).Returns(_chatMessages);
            commContext.Setup(c => c.ChatReadStates).Returns(_readStates);
            commContext.Setup(c => c.EmailFolders).Returns(_emailFolders);
            commContext.Setup(c => c.EmailMessages).Returns(_emailMessages);

            var hrContext = new Mock<HrContext>();
            hrContext.Setup(h => h.Employees).Returns(_employees);
            hrContext.Setup(h => h.Teams).Returns(_teams);

            var projectsContext = new Mock<ProjectsContext>();
            projectsContext.Setup(p => p.Projects).Returns(_projects);

            var coreContext = new Mock<CoreContext>();
            coreContext.Setup(c => c.CompanyProfile).Returns(_companyProfile);

            _mockInAppDispatcher = new Mock<IInAppNotificationDispatcher>();

            _service = new NotificationRoutingService(
                commContext.Object,
                hrContext.Object,
                projectsContext.Object,
                coreContext.Object,
                _mockWebPush.Object,
                _queue,
                _keystore,
                _mockCrypto.Object,
                _mockDataProtection.Object,
                mockChatAccess.Object,
                logger: _mockLogger.Object,
                pushDecision: _mockPushDecision.Object,
                inAppDispatcher: _mockInAppDispatcher.Object);
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
        public void GetTotalBadgeCount_CalculatesCountsCorrectly()
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
            var totalCount = _service.GetTotalBadgeCount(userId);

            // Assert
            Assert.NotNull(_service);
            Assert.NotNull(folder);
            Assert.NotNull(email);
            Assert.NotNull(channel);
            Assert.NotNull(employee);
            Assert.NotNull(msg);
            Assert.Equal(2, totalCount); // 1 email + 1 chat
        }

        [Fact]
        public async Task RouteChatNotificationAsync_SendsDesktopNotification()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-1";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee { Id = recipientId, FirstName = "Bob", LastName = "Jones", IsActive = true, ChatNotificationsEnabled = true, PushNotificationsEnabled = true };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            var message = new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "Hello Desktop Only", SentAt = DateTime.UtcNow };
            
            _mockPushDecision.Setup(p => p.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);
            _readStates.SetNotificationLevel(recipientId, channelId, "All");

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert
            Assert.NotNull(sender);
            Assert.NotNull(recipient);
            Assert.NotNull(channel);
            Assert.NotNull(message);
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == recipientId &&
                r.Title == "Alice Smith" &&
                r.Body == "Hello Desktop Only" &&
                r.Url == $"/chat/{channelId}" &&
                r.Tier == PresenceTier.DesktopOnly &&
                r.Tag == $"chat-{channelId}" &&
                r.Category == "chat" &&
                r.ThreadId == channelId &&
                r.ChannelName == "General" &&
                r.IsGroupChat == true
            )), Times.Once);
            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == recipientId &&
                n.Content == "Hello Desktop Only" &&
                !n.IsReaction
            )), Times.Once);
        }

        [Fact]
        public async Task RouteChatNotificationAsync_WhenTierNone_DispatchesInAppNotification_AndSkipsPush()
        {
            var senderId = "user-sender";
            var recipientId = "user-recipient";
            var channelId = "chan-1";

            var sender = new Employee { Id = senderId, FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee { Id = recipientId, FirstName = "Bob", LastName = "Jones", IsActive = true, ChatNotificationsEnabled = true, PushNotificationsEnabled = true };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel { Id = channelId, Name = "General", ChannelType = ChatChannelType.General };
            _chatChannels.Save(channel);

            var message = new ChatMessage { Id = "msg-1", ChannelId = channelId, SenderId = senderId, Content = "Hello Online Only", SentAt = DateTime.UtcNow };

            _mockPushDecision.Setup(p => p.DetermineNotificationTier(recipientId)).Returns(PresenceTier.None);
            _readStates.SetNotificationLevel(recipientId, channelId, "All");

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert
            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == recipientId &&
                n.Title == "Alice Smith" &&
                n.Content == "Hello Online Only" &&
                n.ChannelId == channelId &&
                !n.IsReaction
            )), Times.Once);
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.IsAny<PushNotificationRequest>()), Times.Never);
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
            Assert.NotNull(sender);
            Assert.NotNull(recipient);
            Assert.NotNull(channel);
            Assert.NotNull(message);
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.IsAny<PushNotificationRequest>()), Times.Never);
        }

        [Fact]
        public void GetBadgeBreakdown_ReturnsCorrectBreakdown()
        {
            var userId = "user-1";
            var folder = new EmailFolder { Id = "fold-1", EmployeeId = userId, Path = "INBOX", UnreadCount = 1 };
            _emailFolders.Save(folder);
            var email = new EmailMessage { Id = "email-1", EmployeeId = userId, FolderPath = "INBOX", IsRead = false };
            _emailMessages.Save(email);
            
            var syncResult = _service.GetBadgeBreakdown(userId);
            var syncTotal = _service.GetTotalBadgeCount(userId);
            
            Assert.IsType<BadgeBreakdownResult>(syncResult);
            Assert.Equal(1, syncResult.Count);
            Assert.Equal(1, syncTotal);
            Assert.True(syncResult.Breakdown.ContainsKey("Email_INBOX"));
        }

        [Fact]
        public async Task RouteEmailNotificationAsync_SendsPush()
        {
            var employee = new Employee { Id = "user-1", PushNotificationsEnabled = true, EmailNotificationsEnabled = true };
            _employees.Save(employee);
            var email = new EmailMessage { Id = "email-1", EmployeeId = "user-1", FromName = "Test", Subject = "Subj", FolderPath = "INBOX" };
            
            _mockPushDecision.Setup(p => p.DetermineNotificationTier("user-1")).Returns(PresenceTier.All);
            
            await _service.RouteEmailNotificationAsync(email);
            
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == "user-1" &&
                r.Title == "New Email from Test" &&
                r.Body == "Subj" &&
                r.Tier == PresenceTier.All &&
                r.Tag == "email-user-1" &&
                r.Category == "email"
            )), Times.Once);
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
            
            _mockPushDecision.Setup(p => p.DetermineNotificationTier("user-1")).Returns(PresenceTier.All);
            
            await _service.RouteReactionNotificationAsync(msg, channel, "👍", reactionSender);
            
            Assert.NotNull(originalSender);
            Assert.NotNull(reactionSender);
            Assert.NotNull(channel);
            Assert.NotNull(msg);
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == "user-1" &&
                r.Title == "Alice reacted 👍 to:" &&
                r.Body == "test" &&
                r.Url == "/chat/chan-1" &&
                r.Tier == PresenceTier.All &&
                r.Tag == "chat-chan-1" &&
                r.Category == "chat"
            )), Times.Once);
            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user-1" &&
                n.Title == "Alice reacted 👍 to:" &&
                n.Content == "test" &&
                n.ChannelId == "chan-1" &&
                n.IsReaction &&
                !n.IsGroupChat
            )), Times.Once);
        }

        [Fact]
        public async Task RouteReactionNotificationAsync_WhenUserOnline_DispatchesInAppNotification_AndSkipsPush()
        {
            var originalSender = new Employee { Id = "user-1", PushNotificationsEnabled = true, ReactionNotificationsEnabled = true };
            var reactionSender = new Employee { Id = "user-2", FirstName = "Alice" };
            _employees.Save(originalSender);
            _employees.Save(reactionSender);

            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.Direct };
            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "online test" };

            // Online / focused
            _mockPushDecision.Setup(p => p.DetermineNotificationTier("user-1")).Returns(PresenceTier.None);

            await _service.RouteReactionNotificationAsync(msg, channel, "❤️", reactionSender);

            // In-app snackbar must be dispatched
            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user-1" &&
                n.Title == "Alice reacted ❤️ to:" &&
                n.Content == "online test" &&
                n.IsReaction
            )), Times.Once);

            // WebPush must be skipped since user is actively in-app
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.IsAny<PushNotificationRequest>()), Times.Never);
        }

        [Fact]
        public async Task RouteReactionNotificationAsync_WhenPushDisabled_StillDispatchesInApp()
        {
            // User disabled WebPush, but has ReactionNotificationsEnabled = true
            var originalSender = new Employee { Id = "user-1", PushNotificationsEnabled = false, ReactionNotificationsEnabled = true };
            var reactionSender = new Employee { Id = "user-2", FirstName = "Alice" };
            _employees.Save(originalSender);
            _employees.Save(reactionSender);

            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.Direct };
            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "no push test" };

            _mockPushDecision.Setup(p => p.DetermineNotificationTier("user-1")).Returns(PresenceTier.All);

            await _service.RouteReactionNotificationAsync(msg, channel, "🎉", reactionSender);

            // In-app notification must still dispatch
            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == "user-1" &&
                n.Title == "Alice reacted 🎉 to:" &&
                n.Content == "no push test" &&
                n.IsReaction
            )), Times.Once);

            // WebPush must not be sent because PushNotificationsEnabled is false
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.IsAny<PushNotificationRequest>()), Times.Never);
        }

        [Fact]
        public async Task RouteModerationNotificationAsync_SendsToMods()
        {
            var mod = new Employee { Id = "mod-1", IsActive = true, IsAdmin = true, PushNotificationsEnabled = true, ModerationNotificationsEnabled = true };
            _employees.Save(mod);
            var report = new ReportedMessage { Id = "rep-1", Reason = "spam" };
            
            _mockPushDecision.Setup(p => p.DetermineNotificationTier("mod-1")).Returns(PresenceTier.All);
            
            await _service.RouteModerationNotificationAsync(report);
            
            Assert.NotNull(mod);
            Assert.NotNull(report);
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == "mod-1" &&
                r.Title == "New Moderation Report" &&
                r.Body == "A message was reported for spam" &&
                r.Url == "/admin/moderation" &&
                r.Tier == PresenceTier.All &&
                r.Tag == "mod-rep-1" &&
                r.Category == "moderation"
            )), Times.Once);
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

            _mockPushDecision.Setup(p => p.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);
            _readStates.SetNotificationLevel(recipientId, channelId, "All");

            // Act
            await _service.RouteChatNotificationAsync(message, channel, sender);

            // Assert: isSilent must be false
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == recipientId &&
                r.Title == "Alice Smith" &&
                r.Body == "First message in unread channel" &&
                r.Url == $"/chat/{channelId}" &&
                r.Tier == PresenceTier.DesktopOnly &&
                r.Tag == $"chat-{channelId}" &&
                r.Category == "chat" &&
                r.ThreadId == channelId &&
                r.ChannelName == "General" &&
                r.IsGroupChat == true &&
                r.IsSilent == false
            )), Times.Once);
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

            _mockPushDecision.Setup(p => p.DetermineNotificationTier(recipientId)).Returns(PresenceTier.DesktopOnly);

            // Act
            await _service.RouteChatNotificationAsync(newMessage, channel, sender);

            // Assert: isSilent must be true
            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == recipientId &&
                r.Title == "Alice Smith" &&
                r.Body == "4" &&
                r.Url == $"/chat/{channelId}" &&
                r.Tier == PresenceTier.DesktopOnly &&
                r.Tag == $"chat-{channelId}" &&
                r.Category == "chat" &&
                r.ThreadId == channelId &&
                r.ChannelName == "General" &&
                r.IsGroupChat == true &&
                r.IsSilent == true
            )), Times.Once);
        }

        [Fact]
        public void Constructor_WithBoundedContexts_InstantiatesSuccessfully()
        {
            var mockComm = new Mock<CommunicationContext>();
            mockComm.Setup(c => c.ChatChannels).Returns(_chatChannels);
            mockComm.Setup(c => c.ChatMessages).Returns(_chatMessages);
            mockComm.Setup(c => c.ChatReadStates).Returns(_readStates);
            mockComm.Setup(c => c.EmailFolders).Returns(_emailFolders);
            mockComm.Setup(c => c.EmailMessages).Returns(_emailMessages);

            var mockHr = new Mock<HrContext>();
            mockHr.Setup(h => h.Employees).Returns(_employees);
            mockHr.Setup(h => h.Teams).Returns(_teams);

            var mockProjects = new Mock<ProjectsContext>();
            mockProjects.Setup(p => p.Projects).Returns(_projects);

            var mockCore = new Mock<CoreContext>();
            mockCore.Setup(c => c.CompanyProfile).Returns(_companyProfile);

            var filter = new NotificationRecipientFilter(_readStates, _chatMessages);
            var decryptor = new NotificationContentDecryptor(_keystore, _mockCrypto.Object);
            var limiter = new NotificationRateLimiter(_readStates, _chatMessages);

            var service = new NotificationRoutingService(
                mockComm.Object,
                mockHr.Object,
                mockProjects.Object,
                mockCore.Object,
                _mockWebPush.Object,
                _queue,
                _keystore,
                _mockCrypto.Object,
                _mockDataProtection.Object,
                new Mock<IChatChannelAccessService>().Object,
                filter,
                decryptor,
                limiter,
                _mockLogger.Object);

            Assert.NotNull(service);
        }

        [Fact]
        public async Task RouteChatNotificationAsync_WhenEncryptedMessage_PopulatesIsEncryptedAndMaskedProperties()
        {
            var sender = new Employee { Id = "user-sender", FirstName = "Alice", LastName = "Smith", IsActive = true };
            var recipient = new Employee { Id = "user-recipient", FirstName = "Bob", LastName = "Jones", IsActive = true, ChatNotificationsEnabled = true, PushNotificationsEnabled = true };
            _employees.Save(sender);
            _employees.Save(recipient);

            var channel = new ChatChannel
            {
                Id = "enc-channel-1",
                Name = "Secret Channel",
                ChannelType = ChatChannelType.General,
                IsEncrypted = true
            };
            _chatChannels.Save(channel);

            _readStates.SetNotificationLevel(recipient.Id, channel.Id, "All");
            _mockPushDecision.Setup(p => p.DetermineNotificationTier(recipient.Id)).Returns(PresenceTier.DesktopOnly);

            var message = new ChatMessage
            {
                Id = "msg-enc-1",
                ChannelId = channel.Id,
                SenderId = sender.Id,
                Content = "EncryptedBase64Content",
                IsEncrypted = true,
                SentAt = DateTime.UtcNow
            };

            await _service.RouteChatNotificationAsync(message, channel, sender);

            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == recipient.Id &&
                n.IsEncrypted == true &&
                n.MaskedContent == "🔒 New Secure Message"
            )), Times.Once);

            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == recipient.Id &&
                r.IsEncrypted == true &&
                r.MaskedBody == "🔒 New Secure Message"
            )), Times.Once);
        }

        [Fact]
        public async Task RouteReactionNotificationAsync_WhenEncryptedMessage_PopulatesIsEncryptedAndMaskedProperties()
        {
            var originalSender = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith", IsActive = true, ReactionNotificationsEnabled = true, PushNotificationsEnabled = true };
            var reactor = new Employee { Id = "user-sender", FirstName = "Bob", LastName = "Jones", IsActive = true };
            _employees.Save(originalSender);
            _employees.Save(reactor);

            var channel = new ChatChannel
            {
                Id = "enc-channel-2",
                Name = "Secret Channel",
                ChannelType = ChatChannelType.General,
                IsEncrypted = true
            };
            _chatChannels.Save(channel);

            _mockPushDecision.Setup(p => p.DetermineNotificationTier(originalSender.Id)).Returns(PresenceTier.DesktopOnly);

            var message = new ChatMessage
            {
                Id = "msg-enc-2",
                ChannelId = channel.Id,
                SenderId = originalSender.Id,
                Content = "EncryptedBase64Content",
                IsEncrypted = true,
                SentAt = DateTime.UtcNow
            };

            await _service.RouteReactionNotificationAsync(message, channel, "👍", reactor);

            _mockInAppDispatcher.Verify(d => d.Dispatch(It.Is<InAppNotification>(n =>
                n.RecipientUserId == originalSender.Id &&
                n.IsEncrypted == true &&
                n.MaskedContent == "🔒 Secure Message"
            )), Times.Once);

            _mockWebPush.Verify(w => w.SendNotificationAsync(It.Is<PushNotificationRequest>(r =>
                r.UserId == originalSender.Id &&
                r.IsEncrypted == true &&
                r.MaskedBody == "🔒 Secure Message"
            )), Times.Once);
        }
    }


