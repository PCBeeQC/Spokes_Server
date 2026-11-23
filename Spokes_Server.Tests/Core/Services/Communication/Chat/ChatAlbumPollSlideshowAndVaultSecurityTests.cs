using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Hubs;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

public sealed class ChatAlbumPollSlideshowAndVaultSecurityTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly IConfiguration _config;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly DiskPersistenceService _persistence;
    private readonly CryptoService _crypto;
    private readonly ServerEscrowService _escrow;
    private readonly ChatStateService _chatState;
    private readonly VoiceStateService _voiceState;
    private readonly FileTokenService _fileTokenService;
    private readonly GlobalKeystoreService _globalKeystore;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IWebPushService> _mockWebPush;
    private readonly Mock<IContentModerationService> _mockModeration;
    private readonly Mock<IChatChannelAccessService> _mockChatAccess;
    private readonly NotificationRoutingService _notificationRouting;
    private readonly PresenceStateService _presenceState;

    public ChatAlbumPollSlideshowAndVaultSecurityTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"Spokes_WS42_{Guid.NewGuid():N}");
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
        _dataProtection = _serviceProvider.GetRequiredService<IDataProtectionProvider>();

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
            _dataProtection,
            _serviceProvider.GetRequiredService<IMemoryCache>());
        _globalKeystore = new GlobalKeystoreService();

        _mockFileService = new Mock<IFileService>();
        _mockWebPush = new Mock<IWebPushService>();
        _mockChatAccess = new Mock<IChatChannelAccessService>();
        _mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns(() => _db.ChatChannels.GetAll().ToList());
        _mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>()))
            .Returns((string cid) =>
            {
                var ch = _db.ChatChannels.GetById(cid);
                if (ch == null) return new List<string>();
                if (ch.ChannelType == ChatChannelType.Direct || ch.ChannelType == ChatChannelType.Group)
                    return ch.ParticipantIds.ToList();
                return _db.Employees.GetAll().Select(e => e.Id).ToList();
            });

        _mockModeration = new Mock<IContentModerationService>();
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>()))
            .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));

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
            _globalKeystore,
            _crypto,
            _dataProtection,
            _mockChatAccess.Object,
            logger: NullLogger<NotificationRoutingService>.Instance);

        _presenceState = new PresenceStateService(_voiceState, _db.CoreContext, _db.HrContext);
    }

    public void Dispose()
    {
        _persistence.FlushAll();
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
        string? encryptedPrivateKey = null,
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
            EncryptedPrivateKey = encryptedPrivateKey,
            Permissions = [.. permissions]
        };
        _db.Employees.Save(employee);
        return employee;
    }

    private (Employee Employee, string PlainPrivateKey, string Password) SaveEmployeeWithRealVault(
        string id,
        string password,
        bool isSuspended = false,
        params string[] permissions)
    {
        var (pubKey, privKey) = _crypto.GenerateRsaKeyPair();
        var kek = _crypto.DeriveKeyFromPassword(password, id);
        var encPriv = _crypto.EncryptAes(privKey, kek);
        var employee = new Employee
        {
            Id = id,
            FirstName = "Vault",
            LastName = id,
            Email = $"{id}@spokes.test",
            IsActive = true,
            IsSuspended = isSuspended,
            PublicKey = pubKey,
            EncryptedPrivateKey = encPriv,
            Permissions = [.. permissions]
        };
        _db.Employees.Save(employee);
        return (employee, privKey, password);
    }

    private ChatAuthorizationService CreateChatAuth(IUserPrivacyService? privacy = null) =>
        new(_db.ChatChannels, _db.Projects, _db.Teams, _db.Employees, privacy);

    private AlbumService CreateAlbumService(IChatAuthorizationService? chatAuth = null) =>
        new(_db.Albums,
            _db.ChatChannels,
            _crypto,
            _escrow,
            _db.Employees,
            chatAuth ?? CreateChatAuth(),
            NullLogger<AlbumService>.Instance,
            _fileTokenService,
            _mockFileService.Object,
            _db.ChatMessages);

    private ChatService CreateChatService(
        IChatAuthorizationService? chatAuth = null,
        IUserPrivacyService? privacy = null) =>
        new(_db.CommunicationContext,
            _db.HrContext,
            _db.ProjectsContext,
            _chatState,
            _notificationRouting,
            _presenceState,
            _mockFileService.Object,
            _mockModeration.Object,
            CreateAlbumService(chatAuth),
            NullLogger<ChatService>.Instance,
            chatAuth: chatAuth ?? CreateChatAuth(privacy),
            crypto: _crypto,
            privacyService: privacy,
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

    private PollService CreatePollService(IChatAuthorizationService? chatAuth = null) =>
        new(_db.CommunicationContext,
            _db.HrContext,
            _db.ProjectsContext,
            chatAuth: chatAuth ?? CreateChatAuth(),
            chatState: _chatState,
            crypto: _crypto,
            dataProtectionProvider: _dataProtection,
            coreContext: _db.CoreContext);

    private UserService CreateUserService(Employee caller)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("EmployeeId", caller.Id),
            new Claim(ClaimTypes.NameIdentifier, caller.Id),
            new Claim("sub", caller.Id)
        ], "TestAuth");
        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(identity)));
        return new UserService(mockAuth.Object, _db.Employees, _db.OpenIdAccounts, _db.CoreContext.CompanyProfile, _db.Teams);
    }

    // -------------------------------------------------------------------------
    // #106: Channel Creation, Team Targets & Encryption Toggle Authorization
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateChannel_EnforcesCreatorParticipantDiscoverabilityAndTeamProjectRules()
    {
        _db.Teams.Save(new Team { Id = "team-a", Name = "Alpha" });
        _db.Teams.Save(new Team { Id = "team-b", Name = "Beta" });
        _db.Projects.Save(new Project
        {
            Id = "proj-restricted",
            Name = "Restricted",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = ["owner-only"]
        });

        var alice = SaveEmployee("alice-106", teamId: "team-a", permissions: AppPermissions.Chat.Use);
        var bob = SaveEmployee("bob-106", teamId: "team-a", permissions: AppPermissions.Chat.Use);
        var noChat = SaveEmployee("nochat-106", teamId: "team-a");
        var creator = SaveEmployee("creator-106", teamId: "team-a", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.CreateChannels, AppPermissions.Projects.View]);

        var privacyMock = new Mock<IUserPrivacyService>();
        privacyMock.Setup(p => p.CanCreatePublicChannels(It.IsAny<Employee?>())).Returns(true);
        privacyMock.Setup(p => p.CanAccessPublicChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>())).Returns(true);
        privacyMock.Setup(p => p.FilterDiscoverableTeams(It.IsAny<Employee?>(), It.IsAny<IEnumerable<Team>>()))
            .Returns((Employee? _, IEnumerable<Team> teams) => teams.ToList());
        privacyMock.Setup(p => p.CanDiscoverUser(alice, bob)).Returns(false);
        privacyMock.Setup(p => p.CanDiscoverUser(creator, bob)).Returns(true);

        var chatAuth = CreateChatAuth(privacyMock.Object);
        var chatService = CreateChatService(chatAuth, privacyMock.Object);

        // Missing Chat.Use
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "dm-no-perm",
                ChannelType = ChatChannelType.Direct,
                CreatedById = noChat.Id,
                ParticipantIds = [noChat.Id, bob.Id]
            }));

        // Forged DM without creator in ParticipantIds
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "dm-forged",
                ChannelType = ChatChannelType.Direct,
                CreatedById = creator.Id,
                ParticipantIds = [alice.Id, bob.Id]
            }));

        // Nondiscoverable DM target rejected
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "dm-hidden-target",
                ChannelType = ChatChannelType.Direct,
                CreatedById = alice.Id,
                ParticipantIds = [alice.Id, bob.Id]
            }));

        // Inaccessible project rejected
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "chan-proj-denied",
                Name = "Secret",
                ChannelType = ChatChannelType.Project,
                LinkedEntityId = "proj-restricted",
                CreatedById = creator.Id
            }));

        // Nonexistent team rejected with ArgumentException
        Assert.Throws<ArgumentException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "chan-team-missing",
                Name = "Ghost Team",
                ChannelType = ChatChannelType.Team,
                AllowedTeamIds = ["team-nonexistent"],
                CreatedById = creator.Id
            }));

        // Team user does not belong to rejected
        Assert.Throws<UnauthorizedAccessException>(() =>
            chatService.CreateChannel(new ChatChannel
            {
                Id = "chan-team-other",
                Name = "Beta",
                ChannelType = ChatChannelType.Team,
                AllowedTeamIds = ["team-b"],
                CreatedById = creator.Id
            }));

        // Authorized Team & DM succeed
        var okTeam = chatService.CreateChannel(new ChatChannel
        {
            Id = "chan-team-ok",
            Name = "Alpha",
            ChannelType = ChatChannelType.Team,
            AllowedTeamIds = ["team-a"],
            CreatedById = creator.Id
        });
        Assert.Equal("chan-team-ok", okTeam.Id);

        var okDm = chatService.CreateChannel(new ChatChannel
        {
            Id = "dm-ok",
            ChannelType = ChatChannelType.Direct,
            CreatedById = creator.Id,
            ParticipantIds = [creator.Id, bob.Id]
        });
        Assert.Equal("dm-ok", okDm.Id);
    }

    [Fact]
    public async Task ToggleChannelEncryptionAsync_EnforcesManagerOrOneToOneDmCreator()
    {
        var member = SaveEmployee("member-106", encryptedPrivateKey: "enc-1", permissions: AppPermissions.Chat.Use);
        var creator = SaveEmployee("creator-106b", encryptedPrivateKey: "enc-2", permissions: AppPermissions.Chat.Use);
        var other = SaveEmployee("other-106", encryptedPrivateKey: "enc-3", permissions: AppPermissions.Chat.Use);

        var general = new ChatChannel
        {
            Id = "chan-gen-enc",
            Name = "General",
            ChannelType = ChatChannelType.General,
            CreatedById = creator.Id
        };
        _db.ChatChannels.Save(general);

        var dm = new ChatChannel
        {
            Id = "dm-enc-106",
            ChannelType = ChatChannelType.Direct,
            CreatedById = creator.Id,
            ParticipantIds = [creator.Id, member.Id]
        };
        _db.ChatChannels.Save(dm);

        var secService = CreateChatSecurityService();

        // Non-manager cannot toggle General channel encryption
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            secService.ToggleChannelEncryptionAsync(general.Id, true, member.Id, privateKeyContext: "priv"));

        // Non-creator DM participant cannot toggle DM encryption (only 1:1 creator or channel manager)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            secService.ToggleChannelEncryptionAsync(dm.Id, true, other.Id, privateKeyContext: "priv"));

        // 1:1 DM creator can enable encryption even without ManageChannels permission
        await secService.ToggleChannelEncryptionAsync(dm.Id, true, creator.Id, privateKeyContext: "priv");
        Assert.True(_db.ChatChannels.GetById(dm.Id)?.IsEncrypted);
    }

    // -------------------------------------------------------------------------
    // #107: Album Mention Validation, Scrub Revocation & Writable Album Filtering
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AlbumMentionLifecycle_StripsForbiddenPreservesNotFoundRevokesOnDeleteAndFiltersArchived()
    {
        var owner = SaveEmployee("owner-107", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);
        var noAlbumView = SaveEmployee("nonalbum-107", permissions: AppPermissions.Chat.Use);
        var outsider = SaveEmployee("outsider-107", permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);

        var channel = new ChatChannel { Id = "chan-107", Name = "General", ChannelType = ChatChannelType.General, CreatedById = owner.Id };
        _db.ChatChannels.Save(channel);

        var archivedChannel = new ChatChannel { Id = "chan-107-arch", Name = "Archived", ChannelType = ChatChannelType.General, CreatedById = owner.Id, IsArchived = true };
        _db.ChatChannels.Save(archivedChannel);

        var ownerAlbum = new Album { Id = "album-owned-107", Title = "Owned", OwnerId = owner.Id, IsEncrypted = false };
        var secretAlbum = new Album { Id = "album-secret-107", Title = "Private", OwnerId = outsider.Id, IsEncrypted = false };
        _db.Albums.Save(ownerAlbum);
        _db.Albums.Save(secretAlbum);

        var chatService = CreateChatService();
        var albumService = CreateAlbumService();

        // Missing album ID hits NOT_FOUND -> preserved in MentionedAlbumIds for unavailable placeholder
        var missingMention = await chatService.SendMessageAsync(owner.Id, channel.Id, "Look #[Missing](album:album-does-not-exist)");
        Assert.True(missingMention.IsSuccess);
        Assert.Contains("album-does-not-exist", missingMention.Value!.MentionedAlbumIds);

        // Caller lacking Albums.View hits FORBIDDEN -> scrubbed from MentionedAlbumIds
        var forbiddenByPerm = await chatService.SendMessageAsync(noAlbumView.Id, channel.Id, "Look #[Secret](album:album-secret-107)");
        Assert.True(forbiddenByPerm.IsSuccess);
        Assert.DoesNotContain("album-secret-107", forbiddenByPerm.Value!.MentionedAlbumIds);

        // Caller lacking album ownership/share hits FORBIDDEN -> scrubbed & NOT shared
        var forbiddenByOwner = await chatService.SendMessageAsync(owner.Id, channel.Id, "Look #[Secret](album:album-secret-107)");
        Assert.True(forbiddenByOwner.IsSuccess);
        Assert.DoesNotContain("album-secret-107", forbiddenByOwner.Value!.MentionedAlbumIds);
        Assert.DoesNotContain(channel.Id, _db.Albums.GetById("album-secret-107")!.SharedWithChannelIds);

        // Owner mentioning own album shares it with channel; deleting last mentioning message revokes share
        var sharedMsg = await chatService.SendMessageAsync(owner.Id, channel.Id, "Look #[Mine](album:album-owned-107)");
        Assert.True(sharedMsg.IsSuccess);
        Assert.Contains("album-owned-107", sharedMsg.Value!.MentionedAlbumIds);
        Assert.Contains(channel.Id, _db.Albums.GetById("album-owned-107")!.SharedWithChannelIds);

        var deleteRes = await chatService.DeleteMessageAsync(owner.Id, sharedMsg.Value.Id);
        Assert.True(deleteRes.IsSuccess);
        Assert.DoesNotContain(channel.Id, _db.Albums.GetById("album-owned-107")!.SharedWithChannelIds);

        // Archived or non-postable channels return no writable albums
        Assert.Single(albumService.GetWritableAlbumsForChannel(owner.Id, channel.Id));
        Assert.Empty(albumService.GetWritableAlbumsForChannel(owner.Id, archivedChannel.Id));
    }

    // -------------------------------------------------------------------------
    // #110: Chat Categories Authorization, sys_archive Pinning & Encrypted Abort
    // -------------------------------------------------------------------------

    [Fact]
    public void ChatCategories_EnforcePermissionsPinArchiveAndAbortDeleteOnLockedEncryptedChannel()
    {
        var manager = SaveEmployee("mgr-110", permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.EditAllPublicChannels]);
        var plainUser = SaveEmployee("plain-110", permissions: AppPermissions.Chat.Use);
        var suspendedMgr = SaveEmployee("susp-110", isSuspended: true, permissions: [AppPermissions.Chat.Use, AppPermissions.Chat.EditAllPublicChannels]);

        var sysArchive = new ChatCategory { Id = "sys_archive", Name = "Archive", DisplayOrder = 10000, IsSystem = true };
        var sysPublic = new ChatCategory { Id = "sys_public", Name = "Public", DisplayOrder = -600, IsSystem = true };
        var sysProjects = new ChatCategory { Id = "sys_projects", Name = "Projects", DisplayOrder = -500, IsSystem = true };
        _db.ChatCategories.Save(sysArchive);
        _db.ChatCategories.Save(sysPublic);
        _db.ChatCategories.Save(sysProjects);

        var chatService = CreateChatService();

        // Unauthorized and suspended users denied
        Assert.False(chatService.CreateCategory("Unauthorized", plainUser.Id).IsSuccess);
        Assert.False(chatService.CreateCategory("Suspended", suspendedMgr.Id).IsSuccess);

        // Authorized user creates category
        var created = chatService.CreateCategory("Engineering", manager.Id);
        Assert.True(created.IsSuccess);
        var customCat = created.Value!;

        // sys_archive cannot be moved away from DisplayOrder = 10000 and cannot be deleted
        Assert.True(chatService.UpdateCategoryOrders(new Dictionary<string, int>
        {
            ["sys_archive"] = 1,
            [customCat.Id] = -550
        }, manager.Id).IsSuccess);
        Assert.Equal(10000, _db.ChatCategories.GetById("sys_archive")!.DisplayOrder);
        Assert.Equal(-550, _db.ChatCategories.GetById(customCat.Id)!.DisplayOrder);
        Assert.False(chatService.DeleteCategory("sys_archive", manager.Id, isSessionUnlocked: true).IsSuccess);

        // Encrypted channel in category aborts DeleteCategory when vault is locked without partial uncategorization
        var ch1 = new ChatChannel { Id = "cat-ch-1", Name = "Unencrypted", ChannelType = ChatChannelType.General, CreatedById = manager.Id, CategoryId = customCat.Id };
        var ch2 = new ChatChannel { Id = "cat-ch-2", Name = "Encrypted", ChannelType = ChatChannelType.General, CreatedById = manager.Id, CategoryId = customCat.Id, IsEncrypted = true };
        _db.ChatChannels.Save(ch1);
        _db.ChatChannels.Save(ch2);

        var lockedDelete = chatService.DeleteCategory(customCat.Id, manager.Id, isSessionUnlocked: false);
        Assert.False(lockedDelete.IsSuccess);
        Assert.Equal("VAULT_LOCKED", lockedDelete.Code);
        Assert.NotNull(_db.ChatCategories.GetById(customCat.Id));
        Assert.Equal(customCat.Id, _db.ChatChannels.GetById(ch1.Id)!.CategoryId);

        // When unlocked by manager, category deletes and channels are cleanly uncategorized
        var unlockedDelete = chatService.DeleteCategory(customCat.Id, manager.Id, isSessionUnlocked: true);
        Assert.True(unlockedDelete.IsSuccess);
        Assert.Null(_db.ChatCategories.GetById(customCat.Id));
        Assert.Null(_db.ChatChannels.GetById(ch1.Id)!.CategoryId);
        Assert.Null(_db.ChatChannels.GetById(ch2.Id)!.CategoryId);
    }

    // -------------------------------------------------------------------------
    // #114: Anonymous Polls HMAC Ballot Anti-Stuffing & Zero Plaintext Leak
    // -------------------------------------------------------------------------

    [Fact]
    public void AnonymousPolls_SupportToggleMoveAddOptionZeroPlaintextAndStripTokensInClone()
    {
        var creator = SaveEmployee("creator-114", permissions: AppPermissions.Chat.Use);
        var voter = SaveEmployee("voter-114", permissions: AppPermissions.Chat.Use);
        var channel = new ChatChannel { Id = "chan-114", Name = "General", ChannelType = ChatChannelType.General, CreatedById = creator.Id };
        _db.ChatChannels.Save(channel);

        var pollService = CreatePollService();
        var created = pollService.CreatePoll(new ChatPoll
        {
            ChannelId = channel.Id,
            Question = "Best day?",
            IsAnonymous = true,
            AllowMultipleAnswers = false,
            AllowAddOptions = true,
            Options =
            [
                new ChatPollOption { Id = "opt-mon", Text = "Monday" },
                new ChatPollOption { Id = "opt-tue", Text = "Tuesday" }
            ]
        }, creator.Id);
        Assert.True(created.IsSuccess);
        var pollId = created.Value!.Id;

        // First vote -> opt-mon = 1
        var v1 = pollService.ToggleVote(pollId, "opt-mon", voter.Id);
        Assert.True(v1.IsSuccess);
        Assert.Equal(1, v1.Value!.Options.First(o => o.Id == "opt-mon").VoteCount);
        Assert.Equal(0, v1.Value.Options.First(o => o.Id == "opt-tue").VoteCount);
        Assert.Equal(["opt-mon"], pollService.GetUserVotedOptionIds(pollId, voter.Id).ToArray());

        // ClonePoll strips internal AnonymousVoterTokens while preserving AnonymousVoteCount
        Assert.All(v1.Value.Options, o =>
        {
            Assert.Empty(o.AnonymousVoterTokens);
            Assert.Empty(o.VoterIds);
        });

        // Switch vote on single-choice anonymous poll -> opt-mon = 0, opt-tue = 1 (total remains 1)
        var v2 = pollService.ToggleVote(pollId, "opt-tue", voter.Id);
        Assert.True(v2.IsSuccess);
        Assert.Equal(0, v2.Value!.Options.First(o => o.Id == "opt-mon").VoteCount);
        Assert.Equal(1, v2.Value.Options.First(o => o.Id == "opt-tue").VoteCount);
        Assert.Equal(1, v2.Value.TotalVotes);
        Assert.Equal(["opt-tue"], pollService.GetUserVotedOptionIds(pollId, voter.Id).ToArray());

        // AddParticipantOption on single-choice anonymous poll moves previous vote from opt-tue to new option
        var v3 = pollService.AddParticipantOption(pollId, voter.Id, "Wednesday");
        Assert.True(v3.IsSuccess);
        Assert.Equal(3, v3.Value!.Options.Count);
        Assert.Equal(0, v3.Value.Options.First(o => o.Id == "opt-tue").VoteCount);
        var wednesday = v3.Value.Options.First(o => o.Text == "Wednesday");
        Assert.Equal(1, wednesday.VoteCount);
        Assert.Null(wednesday.AddedByUserId);
        Assert.Equal(1, v3.Value.TotalVotes);

        // Toggle off -> 0 votes
        var v4 = pollService.ToggleVote(pollId, wednesday.Id, voter.Id);
        Assert.True(v4.IsSuccess);
        Assert.Equal(0, v4.Value!.TotalVotes);
        Assert.Empty(pollService.GetUserVotedOptionIds(pollId, voter.Id));

        // Raw persisted JSON contains zero plaintext voter IDs
        var persisted = _db.ChatPolls.GetById(pollId)!;
        Assert.All(persisted.Options, o =>
        {
            Assert.Empty(o.VoterIds);
            Assert.Null(o.AddedByUserId);
        });
    }

    // -------------------------------------------------------------------------
    // #117 & NV-9: Slideshow Pairing Vault/Suspension Checks & Unpaired Quota/Disconnect
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SlideshowHubAndSessions_EnforceVaultAndSuspensionRetireDuplicatesAndEndAllOnDisconnect()
    {
        var sessionService = new SlideshowSessionService();

        // Duplicate RequestPairingCode from same connection retires previous unpaired session
        var firstUnpaired = sessionService.CreateSession("tv-conn-dup", SlideshowDisplayKind.BlazorPinStage, creatorIp: "10.0.0.5");
        var secondUnpaired = sessionService.CreateSession("tv-conn-dup", SlideshowDisplayKind.BlazorPinStage, creatorIp: "10.0.0.5");
        Assert.Null(sessionService.GetSessionById(firstUnpaired.SessionId));
        Assert.Null(sessionService.GetSessionByPin(firstUnpaired.PairingPin));
        Assert.NotNull(sessionService.GetSessionById(secondUnpaired.SessionId));

        // Null/blank IP shares "__unknown__" per-IP cap (MaxUnpairedSessionsPerIp = 5)
        var nullIpSessions = new List<SlideshowSession>();
        for (int i = 0; i < 5; i++)
        {
            nullIpSessions.Add(sessionService.CreateSession($"tv-null-{i}", SlideshowDisplayKind.BlazorPinStage, creatorIp: null));
        }
        Assert.Throws<InvalidOperationException>(() =>
            sessionService.CreateSession("tv-null-6", SlideshowDisplayKind.BlazorPinStage, creatorIp: "   "));

        // HandleDisconnect ends all sessions sharing a host connection ID
        var presenter = SaveEmployee("presenter-117", permissions: AppPermissions.Albums.View);
        var suspPresenter = SaveEmployee("susp-117", isSuspended: true, permissions: AppPermissions.Albums.View);
        var lockedOwner = SaveEmployee("locked-117", encryptedPrivateKey: "enc-key", permissions: AppPermissions.Albums.View);

        var openAlbum = new Album { Id = "alb-open-117", Title = "Open", OwnerId = presenter.Id, IsEncrypted = false };
        var lockedAlbum = new Album { Id = "alb-locked-117", Title = "Locked", OwnerId = lockedOwner.Id, IsEncrypted = true, EncryptedAlbumKeys = new Dictionary<string, string> { [lockedOwner.Id] = "cipher-key" } };
        _db.Albums.Save(openAlbum);
        _db.Albums.Save(lockedAlbum);

        var s1 = sessionService.CreateDirectSession(presenter.Id, "shared-host-conn", openAlbum.Id);
        var s2 = sessionService.CreateDirectSession(presenter.Id, "shared-host-conn", openAlbum.Id);
        sessionService.HandleDisconnect("shared-host-conn");
        Assert.Null(sessionService.GetSessionById(s1.SessionId));
        Assert.Null(sessionService.GetSessionById(s2.SessionId));

        // SlideshowHub.PairWithTv rejects suspended user and locked encrypted album
        SlideshowHub BuildHub(Employee caller, ScopedKeystoreService keystore)
        {
            var hubContext = new Mock<HubCallerContext>();
            hubContext.Setup(c => c.ConnectionId).Returns("phone-conn-117");
            hubContext.Setup(c => c.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("EmployeeId", caller.Id),
                new Claim(ClaimTypes.NameIdentifier, caller.Id),
                new Claim("sub", caller.Id)
            ], "TestAuth")));

            return new SlideshowHub(
                sessionService,
                _db,
                CreateAlbumService(),
                CreateUserService(caller),
                keystore,
                _serviceProvider,
                NullLogger<SlideshowHub>.Instance)
            {
                Context = hubContext.Object,
                Clients = new Mock<IHubCallerClients>().Object,
                Groups = new Mock<IGroupManager>().Object
            };
        }

        var tvTarget = sessionService.CreateSession("tv-target-117", SlideshowDisplayKind.SignalRCastReceiver, "10.0.0.8");

        var suspHub = BuildHub(suspPresenter, new ScopedKeystoreService(_dataProtection, _crypto, hrContext: _db.HrContext));
        var suspResult = await suspHub.PairWithTv(tvTarget.PairingPin, openAlbum.Id);
        Assert.False(suspResult.IsSuccess);
        Assert.Equal("FORBIDDEN", suspResult.Code);

        var lockedHub = BuildHub(lockedOwner, new ScopedKeystoreService(_dataProtection, _crypto, hrContext: _db.HrContext));
        var lockedResult = await lockedHub.PairWithTv(tvTarget.PairingPin, lockedAlbum.Id);
        Assert.False(lockedResult.IsSuccess);
        Assert.Equal("VAULT_LOCKED", lockedResult.Code);
    }

    // -------------------------------------------------------------------------
    // #120: Suspension Vault Clearing, Cookie/Password Guards & Escrow/Sharing
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SuspensionAndVaultHardening_RevokeSessionVaultCookieBlockUnlockAndFilterRecipients()
    {
        var (vaultEmp, privKey, password) = SaveEmployeeWithRealVault(
            "emp-vault-120",
            "Correct-Vault-Pass-123!",
            isSuspended: false,
            permissions: [AppPermissions.Chat.Use, AppPermissions.Albums.View]);

        var sessionService = new SessionService(
            _db.CoreContext,
            _db.HrContext,
            globalKeystore: _globalKeystore,
            fileTokenService: _fileTokenService);

        var deviceSession = new DeviceSession
        {
            Id = "sess-120",
            EmployeeId = vaultEmp.Id,
            DeviceId = "dev-120",
            HasVaultCookie = true,
            VaultNonce = "initial-nonce-120",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _db.DeviceSessions.Save(deviceSession);
        _globalKeystore.UnlockVault(vaultEmp.Id, vaultEmp.EncryptedPrivateKey!, password, _crypto);
        Assert.True(_globalKeystore.IsUserUnlocked(vaultEmp.Id));

        // 1. Valid cookie unlocks active user
        var protectedToken = VaultCookieProtector.Protect(_dataProtection, vaultEmp.Id, deviceSession.Id, deviceSession.VaultNonce, password);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("SessionId", deviceSession.Id), new Claim("EmployeeId", vaultEmp.Id)], "TestAuth"))
        };
        httpContext.Request.Headers["X-Chat-Vault-Key"] = protectedToken;

        var activeKeystore = new ScopedKeystoreService(
            _dataProtection,
            _crypto,
            _globalKeystore,
            fileTokenService: _fileTokenService,
            deviceSessions: _db.DeviceSessions,
            sessionService: sessionService,
            hrContext: _db.HrContext);
        Assert.True(activeKeystore.TryInitializeFromVaultCookie(httpContext, vaultEmp.Id, vaultEmp.EncryptedPrivateKey!));

        // 2. Wrong password fails DecryptAes / <RSAKeyValue> validation without unlocking
        var badKeystore = new ScopedKeystoreService(
            _dataProtection,
            _crypto,
            _globalKeystore,
            deviceSessions: _db.DeviceSessions,
            sessionService: sessionService,
            hrContext: _db.HrContext);
        Assert.False(await badKeystore.UnlockWithPasswordAsync(new Mock<IJSRuntime>().Object, vaultEmp, "Wrong-Password!", rememberDevice: false));
        Assert.False(badKeystore.IsUnlocked);

        // 3. Suspending user clears HasVaultCookie, rotates VaultNonce, locks GlobalKeystore, and rejects cookie auto-unlock
        sessionService.NotifyUserSuspensionChanged(vaultEmp.Id, true);
        var updatedSession = _db.DeviceSessions.GetById(deviceSession.Id)!;
        Assert.False(updatedSession.HasVaultCookie);
        Assert.NotEqual("initial-nonce-120", updatedSession.VaultNonce);
        Assert.False(_globalKeystore.IsUserUnlocked(vaultEmp.Id));

        // Even if an old token/session is tested against a suspended employee in HrContext, live DB guard rejects auto-unlock
        vaultEmp.IsSuspended = true;
        _db.Employees.Save(vaultEmp);
        var freshSession = new DeviceSession
        {
            Id = "sess-120-susp",
            EmployeeId = vaultEmp.Id,
            DeviceId = "dev-120",
            HasVaultCookie = true,
            VaultNonce = "susp-nonce",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _db.DeviceSessions.Save(freshSession);
        var suspToken = VaultCookieProtector.Protect(_dataProtection, vaultEmp.Id, freshSession.Id, freshSession.VaultNonce, password);
        var suspHttp = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("SessionId", freshSession.Id), new Claim("EmployeeId", vaultEmp.Id)], "TestAuth"))
        };
        suspHttp.Request.Headers["X-Chat-Vault-Key"] = suspToken;

        var suspKeystore = new ScopedKeystoreService(
            _dataProtection,
            _crypto,
            _globalKeystore,
            deviceSessions: _db.DeviceSessions,
            sessionService: sessionService,
            hrContext: _db.HrContext);
        Assert.False(suspKeystore.TryInitializeFromVaultCookie(suspHttp, vaultEmp.Id, vaultEmp.EncryptedPrivateKey!));
        Assert.False(suspKeystore.IsUnlocked);

        // 4. ChatSecurityService ClaimEscrowedKeysAsync and ShareKeyOpportunisticallyAsync enforce suspension & Chat.Use
        var (suspRecipient, _, _) = SaveEmployeeWithRealVault("susp-rec-120", "Pass-120!", isSuspended: true, permissions: AppPermissions.Chat.Use);
        var noPermRecipient = SaveEmployeeWithRealVault("noperm-rec-120", "Pass-120!", isSuspended: false).Employee;
        var validCaller = SaveEmployeeWithRealVault("caller-120", "Pass-120!", isSuspended: false, permissions: AppPermissions.Chat.Use).Employee;

        var encChannel = new ChatChannel
        {
            Id = "chan-sec-120",
            Name = "Secure Room",
            ChannelType = ChatChannelType.General,
            IsEncrypted = true,
            CurrentKeyEpoch = 0,
            EncryptedChannelKeys = new Dictionary<string, string>
            {
                ["ServerMaster"] = _crypto.EncryptRsa(_crypto.GenerateAesKeyBase64(), _escrow.EscrowPublicKey!)
            }
        };
        _db.ChatChannels.Save(encChannel);

        var secService = CreateChatSecurityService();
        await secService.ClaimEscrowedKeysAsync(suspRecipient.Id);
        await secService.ClaimEscrowedKeysAsync(noPermRecipient.Id);
        Assert.False(_db.ChatChannels.GetById(encChannel.Id)!.EncryptedChannelKeys.ContainsKey(suspRecipient.Id));
        Assert.False(_db.ChatChannels.GetById(encChannel.Id)!.EncryptedChannelKeys.ContainsKey(noPermRecipient.Id));

        await secService.ShareKeyOpportunisticallyAsync(encChannel.Id, _crypto.GenerateAesKeyBase64(), 0, validCaller.Id);
        var afterShare = _db.ChatChannels.GetById(encChannel.Id)!;
        Assert.True(afterShare.EncryptedChannelKeys.ContainsKey(validCaller.Id));
        Assert.False(afterShare.EncryptedChannelKeys.ContainsKey(suspRecipient.Id));
        Assert.False(afterShare.EncryptedChannelKeys.ContainsKey(noPermRecipient.Id));
    }
}
