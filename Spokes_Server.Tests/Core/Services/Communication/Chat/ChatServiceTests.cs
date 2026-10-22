namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

using System.Collections.Generic;
using System.IO;
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
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat.Formatters;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Aggregate.Contexts;

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
        private readonly ServerEscrowService _escrowService;

        private readonly Mock<IChatRealtimeGateway> _mockRealtimeGateway;
        private readonly ChatStateService _chatState;
        private readonly Mock<IWebPushService> _mockWebPush;
        private readonly NotificationQueueService _notificationQueue;
        private readonly NotificationRoutingService _notificationRouting;
        private readonly Mock<PresenceStateService> _mockPresence;
        private readonly Mock<IFileService> _mockFileService;
        private readonly Mock<IContentModerationService> _mockModeration;
        private readonly Mock<CommunicationContext> _commContext;
        private readonly Mock<HrContext> _hrContext;
        private readonly Mock<ProjectsContext> _projectsContext;

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

            _mockRealtimeGateway = new Mock<IChatRealtimeGateway>();

            _chatState = new ChatStateService();

            var systemConfigRepo = new SystemConfigRepository(_persistence, _config);
            _albums = new AlbumRepository(_persistence, _config);
            var encService = new EncryptionService(_config);
            var serverConfigRepo = new ServerConfigRepository(_persistence, _config);
            _escrowService = new ServerEscrowService(_companyProfile, serverConfigRepo, new Mock<ICryptoService>().Object);
            _mockPresence = new Mock<PresenceStateService>(_chatState);
            _notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _persistence, _config, _companyProfile);

            _mockWebPush = new Mock<IWebPushService>();

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns((string userId) => _channels.GetAll().ToList());
            mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => new List<string> { "user-1", "user-2" });

            _commContext = new Mock<CommunicationContext>();
            _commContext.Setup(c => c.ChatChannels).Returns(_channels);
            _commContext.Setup(c => c.ChatMessages).Returns(_messages);
            _commContext.Setup(c => c.ChatReadStates).Returns(_readStates);
            _commContext.Setup(c => c.EmailFolders).Returns(new EmailFolderRepository(_persistence, _config));
            _commContext.Setup(c => c.EmailMessages).Returns(new EmailMessageRepository(_persistence, _config));

            _hrContext = new Mock<HrContext>();
            _hrContext.Setup(h => h.Employees).Returns(_employees);
            _hrContext.Setup(h => h.Teams).Returns(_teams);

            _projectsContext = new Mock<ProjectsContext>();
            _projectsContext.Setup(p => p.Projects).Returns(_projects);

            var coreContext = new Mock<CoreContext>();
            coreContext.Setup(c => c.CompanyProfile).Returns(_companyProfile);

            _notificationRouting = new NotificationRoutingService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                coreContext.Object,
                _mockWebPush.Object,
                _notificationQueue,
                new GlobalKeystoreService(),
                new Mock<ICryptoService>().Object,
                new Mock<IDataProtectionProvider>().Object,
                mockChatAccess.Object,
                logger: new Mock<ILogger<NotificationRoutingService>>().Object);

            _mockFileService = new Mock<IFileService>();

            _mockModeration = new Mock<IContentModerationService>();
            _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>()))
                           .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));
            _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                           .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));

            _service = new ChatService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                _mockRealtimeGateway.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, _escrowService, _employees),
                new MarkdownSanitizerService(),
                new Mock<ILogger<ChatService>>().Object);
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

            Assert.True(result.IsSuccess);
            var message = result.Value!;
            Assert.NotNull(message);
            Assert.Equal("Hello World", message.Content);
            Assert.Equal("user-1", message.SenderId);

            // Verify persistence
            Assert.NotNull(_messages.GetById(message.Id));

            // Verify sender is marked in ReadBy
            Assert.Contains("user-1", message.ReadBy);

            // Verify channel read state updated for sender
            var readState = _readStates.GetReadState("user-1", "chan-1");
            Assert.NotNull(readState);

            // Verify realtime broadcast
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "ReceiveMessage", It.IsAny<object>(), default), Times.Once);
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

            Assert.True(result.IsSuccess);
            var updatedAlbum = _albums.GetById("album-1");
            Assert.Contains("chan-album", updatedAlbum.SharedWithChannelIds);
            Assert.Contains("album-1", result.Value!.MentionedAlbumIds);
        }

        [Fact]
        public async Task SendMessageAsync_MentionsInvalidAlbum_IgnoresAndSucceeds()
        {
            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-invalid", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync("user-owner", "chan-invalid", "Check this #[Missing](album:album-invalid)");

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.Contains("album-invalid", result.Value.MentionedAlbumIds);
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

            Assert.True(result.IsSuccess);
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

            Assert.True(result.IsSuccess);
            var updatedAlbum = _albums.GetById("album-std");
            Assert.Contains("chan-album-std", updatedAlbum.SharedWithChannelIds);
            
            // Should parse the mention
            Assert.Contains("album-std", result.Value!.MentionedAlbumIds ?? new List<string>());
        }

        [Fact]
        public async Task StartStopTyping_BroadlyNotifies()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);

            await _service.StartTypingAsync("user-1", "chan-1");
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "UserTyping", It.IsAny<object>(), default), Times.Once);

            await _service.StopTypingAsync("user-1", "chan-1");
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "UserStoppedTyping", It.IsAny<object>(), default), Times.Once);
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

            var result = await _service.EditMessageAsync("user-1", "msg-1", "New Content");
            Assert.True(result.IsSuccess);

            var updated = _messages.GetById("msg-1");
            Assert.Equal("New Content", updated.Content);
            Assert.NotNull(updated.EditedAt);

            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "MessageEdited", It.IsAny<object>(), default), Times.Once);
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

            _mockModeration.Setup(m => m.EvaluateTextAsync("Bad Content", It.IsAny<CancellationToken>()))
                           .ReturnsAsync((true, "Bad Content", TextModerationAction.Block));

            var result = await _service.EditMessageAsync("user-1", "msg-block-test", "Bad Content");
            Assert.False(result.IsSuccess);
            Assert.Equal("MODERATION_BLOCKED", result.Code);

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

            _mockModeration.Setup(m => m.EvaluateTextAsync("Bad Content", It.IsAny<CancellationToken>()))
                           .ReturnsAsync((true, "Xkpw Content", TextModerationAction.Sanitize));

            var result = await _service.EditMessageAsync("user-1", "msg-sanitize-test", "Bad Content");
            Assert.True(result.IsSuccess);

            var updated = _messages.GetById("msg-sanitize-test");
            Assert.NotNull(updated);
            Assert.Equal("Xkpw Content", updated.Content);
            Assert.NotNull(updated.EditedAt);

            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "MessageEdited", It.IsAny<object>(), default), Times.Once);
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

            var result = await _service.EditMessageAsync("user-revoked", "msg-rev", "Hacked Update");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);

            var unchanged = _messages.GetById("msg-rev");
            Assert.NotNull(unchanged);
            Assert.Equal("Original", unchanged.Content);
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-revoked", "MessageEdited", It.IsAny<object>(), default), Times.Never);
        }

        [Fact]
        public async Task EditMessageAsync_EmptyContent_ReturnsInvalidContent()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-empty", SenderId = "user-1", Content = "Original", ChannelId = "chan-1" };
            _messages.Save(msg);

            var result = await _service.EditMessageAsync("user-1", "msg-empty", "   ");
            Assert.False(result.IsSuccess);
            Assert.Equal("INVALID_CONTENT", result.Code);
        }

        [Fact]
        public async Task EditMessageAsync_WhenModerationThrows_ReturnsModerationError()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-mod-fail", SenderId = "user-1", Content = "Original", ChannelId = "chan-1" };
            _messages.Save(msg);

            _mockModeration.Setup(m => m.EvaluateTextAsync("Trigger Failure", It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new HttpRequestException("Moderation endpoint unreachable"));

            var result = await _service.EditMessageAsync("user-1", "msg-mod-fail", "Trigger Failure");
            Assert.False(result.IsSuccess);
            Assert.Equal("MODERATION_ERROR", result.Code);
        }

        [Fact]
        public async Task EditMessageAsync_WhenAlbumEncryptionFails_ReturnsEncryptionError()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-enc", ChannelType = ChatChannelType.General, IsDefaultGeneral = true, IsEncrypted = true };
            _channels.Save(channel);

            var album = new Spokes_Server.Core.Models.Communication.Album { Id = "album-enc", OwnerId = "user-1", IsEncrypted = true };
            _albums.Save(album);

            var msg = new ChatMessage { Id = "msg-enc", SenderId = "user-1", Content = "Original", ChannelId = "chan-enc" };
            _messages.Save(msg);

            // Editing to mention album in encrypted channel without providing private key
            var result = await _service.EditMessageAsync("user-1", "msg-enc", "Updated with #[Photos](album:album-enc)", requestingUserPrivateKey: null);
            Assert.False(result.IsSuccess);
            Assert.Equal("ENCRYPTION_ERROR", result.Code);
        }

        [Fact]
        public async Task EditMessageAsync_RemovingAlbumMention_UnsharesWhenNoOtherMentionsExist()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-album-unshare", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var album = new Spokes_Server.Core.Models.Communication.Album
            {
                Id = "album-unshare",
                OwnerId = "user-1",
                SharedWithChannelIds = new List<string> { channel.Id }
            };
            _albums.Save(album);

            var msg = new ChatMessage
            {
                Id = "msg-with-album",
                SenderId = "user-1",
                Content = "Here is #[Photos](album:album-unshare)",
                ChannelId = channel.Id,
                MentionedAlbumIds = new List<string> { "album-unshare" }
            };
            _messages.Save(msg);

            var result = await _service.EditMessageAsync("user-1", "msg-with-album", "Photos removed");
            Assert.True(result.IsSuccess);

            var updatedAlbum = _albums.GetById("album-unshare");
            Assert.NotNull(updatedAlbum);
            Assert.DoesNotContain(channel.Id, updatedAlbum.SharedWithChannelIds);
        }

        [Fact]
        public async Task EditMessageAsync_RemovingAlbumMention_KeepsSharedWhenOtherMentionsExist()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-album-keep", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var album = new Spokes_Server.Core.Models.Communication.Album
            {
                Id = "album-keep",
                OwnerId = "user-1",
                SharedWithChannelIds = new List<string> { channel.Id }
            };
            _albums.Save(album);

            var msg1 = new ChatMessage
            {
                Id = "msg-with-album-1",
                SenderId = "user-1",
                Content = "First mention #[Photos](album:album-keep)",
                ChannelId = channel.Id,
                MentionedAlbumIds = new List<string> { "album-keep" }
            };
            _messages.Save(msg1);

            var msg2 = new ChatMessage
            {
                Id = "msg-with-album-2",
                SenderId = "user-1",
                Content = "Second mention #[Photos](album:album-keep)",
                ChannelId = channel.Id,
                MentionedAlbumIds = new List<string> { "album-keep" }
            };
            _messages.Save(msg2);

            var result = await _service.EditMessageAsync("user-1", "msg-with-album-1", "Photos removed from msg 1");
            Assert.True(result.IsSuccess);

            var updatedAlbum = _albums.GetById("album-keep");
            Assert.NotNull(updatedAlbum);
            Assert.Contains(channel.Id, updatedAlbum.SharedWithChannelIds);
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

            var result = await _service.DeleteMessageAsync("user-1", "msg-1");
            Assert.True(result.IsSuccess);

            var deleted = _messages.GetById("msg-1");
            Assert.True(deleted.IsDeleted);

            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "MessageDeleted", It.IsAny<object>(), default), Times.Once);
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

            var result = await _service.DeleteMessageAsync("user-del-rev", "msg-del-rev");
            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);

            var unchanged = _messages.GetById("msg-del-rev");
            Assert.NotNull(unchanged);
            Assert.False(unchanged.IsDeleted);
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-del-rev", "MessageDeleted", It.IsAny<object>(), default), Times.Never);
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
            var addResult = await _service.ToggleReactionAsync("user-1", "msg-1", "👍");
            Assert.True(addResult.IsSuccess);
            var reactAdded = _messages.GetById("msg-1");
            Assert.Contains("👍:user-1", reactAdded.Reactions);
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "ReactionAdded", It.IsAny<object>(), default), Times.Once);

            // 2. Remove
            var remResult = await _service.ToggleReactionAsync("user-1", "msg-1", "👍");
            Assert.True(remResult.IsSuccess);
            var reactRemoved = _messages.GetById("msg-1");
            Assert.DoesNotContain("👍:user-1", reactRemoved.Reactions);
            _mockRealtimeGateway.Verify(p => p.SendToChannelAsync("chan-1", "ReactionRemoved", It.IsAny<object>(), default), Times.Once);
        }

        [Fact]
        public void CreateDirectMessageChannel_Works()
        {
            var chan = _service.CreateDirectMessageChannel("user-1", "user-2");
            Assert.NotNull(chan);
            Assert.Equal(ChatChannelType.Direct, chan.ChannelType);
            Assert.Contains("user-1", chan.ParticipantIds);
            Assert.Contains("user-2", chan.ParticipantIds);
        }

        [Fact]
        public void CreateGroupDirectMessageChannel_Works()
        {
            var chan = _service.CreateGroupDirectMessageChannel("user-1", new List<string> { "user-2", "user-3" });
            Assert.NotNull(chan);
            Assert.Contains("user-1", chan.ParticipantIds);
            Assert.Contains("user-2", chan.ParticipantIds);
            Assert.Contains("user-3", chan.ParticipantIds);
        }

        [Fact]
        public void CreateTeamChannel_Works()
        {
            var team = new Team { Id = "team-1", Name = "Engineering" };
            _teams.Save(team);

            var chan = _service.CreateTeamChannel("user-1", new List<string> { "team-1" });
            Assert.NotNull(chan);
            Assert.Equal(ChatChannelType.Team, chan.ChannelType);
            Assert.Equal("Engineering", chan.Name);
            Assert.Contains("team-1", chan.AllowedTeamIds);
        }

        [Fact]
        public void UpdateChannel_NormalizesDirectChannel_RemovingAnnouncementSettings()
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

            _service.UpdateChannel(channel);

            var saved = _channels.GetById("dm_test_update");
            Assert.NotNull(saved);
            Assert.False(saved.IsAnnouncementOnly);
            Assert.Empty(saved.AllowedPostUserIds);
            Assert.Empty(saved.AllowedPostTeamIds);
        }

        [Fact]
        public void UpdateChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var channel = new ChatChannel
            {
                Id = "enc_update_locked",
                Name = "Encrypted Channel",
                IsEncrypted = true
            };
            _channels.Save(channel);

            var updatedChannel = new ChatChannel
            {
                Id = "enc_update_locked",
                Name = "Updated Encrypted Channel",
                IsEncrypted = true
            };
            var result = _service.UpdateChannel(updatedChannel, isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);

            var persisted = _channels.GetById("enc_update_locked");
            Assert.Equal("Encrypted Channel", persisted?.Name);
        }

        [Fact]
        public void UpdateChannel_EncryptedChannel_WhenUnlocked_Succeeds()
        {
            var channel = new ChatChannel
            {
                Id = "enc_update_unlocked",
                Name = "Encrypted Channel",
                IsEncrypted = true
            };
            _channels.Save(channel);

            channel.Name = "Updated Encrypted Channel";
            var result = _service.UpdateChannel(channel, isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            var persisted = _channels.GetById("enc_update_unlocked");
            Assert.Equal("Updated Encrypted Channel", persisted?.Name);
        }

        [Fact]
        public void DeleteChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var channel = new ChatChannel
            {
                Id = "enc_delete_locked",
                Name = "Encrypted Delete",
                IsEncrypted = true
            };
            _channels.Save(channel);

            var result = _service.DeleteChannel(channel.Id, isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);
            Assert.NotNull(_channels.GetById(channel.Id));
        }

        [Fact]
        public void DeleteChannel_EncryptedChannel_WhenUnlocked_SucceedsAndCleansUp()
        {
            var channel = new ChatChannel
            {
                Id = "enc_delete_unlocked",
                Name = "Encrypted Delete",
                IsEncrypted = true
            };
            _channels.Save(channel);

            var msg = new ChatMessage
            {
                Id = "msg_delete_1",
                ChannelId = channel.Id,
                Content = "hello",
                SentAt = DateTime.UtcNow
            };
            _messages.Save(msg);

            var readState = new ChatReadState
            {
                Id = "rs_delete_1",
                ChannelId = channel.Id,
                UserId = "user-1"
            };
            _readStates.Save(readState);

            var result = _service.DeleteChannel(channel.Id, isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            Assert.Null(_channels.GetById(channel.Id));
            Assert.Null(_messages.GetById(msg.Id));
            Assert.Null(_readStates.GetById(readState.Id));
        }

        [Fact]
        public void ArchiveChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var channel = new ChatChannel
            {
                Id = "enc_archive_locked",
                Name = "Encrypted Archive",
                IsEncrypted = true,
                IsArchived = false
            };
            _channels.Save(channel);

            var result = _service.ArchiveChannel(channel.Id, true, isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);
            Assert.False(_channels.GetById(channel.Id)?.IsArchived);
        }

        [Fact]
        public void ArchiveChannel_EncryptedChannel_WhenUnlocked_Succeeds()
        {
            var channel = new ChatChannel
            {
                Id = "enc_archive_unlocked",
                Name = "Encrypted Archive",
                IsEncrypted = true,
                IsArchived = false
            };
            _channels.Save(channel);

            var result = _service.ArchiveChannel(channel.Id, true, isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            Assert.True(_channels.GetById(channel.Id)?.IsArchived);
        }

        [Fact]
        public void CreateChannel_NormalizesDirectChannel_RemovingAnnouncementSettings()
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

            _service.CreateChannel(channel);

            var saved = _channels.GetById("dm_test_create");
            Assert.NotNull(saved);
            Assert.False(saved.IsAnnouncementOnly);
            Assert.Empty(saved.AllowedPostUserIds);
            Assert.Empty(saved.AllowedPostTeamIds);
        }

        [Fact]
        public void UpdateChannelOrders_UpdatesDisplayOrder()
        {
            var channel1 = new ChatChannel { Id = "chan-order-1", Name = "One", DisplayOrder = 0 };
            var channel2 = new ChatChannel { Id = "chan-order-2", Name = "Two", DisplayOrder = 1 };
            _channels.Save(channel1);
            _channels.Save(channel2);

            _service.UpdateChannelOrders(new Dictionary<string, int>
            {
                { "chan-order-1", 5 },
                { "chan-order-2", 10 }
            });

            Assert.Equal(5, _channels.GetById("chan-order-1")?.DisplayOrder);
            Assert.Equal(10, _channels.GetById("chan-order-2")?.DisplayOrder);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageNotFound_ReturnsNotFoundFailure()
        {
            var result = await _service.AdminDeleteMessageAsync("non-existent-msg");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageWithAttachments_DeletesAttachmentsAndMarksMessageDeleted()
        {
            var msg = new ChatMessage
            {
                Id = "admin-del-msg-att",
                ChannelId = "chan-1",
                SenderId = "user-1",
                Content = "secret message",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment { Id = "att-1", FilePath = "uploads/att1.pdf" }
                }
            };
            _messages.Save(msg);

            var result = await _service.AdminDeleteMessageAsync("admin-del-msg-att");

            Assert.True(result.IsSuccess);
            var saved = _messages.GetById("admin-del-msg-att");
            Assert.NotNull(saved);
            Assert.True(saved.IsDeleted);
            Assert.Equal("content deleted", saved.Content);
            Assert.Empty(saved.Attachments);
            _mockFileService.Verify(f => f.DeleteAsync("uploads/att1.pdf"), Times.Once);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageWithoutAttachments_MarksMessageDeletedAndReturnsSuccess()
        {
            var msg = new ChatMessage
            {
                Id = "admin-del-msg-plain",
                ChannelId = "chan-1",
                SenderId = "user-1",
                Content = "plain text message"
            };
            _messages.Save(msg);

            var result = await _service.AdminDeleteMessageAsync("admin-del-msg-plain");

            Assert.True(result.IsSuccess);
            var saved = _messages.GetById("admin-del-msg-plain");
            Assert.NotNull(saved);
            Assert.True(saved.IsDeleted);
            Assert.Equal("content deleted", saved.Content);
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
            Assert.True(ChatChannelFormatter.IsSelfDirectMessage(channel, "user-1"));
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
            Assert.False(ChatChannelFormatter.IsSelfDirectMessage(channel, "user-1"));
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
            var r1 = await _service.SendMessageAsync("user-1", "dm_auto_read", "Hello Bob");
            Assert.True(r1.IsSuccess);
            var m1 = r1.Value!;
            Assert.NotNull(m1);
            Assert.DoesNotContain("user-2", m1.ReadBy);

            // user-2 sends a reply
            var r2 = await _service.SendMessageAsync("user-2", "dm_auto_read", "Hey Alice");
            Assert.True(r2.IsSuccess);
            var m2 = r2.Value!;
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

            var r1 = await _service.SendMessageAsync("user-1", "dm_batch_read", "Message 1");
            var r2 = await _service.SendMessageAsync("user-1", "dm_batch_read", "Message 2");
            Assert.True(r1.IsSuccess);
            Assert.True(r2.IsSuccess);
            var m1 = r1.Value!;
            var m2 = r2.Value!;

            Assert.DoesNotContain("user-2", m1.ReadBy);
            Assert.DoesNotContain("user-2", m2.ReadBy);

            // User 2 opens the channel
            await _service.MarkChannelMessagesAsReadAsync("user-2", "dm_batch_read");

            var updatedM1 = _messages.GetById(m1.Id);
            var updatedM2 = _messages.GetById(m2.Id);

            Assert.Contains("user-2", updatedM1!.ReadBy);
            Assert.Contains("user-2", updatedM2!.ReadBy);
        }

        [Fact]
        public async Task MarkMessageAsReadAsync_PlacesUserIdFirstAndMarksMessageRead()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones" };
            _employees.Save(user1);
            _employees.Save(user2);

            var channel = new ChatChannel
            {
                Id = "dm_single_read",
                Name = "Direct Message",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "user-1", "user-2" }
            };
            _channels.Save(channel);

            var res = await _service.SendMessageAsync("user-1", "dm_single_read", "Hello");
            Assert.True(res.IsSuccess);
            var msg = res.Value!;
            Assert.DoesNotContain("user-2", msg.ReadBy);

            await _service.MarkMessageAsReadAsync("user-2", "dm_single_read", msg.Id);

            var updated = _messages.GetById(msg.Id);
            Assert.Contains("user-2", updated!.ReadBy);
        }


        [Fact]
        public void ExtractMessageTokens_PureParsing_ExtractsMentionsEventsAlbumsAndEveryone()
        {
            var content = "Hello @[Alice Smith] and @[Dev Team], please check #[Sprint Planning](event:evt-42), #[Photos](album:alb-99) and note @everyone!";
            var tokens = ChatService.ExtractMessageTokens(content);

            Assert.Contains("Alice Smith", tokens.MentionedDisplayNames);
            Assert.Contains("Dev Team", tokens.MentionedDisplayNames);
            Assert.True(tokens.MentionsEveryone);
            Assert.Contains("evt-42", tokens.MentionedEventIds);
            Assert.Contains("alb-99", tokens.MentionedAlbumIds);
        }

        [Fact]
        public async Task SendMessageAsync_WithUserAndTeamMentions_ResolvesUserIdsAndTeamFlag()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith" };
            var userLeader = new Employee { Id = "user-leader", FirstName = "Carol", LastName = "Leader" };
            var team1 = new Team { Id = "team-dev", Name = "Dev Team", LeaderId = "user-leader" };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones", TeamId = "team-dev" };
            _employees.Save(user1);
            _employees.Save(userLeader);
            _employees.Save(user2);
            _teams.Save(team1);

            var channel = new ChatChannel
            {
                Id = "chan-mentions",
                Name = "Mentions Chan",
                ChannelType = ChatChannelType.General,
                ParticipantIds = new List<string> { "user-1", "user-2", "user-leader" }
            };
            _channels.Save(channel);

            var res = await _service.SendMessageAsync("user-1", "chan-mentions", "Hey @[Alice Smith] and @[Dev Team]!");
            Assert.True(res.IsSuccess);
            var msg = res.Value!;
            Assert.NotNull(msg);
            Assert.Contains("user-1", msg.MentionedUserIds);
            Assert.Contains("user-2", msg.MentionedUserIds);
            Assert.Contains("user-leader", msg.MentionedUserIds);
            Assert.True(msg.MentionsTeam);
        }

        [Fact]
        public async Task SafeDispatchNotificationAsync_WhenRoutingThrows_LogsErrorAndDoesNotFault()
        {
            var mockLogger = new Mock<ILogger<ChatService>>();
            var service = new ChatService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                _mockRealtimeGateway.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, _escrowService, _employees),
                new MarkdownSanitizerService(),
                mockLogger.Object);

            var expectedEx = new InvalidOperationException("Notification gateway network down");
            var task = service.SafeDispatchNotificationAsync(() => throw expectedEx, "test notification");
            await task;

            mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("test notification")),
                    expectedEx,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task SafeDispatchNotificationAsync_WhenExecuted_CompletesTask()
        {
            var task = _service.SafeDispatchNotificationAsync(() => Task.CompletedTask, "test");
            await task;
            Assert.True(task.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task SendMessageAsync_AutoMarksPreviousMessagesAsRead_UsingBatchSave()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One" };
            var other = new Employee { Id = "user-2", FirstName = "User", LastName = "Two" };
            _employees.Save(sender);
            _employees.Save(other);
            var channel = new ChatChannel { Id = "chan-batch", Name = "General" };
            _channels.Save(channel);

            var prevMessages = Enumerable.Range(1, 5).Select(i => new ChatMessage
            {
                Id = $"prev-msg-{i}",
                ChannelId = "chan-batch",
                SenderId = "user-2",
                Content = $"Message {i}",
                SentAt = DateTime.UtcNow.AddMinutes(-10 + i),
                ReadBy = new List<string> { "user-2" }
            }).ToList();
            _messages.SaveBatch(prevMessages);

            var newMsgRes = await _service.SendMessageAsync("user-1", "chan-batch", "Hello from user 1");
            Assert.True(newMsgRes.IsSuccess);
            var newMsg = newMsgRes.Value!;
            Assert.NotNull(newMsg);

            foreach (var prev in prevMessages)
            {
                var updated = _messages.GetById(prev.Id);
                Assert.NotNull(updated);
                Assert.Contains("user-1", updated.ReadBy);
            }
        }

        [Fact]
        public async Task SendMessageAsync_WhenUserNotFound_ReturnsFailure()
        {
            var channel = new ChatChannel { Id = "chan_not_found_test", Name = "General" };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync(new SendMessageOptions
            {
                SenderId = "nonexistent_user",
                ChannelId = channel.Id,
                Content = "Hello"
            });

            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
            Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SendMessageAsync_WhenChannelNotFound_ReturnsFailure()
        {
            var sender = new Employee { Id = "emp_chan_nf", FirstName = "User", LastName = "One" };
            _employees.Save(sender);

            var result = await _service.SendMessageAsync(new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = "nonexistent_channel",
                Content = "Hello"
            });

            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
            Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SendMessageAsync_WhenModerationBlocked_ReturnsFailure()
        {
            var sender = new Employee { Id = "emp_mod_block", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan_mod_block", Name = "General" };
            _channels.Save(channel);

            _mockModeration.Setup(m => m.EvaluateTextAsync(It.Is<string>(s => s.Contains("bad content")), It.IsAny<CancellationToken>()))
                           .ReturnsAsync((true, string.Empty, TextModerationAction.Block));

            var result = await _service.SendMessageAsync(new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = channel.Id,
                Content = "bad content"
            });

            Assert.False(result.IsSuccess);
            Assert.Equal("MODERATION_BLOCKED", result.Code);
            Assert.Contains("moderation", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SendMessageAsync_WhenValid_ReturnsSuccess()
        {
            var sender = new Employee { Id = "emp_valid_send", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan_valid_send", Name = "General" };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync(new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = channel.Id,
                Content = "Valid message"
            });

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.Equal("Valid message", result.Value.Content);
        }

        [Fact]
        public async Task SendMessageAsync_WhenUnauthorized_ReturnsForbiddenFailure()
        {
            var sender = new Employee { Id = "emp_forbidden", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel
            {
                Id = "chan_forbidden",
                Name = "Secret",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { "other_user" }
            };
            _channels.Save(channel);

            var result = await _service.SendMessageAsync(sender.Id, channel.Id, "Intruder message");

            Assert.False(result.IsSuccess);
            Assert.Equal("FORBIDDEN", result.Code);
            Assert.Contains("not authorized", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AddReactionAsync_WhenAlreadyPresent_ReturnsSuccessWithoutToggling()
        {
            var sender = new Employee { Id = "emp_react_idempotent", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan_react_idempotent", Name = "General" };
            _channels.Save(channel);
            var msg = new ChatMessage { Id = "msg_react_idem", ChannelId = channel.Id, SenderId = sender.Id, Content = "Hello", Reactions = new List<string> { "👍:emp_react_idempotent" } };
            _messages.Save(msg);

            var result = await _service.AddReactionAsync(sender.Id, msg.Id, "👍");

            Assert.True(result.IsSuccess);
            var reloaded = _messages.GetById(msg.Id);
            Assert.Contains("👍:emp_react_idempotent", reloaded!.Reactions);
        }

        [Fact]
        public async Task RemoveReactionAsync_WhenNotPresent_ReturnsSuccessWithoutToggling()
        {
            var sender = new Employee { Id = "emp_rm_react_idempotent", FirstName = "User", LastName = "One" };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan_rm_react_idempotent", Name = "General" };
            _channels.Save(channel);
            var msg = new ChatMessage { Id = "msg_rm_react_idem", ChannelId = channel.Id, SenderId = sender.Id, Content = "Hello", Reactions = new List<string>() };
            _messages.Save(msg);

            var result = await _service.RemoveReactionAsync(sender.Id, msg.Id, "👍");

            Assert.True(result.IsSuccess);
            var reloaded = _messages.GetById(msg.Id);
            Assert.DoesNotContain("👍:emp_rm_react_idempotent", reloaded!.Reactions);
        }

        [Fact]
        public async Task SendMessageAsync_EncryptedChannel_WithValidPrivateKey_EncryptsContentAndSetsFlag()
        {
            var crypto = new CryptoService();
            var keyPair = crypto.GenerateRsaKeyPair();

            var sender = new Employee
            {
                Id = "emp-enc-1",
                FirstName = "Encrypted",
                LastName = "Sender",
                PublicKey = keyPair.PublicKey,
                EncryptedPrivateKey = "vault-key"
            };
            _employees.Save(sender);

            var plainChannelKey = crypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-enc-1",
                Name = "Encrypted Channel",
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [sender.Id] = crypto.EncryptRsa(plainChannelKey, keyPair.PublicKey)
                }
            };
            _channels.Save(channel);

            var options = new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = channel.Id,
                Content = "This is a super secret payload that must be encrypted at rest.",
                PrivateKey = keyPair.PrivateKey
            };

            var result = await _service.SendMessageAsync(options);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            var returnedMsg = result.Value!;
            Assert.True(returnedMsg.IsEncrypted);

            // Verify persistence to disk
            var savedMessage = _messages.GetById(returnedMsg.Id);
            Assert.NotNull(savedMessage);
            Assert.True(savedMessage.IsEncrypted);
            Assert.NotEqual(options.Content, savedMessage.Content);

            // Verify decryption produces the exact original content
            var decryptedContent = crypto.DecryptAes(savedMessage.Content, plainChannelKey);
            Assert.Equal(options.Content, decryptedContent);
        }

        [Fact]
        public async Task SendMessageAsync_EncryptedChannel_MissingPrivateKey_ReturnsVaultLocked()
        {
            var crypto = new CryptoService();
            var keyPair = crypto.GenerateRsaKeyPair();

            var sender = new Employee
            {
                Id = "emp-enc-locked",
                FirstName = "Locked",
                LastName = "User",
                PublicKey = keyPair.PublicKey,
                EncryptedPrivateKey = "vault-key"
            };
            _employees.Save(sender);

            var plainChannelKey = crypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-enc-locked",
                Name = "Encrypted Channel Locked",
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [sender.Id] = crypto.EncryptRsa(plainChannelKey, keyPair.PublicKey)
                }
            };
            _channels.Save(channel);

            var options = new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = channel.Id,
                Content = "Should fail because vault is locked",
                PrivateKey = null // Vault locked
            };

            var result = await _service.SendMessageAsync(options);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);

            // Verify no message was saved to disk
            var messages = _messages.GetAll().Where(m => m.ChannelId == channel.Id);
            Assert.Empty(messages);
        }

        [Fact]
        public async Task SendMessageAsync_EncryptedChannel_WithBase64Attachment_EncryptsAttachmentStreamWithChannelKey()
        {
            var crypto = new CryptoService();
            var keyPair = crypto.GenerateRsaKeyPair();

            var sender = new Employee
            {
                Id = "emp-enc-att",
                FirstName = "Attachment",
                LastName = "User",
                PublicKey = keyPair.PublicKey,
                EncryptedPrivateKey = "vault-key"
            };
            _employees.Save(sender);

            var plainChannelKey = crypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-enc-att",
                Name = "Attachment Channel",
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [sender.Id] = crypto.EncryptRsa(plainChannelKey, keyPair.PublicKey)
                }
            };
            _channels.Save(channel);

            _mockFileService
                .Setup(f => f.UploadStreamAsync("chat", channel.Id, It.IsAny<Stream>(), "voice.webm", plainChannelKey, null))
                .ReturnsAsync("/spokesapi/files/chat/chan-enc-att/voice.webm");

            var options = new SendMessageOptions
            {
                SenderId = sender.Id,
                ChannelId = channel.Id,
                Content = "Voice memo attached",
                PrivateKey = keyPair.PrivateKey,
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment
                    {
                        Id = "att-1",
                        FileName = "voice.webm",
                        FilePath = "data:audio/webm;base64,GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJ8"
                    }
                }
            };

            var result = await _service.SendMessageAsync(options);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            _mockFileService.Verify(
                f => f.UploadStreamAsync("chat", channel.Id, It.IsAny<Stream>(), "voice.webm", plainChannelKey, null),
                Times.Once);
        }

        [Fact]
        public async Task EditMessageAsync_EncryptedChannel_ReEncryptsContent()
        {
            var crypto = new CryptoService();
            var keyPair = crypto.GenerateRsaKeyPair();

            var sender = new Employee
            {
                Id = "emp-enc-edit",
                FirstName = "Edit",
                LastName = "User",
                PublicKey = keyPair.PublicKey,
                EncryptedPrivateKey = "vault-key"
            };
            _employees.Save(sender);

            var plainChannelKey = crypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-enc-edit",
                Name = "Edit Channel",
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [sender.Id] = crypto.EncryptRsa(plainChannelKey, keyPair.PublicKey)
                }
            };
            _channels.Save(channel);

            var originalCipher = crypto.EncryptAes("Original Secret", plainChannelKey);
            var message = new ChatMessage
            {
                Id = "msg-enc-edit-1",
                ChannelId = channel.Id,
                SenderId = sender.Id,
                Content = originalCipher,
                IsEncrypted = true,
                SentAt = DateTime.UtcNow
            };
            _messages.Save(message);

            var newPlaintext = "Updated and edited confidential message";
            var editResult = await _service.EditMessageAsync(sender.Id, message.Id, newPlaintext, keyPair.PrivateKey);

            Assert.True(editResult.IsSuccess, editResult.ErrorMessage);

            var reloaded = _messages.GetById(message.Id);
            Assert.NotNull(reloaded);
            Assert.True(reloaded.IsEncrypted);
            Assert.NotEqual(newPlaintext, reloaded.Content);

            var decrypted = crypto.DecryptAes(reloaded.Content, plainChannelKey);
            Assert.Equal(newPlaintext, decrypted);
        }
}
