namespace Spokes_Server.Tests.Core.Services.Communication;

using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
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
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Core;

public class ChatServiceTests : IDisposable
{
        private readonly string _testDataDir;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;
        private readonly ChatService _service;

        private readonly EmployeeRepository _employees;
        private readonly ChatChannelRepository _channels;
        private readonly ChatMessageRepository _messages;
        private readonly TeamRepository _teams;
        private readonly ChatReadStateRepository _readStates;
        private readonly CompanyProfileRepository _companyProfile;
        private readonly ProjectRepository _projects;
        private readonly AlbumRepository _albums;

        private readonly Mock<IHubContext<ChatHub>> _mockHubContext;
        private readonly Mock<IHubClients> _mockClients;
        private readonly Mock<IClientProxy> _mockClientProxy;
        private readonly ChatStateService _chatState;
        private readonly Mock<IWebPushService> _mockWebPush;
        private readonly NotificationQueueService _notificationQueue;
        private readonly NotificationRoutingService _notificationRouting;
        private readonly Mock<PresenceStateService> _mockPresence;
        private readonly Mock<IFileService> _mockFileService;
        private readonly Mock<IContentModerationService> _mockModeration;

        public ChatServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Chat_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _channels = new ChatChannelRepository(_persistence, _config);
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            _messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
            _teams = new TeamRepository(_persistence, _config);
            _companyProfile = new CompanyProfileRepository(_persistence, _config);
            var profile = _companyProfile.Get();
            profile.Edition = "Family";
            _companyProfile.Save(profile);
            
