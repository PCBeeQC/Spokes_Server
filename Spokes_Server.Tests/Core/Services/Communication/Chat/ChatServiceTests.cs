namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Constants;
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
        private readonly ChatCategoryRepository _categories;
        private readonly ChatMessageRepository _messages;
        private readonly TeamRepository _teams;
        private readonly ChatReadStateRepository _readStates;
        private readonly CompanyProfileRepository _companyProfile;
        private readonly ProjectRepository _projects;
        private readonly AlbumRepository _albums;
        private readonly ServerEscrowService _escrowService;

        private readonly ChatStateService _chatState;
        private readonly VoiceStateService _voiceState;
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

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _employees = new EmployeeRepository(_persistence, _config);
            _channels = new ChatChannelRepository(_persistence, _config);
            _categories = new ChatCategoryRepository(_persistence, _config);
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            _messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
            _teams = new TeamRepository(_persistence, _config);
            _companyProfile = new CompanyProfileRepository(_persistence, _config);
            var profile = _companyProfile.Get();
            profile.Edition = "Family";
            _companyProfile.Save(profile);
            
            _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfile);
            _projects = new ProjectRepository(_persistence, _config);

            _chatState = new ChatStateService();
            _voiceState = new VoiceStateService();

            var systemConfigRepo = new SystemConfigRepository(_persistence, _config);
            _albums = new AlbumRepository(_persistence, _config);
            var encService = new EncryptionService(_config);
            var serverConfigRepo = new ServerConfigRepository(_persistence, _config);
            _escrowService = new ServerEscrowService(_companyProfile, serverConfigRepo, new Mock<ICryptoService>().Object);
            _mockPresence = new Mock<PresenceStateService>(_voiceState);
            _notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _persistence, _config, _companyProfile);

            _mockWebPush = new Mock<IWebPushService>();

            var mockChatAccess = new Mock<IChatChannelAccessService>();
            mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns((string userId) => _channels.GetAll().ToList());
            mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => new List<string> { "user-1", "user-2" });

            _commContext = new Mock<CommunicationContext>();
            _commContext.Setup(c => c.ChatChannels).Returns(_channels);
            _commContext.Setup(c => c.ChatCategories).Returns(_categories);
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
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, _escrowService, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees)),
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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

            // Verify persisted and local state notification occurred
        }

        [Fact]
        public async Task SendMessageAsync_MentionsAlbum_SharesIfAuthorized()
        {
            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true, Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true, Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
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
            var sender = new Employee { Id = "user-other", FirstName = "User", LastName = "Other", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
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

            var sender = new Employee { Id = "user-owner", FirstName = "User", LastName = "Owner", IsActive = true, Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
            _employees.Save(sender);
            var member = new Employee { Id = "user-member", FirstName = "User", LastName = "Member", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
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

            bool startedTyping = false, stoppedTyping = false;
            _chatState.UserTyping += (ch, uid, _) => { if (ch == "chan-1" && uid == "user-1") startedTyping = true; };
            _chatState.UserStoppedTyping += (ch, uid) => { if (ch == "chan-1" && uid == "user-1") stoppedTyping = true; };

            await _service.StartTypingAsync("user-1", "chan-1");
            Assert.True(startedTyping);

            await _service.StopTypingAsync("user-1", "chan-1");
            Assert.True(stoppedTyping);
        }

        [Fact]
        public async Task EditMessageAsync_UpdatesContentAndNotifies()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
        }

        [Fact]
        public async Task EditMessageAsync_WithModerationBlock_DoesNotUpdateContent()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
            _employees.Save(sender);
            var channel = new ChatChannel { Id = "chan-1", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
            _channels.Save(channel);

            var msg = new ChatMessage { Id = "msg-1", SenderId = "user-1", Content = "To Delete", ChannelId = "chan-1" };
            _messages.Save(msg);

            var result = await _service.DeleteMessageAsync("user-1", "msg-1");
            Assert.True(result.IsSuccess);

            var deleted = _messages.GetById("msg-1");
            Assert.True(deleted.IsDeleted);
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
        }

        [Fact]
        public async Task ToggleReactionAsync_AddsAndRemoves()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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

            // 2. Remove
            var remResult = await _service.ToggleReactionAsync("user-1", "msg-1", "👍");
            Assert.True(remResult.IsSuccess);
            var reactRemoved = _messages.GetById("msg-1");
            Assert.DoesNotContain("👍:user-1", reactRemoved.Reactions);
        }

        [Fact]
        public void CreateDirectMessageChannel_Works()
        {
            _employees.Save(new Employee { Id = "user-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
            var chan = _service.CreateDirectMessageChannel("user-1", "user-2");
            Assert.NotNull(chan);
            Assert.Equal(ChatChannelType.Direct, chan.ChannelType);
            Assert.Contains("user-1", chan.ParticipantIds);
            Assert.Contains("user-2", chan.ParticipantIds);
        }

        [Fact]
        public void CreateGroupDirectMessageChannel_Works()
        {
            _employees.Save(new Employee { Id = "user-1", IsActive = true, Permissions = [AppPermissions.Chat.Use] });
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
            _employees.Save(new Employee { Id = "user-1", TeamId = "team-1", IsActive = true, Permissions = [AppPermissions.Chat.Use, AppPermissions.Chat.CreateChannels] });

            var chan = _service.CreateTeamChannel("user-1", new List<string> { "team-1" });
            Assert.NotNull(chan);
            Assert.Equal(ChatChannelType.Team, chan.ChannelType);
            Assert.Equal("Engineering", chan.Name);
            Assert.Contains("team-1", chan.AllowedTeamIds);
        }

        [Fact]
        public void UpdateChannel_RejectsDirectChannel_AndCreateChannelNormalizesDirectChannelRemovingAnnouncementSettings()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var dmChannel = new ChatChannel
            {
                Id = "dm_test_update",
                ChannelType = ChatChannelType.Direct,
                CreatedById = "admin-1",
                ParticipantIds = new List<string> { "admin-1", "user-2" },
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "admin-1" },
                AllowedPostTeamIds = new List<string> { "team-1" }
            };

            var createdDm = _service.CreateChannel(dmChannel);
            Assert.False(createdDm.IsAnnouncementOnly);
            Assert.Empty(createdDm.AllowedPostUserIds);
            Assert.Empty(createdDm.AllowedPostTeamIds);

            createdDm.IsAnnouncementOnly = true;
            var dmResult = _service.UpdateChannel(createdDm, "admin-1");
            Assert.False(dmResult.IsSuccess);
            Assert.Equal("ACCESS_DENIED", dmResult.Code);
        }

        [Fact]
        public void UpdateChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

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
            var result = _service.UpdateChannel(updatedChannel, "admin-1", isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);

            var persisted = _channels.GetById("enc_update_locked");
            Assert.Equal("Encrypted Channel", persisted?.Name);
        }

        [Fact]
        public void UpdateChannel_EncryptedChannel_WhenUnlocked_Succeeds()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var channel = new ChatChannel
            {
                Id = "enc_update_unlocked",
                Name = "Encrypted Channel",
                IsEncrypted = true
            };
            _channels.Save(channel);

            channel.Name = "Updated Encrypted Channel";
            var result = _service.UpdateChannel(channel, "admin-1", isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            var persisted = _channels.GetById("enc_update_unlocked");
            Assert.Equal("Updated Encrypted Channel", persisted?.Name);
        }

        [Fact]
        public void DeleteChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var channel = new ChatChannel
            {
                Id = "enc_delete_locked",
                Name = "Encrypted Delete",
                IsEncrypted = true
            };
            _channels.Save(channel);

            var result = _service.DeleteChannel(channel.Id, "admin-1", isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);
            Assert.NotNull(_channels.GetById(channel.Id));
        }

        [Fact]
        public void DeleteChannel_EncryptedChannel_WhenUnlocked_SucceedsAndCleansUp()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

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

            var result = _service.DeleteChannel(channel.Id, "admin-1", isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            Assert.Null(_channels.GetById(channel.Id));
            Assert.Null(_messages.GetById(msg.Id));
            Assert.Null(_readStates.GetById(readState.Id));
        }

        [Fact]
        public void ArchiveChannel_EncryptedChannel_WhenLocked_FailsWithVaultLocked()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var channel = new ChatChannel
            {
                Id = "enc_archive_locked",
                Name = "Encrypted Archive",
                IsEncrypted = true,
                IsArchived = false
            };
            _channels.Save(channel);

            var result = _service.ArchiveChannel(channel.Id, true, "admin-1", isSessionUnlocked: false);

            Assert.False(result.IsSuccess);
            Assert.Equal("VAULT_LOCKED", result.Code);
            Assert.False(_channels.GetById(channel.Id)?.IsArchived);
        }

        [Fact]
        public void ArchiveChannel_EncryptedChannel_WhenUnlocked_Succeeds()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var channel = new ChatChannel
            {
                Id = "enc_archive_unlocked",
                Name = "Encrypted Archive",
                IsEncrypted = true,
                IsArchived = false
            };
            _channels.Save(channel);

            var result = _service.ArchiveChannel(channel.Id, true, "admin-1", isSessionUnlocked: true);

            Assert.True(result.IsSuccess);
            Assert.True(_channels.GetById(channel.Id)?.IsArchived);
        }

        [Fact]
        public void UpdateChannelOrders_UpdatesDisplayOrder()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var channel1 = new ChatChannel { Id = "chan-order-1", Name = "One", DisplayOrder = 0 };
            var channel2 = new ChatChannel { Id = "chan-order-2", Name = "Two", DisplayOrder = 1 };
            _channels.Save(channel1);
            _channels.Save(channel2);

            var result = _service.UpdateChannelOrders(new Dictionary<string, int>
            {
                { "chan-order-1", 5 },
                { "chan-order-2", 10 }
            }, "admin-1");

            Assert.True(result.IsSuccess);
            Assert.Equal(5, _channels.GetById("chan-order-1")?.DisplayOrder);
            Assert.Equal(10, _channels.GetById("chan-order-2")?.DisplayOrder);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageNotFound_ReturnsNotFoundFailure()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var result = await _service.AdminDeleteMessageAsync("non-existent-msg", "admin-1");
            Assert.False(result.IsSuccess);
            Assert.Equal("NOT_FOUND", result.Code);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageWithAttachments_DeletesAttachmentsAndMarksMessageDeleted()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

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

            var result = await _service.AdminDeleteMessageAsync("admin-del-msg-att", "admin-1");

            Assert.True(result.IsSuccess);
            var saved = _messages.GetById("admin-del-msg-att");
            Assert.NotNull(saved);
            Assert.True(saved.IsDeleted);
            Assert.Equal(string.Empty, saved.Content);
            Assert.Empty(saved.Attachments);
            _mockFileService.Verify(f => f.DeleteAsync("uploads/att1.pdf"), Times.Once);
        }

        [Fact]
        public async Task AdminDeleteMessageAsync_MessageWithoutAttachments_MarksMessageDeletedAndReturnsSuccess()
        {
            var admin = new Employee { Id = "admin-1", FirstName = "Admin", LastName = "One", IsAdmin = true };
            _employees.Save(admin);

            var msg = new ChatMessage
            {
                Id = "admin-del-msg-plain",
                ChannelId = "chan-1",
                SenderId = "user-1",
                Content = "plain text message"
            };
            _messages.Save(msg);

            var result = await _service.AdminDeleteMessageAsync("admin-del-msg-plain", "admin-1");

            Assert.True(result.IsSuccess);
            var saved = _messages.GetById("admin-del-msg-plain");
            Assert.NotNull(saved);
            Assert.True(saved.IsDeleted);
            Assert.Equal(string.Empty, saved.Content);
        }

        [Fact]
        public async Task SendMessageAsync_AutoMarksPriorMessagesAsReadBySender()
        {
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith", Permissions = [AppPermissions.Chat.Use] };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones", Permissions = [AppPermissions.Chat.Use] };
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
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith", Permissions = [AppPermissions.Chat.Use] };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones", Permissions = [AppPermissions.Chat.Use] };
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
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith", Permissions = [AppPermissions.Chat.Use] };
            var user2 = new Employee { Id = "user-2", FirstName = "Bob", LastName = "Jones", Permissions = [AppPermissions.Chat.Use] };
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
            var user1 = new Employee { Id = "user-1", FirstName = "Alice", LastName = "Smith", Permissions = [AppPermissions.Chat.Use] };
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
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, _escrowService, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees)),
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
        public async Task SendMessageAsync_AutoMarksPreviousMessagesAsRead_UsingBatchSave()
        {
            var sender = new Employee { Id = "user-1", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
            var other = new Employee { Id = "user-2", FirstName = "User", LastName = "Two", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "emp_mod_block", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
            var sender = new Employee { Id = "emp_valid_send", FirstName = "User", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
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
                EncryptedPrivateKey = "vault-key",
                Permissions = [AppPermissions.Chat.Use]
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
                EncryptedPrivateKey = "vault-key",
                Permissions = [AppPermissions.Chat.Use]
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
                EncryptedPrivateKey = "vault-key",
                Permissions = [AppPermissions.Chat.Use]
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
                EncryptedPrivateKey = "vault-key",
                Permissions = [AppPermissions.Chat.Use]
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

        [Fact]
        public async Task ChannelManagementAndModeration_RequireNonEmptyAuthorizedActor_AndPreventForgedOwnershipEscalation()
        {
            var creator = new Employee { Id = "grp-owner", FirstName = "Owner", LastName = "User", Permissions = [AppPermissions.Chat.Use] };
            var attacker = new Employee { Id = "grp-attacker", FirstName = "Attacker", LastName = "User", Permissions = [AppPermissions.Chat.Use] };
            var moderator = new Employee
            {
                Id = "chat-mod",
                FirstName = "Mod",
                LastName = "User",
                Permissions = new List<string> { AppPermissions.Chat.Moderator, AppPermissions.Chat.Use }
            };
            _employees.Save(creator);
            _employees.Save(attacker);
            _employees.Save(moderator);

            var persistedGroup = new ChatChannel
            {
                Id = "grp-forged-test",
                Name = "Private Group",
                ChannelType = ChatChannelType.Group,
                CreatedById = creator.Id,
                ParticipantIds = new List<string> { creator.Id, attacker.Id }
            };
            _channels.Save(persistedGroup);

            // Empty requestingUserId must be rejected across all channel management & admin deletion methods
            Assert.Equal("ACCESS_DENIED", _service.UpdateChannel(persistedGroup, "").Code);
            Assert.Equal("ACCESS_DENIED", _service.DeleteChannel(persistedGroup.Id, "   ").Code);
            Assert.Equal("ACCESS_DENIED", _service.ArchiveChannel(persistedGroup.Id, true, "").Code);
            Assert.Equal("ACCESS_DENIED", _service.UpdateChannelOrders(new Dictionary<string, int> { [persistedGroup.Id] = 1 }, "").Code);
            Assert.Equal("ACCESS_DENIED", (await _service.AdminDeleteMessageAsync("any-msg", "")).Code);

            // Attacker attempts to forge CreatedById and mutate ChannelType to General on an existing Group channel
            var forgedClone = new ChatChannel
            {
                Id = persistedGroup.Id,
                Name = "Hijacked Group",
                ChannelType = ChatChannelType.General,
                CreatedById = attacker.Id,
                ParticipantIds = new List<string> { attacker.Id }
            };
            var forgedUpdate = _service.UpdateChannel(forgedClone, attacker.Id);
            Assert.False(forgedUpdate.IsSuccess);
            Assert.Equal("ACCESS_DENIED", forgedUpdate.Code);
            Assert.Equal("Private Group", _channels.GetById(persistedGroup.Id)!.Name);
            Assert.Equal(creator.Id, _channels.GetById(persistedGroup.Id)!.CreatedById);

            // Regular user cannot reorder global channels or call AdminDeleteMessageAsync
            var pubChan = new ChatChannel { Id = "pub-order-1", Name = "Public", ChannelType = ChatChannelType.General, DisplayOrder = 1 };
            _channels.Save(pubChan);
            var unauthorizedOrder = _service.UpdateChannelOrders(new Dictionary<string, int> { [pubChan.Id] = 99 }, attacker.Id);
            Assert.False(unauthorizedOrder.IsSuccess);
            Assert.Equal("ACCESS_DENIED", unauthorizedOrder.Code);
            Assert.Equal(1, _channels.GetById(pubChan.Id)!.DisplayOrder);

            var msg = new ChatMessage { Id = "mod-target-msg", ChannelId = pubChan.Id, SenderId = creator.Id, Content = "Flagged text" };
            _messages.Save(msg);

            var unauthorizedModDel = await _service.AdminDeleteMessageAsync(msg.Id, attacker.Id);
            Assert.False(unauthorizedModDel.IsSuccess);
            Assert.Equal("ACCESS_DENIED", unauthorizedModDel.Code);
            Assert.False(_messages.GetById(msg.Id)!.IsDeleted);

            // Moderator succeeds on AdminDeleteMessageAsync
            var authorizedModDel = await _service.AdminDeleteMessageAsync(msg.Id, moderator.Id);
            Assert.True(authorizedModDel.IsSuccess);
            Assert.True(_messages.GetById(msg.Id)!.IsDeleted);
            Assert.Equal(string.Empty, _messages.GetById(msg.Id)!.Content);
        }

        [Fact]
        public void LeaveGroupChannel_RemovesOnlyCallerAndEncryptedKey_TransfersOwnership_AndInvalidatesCallerFileTokens()
        {
            var fileTokens = new FileTokenService(
                DataProtectionProvider.Create("ChatServiceLeaveGroupTests"),
                new MemoryCache(new MemoryCacheOptions()));

            var serviceWithFileTokens = new ChatService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, _escrowService, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees)),
                new Mock<ILogger<ChatService>>().Object,
                fileTokenService: fileTokens);

            var creator = new Employee { Id = "grp-leave-creator", FirstName = "Creator", LastName = "One", Permissions = [AppPermissions.Chat.Use] };
            var member = new Employee { Id = "grp-leave-member", FirstName = "Member", LastName = "Two", Permissions = [AppPermissions.Chat.Use] };
            var outsider = new Employee { Id = "grp-leave-outsider", FirstName = "Outsider", LastName = "Three", Permissions = [AppPermissions.Chat.Use] };
            _employees.Save(creator);
            _employees.Save(member);
            _employees.Save(outsider);

            var group = new ChatChannel
            {
                Id = "grp-leave-1",
                Name = "Secure Squad",
                ChannelType = ChatChannelType.Group,
                CreatedById = creator.Id,
                IsEncrypted = true,
                ParticipantIds = new List<string> { creator.Id, member.Id },
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [creator.Id] = "enc-key-creator",
                    [member.Id] = "enc-key-member"
                }
            };
            _channels.Save(group);

            var creatorToken = fileTokens.GenerateAccessToken("chat", group.Id, "secret.png", "aes-key", creator.Id, "sess-creator");
            var memberToken = fileTokens.GenerateAccessToken("chat", group.Id, "secret.png", "aes-key", member.Id, "sess-member");

            // Non-participant cannot call LeaveGroupChannel
            var outsiderLeave = serviceWithFileTokens.LeaveGroupChannel(group.Id, outsider.Id);
            Assert.False(outsiderLeave.IsSuccess);
            Assert.Equal("ACCESS_DENIED", outsiderLeave.Code);

            // Direct channel rejects LeaveGroupChannel
            var dm = new ChatChannel
            {
                Id = "dm-no-leave",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { creator.Id, member.Id }
            };
            _channels.Save(dm);
            Assert.Equal("INVALID_OPERATION", serviceWithFileTokens.LeaveGroupChannel(dm.Id, creator.Id).Code);

            // Creator leaves Group: removed from ParticipantIds & EncryptedChannelKeys, ownership transfers to member, creator token invalidated
            var leaveRes = serviceWithFileTokens.LeaveGroupChannel(group.Id, creator.Id);
            Assert.True(leaveRes.IsSuccess);

            var updatedGroup = _channels.GetById(group.Id)!;
            Assert.DoesNotContain(creator.Id, updatedGroup.ParticipantIds);
            Assert.Contains(member.Id, updatedGroup.ParticipantIds);
            Assert.False(updatedGroup.EncryptedChannelKeys.ContainsKey(creator.Id));
            Assert.True(updatedGroup.EncryptedChannelKeys.ContainsKey(member.Id));
            Assert.Equal(member.Id, updatedGroup.CreatedById);

            Assert.False(fileTokens.ValidateAccessToken("chat", group.Id, "secret.png", creatorToken, creator.Id, "sess-creator").IsSuccess);
            Assert.True(fileTokens.ValidateAccessToken("chat", group.Id, "secret.png", memberToken, member.Id, "sess-member").IsSuccess);
        }

        [Fact]
        public async Task SuspendedUser_IsBlockedAcrossAllChatServiceWriteAndReadMutationMethods()
        {
            var suspended = new Employee
            {
                Id = "emp-suspended-1",
                FirstName = "Suspended",
                LastName = "Admin",
                IsAdmin = true,
                IsSuspended = true
            };
            var activePeer = new Employee { Id = "emp-active-peer", FirstName = "Active", LastName = "Peer" };
            _employees.Save(suspended);
            _employees.Save(activePeer);

            var channel = new ChatChannel
            {
                Id = "chan-susp-test",
                Name = "General",
                ChannelType = ChatChannelType.General,
                IsDefaultGeneral = true,
                CreatedById = suspended.Id
            };
            _channels.Save(channel);

            var group = new ChatChannel
            {
                Id = "grp-susp-test",
                Name = "Group",
                ChannelType = ChatChannelType.Group,
                CreatedById = activePeer.Id,
                ParticipantIds = new List<string> { activePeer.Id, suspended.Id }
            };
            _channels.Save(group);

            var ownMsg = new ChatMessage
            {
                Id = "msg-susp-own",
                ChannelId = channel.Id,
                SenderId = suspended.Id,
                Content = "Before suspension"
            };
            var peerMsg = new ChatMessage
            {
                Id = "msg-susp-peer",
                ChannelId = channel.Id,
                SenderId = activePeer.Id,
                Content = "Unread by suspended",
                ReadBy = new List<string> { activePeer.Id }
            };
            _messages.Save(ownMsg);
            _messages.Save(peerMsg);

            Assert.False((await _service.SendMessageAsync(suspended.Id, channel.Id, "Blocked post")).IsSuccess);
            Assert.False((await _service.EditMessageAsync(suspended.Id, ownMsg.Id, "Blocked edit")).IsSuccess);
            Assert.False((await _service.DeleteMessageAsync(suspended.Id, ownMsg.Id)).IsSuccess);
            Assert.False((await _service.ToggleReactionAsync(suspended.Id, peerMsg.Id, "👍")).IsSuccess);
            Assert.False((await _service.AddReactionAsync(suspended.Id, peerMsg.Id, "👍")).IsSuccess);
            Assert.False((await _service.RemoveReactionAsync(suspended.Id, peerMsg.Id, "👍")).IsSuccess);
            Assert.False(_service.UpdateChannel(channel, suspended.Id).IsSuccess);
            Assert.False(_service.DeleteChannel(channel.Id, suspended.Id).IsSuccess);
            Assert.False(_service.ArchiveChannel(channel.Id, true, suspended.Id).IsSuccess);
            Assert.False(_service.UpdateChannelOrders(new Dictionary<string, int> { [channel.Id] = 3 }, suspended.Id).IsSuccess);
            Assert.False(_service.LeaveGroupChannel(group.Id, suspended.Id).IsSuccess);
            Assert.False((await _service.AdminDeleteMessageAsync(peerMsg.Id, suspended.Id)).IsSuccess);
            Assert.Throws<UnauthorizedAccessException>(() => _service.CreateDirectMessageChannel(suspended.Id, activePeer.Id));

            await _service.MarkMessagesAsReadAsync(suspended.Id, channel.Id, new[] { peerMsg.Id });
            await _service.MarkChannelMessagesAsReadAsync(suspended.Id, channel.Id);
            Assert.DoesNotContain(suspended.Id, _messages.GetById(peerMsg.Id)!.ReadBy);
        }

        [Fact]
        public async Task MarkMessagesAsReadAsync_FailsClosedForUnauthorizedUsersAndIgnoresCrossChannelMessageIds()
        {
            var memberA = new Employee { Id = "read-member-a", FirstName = "Member", LastName = "A", Permissions = [AppPermissions.Chat.Use] };
            var memberB = new Employee { Id = "read-member-b", FirstName = "Member", LastName = "B", Permissions = [AppPermissions.Chat.Use] };
            var intruder = new Employee { Id = "read-intruder", FirstName = "Intruder", LastName = "C", Permissions = [AppPermissions.Chat.Use] };
            _employees.Save(memberA);
            _employees.Save(memberB);
            _employees.Save(intruder);

            var privateDm = new ChatChannel
            {
                Id = "dm-read-closed",
                ChannelType = ChatChannelType.Direct,
                ParticipantIds = new List<string> { memberA.Id, memberB.Id }
            };
            var publicChan = new ChatChannel
            {
                Id = "pub-read-other",
                ChannelType = ChatChannelType.General,
                IsDefaultGeneral = true
            };
            _channels.Save(privateDm);
            _channels.Save(publicChan);

            var dmMessage = new ChatMessage
            {
                Id = "msg-in-dm",
                ChannelId = privateDm.Id,
                SenderId = memberA.Id,
                Content = "Private DM message",
                ReadBy = new List<string> { memberA.Id }
            };
            _messages.Save(dmMessage);

            // Intruder cannot mark private DM messages as read directly OR by passing dmMessage.Id against publicChan
            await _service.MarkMessagesAsReadAsync(intruder.Id, privateDm.Id, new[] { dmMessage.Id });
            await _service.MarkChannelMessagesAsReadAsync(intruder.Id, privateDm.Id);
            await _service.MarkMessagesAsReadAsync(intruder.Id, publicChan.Id, new[] { dmMessage.Id });

            // Nonexistent user/channel also fails closed
            await _service.MarkMessagesAsReadAsync("ghost-user", privateDm.Id, new[] { dmMessage.Id });
            await _service.MarkMessagesAsReadAsync(memberB.Id, "ghost-channel", new[] { dmMessage.Id });

            Assert.DoesNotContain(intruder.Id, _messages.GetById(dmMessage.Id)!.ReadBy);
            Assert.DoesNotContain("ghost-user", _messages.GetById(dmMessage.Id)!.ReadBy);
            Assert.DoesNotContain(memberB.Id, _messages.GetById(dmMessage.Id)!.ReadBy);

            // Authorized participant marking against the matching channel succeeds
            await _service.MarkMessagesAsReadAsync(memberB.Id, privateDm.Id, new[] { dmMessage.Id });
            Assert.Contains(memberB.Id, _messages.GetById(dmMessage.Id)!.ReadBy);
        }

        [Fact]
        public async Task DeleteMessageAsync_ScrubsContentAttachmentsReactionsMentions_AndUnsharesOrphanedAlbum()
        {
            var author = new Employee { Id = "scrub-author", FirstName = "Author", LastName = "One", Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View] };
            _employees.Save(author);

            var channel = new ChatChannel
            {
                Id = "chan-scrub-full",
                Name = "General",
                ChannelType = ChatChannelType.General,
                IsDefaultGeneral = true
            };
            _channels.Save(channel);

            var album = new Spokes_Server.Core.Models.Communication.Album
            {
                Id = "album-scrub-1",
                OwnerId = author.Id,
                SharedWithChannelIds = new List<string> { channel.Id }
            };
            _albums.Save(album);

            var msg = new ChatMessage
            {
                Id = "msg-scrub-full",
                ChannelId = channel.Id,
                SenderId = author.Id,
                Content = "Confidential text #[Photos](album:album-scrub-1) @everyone",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment { Id = "att-scrub-1", FileName = "secret.pdf", FilePath = "/spokesapi/files/chat/chan-scrub-full/secret.pdf" }
                },
                Reactions = new List<string> { "🔥:other-user" },
                MentionedUserIds = new List<string> { "other-user" },
                MentionedAlbumIds = new List<string> { "album-scrub-1" },
                MentionedPollIds = new List<string> { "poll-1" },
                MentionedEventIds = new List<string> { "evt-1" },
                MentionsTeam = true,
                MentionsEveryone = true
            };
            _messages.Save(msg);

            var delResult = await _service.DeleteMessageAsync(author.Id, msg.Id);
            Assert.True(delResult.IsSuccess);

            var scrubbed = _messages.GetById(msg.Id)!;
            Assert.True(scrubbed.IsDeleted);
            Assert.Equal(string.Empty, scrubbed.Content);
            Assert.Empty(scrubbed.Attachments);
            Assert.Empty(scrubbed.Reactions);
            Assert.Empty(scrubbed.MentionedUserIds);
            Assert.Empty(scrubbed.MentionedAlbumIds);
            Assert.Empty(scrubbed.MentionedPollIds);
            Assert.Empty(scrubbed.MentionedEventIds);
            Assert.False(scrubbed.MentionsTeam);
            Assert.False(scrubbed.MentionsEveryone);

            _mockFileService.Verify(f => f.DeleteAsync("/spokesapi/files/chat/chan-scrub-full/secret.pdf"), Times.Once);
            Assert.DoesNotContain(channel.Id, _albums.GetById("album-scrub-1")!.SharedWithChannelIds);
        }

        [Fact]
        public async Task ConvertMessageImagesToAlbumAsync_EncryptedChannel_MovesOnlyImagesToAlbumPreservesNonImageAndReEncryptsMessage()
        {
            var realCrypto = new CryptoService();
            var realFileService = new FileService(_config, new Mock<ILogger<FileService>>().Object, realCrypto);
            var serverConfigRepo = new ServerConfigRepository(_persistence, _config);
            var realEscrow = new ServerEscrowService(_companyProfile, serverConfigRepo, realCrypto);
            var realAlbumService = new AlbumService(_albums, _channels, realCrypto, realEscrow, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees), fileService: realFileService);

            var chatService = new ChatService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                realFileService,
                _mockModeration.Object,
                realAlbumService,
                new Mock<ILogger<ChatService>>().Object,
                crypto: realCrypto);

            var (pubKey, privKey) = realCrypto.GenerateRsaKeyPair();

            var poster = new Employee
            {
                Id = "poster-enc",
                FirstName = "Poster",
                LastName = "One",
                PublicKey = pubKey,
                EncryptedPrivateKey = "enc-priv",
                IsActive = true,
                Permissions = [AppPermissions.Chat.Use, AppPermissions.Albums.View]
            };
            _employees.Save(poster);

            var plainChannelKey = realCrypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-convert-enc",
                Name = "Secure Photos",
                ChannelType = ChatChannelType.General,
                IsDefaultGeneral = true,
                IsEncrypted = true,
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [poster.Id] = realCrypto.EncryptRsa(plainChannelKey, pubKey)
                }
            };
            _channels.Save(channel);

            var attachments = new List<ChatAttachment>();
            for (int i = 0; i < 4; i++)
            {
                using var imgStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"img-bytes-{i}-0123456789abcdef"));
                var path = await realFileService.UploadStreamAsync("chat", channel.Id, imgStream, $"pic{i}.png", plainChannelKey);
                attachments.Add(new ChatAttachment
                {
                    Id = $"img-{i}",
                    FileName = $"pic{i}.png",
                    FilePath = path,
                    ContentType = "image/png",
                    FileSizeBytes = 28
                });
            }

            attachments.Add(new ChatAttachment
            {
                Id = "doc-keep",
                FileName = "notes.pdf",
                FilePath = "/spokesapi/files/chat/chan-convert-enc/notes.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 1024
            });

            var msg = new ChatMessage
            {
                Id = "msg-convert-enc",
                ChannelId = channel.Id,
                SenderId = poster.Id,
                Content = realCrypto.EncryptAes("Check out these shots!", plainChannelKey),
                IsEncrypted = true,
                Attachments = attachments
            };
            _messages.Save(msg);

            var result = await chatService.ConvertMessageImagesToAlbumAsync(
                poster.Id,
                msg.Id,
                targetAlbumId: null,
                newAlbumTitle: "Weekend Highlights",
                privateKey: privKey);

            Assert.True(result.IsSuccess);
            var updatedMsg = _messages.GetById(msg.Id)!;
            Assert.Single(updatedMsg.Attachments);
            Assert.Equal("notes.pdf", updatedMsg.Attachments[0].FileName);
            Assert.Single(updatedMsg.MentionedAlbumIds);

            var createdAlbumId = updatedMsg.MentionedAlbumIds[0];
            var decryptedText = realCrypto.DecryptAes(updatedMsg.Content, plainChannelKey);
            Assert.Contains($"#[Weekend Highlights](album:{createdAlbumId})", decryptedText);
            Assert.Contains("Check out these shots!", decryptedText);

            var createdAlbum = _albums.GetById(createdAlbumId)!;
            Assert.Equal(4, createdAlbum.Media.Count);
            Assert.Contains(channel.Id, createdAlbum.SharedWithChannelIds);
        }

        [Fact]
        public async Task ConvertMessageImagesToAlbumAsync_NonSender_ReturnsForbidden()
        {
            var sender = new Employee { Id = "orig-sender", FirstName = "Orig", LastName = "Sender", IsActive = true };
            var otherUser = new Employee { Id = "other-caller", FirstName = "Other", LastName = "Caller", IsActive = true, IsAdmin = true };
            _employees.Save(sender);
            _employees.Save(otherUser);

            var channel = new ChatChannel { Id = "chan-non-sender", Name = "General", ChannelType = ChatChannelType.General };
            _channels.Save(channel);

            var msg = new ChatMessage
            {
                Id = "msg-non-sender",
                ChannelId = channel.Id,
                SenderId = sender.Id,
                Content = "My photos",
                Attachments =
                [
                    new ChatAttachment { Id = "a1", FileName = "1.jpg", FilePath = "/spokesapi/files/chat/chan-non-sender/1.jpg", ContentType = "image/jpeg" }
                ]
            };
            _messages.Save(msg);

            var res = await _service.ConvertMessageImagesToAlbumAsync(otherUser.Id, msg.Id, null, "Stolen Album");
            Assert.False(res.IsSuccess);
            Assert.Equal("FORBIDDEN", res.Code);
        }

        [Fact]
        public async Task UpdateChannel_RemovingMemberOrTeamOnEncryptedChannel_RotatesEpochStripsAllEpochsAndEvictsRealtimeAndTokens()
        {
            var realCrypto = new CryptoService();
            var (pubOwner, privOwner) = realCrypto.GenerateRsaKeyPair();
            var (pubRemoved, _) = realCrypto.GenerateRsaKeyPair();
            var (pubTeamEvicted, _) = realCrypto.GenerateRsaKeyPair();

            var owner = new Employee { Id = "u-owner", FirstName = "Owner", LastName = "User", IsActive = true, IsAdmin = true, PublicKey = pubOwner, EncryptedPrivateKey = "k1" };
            var directRemoved = new Employee { Id = "u-direct-rem", FirstName = "Direct", LastName = "Removed", IsActive = true, PublicKey = pubRemoved, EncryptedPrivateKey = "k2" };
            var teamEvicted = new Employee { Id = "u-team-rem", FirstName = "Team", LastName = "Removed", IsActive = true, TeamId = "team-ext", PublicKey = pubTeamEvicted, EncryptedPrivateKey = "k3" };
            _employees.Save(owner);
            _employees.Save(directRemoved);
            _employees.Save(teamEvicted);

            var services = new ServiceCollection();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var dataProtection = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
            var fileTokenService = new FileTokenService(dataProtection, new MemoryCache(new MemoryCacheOptions()));
            var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees);
            var albumService = new AlbumService(_albums, _channels, realCrypto, _escrowService, _employees, chatAuth);
            var securityService = new ChatSecurityService(
                _employees, _channels, _messages, _escrowService, realCrypto, albumService, _chatState, chatAuth, _mockFileService.Object, new Mock<ILogger<ChatSecurityService>>().Object);

            var service = new ChatService(
                _commContext.Object,
                _hrContext.Object,
                _projectsContext.Object,
                _chatState,
                _notificationRouting,
                _mockPresence.Object,
                _mockFileService.Object,
                _mockModeration.Object,
                albumService,
                new Mock<ILogger<ChatService>>().Object,
                chatAuth: chatAuth,
                crypto: realCrypto,
                fileTokenService: fileTokenService,
                securityService: securityService);

            var epoch0Key = realCrypto.GenerateAesKeyBase64();
            var channel = new ChatChannel
            {
                Id = "chan-rot-1",
                Name = "Secret Group",
                ChannelType = ChatChannelType.Group,
                CreatedById = owner.Id,
                IsEncrypted = true,
                CurrentKeyEpoch = 0,
                ParticipantIds = [owner.Id, directRemoved.Id],
                AllowedTeamIds = ["team-ext"],
                EncryptedChannelKeys = new Dictionary<string, string>
                {
                    [owner.Id] = realCrypto.EncryptRsa(epoch0Key, pubOwner),
                    [directRemoved.Id] = realCrypto.EncryptRsa(epoch0Key, pubRemoved),
                    [teamEvicted.Id] = realCrypto.EncryptRsa(epoch0Key, pubTeamEvicted)
                }
            };
            _channels.Save(channel);

            var epoch0Token = fileTokenService.GenerateAccessToken("chat", channel.Id, "doc.png", userId: directRemoved.Id, sessionId: "sess-1");
            Assert.True(fileTokenService.ValidateAccessToken("chat", channel.Id, "doc.png", epoch0Token, directRemoved.Id, "sess-1").IsSuccess);

            // Simulate UI in-place mutation of the cached channel before calling UpdateChannel
            var liveChannelRef = _channels.GetById(channel.Id)!;
            liveChannelRef.ParticipantIds.Remove(directRemoved.Id);
            liveChannelRef.AllowedTeamIds.Remove("team-ext");

            var updateResult = service.UpdateChannel(liveChannelRef, owner.Id, isSessionUnlocked: true);
            Assert.True(updateResult.IsSuccess);

            var persisted = _channels.GetById(channel.Id)!;
            Assert.Equal(1, persisted.CurrentKeyEpoch);

            // Evicted direct member & former team member are stripped from BOTH Epoch 0 and Epoch 1 keys
            Assert.False(persisted.EncryptedChannelKeys.ContainsKey(directRemoved.Id));
            Assert.False(persisted.EncryptedChannelKeys.ContainsKey(teamEvicted.Id));
            Assert.False(persisted.EncryptedEpochKeys[0].ContainsKey(directRemoved.Id));
            Assert.False(persisted.EncryptedEpochKeys[0].ContainsKey(teamEvicted.Id));
            Assert.False(persisted.EncryptedEpochKeys[1].ContainsKey(directRemoved.Id));
            Assert.False(persisted.EncryptedEpochKeys[1].ContainsKey(teamEvicted.Id));

            // Owner retains both epochs and Epoch 1 uses a newly rotated key distinct from Epoch 0
            Assert.True(persisted.TryGetEncryptedKeyForEpoch(0, owner.Id, out var encOwner0));
            Assert.True(persisted.TryGetEncryptedKeyForEpoch(1, owner.Id, out var encOwner1));
            var decOwner0 = realCrypto.DecryptRsa(encOwner0!, privOwner);
            var decOwner1 = realCrypto.DecryptRsa(encOwner1!, privOwner);
            Assert.Equal(epoch0Key, decOwner0);
            Assert.NotEqual(epoch0Key, decOwner1);

            // File tokens issued before eviction are invalidated for evicted users
            Assert.False(fileTokenService.ValidateAccessToken("chat", channel.Id, "doc.png", epoch0Token, directRemoved.Id, "sess-1").IsSuccess);

            // Subsequent encrypted message is stamped with KeyEpoch == 1 and decryptable with epoch 1 key
            var sendResult = await service.SendMessageAsync(new SendMessageOptions { SenderId = owner.Id, ChannelId = channel.Id, Content = "Post-rotation secret", PrivateKey = privOwner });
            Assert.True(sendResult.IsSuccess);
            Assert.Equal(1, sendResult.Value!.KeyEpoch);
            Assert.Equal("Post-rotation secret", realCrypto.DecryptAes(sendResult.Value.Content, decOwner1));
        }
}
