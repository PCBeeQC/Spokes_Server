using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public sealed class ChatModerationVoiceCastAndCascadeSecurityTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly IConfiguration _config;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly EncryptionService _encryption;
    private readonly CryptoService _crypto;
    private readonly ServerEscrowService _escrow;
    private readonly ChatStateService _chatState;
    private readonly VoiceStateService _voiceState;
    private readonly FileTokenService _fileTokenService;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IWebPushService> _mockWebPush;
    private readonly Mock<IContentModerationService> _mockModeration;
    private readonly Mock<IChatChannelAccessService> _mockChatAccess;
    private readonly Mock<IUserPrivacyService> _mockPrivacy;
    private readonly NotificationRoutingService _notificationRouting;
    private readonly PresenceStateService _presenceState;

    public ChatModerationVoiceCastAndCascadeSecurityTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"Spokes_WS36_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataPath);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_config);
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _persistence = _serviceProvider.GetRequiredService<DiskPersistenceService>();
        _encryption = _serviceProvider.GetRequiredService<EncryptionService>();

        var profile = _db.CoreContext.CompanyProfile.Get();
        profile.Edition = "Business";
        _db.CoreContext.CompanyProfile.Save(profile);

        _crypto = new CryptoService();
        _escrow = new ServerEscrowService(
            _db.CoreContext.CompanyProfile,
            _db.CoreContext.ServerConfigs,
            _crypto,
            configuration: _config);
        _chatState = new ChatStateService();
        _voiceState = new VoiceStateService();
        _fileTokenService = new FileTokenService(
            _serviceProvider.GetRequiredService<IDataProtectionProvider>(),
            _serviceProvider.GetRequiredService<IMemoryCache>());

        _mockFileService = new Mock<IFileService>();
        _mockWebPush = new Mock<IWebPushService>();
        _mockChatAccess = new Mock<IChatChannelAccessService>();
        _mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns(() => _db.ChatChannels.GetAll().ToList());
        _mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns(new List<string>());

        _mockModeration = new Mock<IContentModerationService>();
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>()))
            .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));

        _mockPrivacy = new Mock<IUserPrivacyService>();
        _mockPrivacy.Setup(p => p.CanAccessPublicChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>())).Returns(true);
        _mockPrivacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

        var notificationQueue = new NotificationQueueService(
            NullLogger<NotificationQueueService>.Instance,
            _persistence,
            _config,
            _db.CoreContext.CompanyProfile);

        _notificationRouting = new NotificationRoutingService(
            _db.CommunicationContext,
            _db.HrContext,
            _db.CoreContext,
            _mockWebPush.Object,
            notificationQueue,
            new GlobalKeystoreService(),
            _crypto,
            _serviceProvider.GetRequiredService<IDataProtectionProvider>(),
            _mockChatAccess.Object,
            logger: NullLogger<NotificationRoutingService>.Instance);

        _presenceState = new PresenceStateService(_voiceState, _db.CoreContext, _db.HrContext);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_testDataPath))
            {
                Directory.Delete(_testDataPath, true);
            }
        }
        catch { }
    }

    private Employee SaveEmployee(
        string id,
        bool isAdmin = false,
        bool isSuspended = false,
        bool isActive = true,
        string? teamId = null,
        params string[] permissions)
    {
        var (pubKey, _) = _crypto.GenerateRsaKeyPair();
        var employee = new Employee
        {
            Id = id,
            FirstName = "Test",
            LastName = id,
            Email = $"{id}@spokes.test",
            IsAdmin = isAdmin,
            IsSuspended = isSuspended,
            IsActive = isActive,
            TeamId = teamId,
            PublicKey = pubKey,
            Permissions = [.. permissions]
        };
        _db.Employees.Save(employee);
        return employee;
    }

    private ChatAuthorizationService CreateChatAuth(IUserPrivacyService? privacy = null) =>
        new(_db.ChatChannels, _db.Projects, _db.Teams, _db.Employees, privacy ?? _mockPrivacy.Object);

    private AlbumService CreateAlbumService(IChatAuthorizationService? chatAuth = null, IFileService? fileService = null) =>
        new(_db.Albums,
            _db.ChatChannels,
            _crypto,
            _escrow,
            _db.Employees,
            chatAuth ?? CreateChatAuth(),
            NullLogger<AlbumService>.Instance,
            _fileTokenService,
            fileService ?? _mockFileService.Object,
            _db.ChatMessages);

    private ChatService CreateChatService(
        IChatAuthorizationService? chatAuth = null,
        IFileService? fileService = null,
        IUserPrivacyService? privacy = null) =>
        new(_db.CommunicationContext,
            _db.HrContext,
            _db.ProjectsContext,
            _chatState,
            _notificationRouting,
            _presenceState,
            fileService ?? _mockFileService.Object,
            _mockModeration.Object,
            CreateAlbumService(chatAuth, fileService),
            NullLogger<ChatService>.Instance,
            chatAuth: chatAuth ?? CreateChatAuth(privacy),
            crypto: _crypto,
            privacyService: privacy ?? _mockPrivacy.Object,
            fileTokenService: _fileTokenService);

    private ChatSecurityService CreateChatSecurityService(IChatAuthorizationService? chatAuth = null) =>
        new(_db.Employees,
            _db.ChatChannels,
            _db.ChatMessages,
            _escrow,
            _crypto,
            CreateAlbumService(chatAuth),
            _chatState,
            chatAuth ?? CreateChatAuth(),
            _mockFileService.Object,
            NullLogger<ChatSecurityService>.Instance);

    private ModerationReportService CreateModerationReportService(
        Mock<IChatService>? chatServiceMock = null,
        IChatAuthorizationService? chatAuth = null) =>
        new(_db.CommunicationContext,
            _db.HrContext,
            (chatServiceMock ?? new Mock<IChatService>()).Object,
            _chatState,
            _notificationRouting,
            TimeProvider.System,
            NullLogger<ModerationReportService>.Instance,
            _encryption,
            chatAuth ?? CreateChatAuth());

    private PhysicalVoiceRoomService CreatePhysicalVoiceRoomService(
        IUserPrivacyService? privacy = null,
        IChatAuthorizationService? chatAuth = null) =>
        new(_db.CommunicationContext.PhysicalVoiceRooms,
            _voiceState,
            NullLogger<PhysicalVoiceRoomService>.Instance,
            _db.Employees,
            _serviceProvider,
            privacy ?? _mockPrivacy.Object,
            _db.ChatChannels,
            chatAuth ?? CreateChatAuth(privacy));

    private string CreateDiskUploadAndThumb(string category, string contextId, string fileName)
    {
        var dir = Path.Combine(_testDataPath, "Uploads", category, contextId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), [1, 2, 3, 4]);
        var thumbName = $"{Path.GetFileNameWithoutExtension(fileName)}_thumb.jpg";
        File.WriteAllBytes(Path.Combine(dir, thumbName), [5, 6, 7, 8]);
        return $"/spokesapi/files/{category}/{contextId}/{fileName}";
    }

    // #34: Thumbnail token cannot be escalated to authorize full-resolution file access.
    [Fact]
    public void FileTokenService_ThumbnailToken_ValidatesThumbnailAndRejectsFullFile()
    {
        var token = _fileTokenService.GenerateAccessToken(
            "albums",
            "alb-1",
            "pic.jpg",
            "S2V5MTIz",
            "user-1",
            "sess-1",
            isThumbnail: true);

        var thumbResult = _fileTokenService.ValidateAccessToken(
            "albums",
            "alb-1",
            "pic.jpg",
            token,
            "user-1",
            "sess-1",
            isThumbnail: true);

        var fullFileResult = _fileTokenService.ValidateAccessToken(
            "albums",
            "alb-1",
            "pic.jpg",
            token,
            "user-1",
            "sess-1",
            isThumbnail: false);

        Assert.Equal(FileTokenStatus.Valid, thumbResult.Status);
        Assert.Equal(FileTokenStatus.Invalid, fullFileResult.Status);
        Assert.False(fullFileResult.IsSuccess);
    }

    // #41: Active user missing AppPermissions.Chat.Use is denied channel access, file access, sending, and DM creation.
    [Fact]
    public async Task ChatUsePermission_MissingOnActiveEmployee_DeniesChannelFileSendAndDmCreation()
    {
        var noChat = SaveEmployee("no-chat");
        var peer = SaveEmployee("chat-peer", permissions: AppPermissions.Chat.Use);

        var channel = new ChatChannel
        {
            Id = "chan-pub-41",
            Name = "Public",
            ChannelType = ChatChannelType.General
        };
        _db.ChatChannels.Save(channel);

        var chatAuth = CreateChatAuth();
        var fileProvider = new ChatFileAccessProvider(chatAuth, _db.ChatChannels);
        var chatService = CreateChatService(chatAuth);

        Assert.False(chatAuth.CanUserAccessChannel(channel, noChat));
        Assert.True(chatAuth.CanUserAccessChannel(channel, peer));

        Assert.False(fileProvider.CanAccess(noChat, channel.Id));
        Assert.False(fileProvider.CanUpload(noChat, channel.Id));

        var sendResult = await chatService.SendMessageAsync(noChat.Id, channel.Id, "Blocked message");
        Assert.False(sendResult.IsSuccess);

        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateDirectMessageChannel(noChat.Id, peer.Id));
    }

    // #44: SecureSearchMessagesAsync rejects unknown users, locked vaults, and ServerMaster-only encrypted DMs.
    [Fact]
    public async Task SecureSearchMessagesAsync_EnforcesUserExistencePrivateKeyAndNoServerMasterFallbackOnDms()
    {
        var (pubKey, privKey) = _crypto.GenerateRsaKeyPair();
        var alice = new Employee
        {
            Id = "alice-44",
            FirstName = "Alice",
            LastName = "Search",
            IsActive = true,
            PublicKey = pubKey,
            Permissions = [AppPermissions.Chat.Use]
        };
        _db.Employees.Save(alice);

        var plainGeneralKey = _crypto.GenerateAesKeyBase64();
        var generalChannel = new ChatChannel
        {
            Id = "chan-enc-44",
            Name = "Encrypted General",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                ["ServerMaster"] = _crypto.EncryptRsa(plainGeneralKey, _escrow.GetRequiredPublicKey())
            }
        };
        _db.ChatChannels.Save(generalChannel);
        _db.ChatMessages.Save(new ChatMessage
        {
            Id = "msg-gen-44",
            ChannelId = generalChannel.Id,
            SenderId = alice.Id,
            Content = _crypto.EncryptAes("secret-alpha", plainGeneralKey),
            IsEncrypted = true
        });

        var plainDmKey = _crypto.GenerateAesKeyBase64();
        var dmChannel = new ChatChannel
        {
            Id = "dm-enc-44",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = [alice.Id, "bob-44"],
            IsEncrypted = true,
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                ["ServerMaster"] = _crypto.EncryptRsa(plainDmKey, _escrow.GetRequiredPublicKey())
            }
        };
        _db.ChatChannels.Save(dmChannel);
        _db.ChatMessages.Save(new ChatMessage
        {
            Id = "msg-dm-44",
            ChannelId = dmChannel.Id,
            SenderId = alice.Id,
            Content = _crypto.EncryptAes("secret-dm", plainDmKey),
            IsEncrypted = true
        });

        var securityService = CreateChatSecurityService();

        var missingUserResults = await securityService.SecureSearchMessagesAsync(generalChannel.Id, "secret", "ghost-user", privKey);
        var lockedVaultResults = await securityService.SecureSearchMessagesAsync(generalChannel.Id, "secret", alice.Id, requestingUserPrivateKey: null);
        var dmServerMasterOnlyResults = await securityService.SecureSearchMessagesAsync(dmChannel.Id, "secret", alice.Id, privKey);

        Assert.Empty(missingUserResults);
        Assert.Empty(lockedVaultResults);
        Assert.Empty(dmServerMasterOnlyResults);

        // Positive control: adding user's own encrypted channel key allows decrypting DM search results.
        dmChannel.EncryptedChannelKeys[alice.Id] = _crypto.EncryptRsa(plainDmKey, pubKey);
        _db.ChatChannels.Save(dmChannel);
        var authorizedDmResults = await securityService.SecureSearchMessagesAsync(dmChannel.Id, "secret-dm", alice.Id, privKey);
        Assert.Single(authorizedDmResults);
    }

    // #46, #95: Channel creation requires Chat.CreateChannels + team membership, and UpdateChannel blocks type/entity tampering.
    [Fact]
    public void ChannelCreationAndUpdate_EnforceCreatePermissionTeamScopeAndRejectRestrictedRebinding()
    {
        _db.Teams.Save(new Team { Id = "team-a", Name = "Team A" });
        _db.Teams.Save(new Team { Id = "team-b", Name = "Team B" });

        _db.Projects.Save(new Project
        {
            Id = "proj-secret",
            Name = "Secret Project",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["owner-only"]
        });

        var noCreateUser = SaveEmployee("user-no-create", teamId: "team-a", permissions: AppPermissions.Chat.Use);
        var creatorUser = SaveEmployee(
            "user-creator",
            teamId: "team-a",
            permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.CreateChannels, AppPermissions.Projects.View]);

        var chatService = CreateChatService();

        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateTeamChannel(noCreateUser.Id, ["team-a"]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "chan-bad-create",
                Name = "Unauthorized",
                ChannelType = ChatChannelType.General,
                CreatedById = noCreateUser.Id
            }));

        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateTeamChannel(creatorUser.Id, ["team-b"]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "chan-bad-team",
                Name = "Cross-Team",
                ChannelType = ChatChannelType.Team,
                LinkedEntityId = "team-b",
                AllowedTeamIds = ["team-b"],
                CreatedById = creatorUser.Id
            }));

        var admin = SaveEmployee("admin-95", isAdmin: true);
        var general = new ChatChannel
        {
            Id = "chan-gen-95",
            Name = "General",
            ChannelType = ChatChannelType.General,
            CreatedById = admin.Id
        };
        _db.ChatChannels.Save(general);

        var projectChannel = new ChatChannel
        {
            Id = "chan-proj-95",
            Name = "Proj",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "proj-allowed",
            CreatedById = admin.Id
        };
        _db.Projects.Save(new Project
        {
            Id = "proj-allowed",
            Name = "Allowed",
            AccessPolicy = ProjectAccessPolicy.Public
        });
        _db.ChatChannels.Save(projectChannel);

        var typeMutateAttempt = new ChatChannel
        {
            Id = general.Id,
            Name = "Escalated",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "proj-allowed",
            CreatedById = admin.Id
        };
        var typeMutateResult = chatService.UpdateChannel(typeMutateAttempt, admin.Id);
        Assert.False(typeMutateResult.IsSuccess);
        Assert.Equal(ChatChannelType.General, _db.ChatChannels.GetById(general.Id)!.ChannelType);

        var rebindAttempt = new ChatChannel
        {
            Id = projectChannel.Id,
            Name = "Rebound",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "proj-secret",
            CreatedById = creatorUser.Id
        };
        var rebindResult = chatService.UpdateChannel(rebindAttempt, admin.Id);
        Assert.False(rebindResult.IsSuccess);
        Assert.Equal("proj-allowed", _db.ChatChannels.GetById(projectChannel.Id)!.LinkedEntityId);

        var bindSecretAttempt = new ChatChannel
        {
            Id = "chan-bind-secret",
            Name = "Secret Bind",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "proj-secret",
            CreatedById = creatorUser.Id
        };
        var bindSecretResult = chatService.UpdateChannel(bindSecretAttempt, creatorUser.Id);
        Assert.False(bindSecretResult.IsSuccess);
        Assert.Null(_db.ChatChannels.GetById(bindSecretAttempt.Id));
    }

    // #29, #45: DeleteChannel cascades messages, attachments, polls, reports, and album shares/keys.
    [Fact]
    public void DeleteChannel_CascadesMessagesAttachmentsPollsReportsAndAlbumShares()
    {
        var admin = SaveEmployee("admin-del-chan", isAdmin: true);
        var channel = new ChatChannel
        {
            Id = "chan-del-29",
            Name = "Ephemeral",
            ChannelType = ChatChannelType.General,
            CreatedById = admin.Id
        };
        _db.ChatChannels.Save(channel);

        _db.ChatMessages.Save(new ChatMessage
        {
            Id = "msg-del-29",
            ChannelId = channel.Id,
            SenderId = admin.Id,
            Content = "Attachment",
            Attachments = [new ChatAttachment { Id = "att-29", FileName = "doc.pdf", FilePath = "/spokesapi/files/chat/chan-del-29/doc.pdf" }]
        });
        _db.ChatPolls.Save(new ChatPoll { Id = "poll-29", ChannelId = channel.Id, CreatorId = admin.Id, Question = "Q?" });
        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-29",
            ChannelId = channel.Id,
            MessageId = "msg-del-29",
            ReporterId = admin.Id,
            ReportedUserId = admin.Id,
            Reason = "Spam"
        });
        _db.Albums.Save(new Album
        {
            Id = "alb-29",
            OwnerId = admin.Id,
            SharedWithChannelIds = [channel.Id],
            EncryptedAlbumKeys = new Dictionary<string, string>
            {
                [$"Channel_{channel.Id}"] = "enc-key",
                [admin.Id] = "owner-key"
            }
        });

        var chatService = CreateChatService();
        var res = chatService.DeleteChannel(channel.Id, admin.Id);

        Assert.True(res.IsSuccess);
        Assert.Null(_db.ChatChannels.GetById(channel.Id));
        Assert.Empty(_db.ChatMessages.GetAllByChannelIncludingCold(channel.Id));
        _mockFileService.Verify(f => f.DeleteAsync("/spokesapi/files/chat/chan-del-29/doc.pdf"), Times.Once);
        Assert.Null(_db.ChatPolls.GetById("poll-29"));
        Assert.Null(_db.ReportedMessages.GetById("rep-29"));

        var album = _db.Albums.GetById("alb-29")!;
        Assert.DoesNotContain(channel.Id, album.SharedWithChannelIds);
        Assert.False(album.EncryptedAlbumKeys.ContainsKey($"Channel_{channel.Id}"));
        Assert.True(album.EncryptedAlbumKeys.ContainsKey(admin.Id));
    }

    // #29, #45: ProjectService.DeleteProject cascades linked project channel, messages, disk attachments+thumbs, polls, reports, and albums.
    [Fact]
    public void DeleteProject_CascadesLinkedChannelMessagesPollsReportsDiskFilesAndAlbumShares()
    {
        var admin = SaveEmployee("admin-proj-del", isAdmin: true);
        var project = new Project { Id = "proj-cascade-45", Name = "Cascade Project" };
        _db.Projects.Save(project);

        var projChannel = new ChatChannel
        {
            Id = "chan-proj-cascade",
            Name = project.Name,
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id,
            CreatedById = admin.Id
        };
        _db.ChatChannels.Save(projChannel);

        var fileUrl = CreateDiskUploadAndThumb("chat", projChannel.Id, "spec.png");
        _db.ChatMessages.Save(new ChatMessage
        {
            Id = "msg-proj-45",
            ChannelId = projChannel.Id,
            SenderId = admin.Id,
            Content = "Project file",
            Attachments = [new ChatAttachment { Id = "att-proj-45", FileName = "spec.png", FilePath = fileUrl }]
        });
        _db.ChatPolls.Save(new ChatPoll { Id = "poll-proj-45", ChannelId = projChannel.Id, CreatorId = admin.Id, Question = "Ship?" });
        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-proj-45",
            ChannelId = projChannel.Id,
            MessageId = "msg-proj-45",
            ReporterId = admin.Id,
            ReportedUserId = admin.Id,
            Reason = "Test"
        });
        _db.Albums.Save(new Album
        {
            Id = "alb-proj-45",
            OwnerId = admin.Id,
            SharedWithChannelIds = [projChannel.Id],
            EncryptedAlbumKeys = new Dictionary<string, string> { [$"Channel_{projChannel.Id}"] = "enc" }
        });

        var projectService = new ProjectService(_db, _persistence, _config, NullLogger<ProjectService>.Instance);
        var result = projectService.DeleteProject(project.Id);

        Assert.True(result.IsSuccess);
        Assert.Null(_db.Projects.GetById(project.Id));
        Assert.Null(_db.ChatChannels.GetById(projChannel.Id));
        Assert.Empty(_db.ChatMessages.GetAllByChannelIncludingCold(projChannel.Id));
        Assert.Null(_db.ChatPolls.GetById("poll-proj-45"));
        Assert.Null(_db.ReportedMessages.GetById("rep-proj-45"));

        Assert.False(File.Exists(Path.Combine(_testDataPath, "Uploads", "chat", projChannel.Id, "spec.png")));
        Assert.False(File.Exists(Path.Combine(_testDataPath, "Uploads", "chat", projChannel.Id, "spec_thumb.jpg")));

        var album = _db.Albums.GetById("alb-proj-45")!;
        Assert.DoesNotContain(projChannel.Id, album.SharedWithChannelIds);
        Assert.False(album.EncryptedAlbumKeys.ContainsKey($"Channel_{projChannel.Id}"));
    }

    // #29, #45: AccountDeletionService.DeleteAccount scrubs targeted reports and cleans owned/shared albums.
    [Fact]
    public void DeleteAccount_ScrubsTargetedReportsAndCleansOwnedAndSharedAlbums()
    {
        SaveEmployee("admin-keeper", isAdmin: true);
        var victim = SaveEmployee("victim-45", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);
        var colleague = SaveEmployee("colleague-45", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);

        var reportFileUrl = CreateDiskUploadAndThumb("chat", "chan-rep-45", "evidence.png");
        _db.ReportedMessages.Save(new ReportedMessage
        {
            Id = "rep-victim-45",
            ChannelId = "chan-rep-45",
            MessageId = "msg-v-45",
            ReporterId = colleague.Id,
            ReportedUserId = victim.Id,
            Reason = "Harassment",
            SnapshotContent = _encryption.Encrypt("Sensitive evidence text"),
            Attachments = [new ChatAttachment { Id = "att-v-45", FileName = "evidence.png", FilePath = reportFileUrl }]
        });

        var ownedAlbumUrl = CreateDiskUploadAndThumb("albums", "alb-owned-45", "photo.png");
        _db.Albums.Save(new Album
        {
            Id = "alb-owned-45",
            OwnerId = victim.Id,
            Media = [new AlbumMedia { Id = "m1", FileName = "photo.png", FilePath = ownedAlbumUrl }]
        });
        _db.Albums.Save(new Album
        {
            Id = "alb-shared-45",
            OwnerId = colleague.Id,
            ContributorUserIds = [victim.Id, colleague.Id],
            EncryptedAlbumKeys = new Dictionary<string, string>
            {
                [victim.Id] = "victim-key",
                [colleague.Id] = "colleague-key"
            }
        });

        var deletionService = new AccountDeletionService(
            _db.HrContext,
            _db.CoreContext,
            _db.CommunicationContext,
            _db.ProjectsContext,
            _config,
            null,
            NullLogger<AccountDeletionService>.Instance);

        deletionService.DeleteAccount(victim.Id);

        var scrubbedReport = _db.ReportedMessages.GetById("rep-victim-45")!;
        Assert.Equal(AccountDeletionService.SystemDeletedUserId, scrubbedReport.ReportedUserId);
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, scrubbedReport.SnapshotContent);
        Assert.Empty(scrubbedReport.Attachments);
        Assert.False(File.Exists(Path.Combine(_testDataPath, "Uploads", "chat", "chan-rep-45", "evidence.png")));
        Assert.False(File.Exists(Path.Combine(_testDataPath, "Uploads", "chat", "chan-rep-45", "evidence_thumb.jpg")));

        Assert.Null(_db.Albums.GetById("alb-owned-45"));
        Assert.False(File.Exists(Path.Combine(_testDataPath, "Uploads", "albums", "alb-owned-45", "photo.png")));

        var sharedAlbum = _db.Albums.GetById("alb-shared-45")!;
        Assert.DoesNotContain(victim.Id, sharedAlbum.ContributorUserIds);
        Assert.False(sharedAlbum.EncryptedAlbumKeys.ContainsKey(victim.Id));
        Assert.True(sharedAlbum.EncryptedAlbumKeys.ContainsKey(colleague.Id));
    }

    // #66: BillFileAccessProvider, ChatFileAccessProvider, and AlbumFileAccessProvider enforce bill, chat, album, and archive permissions.
    [Fact]
    public void FileAccessProviders_EnforceBillChatAndAlbumPermissionsAndArchiveBoundary()
    {
        var billViewer = SaveEmployee("bill-view", permissions: AppPermissions.Purchases.ViewBills);
        var poOnlyUser = SaveEmployee("po-only", permissions: AppPermissions.Purchases.ViewPOs);
        var billProvider = new BillFileAccessProvider();

        Assert.True(billProvider.CanAccess(billViewer, "bill-1"));
        Assert.False(billProvider.CanAccess(poOnlyUser, "bill-1"));

        var chatUser = SaveEmployee("chat-no-arch", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);
        var archiveUser = SaveEmployee("chat-arch", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.ViewArchive, AppPermissions.Albums.View]);
        var noAlbumOwner = SaveEmployee("owner-no-alb", permissions: AppPermissions.Chat.Use);

        var archivedChannel = new ChatChannel
        {
            Id = "chan-arch-66",
            Name = "Archived",
            ChannelType = ChatChannelType.General,
            IsArchived = true
        };
        _db.ChatChannels.Save(archivedChannel);

        var sharedAlbum = new Album
        {
            Id = "alb-arch-66",
            OwnerId = noAlbumOwner.Id,
            SharedWithChannelIds = [archivedChannel.Id]
        };
        _db.Albums.Save(sharedAlbum);

        var chatAuth = CreateChatAuth();
        var chatProvider = new ChatFileAccessProvider(chatAuth, _db.ChatChannels);
        var albumProvider = new AlbumFileAccessProvider(_db.Albums, chatAuth, _db.ChatChannels);

        Assert.False(chatProvider.CanAccess(chatUser, archivedChannel.Id));
        Assert.True(chatProvider.CanAccess(archiveUser, archivedChannel.Id));

        Assert.False(albumProvider.CanAccess(noAlbumOwner, sharedAlbum.Id));
        Assert.False(albumProvider.CanUpload(noAlbumOwner, sharedAlbum.Id));

        Assert.False(albumProvider.CanAccess(chatUser, sharedAlbum.Id));
        Assert.True(albumProvider.CanAccess(archiveUser, sharedAlbum.Id));
    }

    // #75, #76: DM participants can report DMs, spoofed metadata is overridden, and SnapshotContent is encrypted at rest.
    [Fact]
    public async Task SubmitReportAsync_AllowsDmParticipantOverridesMetadataAndEncryptsSnapshotAtRest()
    {
        var alice = SaveEmployee("alice-75", permissions: AppPermissions.Chat.Use);
        var bob = SaveEmployee("bob-75", permissions: AppPermissions.Chat.Use);

        var dmChannel = new ChatChannel
        {
            Id = "dm-75",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = [alice.Id, bob.Id]
        };
        _db.ChatChannels.Save(dmChannel);

        var msg = new ChatMessage
        {
            Id = "msg-dm-75",
            ChannelId = dmChannel.Id,
            SenderId = bob.Id,
            Content = "Original DM text"
        };
        _db.ChatMessages.Save(msg);

        var moderationService = CreateModerationReportService();
        var result = await moderationService.SubmitReportAsync(
            new ReportedMessage
            {
                Id = "rep-dm-75",
                MessageId = msg.Id,
                ChannelId = "spoofed-public-chan",
                ReportedUserId = "innocent-third-party",
                Reason = "Abuse",
                SnapshotContent = "Sensitive DM abuse text"
            },
            alice.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(dmChannel.Id, result.Value!.ChannelId);
        Assert.Equal(bob.Id, result.Value.ReportedUserId);
        Assert.Equal("Original DM text", result.Value.SnapshotContent);

        var persisted = _db.ReportedMessages.GetById("rep-dm-75")!;
        Assert.StartsWith("v2:", persisted.SnapshotContent, StringComparison.Ordinal);
        Assert.True(_encryption.CanDecrypt(persisted.SnapshotContent));
        Assert.Equal("Original DM text", _encryption.Decrypt(persisted.SnapshotContent));
    }

    // #75, #76: Non-admin moderators cannot view/act on DMs or inaccessible channels or moderate admins/peer moderators, while Global Admin can; Dismiss/Resolve scrub snapshots.
    [Fact]
    public async Task ModerationQueueAndActions_EnforceAdminOnlyDmAccessHierarchyAndSnapshotScrubbing()
    {
        var admin = SaveEmployee("admin-76", isAdmin: true, permissions: AppPermissions.Chat.Use);
        var mod1 = SaveEmployee("mod-1", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]);
        var mod2 = SaveEmployee("mod-2", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]);
        var alice = SaveEmployee("alice-76", permissions: AppPermissions.Chat.Use);
        var bob = SaveEmployee("bob-76", permissions: AppPermissions.Chat.Use);

        var pubChannel = new ChatChannel { Id = "chan-pub-76", Name = "General", ChannelType = ChatChannelType.General };
        var privChannel = new ChatChannel { Id = "chan-priv-76", Name = "Exec", ChannelType = ChatChannelType.General, ParticipantIds = [alice.Id, bob.Id] };
        var dmChannel = new ChatChannel { Id = "dm-76", ChannelType = ChatChannelType.Direct, ParticipantIds = [alice.Id, bob.Id] };
        _db.ChatChannels.Save(pubChannel);
        _db.ChatChannels.Save(privChannel);
        _db.ChatChannels.Save(dmChannel);

        var pubMsg = new ChatMessage { Id = "m-pub", ChannelId = pubChannel.Id, SenderId = alice.Id, Content = "Bad pub" };
        var adminMsg = new ChatMessage { Id = "m-adm", ChannelId = pubChannel.Id, SenderId = admin.Id, Content = "Admin msg" };
        var mod2Msg = new ChatMessage { Id = "m-mod2", ChannelId = pubChannel.Id, SenderId = mod2.Id, Content = "Mod2 msg" };
        var privMsg = new ChatMessage { Id = "m-priv", ChannelId = privChannel.Id, SenderId = bob.Id, Content = "Priv msg" };
        var dmMsg = new ChatMessage { Id = "m-dm", ChannelId = dmChannel.Id, SenderId = bob.Id, Content = "DM msg" };
        foreach (var m in new[] { pubMsg, adminMsg, mod2Msg, privMsg, dmMsg })
        {
            _db.ChatMessages.Save(m);
        }

        var chatServiceMock = new Mock<IChatService>();
        chatServiceMock.Setup(c => c.AdminDeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult.Success());
        chatServiceMock.Setup(c => c.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult.Success());
        var reportService = CreateModerationReportService(chatServiceMock);

        await reportService.SubmitReportAsync(new ReportedMessage { Id = "r-pub", MessageId = pubMsg.Id, Reason = "Spam", SnapshotContent = "Snap pub" }, bob.Id);
        await reportService.SubmitReportAsync(new ReportedMessage { Id = "r-adm", MessageId = adminMsg.Id, Reason = "Spam", SnapshotContent = "Snap adm" }, alice.Id);
        await reportService.SubmitReportAsync(new ReportedMessage { Id = "r-mod2", MessageId = mod2Msg.Id, Reason = "Spam", SnapshotContent = "Snap mod2" }, alice.Id);
        await reportService.SubmitReportAsync(new ReportedMessage { Id = "r-priv", MessageId = privMsg.Id, Reason = "Spam", SnapshotContent = "Snap priv" }, alice.Id);
        await reportService.SubmitReportAsync(new ReportedMessage { Id = "r-dm", MessageId = dmMsg.Id, Reason = "Abuse", SnapshotContent = "Snap dm" }, alice.Id);

        var modQueue = reportService.GetReportItems(ModerationQueueTab.Open, viewerEmployeeId: mod1.Id).Select(i => i.Report).ToList();
        Assert.DoesNotContain(modQueue, r => r.Id == "r-dm");
        Assert.DoesNotContain(modQueue, r => r.Id == "r-priv");
        Assert.Contains(modQueue, r => r.Id == "r-pub");

        var adminQueue = reportService.GetReportItems(ModerationQueueTab.Open, viewerEmployeeId: admin.Id).Select(i => i.Report).ToList();
        Assert.Contains(adminQueue, r => r.Id == "r-dm");
        Assert.Contains(adminQueue, r => r.Id == "r-priv");

        Assert.False(reportService.DismissReport("r-dm", mod1.Id).IsSuccess);
        Assert.False((await reportService.ResolveAndDeleteMessageAsync("r-priv", mod1.Id)).IsSuccess);
        Assert.False(reportService.DismissReport("r-adm", mod1.Id).IsSuccess);
        Assert.False(reportService.DismissReport("r-mod2", mod1.Id).IsSuccess);

        // Add attachment to verify scrubbing on dismiss
        var pubPersisted = _db.ReportedMessages.GetById("r-pub")!;
        pubPersisted.Attachments = [new ChatAttachment { Id = "a1", FileName = "shot.png", FilePath = "/spokesapi/files/chat/chan-pub-76/shot.png" }];
        _db.ReportedMessages.Save(pubPersisted);

        var dismissOk = reportService.DismissReport("r-pub", mod1.Id);
        Assert.True(dismissOk.IsSuccess);
        var dismissed = _db.ReportedMessages.GetById("r-pub")!;
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, dismissed.SnapshotContent);
        Assert.Empty(dismissed.Attachments);

        var resolveDm = await reportService.ResolveAndDeleteMessageAsync("r-dm", admin.Id);
        Assert.True(resolveDm.IsSuccess);
        var resolvedDm = _db.ReportedMessages.GetById("r-dm")!;
        Assert.Equal(ReportedMessageResolution.DeletedContentPlaceholder, resolvedDm.SnapshotContent);
        Assert.Empty(resolvedDm.Attachments);
    }

    // #79, #80: Physical room management excludes moderators/creators; suspended & cross-silo discovery blocked while PIN-free same-silo self-enrollment succeeds.
    [Fact]
    public async Task PhysicalVoiceRooms_RestrictManageRolesAndIsolateCrossSiloAndSuspendedUsersWithoutPin()
    {
        var admin = SaveEmployee("admin-80", isAdmin: true);
        var settingsManager = SaveEmployee("mgr-settings", permissions: [AppPermissions.Chat.Use, AppPermissions.Admin.ManageSettings]);
        var moderator = SaveEmployee("mod-80", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]);
        var creatorOnly = SaveEmployee("creator-80", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.CreateChannels, AppPermissions.Chat.EditAllPublicChannels]);
        var alice = SaveEmployee("alice-80", permissions: AppPermissions.Chat.Use);
        var bob = SaveEmployee("bob-80", permissions: AppPermissions.Chat.Use);
        var charlie = SaveEmployee("charlie-80", permissions: AppPermissions.Chat.Use);
        var suspended = SaveEmployee("susp-80", isSuspended: true, permissions: AppPermissions.Chat.Use);

        var privacy = new Mock<IUserPrivacyService>();
        privacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>()))
            .Returns((Employee? caller, Employee target) =>
                !(caller?.Id == bob.Id && target.Id == alice.Id));

        _db.CommunicationContext.PhysicalVoiceRooms.Save(new PhysicalVoiceRoom
        {
            Id = "room-cross-silo",
            Name = "Alice Room",
            CreatedByUserId = alice.Id,
            EnrolledDevices = [new PhysicalRoomDeviceEnrollment { EmployeeId = alice.Id, DeviceId = "dev-a", DeviceName = "PC-A" }]
        });
        _db.CommunicationContext.PhysicalVoiceRooms.Save(new PhysicalVoiceRoom
        {
            Id = "room-same-silo",
            Name = "Charlie Room",
            CreatedByUserId = charlie.Id,
            EnrolledDevices = [new PhysicalRoomDeviceEnrollment { EmployeeId = charlie.Id, DeviceId = "dev-c", DeviceName = "PC-C" }]
        });

        var voiceRoomService = CreatePhysicalVoiceRoomService(privacy.Object);

        Assert.True(voiceRoomService.CanUserManagePhysicalRooms(admin.Id));
        Assert.True(voiceRoomService.CanUserManagePhysicalRooms(settingsManager.Id));
        Assert.False(voiceRoomService.CanUserManagePhysicalRooms(moderator.Id));
        Assert.False(voiceRoomService.CanUserManagePhysicalRooms(creatorOnly.Id));

        Assert.Empty(voiceRoomService.GetRoomsForUser(suspended.Id));
        var suspEnroll = await voiceRoomService.EnrollUserDeviceAsync(
            "room-same-silo",
            suspended.Id,
            "dev-s",
            "Susp-PC",
            actorUserId: suspended.Id);
        Assert.False(suspEnroll.IsSuccess);

        var bobRooms = voiceRoomService.GetRoomsForUser(bob.Id);
        Assert.DoesNotContain(bobRooms, r => r.Id == "room-cross-silo");
        Assert.Contains(bobRooms, r => r.Id == "room-same-silo");

        var crossSiloEnroll = await voiceRoomService.EnrollUserDeviceAsync(
            "room-cross-silo",
            bob.Id,
            "dev-b",
            "Bob-PC",
            actorUserId: bob.Id);
        Assert.False(crossSiloEnroll.IsSuccess);
        Assert.Equal("FORBIDDEN", crossSiloEnroll.Code);

        // One-click PIN-free self-enrollment succeeds within discoverability silo.
        var sameSiloEnroll = await voiceRoomService.EnrollUserDeviceAsync(
            "room-same-silo",
            bob.Id,
            "dev-b",
            "Bob-PC",
            actorUserId: bob.Id);
        Assert.True(sameSiloEnroll.IsSuccess);
    }

    // #79, #80: Moderator physical-room assignment requires shared active accessible non-DM call, target hierarchy, and room discoverability.
    [Fact]
    public async Task AdminAssignParticipantDeviceToRoomAsync_ScopesModeratorsToActiveNonDmCallsHierarchyAndSilo()
    {
        var admin = SaveEmployee("admin-assign", isAdmin: true, permissions: AppPermissions.Chat.Use);
        var mod = SaveEmployee("mod-assign", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]);
        var target = SaveEmployee("target-assign", permissions: AppPermissions.Chat.Use);
        var hidden = SaveEmployee("hidden-assign", permissions: AppPermissions.Chat.Use);

        var privacy = new Mock<IUserPrivacyService>();
        privacy.Setup(p => p.CanAccessPublicChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>())).Returns(true);
        privacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>()))
            .Returns((Employee? caller, Employee member) =>
                !(caller?.Id == mod.Id && member.Id == hidden.Id));

        var voiceChan = new ChatChannel { Id = "chan-voice-80", Name = "Lounge", ChannelType = ChatChannelType.General };
        var dmChan = new ChatChannel { Id = "chan-dm-80", ChannelType = ChatChannelType.Direct, ParticipantIds = [mod.Id, target.Id] };
        _db.ChatChannels.Save(voiceChan);
        _db.ChatChannels.Save(dmChan);

        _db.CommunicationContext.PhysicalVoiceRooms.Save(new PhysicalVoiceRoom
        {
            Id = "room-mod-visible",
            Name = "Shared Lab",
            CreatedByUserId = mod.Id
        });
        _db.CommunicationContext.PhysicalVoiceRooms.Save(new PhysicalVoiceRoom
        {
            Id = "room-mod-hidden",
            Name = "Hidden Silo",
            CreatedByUserId = hidden.Id
        });

        var service = CreatePhysicalVoiceRoomService(privacy.Object);

        // Outside any active voice call -> denied
        var noCallResult = await service.AdminAssignParticipantDeviceToRoomAsync(
            mod.Id, target.Id, "dev-t", "TargetPC", "room-mod-visible");
        Assert.False(noCallResult.IsSuccess);

        // Sharing only a Direct DM call -> denied
        _voiceState.NotifyVoiceMemberJoined(dmChan.Id, mod.Id, "dev-m", "ModPC");
        _voiceState.NotifyVoiceMemberJoined(dmChan.Id, target.Id, "dev-t", "TargetPC");
        var dmCallResult = await service.AdminAssignParticipantDeviceToRoomAsync(
            mod.Id, target.Id, "dev-t", "TargetPC", "room-mod-visible", channelId: dmChan.Id);
        Assert.False(dmCallResult.IsSuccess);

        _voiceState.NotifyVoiceMemberLeft(dmChan.Id, mod.Id);
        _voiceState.NotifyVoiceMemberLeft(dmChan.Id, target.Id);

        // Sharing active accessible General channel
        _voiceState.NotifyVoiceMemberJoined(voiceChan.Id, mod.Id, "dev-m", "ModPC");
        _voiceState.NotifyVoiceMemberJoined(voiceChan.Id, target.Id, "dev-t", "TargetPC");
        _voiceState.NotifyVoiceMemberJoined(voiceChan.Id, admin.Id, "dev-a", "AdminPC");

        var assignAdminResult = await service.AdminAssignParticipantDeviceToRoomAsync(
            mod.Id, admin.Id, "dev-a", "AdminPC", "room-mod-visible", channelId: voiceChan.Id);
        Assert.False(assignAdminResult.IsSuccess);

        var assignHiddenRoomResult = await service.AdminAssignParticipantDeviceToRoomAsync(
            mod.Id, target.Id, "dev-t", "TargetPC", "room-mod-hidden", channelId: voiceChan.Id);
        Assert.False(assignHiddenRoomResult.IsSuccess);

        var validModAssign = await service.AdminAssignParticipantDeviceToRoomAsync(
            mod.Id, target.Id, "dev-t", "TargetPC", "room-mod-visible", channelId: voiceChan.Id);
        Assert.True(validModAssign.IsSuccess);
    }

    // #92, #93: UserScopedSlideshowBridge re-validates live user permissions and album share membership on every GetMedia call.
    [Fact]
    public async Task UserScopedSlideshowBridge_GetMedia_ReturnsForbidWhenShareOrAlbumsPermissionRevoked()
    {
        var presenter = SaveEmployee("presenter-93", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);
        var owner = SaveEmployee("owner-93", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);

        var privChan = new ChatChannel
        {
            Id = "chan-cast-93",
            Name = "Family Photos",
            ChannelType = ChatChannelType.General,
            ParticipantIds = [owner.Id, presenter.Id]
        };
        _db.ChatChannels.Save(privChan);

        var fileService = new FileService(_config, NullLogger<FileService>.Instance, _crypto);
        using var imgStream = new MemoryStream(Encoding.UTF8.GetBytes("fake-jpeg-bytes-for-cast-bridge-test"));
        await fileService.UploadStreamAsync("albums", "alb-cast-93", imgStream, "slide1.jpg");

        var album = new Album
        {
            Id = "alb-cast-93",
            Title = "Cast Album",
            OwnerId = owner.Id,
            IsEncrypted = false,
            SharedWithChannelIds = [privChan.Id],
            Media = [new AlbumMedia { Id = "m1", FileName = "slide1.jpg", FilePath = "/spokesapi/files/albums/alb-cast-93/slide1.jpg" }]
        };
        _db.Albums.Save(album);

        var chatAuth = CreateChatAuth();
        var chatAccess = new Mock<IChatChannelAccessService>();
        chatAccess.Setup(a => a.GetUsersForChannel(privChan.Id))
            .Returns([owner.Id, presenter.Id]);

        var albumService = new AlbumService(
            _db.Albums,
            _db.ChatChannels,
            _crypto,
            _escrow,
            _db.Employees,
            chatAuth,
            NullLogger<AlbumService>.Instance,
            _fileTokenService,
            fileService,
            _db.ChatMessages);

        var fileAuth = new FileAuthorizationService(
            [new AlbumFileAccessProvider(_db.Albums, chatAuth, _db.ChatChannels)]);

        var mockAuthState = new Mock<AuthenticationStateProvider>();
        mockAuthState.Setup(s => s.GetAuthenticationStateAsync())
            .ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        var userService = new UserService(mockAuthState.Object, _db.Employees, _db.CoreContext.OpenIdAccounts);

        var fileController = new FileController(
            fileService,
            fileAuth,
            _db.CoreContext,
            _db.CommunicationContext,
            NullLogger<FileController>.Instance,
            userService,
            new ImageProcessingService(),
            fileTokenService: _fileTokenService,
            config: _config);

        var bridgeServices = new ServiceCollection();
        bridgeServices.AddSingleton(_db);
        bridgeServices.AddSingleton(_db.Employees);
        bridgeServices.AddSingleton(_db.Albums);
        bridgeServices.AddSingleton(albumService);
        bridgeServices.AddSingleton(fileController);
        using var bridgeProvider = bridgeServices.BuildServiceProvider();

        var bridge = new UserScopedSlideshowBridge(
            bridgeProvider,
            presenter,
            album.Id,
            plainAlbumKey: null,
            new Dictionary<string, string> { ["m1"] = "slide1.jpg" });

        // Initially authorized: streams file bytes (not ForbidResult)
        var initialResult = await bridge.GetMediaAsync("m1", thumb: false);
        Assert.IsNotType<ForbidResult>(initialResult);

        // Case A: Unshare channel -> immediate ForbidResult
        album.SharedWithChannelIds.Clear();
        _db.Albums.Save(album);
        Assert.IsType<ForbidResult>(await bridge.GetMediaAsync("m1", thumb: false));

        // Restore share, then revoke AppPermissions.Albums.View -> immediate ForbidResult
        album.SharedWithChannelIds = [privChan.Id];
        _db.Albums.Save(album);
        Assert.IsNotType<ForbidResult>(await bridge.GetMediaAsync("m1", thumb: false));

        presenter.Permissions.Remove(AppPermissions.Albums.View);
        _db.Employees.Save(presenter);
        Assert.IsType<ForbidResult>(await bridge.GetMediaAsync("m1", thumb: false));
    }
}