            _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfile);
            _projects = new ProjectRepository(_persistence, _config);

            _mockHubContext = new Mock<IHubContext<ChatHub>>();
            _mockClients = new Mock<IHubClients>();
            _mockClientProxy = new Mock<IClientProxy>();
            _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
            _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

            _chatState = new ChatStateService();

            var pushRepo = new PushSubscriptionRepository(_persistence, _config);
            var systemConfigRepo = new SystemConfigRepository(_persistence, _config);
            _albums = new AlbumRepository(_persistence, _config);
            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigRepo = new ServerConfigRepository(_persistence, _config, encService);
            _mockPresence = new Mock<PresenceStateService>(_chatState);
            _notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object);

            _mockWebPush = new Mock<IWebPushService>();

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns((string userId) => _channels.GetAll().ToList());
            mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => new List<string> { "user-1", "user-2" });

            var mockServiceProvider = new Mock<IServiceProvider>();
            mockServiceProvider.Setup(sp => sp.GetService(typeof(IChatChannelAccessService))).Returns(mockChatAccess.Object);

            _notificationRouting = new NotificationRoutingService(
                _employees,
                _projects,
                _readStates,
                _teams,
                _mockWebPush.Object,
                _notificationQueue,
                new GlobalKeystoreService(),
                new Mock<ICryptoService>().Object,
                new Mock<IDataProtectionProvider>().Object,
                new EmailFolderRepository(_persistence, _config),
                new EmailMessageRepository(_persistence, _config),
                _channels,
                _messages,
                _companyProfile,
                new Mock<ILogger<NotificationRoutingService>>().Object,
                mockServiceProvider.Object);

            _mockFileService = new Mock<IFileService>();

            _mockModeration = new Mock<IContentModerationService>();
            _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>()))
                           .ReturnsAsync((false, string.Empty, Spokes_Server.Core.Models.Core.TextModerationAction.Sanitize));

            _service = new ChatService(
                _employees,
                _channels,
                _messages,
                _teams,
                _readStates,
                _projects,
                _mockHubContext.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                new Mock<ILogger<ChatService>>().Object,
                _config,
                _mockFileService.Object,
                new Mock<ICryptoService>().Object,
                new GlobalKeystoreService(),
                null!,
                _mockModeration.Object,
                systemConfigRepo,
                _companyProfile,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, null!, _employees),
                new MarkdownSanitizerService());
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
        public async Task SendMessageAsync_SavesAndBroadcasts()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", Name = "General" };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync("user-1", "chan-1", "Hello World");

            Assert.NotNull(result);
            Assert.Equal("Hello World", result.Content);
            Assert.Equal("user-1", result.SenderId);

            // Verify persistence
            Assert.NotNull(_messages.GetById(result.Id));

            // Verify sender is marked in ReadBy
            Assert.Contains("user-1", result.ReadBy);

            // Verify channel read state updated for sender
            var readState = _readStates.GetReadState("user-1", "chan-1");
            Assert.NotNull(readState);

            // Verify SignalR broadcast
            _mockClients.Verify(c => c.Group("channel_chan-1"), Times.Once);
            _mockClientProxy.Verify(p => p.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task SendCallInviteAsync_MarksSenderAsRead()
        {
            var caller = new Employee { Id = "user-caller", FirstName = "Caller", LastName = "User" };
            _employees.Save(caller);
            var channel = new ChatChannel { Id = "chan-call", Name = "General" };
            _channels.Save(channel);

            var result = await _service.SendCallInviteAsync("user-caller", "chan-call");

            Assert.NotNull(result);
            Assert.Contains("user-caller", result.ReadBy);
            var readState = _readStates.GetReadState("user-caller", "chan-call");
            Assert.NotNull(readState);
        }

        [Fact]
        public async Task SendMessageAsync_MentionsAlbum_SharesIfAuthorized()
        {
            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true };
            _employees.Save(member);

            var channel = new ChatChannel { Id = "chan-album", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);
            var album = new Spokes_Server.Core.Models.Communication.Album { Id = "album-1", OwnerId = "user-owner" };
            _albums.Save(album);

            var result = await _service.SendMessageAsync("user-owner", "chan-album", "Check this #[Vacation](album:album-1)");

            var updatedAlbum = _albums.GetById("album-1");
            Assert.Contains("chan-album", updatedAlbum.SharedWithChannelIds);
            Assert.Contains("album-1", result.MentionedAlbumIds);
        }

        [Fact]
        public async Task SendMessageAsync_MentionsInvalidAlbum_IgnoresAndSucceeds()
        {
            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-invalid", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync("user-owner", "chan-invalid", "Check this #[Missing](album:album-invalid)");

            Assert.NotNull(result);
            Assert.Contains("album-invalid", result.MentionedAlbumIds);
        }

        [Fact]
        public async Task SendMessageAsync_MentionsAlbum_DoesNotShareIfUnauthorized()
        {
            var sender = new Employee { Id = "user-other", FirstName = "User", LastName = "Other", IsActive = true };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true };
            _employees.Save(member);

            var channel = new ChatChannel { Id = "chan-album2", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);
            var album = new Spokes_Server.Core.Models.Communication.Album { Id = "album-2", OwnerId = "user-owner" };
            _albums.Save(album);

            var result = await _service.SendMessageAsync("user-other", "chan-album2", "Look at this #[Private](album:album-2)");

            var updatedAlbum = _albums.GetById("album-2");
            Assert.DoesNotContain("chan-album2", updatedAlbum.SharedWithChannelIds);
        }

        [Fact]
        public async Task SendMessageAsync_MentionsAlbum_ProcessedInBusinessEdition()
        {
            var profile = _companyProfile.Get();
            profile.Edition = "Business";
            _companyProfile.Save(profile);

            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true };
            _employees.Save(member);

            var channel = new ChatChannel { Id = "chan-album-std", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);
            var album = new Spokes_Server.Core.Models.Communication.Album { Id = "album-std", OwnerId = "user-owner" };
            _albums.Save(album);

            var result = await _service.SendMessageAsync("user-owner", "chan-album-std", "Check this #[Vacation](album:album-std)");

            var updatedAlbum = _albums.GetById("album-std");
            Assert.Contains("chan-album-std", updatedAlbum.SharedWithChannelIds);
            
            // Should parse the mention
            Assert.Contains("album-std", result.MentionedAlbumIds ?? new List<string>());
        }

        [Fact]
        public async Task StartStopTyping_BroadlyNotifies()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);

            await _service.StartTypingAsync("user-1", "chan-1");
            _mockClientProxy.Verify(p => p.SendCoreAsync("UserTyping", It.IsAny<object[]>(), default), Times.Once);

            await _service.StopTypingAsync("user-1", "chan-1");
            _mockClientProxy.Verify(p => p.SendCoreAsync("UserStoppedTyping", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task EditMessageAsync_UpdatesContentAndNotifies()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "Old", ChannelId = "chan-1" };
            _messages.Save(msg);

            await _service.EditMessageAsync("user-1", "msg-1", "New Content");

            var updated = _messages.GetById("msg-1");
            Assert.Equal("New Content", updated.Content);
            Assert.NotNull(updated.EditedAt);

            _mockClientProxy.Verify(p => p.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task EditMessageAsync_WithModerationBlock_DoesNotUpdateContent()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-block-test", SenderId = "user-1", Content = "Old Safe", ChannelId = "chan-1" };
            _messages.Save(msg);

            _mockModeration.Setup(m => m.EvaluateTextAsync("Bad Content"))
                           .ReturnsAsync((true, "Bad Content", Spokes_Server.Core.Models.Core.TextModerationAction.Block));

            await _service.EditMessageAsync("user-1", "msg-block-test", "Bad Content");

            var unedited = _messages.GetById("msg-block-test");
            Assert.NotNull(unedited);
            Assert.Equal("Old Safe", unedited.Content);
            Assert.Null(unedited.EditedAt);
        }

        [Fact]
        public async Task EditMessageAsync_WithModerationSanitize_UpdatesToSanitizedContent()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-sanitize-test", SenderId = "user-1", Content = "Old Safe", ChannelId = "chan-1" };
            _messages.Save(msg);

            _mockModeration.Setup(m => m.EvaluateTextAsync("Bad Content"))
                           .ReturnsAsync((true, "Xkpw Content", Spokes_Server.Core.Models.Core.TextModerationAction.Sanitize));

            await _service.EditMessageAsync("user-1", "msg-sanitize-test", "Bad Content");

            var updated = _messages.GetById("msg-sanitize-test");
            Assert.NotNull(updated);
            Assert.Equal("Xkpw Content", updated.Content);
            Assert.NotNull(updated.EditedAt);

            _mockClientProxy.Verify(p => p.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task EditMessageAsync_RevokedUser_DoesNotUpdateContent()
        {
            var sender = new Employee { Id = "user-revoked", FirstName = "User", LastName = "Revoked" };
            _employees.Save(sender);
            // Group channel where user is not in ParticipantIds
            var channel = new ChatChannel
            {
                Id = "chan-revoked",
                ChannelType = ChatChannelType.Group,
                CreatedById = "other-user",
                ParticipantIds = new List<string> { "other-user" }
            };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-rev", SenderId = "user-revoked", Content = "Original", ChannelId = channel.Id };
            _messages.Save(msg);

            await _service.EditMessageAsync("user-revoked", "msg-rev", "Hacked Update");

            var unchanged = _messages.GetById("msg-rev");
            Assert.NotNull(unchanged);
            Assert.Equal("Original", unchanged.Content);
            _mockClientProxy.Verify(p => p.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), default), Times.Never);
        }

        [Fact]
        public async Task DeleteMessageAsync_MarksAsDeleted()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "To Delete", ChannelId = "chan-1" };
            _messages.Save(msg);

            await _service.DeleteMessageAsync("user-1", "msg-1");

            var deleted = _messages.GetById("msg-1");
            Assert.True(deleted.IsDeleted);

            _mockClientProxy.Verify(p => p.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task DeleteMessageAsync_RevokedUser_DoesNotDelete()
        {
            var sender = new Employee { Id = "user-del-rev", FirstName = "User", LastName = "Revoked" };
            _employees.Save(sender);
            var channel = new ChatChannel
            {
                Id = "chan-del-rev",
                ChannelType = ChatChannelType.Group,
                CreatedById = "other-user",
                ParticipantIds = new List<string> { "other-user" }
            };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-del-rev", SenderId = "user-del-rev", Content = "Keep me", ChannelId = channel.Id };
            _messages.Save(msg);

            await _service.DeleteMessageAsync("user-del-rev", "msg-del-rev");

            var unchanged = _messages.GetById("msg-del-rev");
            Assert.NotNull(unchanged);
            Assert.False(unchanged.IsDeleted);
            _mockClientProxy.Verify(p => p.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), default), Times.Never);
        }

        [Fact]
        public async Task ToggleReactionAsync_AddsAndRemoves()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);
            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-2", Content = "React", ChannelId = "chan-1" };
            _messages.Save(msg);

            // 1. Add
            await _service.ToggleReactionAsync("user-1", "msg-1", "👍");
            var reactAdded = _messages.GetById("msg-1");
            Assert.Contains("👍:user-1", reactAdded.Reactions);
            _mockClientProxy.Verify(p => p.SendCoreAsync("ReactionAdded", It.IsAny<object[]>(), default), Times.Once);

            // 2. Remove
            await _service.ToggleReactionAsync("user-1", "msg-1", "👍");
            var reactRemoved = _messages.GetById("msg-1");
            Assert.DoesNotContain("👍:user-1", reactRemoved.Reactions);
            _mockClientProxy.Verify(p => p.SendCoreAsync("ReactionRemoved", It.IsAny<object[]>(), default), Times.Once);
        }

        [Fact]
        public async Task CreateDirectMessageChannelAsync_Works()
        {
            var chan = await _service.CreateDirectMessageChannelAsync("user-1", "user-2");
            Assert.NotNull(chan);
            Assert.Equal(ChatChannelType.Direct, chan.ChannelType);
            Assert.Contains("user-1", chan.ParticipantIds);
            Assert.Contains("user-2", chan.ParticipantIds);
        }

        [Fact]
        public async Task UpdateChannelAsync_NormalizesDirectChannel_RemovingAnnouncementSettings()
        {
            var channel = new ChatChannel
            {
                Id = "dm_test_update",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" },
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "user-1" },
                AllowedPostTeamIds = new List<string> { "team-1" }
            };

            await _service.UpdateChannelAsync(channel);

            var saved = _channels.GetById("dm_test_update");
            Assert.NotNull(saved);
            Assert.False(saved.IsAnnouncementOnly);
            Assert.Empty(saved.AllowedPostUserIds);
            Assert.Empty(saved.AllowedPostTeamIds);
        }

        [Fact]
        public async Task CreateChannelAsync_NormalizesDirectChannel_RemovingAnnouncementSettings()
        {
            var channel = new ChatChannel
            {
                Id = "dm_test_create",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" },
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "user-1" },
                AllowedPostTeamIds = new List<string> { "team-1" }
            };

            await _service.CreateChannelAsync(channel);

            var saved = _channels.GetById("dm_test_create");
            Assert.NotNull(saved);
            Assert.False(saved.IsAnnouncementOnly);
            Assert.Empty(saved.AllowedPostUserIds);
            Assert.Empty(saved.AllowedPostTeamIds);
        }

        [Fact]
        public void GetChannelDisplayName_SelfDM_ReturnsFullNameWithSuffix()
        {
            var user = new Employee { Id = "user-1", FirstName = "Mats", LastName = "Larsson" };
            _employees.Save(user);

            var channel = new ChatChannel
            {
                Id = "dm_self",
                Name = "Direct Message",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-1" }
            };

            var name = _service.GetChannelDisplayName(channel, "user-1");
            Assert.Equal("Mats Larsson (you)", name);
            Assert.True(_service.IsSelfDirectMessage(channel, "user-1"));
        }

        [Fact]
        public void GetChannelDisplayName_TwoPersonDM_ReturnsOtherPerson()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Mats", LastName = "Larsson" };
            var user2 = new Employee { Id = "user-2", FirstName = "Jane", LastName = "Doe" };
            _employees.Save(user1);
            _employees.Save(user2);

            var channel = new ChatChannel
            {
                Id = "dm_pair",
                Name = "Direct Message",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };

            var name = _service.GetChannelDisplayName(channel, "user-1");
            Assert.Equal("Jane Doe", name);
            Assert.False(_service.IsSelfDirectMessage(channel, "user-1"));
        }

        [Fact]
        public async Task SendMessageAsync_AutoMarksPriorMessagesAsReadBySender()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones" };
            _employees.Save(user1);
            _employees.Save(user2);

            var channel = new ChatChannel
            {
                Id = "dm_auto_read",
                Name = "Direct Message",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };
            _channels.Save(channel);

            // user-1 sends a message
            var m1 = await _service.SendMessageAsync("user-1", "dm_auto_read", "Hello Bob");
            Assert.NotNull(m1);
            Assert.DoesNotContain("user-2", m1.ReadBy);

            // user-2 sends a reply
            var m2 = await _service.SendMessageAsync("user-2", "dm_auto_read", "Hey Alice");
            Assert.NotNull(m2);

            // Prior message m1 should now be marked as read by user-2
            var updatedM1 = _messages.GetById(m1.Id);
            Assert.NotNull(updatedM1);
            Assert.Contains("user-2", updatedM1.ReadBy);
        }

        [Fact]
        public async Task MarkChannelMessagesAsReadAsync_MarksAllUnreadMessagesForUser()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones" };
            _employees.Save(user1);
            _employees.Save(user2);

            var channel = new ChatChannel
            {
                Id = "dm_batch_read",
                Name = "Direct Message",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };
            _channels.Save(channel);

            var m1 = await _service.SendMessageAsync("user-1", "dm_batch_read", "Message 1");
            var m2 = await _service.SendMessageAsync("user-1", "dm_batch_read", "Message 2");

            Assert.DoesNotContain("user-2", m1!.ReadBy);
            Assert.DoesNotContain("user-2", m2!.ReadBy);

            // User 2 opens the channel
            await _service.MarkChannelMessagesAsReadAsync("dm_batch_read", "user-2");

            var updatedM1 = _messages.GetById(m1.Id);
            var updatedM2 = _messages.GetById(m2.Id);

            Assert.Contains("user-2", updatedM1!.ReadBy);
            Assert.Contains("user-2", updatedM2!.ReadBy);
        }

        [Fact]
        public async Task LeaveVoiceAsync_WhenActiveInviteIsJoined_TransitionsToEndedWhenLastUserLeaves()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            _employees.Save(user1);

            var channel = new ChatChannel
            {
                Id = "call_channel_joined",
                Name = "Direct Call",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };
            _channels.Save(channel);

            var invite = new ChatMessage
            {
                ChannelId = channel.Id,
                SenderId = "user-1",
                MessageType = "CallInvite",
                CallStatus = "Joined",
                SentAt = DateTime.UtcNow
            };
            _messages.Save(invite);

            _chatState.NotifyVoiceMemberJoined(channel.Id, "user-1");

            await _service.LeaveVoiceAsync("user-1", channel.Id);

            var updatedInvite = _messages.GetById(invite.Id);
            Assert.NotNull(updatedInvite);
            Assert.Equal("Ended", updatedInvite.CallStatus);
        }

        [Fact]
        public async Task LeaveVoiceAsync_WhenActiveInviteIsActive_TransitionsToEndedWhenLastUserLeaves()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            _employees.Save(user1);

            var channel = new ChatChannel
            {
                Id = "call_channel_active",
                Name = "Direct Call",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };
            _channels.Save(channel);

            var invite = new ChatMessage
            {
                ChannelId = channel.Id,
                SenderId = "user-1",
                MessageType = "CallInvite",
                CallStatus = "Active",
                SentAt = DateTime.UtcNow
            };
            _messages.Save(invite);

            _chatState.NotifyVoiceMemberJoined(channel.Id, "user-1");

            await _service.LeaveVoiceAsync("user-1", channel.Id);

            var updatedInvite = _messages.GetById(invite.Id);
            Assert.NotNull(updatedInvite);
            Assert.Equal("Ended", updatedInvite.CallStatus);
        }
    }
