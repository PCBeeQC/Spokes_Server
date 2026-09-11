using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services;

namespace Spokes_Server.Tests.Core.Services.Communication
{
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
            mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => new List<string> { "user-recipient", "user-1", "user-sender" });

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
                It.IsAny<bool>()
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
                BlockedUserIds = new List<string> { senderId }
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
                It.IsAny<bool>()
            ), Times.Never);
        }
    }
}
