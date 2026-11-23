using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Security;

public class UserPrivacyAndSiloIsolationSecurityTests : TestDataTestBase
{
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly ProjectRepository _projects;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly ChatReadStateRepository _readStates;
    private readonly ReportedMessageRepository _reportedMessages;
    private readonly AlbumRepository _albums;
    private readonly UserPrivacyService _privacyService;

    public UserPrivacyAndSiloIsolationSecurityTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataPath,
                ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
            })
            .Build();

        _persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _serverConfigs = new ServerConfigRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _projects = new ProjectRepository(_persistence, _config);
        _openIdAccounts = new OpenIdAccountRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _messages = new ChatMessageRepository(_persistence, _config, _companyProfiles);
        _readStates = new ChatReadStateRepository(_persistence, _config, _companyProfiles);
        _reportedMessages = new ReportedMessageRepository(_persistence, _config);
        _albums = new AlbumRepository(_persistence, _config);

        _privacyService = new UserPrivacyService(_companyProfiles, _employees, _teams);
    }

    public override void Dispose()
    {
        _persistence.FlushAll();
        _persistence.Dispose();
        base.Dispose();
    }

    private UserService CreateUserService(Employee caller)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("EmployeeId", caller.Id),
            new Claim(ClaimTypes.NameIdentifier, caller.Id)
        ], "TestAuth"));
        var mockAuth = new Mock<AuthenticationStateProvider>();
        mockAuth.Setup(a => a.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        return new UserService(mockAuth.Object, _employees, _openIdAccounts, _companyProfiles, _teams, sessionService: null);
    }

    [Fact]
    public void UserPrivacyService_SuspendedAdminLosesAdminBypass_AndUserManagerCannotPierceStealthAdmin()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.ContactsOnly,
            AdminVisibility = AdminVisibilityMode.None,
            HidePublicChannelMemberList = true,
            RestrictAlbumAudienceVisibility = true,
            AllowNonAdminsCompanyWideCalendar = false
        };
        _companyProfiles.Save(profile);

        var suspendedAdmin = new Employee
        {
            Id = "admin-suspended",
            FirstName = "Suspended",
            LastName = "Admin",
            Email = "sus-admin@example.com",
            IsActive = true,
            IsAdmin = true,
            IsSuspended = true
        };
        var stealthAdmin = new Employee
        {
            Id = "admin-stealth",
            FirstName = "Stealth",
            LastName = "Root",
            Email = "stealth-root@example.com",
            IsActive = true,
            IsAdmin = true,
            IsEmailPublic = false
        };
        var userManager = new Employee
        {
            Id = "emp-user-mgr",
            FirstName = "Uma",
            LastName = "Manager",
            Email = "uma@example.com",
            IsActive = true,
            Permissions = [AppPermissions.Admin.ManageUsers, AppPermissions.Hr.ManageEmployees]
        };
        var regular = new Employee
        {
            Id = "emp-reg",
            FirstName = "Reg",
            LastName = "Ular",
            Email = "reg-private@example.com",
            IsActive = true,
            IsEmailPublic = false
        };

        _employees.Save(suspendedAdmin);
        _employees.Save(stealthAdmin);
        _employees.Save(userManager);
        _employees.Save(regular);

        var publicChannel = new ChatChannel { Id = "chan-pub", Name = "Announcements", ChannelType = ChatChannelType.General };
        var album = new Album { Id = "alb-1", Title = "Shared", OwnerId = stealthAdmin.Id };

        // Suspended admin loses IsEffectiveAdmin privileges -> cannot discover siloed peers, pierce stealth, or unhide lists
        Assert.False(_privacyService.CanDiscoverUser(suspendedAdmin, regular));
        Assert.False(_privacyService.CanDiscoverUser(suspendedAdmin, stealthAdmin));
        Assert.False(_privacyService.CanViewUserEmail(suspendedAdmin, regular));
        Assert.False(_privacyService.CanCreateCompanyWideEvents(suspendedAdmin));
        Assert.True(_privacyService.ShouldHideMemberList(publicChannel, suspendedAdmin));
        Assert.True(_privacyService.ShouldRestrictAlbumAudience(album, suspendedAdmin));

        // Delegated user/HR manager can manage/view non-admin directory users, NOT stealth admins (#113)
        Assert.True(_privacyService.CanDiscoverUser(userManager, regular));
        Assert.True(_privacyService.CanViewUserEmail(userManager, regular));
        Assert.False(_privacyService.CanDiscoverUser(userManager, stealthAdmin));
        Assert.False(_privacyService.CanViewUserEmail(userManager, stealthAdmin));
    }

    [Fact]
    public async Task SearchDiscoverableEmployeesAsync_And_ChatSearchSuggestionService_HidePrivateEmails()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.All,
            DiscoveryMethod = UserDiscoveryMethod.OpenSearch,
            AdminVisibility = AdminVisibilityMode.None,
            AllowDirectoryBrowsing = true
        };
        _companyProfiles.Save(profile);

        var viewer = new Employee { Id = "emp-viewer", FirstName = "Victor", LastName = "Viewer", Email = "viewer@example.com", IsActive = true };
        var hiddenEmailPeer = new Employee { Id = "emp-peer", FirstName = "Penny", LastName = "Peer", Email = "secret-penny-alias@example.com", IsActive = true, IsEmailPublic = false, Position = "Designer" };
        var publicEmailPeer = new Employee { Id = "emp-pub", FirstName = "Paul", LastName = "Public", Email = "public-paul@example.com", IsActive = true, IsEmailPublic = true };
        var stealthAdmin = new Employee { Id = "admin-hidden", FirstName = "Shadow", LastName = "Admin", Email = "shadow@example.com", IsActive = true, IsAdmin = true };

        _employees.Save(viewer);
        _employees.Save(hiddenEmailPeer);
        _employees.Save(publicEmailPeer);
        _employees.Save(stealthAdmin);

        var byPrivateEmail = (await _privacyService.SearchDiscoverableEmployeesAsync(viewer, "secret-penny-alias")).ToList();
        Assert.Empty(byPrivateEmail);

        var byPublicEmail = (await _privacyService.SearchDiscoverableEmployeesAsync(viewer, "public-paul")).ToList();
        Assert.Single(byPublicEmail);
        Assert.Equal(publicEmailPeer.Id, byPublicEmail[0].Id);

        var byStealthName = (await _privacyService.SearchDiscoverableEmployeesAsync(viewer, "Shadow")).ToList();
        Assert.Empty(byStealthName);

        var suggestionsByPrivateEmail = ChatSearchSuggestionService.GetSuggestions(
            "from:secret-penny-alias",
            22,
            viewer.Id,
            [viewer, hiddenEmailPeer, publicEmailPeer],
            emp => _privacyService.CanViewUserEmail(viewer, emp));
        Assert.DoesNotContain(suggestionsByPrivateEmail, s => s.Employee?.Id == hiddenEmailPeer.Id);
    }

    [Fact]
    public async Task ChatSecurityService_SearchMessagesAsync_HonorsPrivacyEmailAndAdminStealth()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.All,
            DiscoveryMethod = UserDiscoveryMethod.OpenSearch,
            AdminVisibility = AdminVisibilityMode.None
        };
        _companyProfiles.Save(profile);

        var viewer = new Employee { Id = "emp-v", FirstName = "Vera", LastName = "View", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        var privateEmailSender = new Employee { Id = "emp-s", FirstName = "Sam", LastName = "Secret", Email = "hidden-sam@corp.test", IsActive = true, IsEmailPublic = false, Permissions = [AppPermissions.Chat.Use] };
        var stealthAdmin = new Employee { Id = "emp-a", FirstName = "Root", LastName = "Op", Email = "root@corp.test", IsActive = true, IsAdmin = true, Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(viewer);
        _employees.Save(privateEmailSender);
        _employees.Save(stealthAdmin);

        var channel = new ChatChannel { Id = "chan-shared", Name = "Shared", ChannelType = ChatChannelType.General, ParticipantIds = [viewer.Id, privateEmailSender.Id, stealthAdmin.Id] };
        _channels.Save(channel);

        _messages.Save(new ChatMessage { Id = "m1", ChannelId = channel.Id, SenderId = privateEmailSender.Id, Content = "hello", SentAt = DateTime.UtcNow.AddMinutes(-2) });
        _messages.Save(new ChatMessage { Id = "m2", ChannelId = channel.Id, SenderId = stealthAdmin.Id, Content = "notice", SentAt = DateTime.UtcNow.AddMinutes(-1) });

        var mockCrypto = new Mock<ICryptoService>();
        var escrow = new ServerEscrowService(_companyProfiles, _serverConfigs, mockCrypto.Object);
        var mockChatAuth = new Mock<IChatAuthorizationService>();
        mockChatAuth.Setup(a => a.CanUserAccessChannel(It.IsAny<ChatChannel>(), It.IsAny<Employee>())).Returns(true);
        var albumService = new AlbumService(_albums, _channels, mockCrypto.Object, escrow, _employees, mockChatAuth.Object);

        var secService = new ChatSecurityService(
            _employees,
            _channels,
            _messages,
            escrow,
            mockCrypto.Object,
            albumService,
            new ChatStateService(),
            mockChatAuth.Object,
            new Mock<IFileService>().Object,
            NullLogger<ChatSecurityService>.Instance,
            _privacyService);

        var byHiddenEmail = await secService.SecureSearchMessagesAsync(channel.Id, "from:hidden-sam@corp.test", viewer.Id);
        Assert.Empty(byHiddenEmail);

        var byAdminAlias = await secService.SecureSearchMessagesAsync(channel.Id, "from:admin", viewer.Id);
        Assert.DoesNotContain(byAdminAlias, m => m.SenderId == stealthAdmin.Id);
    }

    [Fact]
    public void SearchService_EnforcesDirectoryBrowsingToggle_AndPrivateEmailFiltering()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.All,
            DiscoveryMethod = UserDiscoveryMethod.OpenSearch,
            AllowDirectoryBrowsing = true
        };
        _companyProfiles.Save(profile);

        var viewer = new Employee { Id = "emp-search-v", FirstName = "Nora", LastName = "Normal", Email = "nora@corp.test", IsActive = true };
        var peer = new Employee { Id = "emp-search-p", FirstName = "Evan", LastName = "HiddenMail", Email = "verysecret99@corp.test", IsActive = true, IsEmailPublic = false, Position = "QA" };
        _employees.Save(viewer);
        _employees.Save(peer);

        var userService = CreateUserService(viewer);
        var mockSeq = new Mock<SequenceService>(_config);
        var searchService = new SearchService(
            _projects,
            _employees,
            new QuoteRepository(_persistence, _config, mockSeq.Object, NullLogger<QuoteRepository>.Instance),
            new InvoiceRepository(_persistence, _config, mockSeq.Object),
            new PurchaseOrderRepository(_persistence, _config, mockSeq.Object),
            new ExpenseReportRepository(_persistence, _config, mockSeq.Object),
            new BoardCardRepository(_persistence, _config),
            new BoardRepository(_persistence, _config),
            _privacyService,
            userService);

        // Private email cannot be matched or shown in Subtitle (#43)
        Assert.Empty(searchService.Search("verysecret99"));
        var byName = searchService.Search("Evan HiddenMail");
        var empHit = Assert.Single(byName, r => r.Type == "Employee");
        Assert.DoesNotContain("verysecret99", empHit.Subtitle, StringComparison.OrdinalIgnoreCase);

        // Disabling directory browsing hides employee search hits completely for non-admins
        profile.Privacy.AllowDirectoryBrowsing = false;
        _companyProfiles.Save(profile);
        Assert.Empty(searchService.Search("Evan HiddenMail"));
    }

    [Fact]
    public async Task ChatService_MarkMessageAsReadAsync_SuppressesBroadcastWhenMemberListHidden()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig { HidePublicChannelMemberList = true };
        _companyProfiles.Save(profile);

        var reader = new Employee { Id = "emp-r", FirstName = "Rita", LastName = "Read", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        var sender = new Employee { Id = "emp-w", FirstName = "Walt", LastName = "Write", IsActive = true, Permissions = [AppPermissions.Chat.Use] };
        _employees.Save(reader);
        _employees.Save(sender);

        var channel = new ChatChannel { Id = "chan-pub-read", Name = "News", ChannelType = ChatChannelType.General };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "msg-read-1", ChannelId = channel.Id, SenderId = sender.Id, Content = "Update", SentAt = DateTime.UtcNow };
        _messages.Save(msg);

        var chatState = new ChatStateService();
        var voiceState = new VoiceStateService();
        var mockCrypto = new Mock<ICryptoService>();
        var escrow = new ServerEscrowService(_companyProfiles, _serverConfigs, mockCrypto.Object);
        var albumService = new AlbumService(_albums, _channels, mockCrypto.Object, escrow, _employees, new ChatAuthorizationService(_channels, _projects, _teams, _employees, _privacyService));

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ChatChannels).Returns(_channels);
        commContext.Setup(c => c.ChatMessages).Returns(_messages);
        commContext.Setup(c => c.ChatReadStates).Returns(_readStates);
        commContext.Setup(c => c.ReportedMessages).Returns(_reportedMessages);

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);
        hrContext.Setup(h => h.Teams).Returns(_teams);

        var projContext = new Mock<ProjectsContext>();
        projContext.Setup(p => p.Projects).Returns(_projects);

        var coreContext = new Mock<CoreContext>();
        coreContext.Setup(c => c.CompanyProfile).Returns(_companyProfiles);

        var mockChatAccess = new Mock<IChatChannelAccessService>();
        var routing = new NotificationRoutingService(
            commContext.Object,
            hrContext.Object,
            coreContext.Object,
            new Mock<IWebPushService>().Object,
            new NotificationQueueService(NullLogger<NotificationQueueService>.Instance, _persistence, _config, _companyProfiles),
            new GlobalKeystoreService(),
            mockCrypto.Object,
            new Mock<IDataProtectionProvider>().Object,
            mockChatAccess.Object,
            logger: NullLogger<NotificationRoutingService>.Instance);

        var chatService = new ChatService(
            commContext.Object,
            hrContext.Object,
            projContext.Object,
            chatState,
            routing,
            new PresenceStateService(voiceState),
            new Mock<IFileService>().Object,
            new Mock<IContentModerationService>().Object,
            albumService,
            NullLogger<ChatService>.Instance,
            privacyService: _privacyService);

        bool localReadFired = false;
        chatState.MessageRead += (_, _, _) => localReadFired = true;

        await chatService.MarkMessageAsReadAsync(reader.Id, channel.Id, msg.Id);

        Assert.True(localReadFired);
        Assert.True(_privacyService.ShouldHideMemberList(channel, null));
    }

    [Fact]
    public async Task GlobalVoiceService_RejectsUnverifiedWebRtcParticipantSlotsAndSpeakers()
    {
        var jsMock = new Mock<IJSRuntime>();
        var voiceModuleMock = new Mock<IJSObjectReference>();
        voiceModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
        voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
        voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAndroidDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
        jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>())).Returns(new ValueTask<IJSObjectReference>(voiceModuleMock.Object));

        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var voiceChannelMock = new Mock<VoiceChannelService>();
        voiceChannelMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ch, uid, _) => voiceState.NotifyVoiceMemberJoined(ch, uid))
            .Returns(Task.CompletedTask);
        voiceChannelMock.Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var jsInterop = new VoiceJsInteropService(jsMock.Object);
        var deviceService = new VoiceDeviceService(jsInterop, NullLogger<VoiceDeviceService>.Instance);
        await using var voiceService = new GlobalVoiceService(
            jsInterop,
            voiceChannelMock.Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance,
            deviceService);

        await voiceService.ConnectAsync(new VoiceConnectionOptions("token", "url", "voice-ch-1", "self-1", 64, "rnnoise"));
        voiceState.NotifyVoiceMemberJoined("voice-ch-1", "verified-peer");

        voiceService.OnRoomConnected(["verified-peer", "spoofed-peer"]);
        voiceService.OnParticipantConnected("spoofed-joiner");
        voiceService.EnsureParticipantSlot("spoofed-slot");

        // Server-authoritative speaker state filters out unverified participants,
        // and local JS callback (OnLocalSpeakingChanged(bool)) structurally cannot spoof remote IDs.
        voiceState.SetUserSpeaking("voice-ch-1", "verified-peer", true);
        voiceState.SetUserSpeaking("voice-ch-1", "spoofed-speaker", true);
        voiceService.OnLocalSpeakingChanged(true);

        Assert.Contains("verified-peer", voiceService.CallParticipants);
        Assert.DoesNotContain("spoofed-peer", voiceService.CallParticipants);
        Assert.DoesNotContain("spoofed-joiner", voiceService.CallParticipants);
        Assert.DoesNotContain("spoofed-slot", voiceService.CallParticipants);
        Assert.Contains("verified-peer", voiceService.ActiveSpeakers);
        Assert.Contains("self-1", voiceService.ActiveSpeakers);
        Assert.DoesNotContain("spoofed-speaker", voiceService.ActiveSpeakers);
    }

    [Fact]
    public async Task ModerationReportService_RedactsUndiscoverableReporterAndResolverForViewer()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig
        {
            BaseScope = NetworkScope.SameTeam,
            AdminVisibility = AdminVisibilityMode.None
        };
        _companyProfiles.Save(profile);

        var teamMod = new Team { Id = "team-mod", Name = "Mods" };
        var teamReporter = new Team { Id = "team-rep", Name = "Other" };
        _teams.Save(teamMod);
        _teams.Save(teamReporter);

        var mod = new Employee
        {
            Id = "emp-mod",
            FirstName = "Morgan",
            LastName = "Mod",
            TeamId = teamMod.Id,
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use, AppPermissions.Chat.Moderator]
        };
        var stealthAdmin = new Employee
        {
            Id = "emp-stealth",
            FirstName = "Silent",
            LastName = "Admin",
            IsActive = true,
            IsAdmin = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        var reporter = new Employee
        {
            Id = "emp-rep",
            FirstName = "Rory",
            LastName = "Hidden",
            TeamId = teamReporter.Id,
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        var offender = new Employee
        {
            Id = "emp-off",
            FirstName = "Oscar",
            LastName = "SameTeam",
            TeamId = teamMod.Id,
            IsActive = true,
            Permissions = [AppPermissions.Chat.Use]
        };
        _employees.Save(mod);
        _employees.Save(stealthAdmin);
        _employees.Save(reporter);
        _employees.Save(offender);

        var ch = new ChatChannel { Id = "chan-g", Name = "General", ChannelType = ChatChannelType.General };
        _channels.Save(ch);
        var msg = new ChatMessage { Id = "msg-1", ChannelId = ch.Id, SenderId = offender.Id, Content = "Bad words", SentAt = DateTime.UtcNow };
        _messages.Save(msg);

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ReportedMessages).Returns(_reportedMessages);
        commContext.Setup(c => c.ChatChannels).Returns(_channels);
        commContext.Setup(c => c.ChatMessages).Returns(_messages);

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);

        var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees, _privacyService);
        var reportService = new ModerationReportService(
            commContext.Object,
            hrContext.Object,
            new Mock<IChatService>().Object,
            new ChatStateService(),
            chatAuth: chatAuth,
            privacyService: _privacyService);

        var submit = await reportService.SubmitReportAsync(
            new ReportedMessage { MessageId = msg.Id, Reason = "Abuse" },
            reporter.Id);
        Assert.True(submit.IsSuccess);

        var itemsForMod = reportService.GetReportItems(ModerationQueueTab.Open, unknownUserLabel: "Member", viewerEmployeeId: mod.Id);
        var openItem = Assert.Single(itemsForMod);
        Assert.Equal(string.Empty, openItem.Report.ReporterId);
        Assert.Equal("Member", openItem.ReporterName);
        Assert.Equal(offender.Id, openItem.Report.ReportedUserId);
        Assert.Equal(offender.FullName, openItem.ReportedUserName);

        // Stealth admin resolves the report -> non-admin moderator sees "Moderator", not stealth admin's identity
        var dismiss = reportService.DismissReport(openItem.Report.Id, stealthAdmin.Id);
        Assert.True(dismiss.IsSuccess);

        var historyForMod = reportService.GetReportItems(ModerationQueueTab.History, viewerEmployeeId: mod.Id);
        var historyItem = Assert.Single(historyForMod);
        Assert.Equal(string.Empty, historyItem.Report.ResolvedByEmployeeId);
        Assert.Equal("Moderator", historyItem.Report.ResolvedBy);
    }

    [Fact]
    public void UserPrivacyService_RestrictsAlbumAudience_And_UserServiceEnforcesProjectVisibility()
    {
        var profile = _companyProfiles.Get() ?? new CompanyProfile();
        profile.Privacy = new PrivacyConfig { RestrictAlbumAudienceVisibility = true };
        _companyProfiles.Save(profile);

        var owner = new Employee { Id = "emp-own", FirstName = "Owen", LastName = "Owner", IsActive = true };
        var viewer = new Employee { Id = "emp-view", FirstName = "Vera", LastName = "Viewer", IsActive = true };
        _employees.Save(owner);
        _employees.Save(viewer);

        var album = new Album { Id = "alb-sec", Title = "Photos", OwnerId = owner.Id };
        Assert.True(_privacyService.ShouldRestrictAlbumAudience(album, viewer));
        Assert.False(_privacyService.ShouldRestrictAlbumAudience(album, owner));

        var project = new Project
        {
            Id = "proj-priv",
            Name = "Confidential",
            ProjectNumber = "CONF-1",
            IsActive = true,
            AccessPolicy = ProjectAccessPolicy.Restricted
        };
        _projects.Save(project);

        var userService = CreateUserService(viewer);
        Assert.False(userService.CanViewProject(viewer, project));
        Assert.False(userService.CanViewProject(null, project));
    }
}
