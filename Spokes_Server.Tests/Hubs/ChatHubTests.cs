using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Hubs;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Helpers;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using Spokes_Server.Aggregate.Contexts;

namespace Spokes_Server.Tests.Hubs;

public class ChatHubTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly ChatChannelRepository _channels;
    private readonly ChatMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly ProjectRepository _projects;
    private readonly OpenIdAccountRepository _openIdAccounts;
    private readonly TeamRepository _teams;
    private readonly ChatStateService _chatState;
    private readonly ChatService _chatService;
    private readonly Mock<IContentModerationService> _mockModeration;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Mock<HubCallerContext> _mockContext;

    public ChatHubTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_ChatHub_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _channels = new ChatChannelRepository(_writer, mockConfig.Object);
        var companyProfiles = new CompanyProfileRepository(_writer, mockConfig.Object);
        _messages = new ChatMessageRepository(_writer, mockConfig.Object, companyProfiles);
        _employees = new EmployeeRepository(_writer, mockConfig.Object);
        _projects = new ProjectRepository(_writer, mockConfig.Object);
        _openIdAccounts = new OpenIdAccountRepository(_writer, mockConfig.Object);
        _teams = new TeamRepository(_writer, mockConfig.Object);

        // Setup SignalR Mocks
        _mockClients = new Mock<IHubCallerClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockGroups = new Mock<IGroupManager>();
        _mockContext = new Mock<HubCallerContext>();

        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockContext.Setup(c => c.ConnectionId).Returns("test-connection");

        var mockHubContext = new Mock<IHubContext<ChatHub>>();
        var mockHubClients = new Mock<IHubClients>();
        mockHubContext.Setup(h => h.Clients).Returns(mockHubClients.Object);
        mockHubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        var readStates = new ChatReadStateRepository(_writer, mockConfig.Object, companyProfiles);
        _chatState = new ChatStateService();
        var albums = new AlbumRepository(_writer, mockConfig.Object);
        var systemConfigRepo = new SystemConfigRepository(_writer, mockConfig.Object);
        var serverConfigs = new ServerConfigRepository(_writer, mockConfig.Object);
        var escrow = new ServerEscrowService(companyProfiles, serverConfigs, new Mock<ICryptoService>().Object);
        var notificationQueue = new NotificationQueueService(new Mock<ILogger<NotificationQueueService>>().Object, _writer, mockConfig.Object, companyProfiles);
        var mockWebPush = new Mock<IWebPushService>();

        var mockChatAccess = new Mock<IChatChannelAccessService>();
        mockChatAccess.Setup(c => c.GetChannelsForUser(It.IsAny<string>())).Returns((string userId) => _channels.GetAll().ToList());
        mockChatAccess.Setup(c => c.GetUsersForChannel(It.IsAny<string>())).Returns((string channelId) => new List<string> { "u1", "u2" });

        var commContext = new Mock<CommunicationContext>();
        commContext.Setup(c => c.ChatChannels).Returns(_channels);
        commContext.Setup(c => c.ChatMessages).Returns(_messages);
        commContext.Setup(c => c.ChatReadStates).Returns(readStates);
        commContext.Setup(c => c.EmailFolders).Returns(new EmailFolderRepository(_writer, mockConfig.Object));
        commContext.Setup(c => c.EmailMessages).Returns(new EmailMessageRepository(_writer, mockConfig.Object));

        var hrContext = new Mock<HrContext>();
        hrContext.Setup(h => h.Employees).Returns(_employees);
        hrContext.Setup(h => h.Teams).Returns(_teams);

        var projectsContext = new Mock<ProjectsContext>();
        projectsContext.Setup(p => p.Projects).Returns(_projects);

        var coreContext = new Mock<CoreContext>();
        coreContext.Setup(c => c.CompanyProfile).Returns(companyProfiles);

        var notificationRouting = new NotificationRoutingService(
            commContext.Object,
            hrContext.Object,
            projectsContext.Object,
            coreContext.Object,
            mockWebPush.Object,
            notificationQueue,
            new GlobalKeystoreService(),
            new Mock<ICryptoService>().Object,
            new Mock<IDataProtectionProvider>().Object,
            mockChatAccess.Object,
            logger: new Mock<ILogger<NotificationRoutingService>>().Object);

        _mockModeration = new Mock<IContentModerationService>();
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>()))
                       .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));
        _mockModeration.Setup(m => m.EvaluateTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync((false, string.Empty, TextModerationAction.Sanitize));

        _chatService = new ChatService(
            commContext.Object,
            hrContext.Object,
            projectsContext.Object,
            new ChatRealtimeGateway(mockHubContext.Object),
            _chatState,
            notificationRouting,
            new Mock<PresenceStateService>(_chatState).Object,
            new Mock<IFileService>().Object,
            _mockModeration.Object,
            new AlbumService(albums, _channels, new Mock<ICryptoService>().Object, escrow, _employees),
            new MarkdownSanitizerService(),
            new Mock<ILogger<ChatService>>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    private ChatHub CreateHub(string? userId = "u1")
    {
        if (userId != null)
        {
            var claims = new[] { new Claim("sub", userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var principal = new ClaimsPrincipal(identity);
            _mockContext.Setup(c => c.User).Returns(principal);
        }
        else
        {
            _mockContext.Setup(c => c.User).Returns((ClaimsPrincipal?)null);
        }

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
        var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees);
        return new ChatHub(_channels, userService, new Mock<ILogger<ChatHub>>().Object, chatAuth, _chatService)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = _mockGroups.Object
        };
    }

    private (Employee employee, ChatHub hub) CreateAuthenticatedHub(
        string employeeId = "e_test",
        string sub = "sub_test",
        bool isAdmin = false,
        string? teamId = null,
        string firstName = "Test",
        string lastName = "User",
        List<string>? permissions = null)
    {
        var emp = new Employee
        {
            Id = employeeId,
            FirstName = firstName,
            LastName = lastName,
            IsAdmin = isAdmin,
            TeamId = teamId,
            Permissions = permissions ?? new List<string>()
        };
        _employees.Save(emp);
        _openIdAccounts.Save(new OpenIdAccount { Sub = sub, LinkedEmployeeId = employeeId });
        var hub = CreateHub(sub);
        return (emp, hub);
    }


    [Fact]
    public async Task OnConnectedAsync_AddsUserToGroup()
    {
        _employees.Save(new Employee { Id = "e123", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "user123", LinkedEmployeeId = "e123" });
        var hub = CreateHub("user123");

        await hub.OnConnectedAsync();

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", "user_e123", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_AddsToChannelGroup()
    {
        _employees.Save(new Employee { Id = "e_join", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "u_join", LinkedEmployeeId = "e_join" });
        _channels.Save(new ChatChannel { Id = "c1" });
        var hub = CreateHub("u_join");

        await hub.JoinChannelAsync("c1");

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", "channel_c1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LeaveChannel_RemovesFromChannelGroup()
    {
        var hub = CreateHub();

        await hub.LeaveChannelAsync("c1");

        _mockGroups.Verify(g => g.RemoveFromGroupAsync("test-connection", "channel_c1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_SavesAndBroadcasts()
    {
        _employees.Save(new Employee { Id = "e1", FirstName = "Test", LastName = "User", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "user123", LinkedEmployeeId = "e1" });
        _channels.Save(new ChatChannel { Id = "c1", ChannelType = "Public" });

        var hub = CreateHub("user123");

        await hub.SendMessageAsync("c1", "Hello world!");

        var savedMsg = Assert.Single(_messages.GetAll());
        Assert.Equal("Hello world!", savedMsg.Content);
        Assert.Equal("e1", savedMsg.SenderId);

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendMessage_PersistenceFailure_ThrowsHubExceptionAndDoesNotBroadcast()
    {
        _employees.Save(new Employee { Id = "e_fail", FirstName = "Fail", LastName = "User", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "user_fail", LinkedEmployeeId = "e_fail" });
        _channels.Save(new ChatChannel { Id = "c_fail", ChannelType = "Public" });

        var mockConfig = new Mock<IConfiguration>();
        var companyProfiles = new CompanyProfileRepository(_writer, mockConfig.Object);
        var throwingRepo = new Mock<ChatMessageRepository>(_writer, mockConfig.Object, companyProfiles);
        throwingRepo.Setup(r => r.Save(It.IsAny<ChatMessage>()))
            .Throws(new IOException("Disk write failure"));

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
        var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees);

        var mockChatService = new Mock<IChatService>();
        mockChatService.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult<ChatMessage>.Failure("Failed to save message to persistence: channel not found or access denied."));

        var hub = new ChatHub(_channels, userService, new Mock<ILogger<ChatHub>>().Object, chatAuth, mockChatService.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = _mockGroups.Object
        };

        var claims = new[] { new Claim("sub", "user_fail") };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _mockContext.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync("c_fail", "Failed msg"));
        Assert.Contains("Failed to save message to persistence", ex.Message);

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddReaction_AddsAndBroadcasts()
    {
        _employees.Save(new Employee { Id = "e4", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "u_rea", LinkedEmployeeId = "e4" });
        _channels.Save(new ChatChannel { Id = "c4" });
        var msg = new ChatMessage { Id = "m3", ChannelId = "c4" };
        _messages.Save(msg);

        var hub = CreateHub("u_rea");

        await hub.AddReactionAsync("m3", "👍");

        var updated = _messages.GetById("m3");
        Assert.NotNull(updated);
        Assert.Contains("👍:e4", updated.Reactions);

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("ReactionAdded", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveReaction_RemovesAndBroadcasts()
    {
        _employees.Save(new Employee { Id = "e5", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "u_rem", LinkedEmployeeId = "e5" });
        _channels.Save(new ChatChannel { Id = "c5" });
        var msg = new ChatMessage { Id = "m4", ChannelId = "c5" };
        msg.Reactions.Add("❤️:e5");
        _messages.Save(msg);

        var hub = CreateHub("u_rem");

        await hub.RemoveReactionAsync("m4", "❤️");

        var updated = _messages.GetById("m4");
        Assert.NotNull(updated);
        Assert.Empty(updated.Reactions);

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("ReactionRemoved", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TypingIndicators_BroadcastToOthers()
    {
        _employees.Save(new Employee { Id = "e6", IsAdmin = true });
        _openIdAccounts.Save(new OpenIdAccount { Sub = "u_type", LinkedEmployeeId = "e6" });
        _channels.Save(new ChatChannel { Id = "c6" });

        string? typingUser = null;
        string? typingChannel = null;
        _chatState.UserTyping += (c, u, _) => { typingChannel = c; typingUser = u; };

        string? stoppedUser = null;
        string? stoppedChannel = null;
        _chatState.UserStoppedTyping += (c, u) => { stoppedChannel = c; stoppedUser = u; };

        var hub = CreateHub("u_type");

        await hub.StartTypingAsync("c6");
        _mockClientProxy.Verify(c => c.SendCoreAsync("UserTyping", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("e6", typingUser);
        Assert.Equal("c6", typingChannel);

        await hub.StopTypingAsync("c6");
        _mockClientProxy.Verify(c => c.SendCoreAsync("UserStoppedTyping", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("e6", stoppedUser);
        Assert.Equal("c6", stoppedChannel);
    }

    #region OnConnectedAsync & OnDisconnectedAsync Tests

    [Fact]
    public async Task OnConnectedAsync_UnauthenticatedUser_DoesNotAddToGroup()
    {
        var hub = CreateHub(null);

        await hub.OnConnectedAsync();

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_UnknownEmployeeSub_DoesNotAddToGroup()
    {
        var hub = CreateHub("nonexistent_sub");

        await hub.OnConnectedAsync();

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnDisconnectedAsync_CompletesSuccessfully()
    {
        var (_, hub) = CreateAuthenticatedHub("e_disc", "u_disc", isAdmin: true);

        var exception = await Record.ExceptionAsync(() => hub.OnDisconnectedAsync(null));

        Assert.Null(exception);
    }

    #endregion

    #region JoinChannel Access Tests

    [Fact]
    public async Task JoinChannel_UnauthenticatedUser_DoesNotAddToGroup()
    {
        var hub = CreateHub(null);
        _channels.Save(new ChatChannel { Id = "c_pub", ChannelType = ChatChannelType.General, IsDefaultGeneral = true });

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync("c_pub"));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_ChannelNotFound_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_notfound", "u_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync("nonexistent_channel"));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_DirectChannel_UserNotInParticipantIds_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_dm_out", "u_dm_out", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_restricted",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user_1", "user_2" }
        };
        _channels.Save(dm);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(dm.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_DirectChannel_UserInParticipantIds_AddsToGroup()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_dm_in", "u_dm_in", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_allowed",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { emp.Id, "user_other" }
        };
        _channels.Save(dm);

        await hub.JoinChannelAsync(dm.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{dm.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_TeamChannel_UserTeamMatches_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_team_ok", "u_team_ok", teamId: "team_alpha");
        var channel = new ChatChannel
        {
            Id = "c_team_match",
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = "team_alpha"
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_TeamChannel_UserTeamDoesNotMatch_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_team_mismatch", "u_team_mismatch", teamId: "team_beta");
        var channel = new ChatChannel
        {
            Id = "c_team_other",
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = "team_alpha"
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_RestrictedGeneralChannel_UserInParticipantIds_AddsToGroup()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_gen_part", "u_gen_part");
        var channel = new ChatChannel
        {
            Id = "c_gen_part",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string> { emp.Id }
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_RestrictedGeneralChannel_UserTeamAllowed_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_gen_team", "u_gen_team", teamId: "team_allowed");
        var channel = new ChatChannel
        {
            Id = "c_gen_team",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            AllowedTeamIds = new List<string> { "team_allowed" }
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_RestrictedGeneralChannel_UserNotAllowed_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_gen_denied", "u_gen_denied", teamId: "team_denied");
        var channel = new ChatChannel
        {
            Id = "c_gen_denied",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string> { "other_user" },
            AllowedTeamIds = new List<string> { "team_other" }
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_PublicGeneralChannel_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_gen_pub", "u_gen_pub");
        var channel = new ChatChannel
        {
            Id = "c_gen_pub",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string>(),
            AllowedTeamIds = new List<string>()
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_DefaultGeneralChannel_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_gen_def", "u_gen_def");
        var channel = new ChatChannel
        {
            Id = "c_gen_def",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_LinkedEntityNull_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_proj_null", "u_proj_null");
        var channel = new ChatChannel
        {
            Id = "c_proj_null",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = null
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_ProjectNotFound_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_proj_notfound", "u_proj_notfound");
        var channel = new ChatChannel
        {
            Id = "c_proj_notfound",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = "p_missing"
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_PublicAccessPolicy_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_proj_pub", "u_proj_pub", permissions: [AppPermissions.Projects.View]);
        var project = new Project { Id = "p_pub", AccessPolicy = ProjectAccessPolicy.Public };
        _projects.Save(project);

        var channel = new ChatChannel
        {
            Id = "c_proj_pub",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_RestrictedUserInAllowedUsers_AddsToGroup()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_proj_allowed_u", "u_proj_allowed_u", permissions: [AppPermissions.Projects.View]);
        var project = new Project
        {
            Id = "p_res_user",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = new List<string> { emp.Id }
        };
        _projects.Save(project);

        var channel = new ChatChannel
        {
            Id = "c_proj_user",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_RestrictedUserTeamInAllowedTeams_AddsToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_proj_team", "u_proj_team", teamId: "team_allowed_proj", permissions: [AppPermissions.Projects.View]);
        var project = new Project
        {
            Id = "p_res_team",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedTeamIds = new List<string> { "team_allowed_proj" }
        };
        _projects.Save(project);

        var channel = new ChatChannel
        {
            Id = "c_proj_team",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_ProjectChannel_LacksProjectsViewPermission_ThrowsHubException()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_proj_no_perm", "u_proj_no_perm", permissions: []);
        var project = new Project
        {
            Id = "p_no_perm_chat",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = [emp.Id]
        };
        _projects.Save(project);

        var channel = new ChatChannel
        {
            Id = "c_proj_no_perm",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));
    }


    [Fact]
    public async Task JoinChannel_ProjectChannel_RestrictedUserNotAllowed_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_proj_denied", "u_proj_denied", teamId: "team_other");
        var project = new Project
        {
            Id = "p_res_denied",
            AccessPolicy = ProjectAccessPolicy.Restricted,
            AllowedUserIds = new List<string> { "other_user" },
            AllowedTeamIds = new List<string> { "team_secret" }
        };
        _projects.Save(project);

        var channel = new ChatChannel
        {
            Id = "c_proj_denied",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = project.Id
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_AdminUser_CannotAccessDirectMessage_WhenNotParticipant()
    {
        var (_, hub) = CreateAuthenticatedHub("e_admin_bypass", "u_admin_bypass", isAdmin: true);
        var dm = new ChatChannel
        {
            Id = "c_dm_private",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user_1", "user_2" }
        };
        _channels.Save(dm);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(dm.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JoinChannel_AdminUser_CanAccessRestrictedGeneralChannel()
    {
        var (_, hub) = CreateAuthenticatedHub("e_admin_bypass", "u_admin_bypass", isAdmin: true);
        var channel = new ChatChannel
        {
            Id = "c_restricted_general",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = false,
            ParticipantIds = new List<string> { "user_1", "user_2" }
        };
        _channels.Save(channel);

        await hub.JoinChannelAsync(channel.Id);

        _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", $"channel_{channel.Id}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinChannel_UnknownChannelType_DoesNotAddToGroup()
    {
        var (_, hub) = CreateAuthenticatedHub("e_unknown_type", "u_unknown_type");
        var channel = new ChatChannel
        {
            Id = "c_unknown_type",
            ChannelType = "InvalidType"
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync(channel.Id));

        _mockGroups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region SendMessage Tests

    [Fact]
    public async Task SendMessage_UnauthenticatedUser_DoesNothing()
    {
        var hub = CreateHub(null);
        var channel = new ChatChannel { Id = "c_send_unauth", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync(channel.Id, "Hello"));

        Assert.Empty(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_ChannelNotFound_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_send_notfound", "u_send_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync("missing_channel", "Hello"));

        Assert.Empty(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_UserDeniedReadAccess_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_send_denied", "u_send_denied", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_send_dm_denied",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "u1", "u2" }
        };
        _channels.Save(dm);

        await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync(dm.Id, "Hello direct"));

        Assert.Empty(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_UserDeniedPostAccessOnAnnouncementChannel_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_send_ann_denied", "u_send_ann_denied", isAdmin: false);
        var channel = new ChatChannel
        {
            Id = "c_ann_denied",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "other_user" }
        };
        _channels.Save(channel);

        await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync(channel.Id, "Unauthorized announcement"));

        Assert.Empty(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendMessage_AnnouncementChannel_AllowedUser_SavesAndBroadcasts()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_ann_allowed", "u_ann_allowed", isAdmin: false);
        var channel = new ChatChannel
        {
            Id = "c_ann_allowed",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { emp.Id }
        };
        _channels.Save(channel);

        await hub.SendMessageAsync(channel.Id, "Authorized announcement");

        Assert.Single(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_AnnouncementChannel_LeaderOfAllowedTeam_SavesAndBroadcasts()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_team_leader", "u_team_leader", isAdmin: false);
        var team = new Team { Id = "team_lead_post", LeaderId = emp.Id };
        _teams.Save(team);

        var channel = new ChatChannel
        {
            Id = "c_ann_leader",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostTeamIds = new List<string> { team.Id }
        };
        _channels.Save(channel);

        await hub.SendMessageAsync(channel.Id, "Leader announcement");

        Assert.Single(_messages.GetAll());
        _mockClientProxy.Verify(c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_AutoMarksPreviousUnreadMessagesAsReadBySender()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_sender_read", "u_sender_read");
        var channel = new ChatChannel
        {
            Id = "c_read_marker",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var prevMsg = new ChatMessage
        {
            Id = "m_prev_unread",
            ChannelId = channel.Id,
            SenderId = "other_sender",
            Content = "Unread earlier message",
            ReadBy = new List<string> { "other_sender" }
        };
        _messages.Save(prevMsg);

        await hub.SendMessageAsync(channel.Id, "My reply");

        var updatedPrev = _messages.GetById(prevMsg.Id);
        Assert.NotNull(updatedPrev);
        Assert.Contains(emp.Id, updatedPrev.ReadBy);
    }

    #endregion

    #region StartTyping & StopTyping Tests

    [Fact]
    public async Task StartTyping_UnauthenticatedUser_DoesNotBroadcast()
    {
        var hub = CreateHub(null);

        await hub.StartTypingAsync("c1");

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartTyping_ChannelNotFound_DoesNotBroadcast()
    {
        var (_, hub) = CreateAuthenticatedHub("e_type_notfound", "u_type_notfound");

        await hub.StartTypingAsync("nonexistent_chan");

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartTyping_UserDeniedAccess_DoesNotBroadcast()
    {
        var (_, hub) = CreateAuthenticatedHub("e_type_denied", "u_type_denied", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_type",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "other1", "other2" }
        };
        _channels.Save(dm);

        await hub.StartTypingAsync(dm.Id);

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopTyping_UnauthenticatedUser_DoesNotBroadcast()
    {
        var hub = CreateHub(null);

        await hub.StopTypingAsync("c1");

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopTyping_ChannelNotFound_DoesNotBroadcast()
    {
        var (_, hub) = CreateAuthenticatedHub("e_stop_notfound", "u_stop_notfound");

        await hub.StopTypingAsync("nonexistent_chan");

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopTyping_UserDeniedAccess_DoesNotBroadcast()
    {
        var (_, hub) = CreateAuthenticatedHub("e_stop_denied", "u_stop_denied", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_stop",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "other1", "other2" }
        };
        _channels.Save(dm);

        await hub.StopTypingAsync(dm.Id);

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region EditMessage Tests

    [Fact]
    public async Task EditMessage_UnauthenticatedUser_DoesNothing()
    {
        var hub = CreateHub(null);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync("m1", "Updated"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_MessageNotFound_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_edit_notfound", "u_edit_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync("m_nonexistent", "Updated"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_UserNotOriginalSender_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_imposter", "u_imposter");
        var channel = new ChatChannel { Id = "c_edit_imp", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_original",
            ChannelId = channel.Id,
            SenderId = "e_original_author",
            Content = "Original content"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Hacked content"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Original content", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_ChannelNotFound_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_nochan", "u_edit_nochan");
        var msg = new ChatMessage
        {
            Id = "m_nochan",
            ChannelId = "c_nonexistent",
            SenderId = emp.Id,
            Content = "Original"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Updated"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Original", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_AnnouncementOnlyChannelUserCannotPost_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_ann_denied", "u_edit_ann_denied", isAdmin: false);
        var channel = new ChatChannel
        {
            Id = "c_edit_ann",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "other_post_user" }
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_edit_ann",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Old announcement"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Attempted edit"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Old announcement", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_WhenModerationBlocks_ThrowsHubException()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_mod_user", "u_mod_user");
        var channel = new ChatChannel { Id = "c_edit_mod", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_edit_mod",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Original text"
        };
        _messages.Save(msg);

        _mockModeration.Setup(m => m.EvaluateTextAsync("Blocked content"))
                       .ReturnsAsync((true, "Blocked content", TextModerationAction.Block));
        _mockModeration.Setup(m => m.EvaluateTextAsync("Blocked content", It.IsAny<CancellationToken>()))
                       .ReturnsAsync((true, "Blocked content", TextModerationAction.Block));

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Blocked content"));
        Assert.Contains("violates moderation policy", ex.Message, StringComparison.OrdinalIgnoreCase);

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Original text", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_EmptyContent_ThrowsHubException()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_empty_user", "u_empty_user");
        var channel = new ChatChannel { Id = "c_edit_emp", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_edit_emp",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Original text"
        };
        _messages.Save(msg);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "   "));
        Assert.Contains("cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region DeleteMessage Tests

    [Fact]
    public async Task DeleteMessage_UnauthenticatedUser_DoesNothing()
    {
        var hub = CreateHub(null);

        await Assert.ThrowsAsync<HubException>(() => hub.DeleteMessageAsync("m1"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMessage_MessageNotFound_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_del_notfound", "u_del_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.DeleteMessageAsync("m_nonexistent"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMessage_UserNotOriginalSender_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_del_imposter", "u_del_imposter");
        var channel = new ChatChannel { Id = "c_del_imp", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_del_original",
            ChannelId = channel.Id,
            SenderId = "e_original_author",
            Content = "Do not delete me",
            IsDeleted = false
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.DeleteMessageAsync(msg.Id));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region AddReaction & RemoveReaction Tests

    [Fact]
    public async Task AddReaction_UnauthenticatedUser_DoesNothing()
    {
        var hub = CreateHub(null);

        await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync("m1", "👍"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddReaction_MessageNotFound_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_rea_notfound", "u_rea_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync("m_missing", "👍"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddReaction_ChannelNotFound_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_rea_nochan", "u_rea_nochan");
        var msg = new ChatMessage
        {
            Id = "m_rea_nochan",
            ChannelId = "c_missing_channel",
            SenderId = emp.Id
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddReaction_UserDeniedReadAccess_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_rea_read_denied", "u_rea_read_denied", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_rea_denied",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user_x", "user_y" }
        };
        _channels.Save(dm);

        var msg = new ChatMessage
        {
            Id = "m_rea_denied",
            ChannelId = dm.Id,
            SenderId = "user_x"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddReaction_UserDeniedPostAccessOnAnnouncementChannel_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_rea_ann_denied", "u_rea_ann_denied", isAdmin: false);
        var channel = new ChatChannel
        {
            Id = "c_ann_rea_denied",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "allowed_poster" }
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_ann_rea_denied",
            ChannelId = channel.Id,
            SenderId = "allowed_poster"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddReaction_ReactionAlreadyExists_DoesNotDuplicateOrBroadcastAgain()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_dup_rea", "u_dup_rea");
        var channel = new ChatChannel
        {
            Id = "c_dup_rea",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_dup_rea",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Reactions = new List<string> { $"👍:{emp.Id}" }
        };
        _messages.Save(msg);

        await hub.AddReactionAsync(msg.Id, "👍");

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_UnauthenticatedUser_DoesNothing()
    {
        var hub = CreateHub(null);

        await Assert.ThrowsAsync<HubException>(() => hub.RemoveReactionAsync("m1", "👍"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_MessageNotFound_DoesNothing()
    {
        var (_, hub) = CreateAuthenticatedHub("e_rem_notfound", "u_rem_notfound");

        await Assert.ThrowsAsync<HubException>(() => hub.RemoveReactionAsync("m_missing", "👍"));

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_ChannelNotFound_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_rem_nochan", "u_rem_nochan");
        var msg = new ChatMessage
        {
            Id = "m_rem_nochan",
            ChannelId = "c_missing_channel",
            SenderId = emp.Id,
            Reactions = new List<string> { $"👍:{emp.Id}" }
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.RemoveReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Contains($"👍:{emp.Id}", reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_UserDeniedReadAccess_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_rem_denied", "u_rem_denied", isAdmin: false);
        var dm = new ChatChannel
        {
            Id = "c_dm_rem_denied",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = new List<string> { "user_a", "user_b" }
        };
        _channels.Save(dm);

        var msg = new ChatMessage
        {
            Id = "m_rem_denied",
            ChannelId = dm.Id,
            SenderId = "user_a",
            Reactions = new List<string> { $"👍:{emp.Id}" }
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.RemoveReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Contains($"👍:{emp.Id}", reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_UserDeniedPostAccessOnAnnouncementChannel_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_rem_ann_denied", "u_rem_ann_denied", isAdmin: false);
        var channel = new ChatChannel
        {
            Id = "c_ann_rem_denied",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            IsAnnouncementOnly = true,
            AllowedPostUserIds = new List<string> { "allowed_poster" }
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_ann_rem_denied",
            ChannelId = channel.Id,
            SenderId = "allowed_poster",
            Reactions = new List<string> { $"👍:{emp.Id}" }
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.RemoveReactionAsync(msg.Id, "👍"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Contains($"👍:{emp.Id}", reloaded.Reactions);
        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveReaction_ReactionNotInList_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_rem_none", "u_rem_none");
        var channel = new ChatChannel
        {
            Id = "c_rem_none",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_rem_none",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Reactions = new List<string>()
        };
        _messages.Save(msg);

        await hub.RemoveReactionAsync(msg.Id, "❤️");

        _mockClientProxy.Verify(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Edit and Delete Message Authorization Tests

    [Fact]
    public async Task EditMessage_ActiveAuthorWithAccess_UpdatesMessageAndBroadcasts()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_author", "u_edit_author");
        var channel = new ChatChannel
        {
            Id = "c_edit_auth",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_edit_1",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Original message text"
        };
        _messages.Save(msg);

        await hub.EditMessageAsync(msg.Id, "Updated message text");

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Updated message text", reloaded.Content);
        Assert.NotNull(reloaded.EditedAt);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditMessage_NonAuthor_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_imposter", "u_edit_imposter");
        var channel = new ChatChannel
        {
            Id = "c_edit_imp",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_edit_imp",
            ChannelId = channel.Id,
            SenderId = "legit_author",
            Content = "Original message text"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Hacked message text"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Original message text", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditMessage_UserRevokedFromChannel_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_revoked", "u_edit_revoked");
        // Channel is restricted Group channel where emp is no longer a member
        var channel = new ChatChannel
        {
            Id = "c_edit_rev",
            ChannelType = ChatChannelType.Group,
            CreatedById = "other_user",
            ParticipantIds = new List<string> { "other_user", "another_user" }
        };
        _channels.Save(channel);

        // Message was originally sent by emp before they were removed
        var msg = new ChatMessage
        {
            Id = "m_edit_rev",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Original message text"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync(msg.Id, "Attempted update after revocation"));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Original message text", reloaded.Content);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMessage_ActiveAuthorWithAccess_MarksDeletedAndBroadcasts()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_del_author", "u_del_author");
        var channel = new ChatChannel
        {
            Id = "c_del_auth",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_del_1",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Message to be deleted"
        };
        _messages.Save(msg);

        await hub.DeleteMessageAsync(msg.Id);

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteMessage_NonAuthorRegularUser_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_del_nonauthor", "u_del_nonauthor");
        var channel = new ChatChannel
        {
            Id = "c_del_nonauthor",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_del_2",
            ChannelId = channel.Id,
            SenderId = "legit_author",
            Content = "Protected message"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.DeleteMessageAsync(msg.Id));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMessage_UserRevokedFromChannel_DoesNothing()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_del_revoked", "u_del_revoked");
        var channel = new ChatChannel
        {
            Id = "c_del_rev",
            ChannelType = ChatChannelType.Group,
            CreatedById = "other_user",
            ParticipantIds = new List<string> { "other_user" }
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_del_rev",
            ChannelId = channel.Id,
            SenderId = emp.Id,
            Content = "Message from before removal"
        };
        _messages.Save(msg);

        await Assert.ThrowsAsync<HubException>(() => hub.DeleteMessageAsync(msg.Id));

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteMessage_AdminCanDeleteMessageInChannel()
    {
        var (admin, hub) = CreateAuthenticatedHub("e_admin_del", "u_admin_del", isAdmin: true);
        var channel = new ChatChannel
        {
            Id = "c_admin_del",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_admin_del",
            ChannelId = channel.Id,
            SenderId = "regular_user",
            Content = "Spam to be moderated"
        };
        _messages.Save(msg);

        await hub.DeleteMessageAsync(msg.Id);

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteMessage_ModeratorCanDeleteMessageInChannel()
    {
        var (mod, hub) = CreateAuthenticatedHub("e_mod_del", "u_mod_del");
        mod.Permissions.Add(AppPermissions.Chat.Moderator);
        _employees.Save(mod);

        var channel = new ChatChannel
        {
            Id = "c_mod_del",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true
        };
        _channels.Save(channel);

        var msg = new ChatMessage
        {
            Id = "m_mod_del",
            ChannelId = channel.Id,
            SenderId = "regular_user",
            Content = "Offensive text"
        };
        _messages.Save(msg);

        await hub.DeleteMessageAsync(msg.Id);

        var reloaded = _messages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDeleted);
        _mockClientProxy.Verify(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ChatHub_CanBeActivatedByActivatorUtilities_WithoutAmbiguousConstructors()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_channels);
        services.AddSingleton(_messages);
        services.AddSingleton(_projects);
        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
        services.AddSingleton(userService);
        services.AddSingleton(_teams);
        services.AddSingleton(new Mock<ILogger<ChatHub>>().Object);
        services.AddSingleton(Mock.Of<IChatAuthorizationService>());
        services.AddSingleton<IChatService>(_chatService);
        services.AddSingleton(_chatService);

        var sp = services.BuildServiceProvider();
        var hub = ActivatorUtilities.CreateInstance<ChatHub>(sp);
        Assert.NotNull(hub);
    }

    [Fact]
    public async Task SendMessage_WhenChatServiceInjected_DelegatesDirectly()
    {
        var (emp, baseHub) = CreateAuthenticatedHub("e_del", "u_del");
        var mockChatService = new Mock<IChatService>();

        mockChatService
            .Setup(s => s.SendMessageAsync(It.Is<SendMessageOptions>(o =>
                o.SenderId == emp.Id &&
                o.ChannelId == "chan_target" &&
                o.Content == "Hello delegated" &&
                o.ReplyToId == "reply_1"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult<ChatMessage>.Success(new ChatMessage { Id = "msg_del", Content = "Hello delegated" }));

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
        var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees);
        var hub = new ChatHub(_channels, userService, Mock.Of<ILogger<ChatHub>>(), chatAuth, mockChatService.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = _mockGroups.Object
        };

        await hub.SendMessageAsync("chan_target", "Hello delegated", "reply_1");

        mockChatService.Verify(s => s.SendMessageAsync(It.Is<SendMessageOptions>(o =>
            o.SenderId == emp.Id &&
            o.ChannelId == "chan_target" &&
            o.Content == "Hello delegated" &&
            o.ReplyToId == "reply_1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessageAsync_WhenModerationBlocked_ThrowsHubExceptionWithModerationReason()
    {
        var (emp, _) = CreateAuthenticatedHub("e_mod_hub", "u_mod_hub");
        var mockChatService = new Mock<IChatService>();
        mockChatService
            .Setup(s => s.SendMessageAsync(It.IsAny<SendMessageOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SpokesResult<ChatMessage>.Failure("Message content violates moderation policy.", "MODERATION_BLOCKED"));

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
        var chatAuth = new ChatAuthorizationService(_channels, _projects, _teams, _employees);
        var hub = new ChatHub(_channels, userService, Mock.Of<ILogger<ChatHub>>(), chatAuth, mockChatService.Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = _mockGroups.Object
        };

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.SendMessageAsync("chan_1", "offensive text"));
        Assert.Equal("Message content violates moderation policy.", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_BroadcastPayload_ContainsBothIdAndMessageId_AndUserIdAndSenderId()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_payload", "u_payload");
        var channel = new ChatChannel { Id = "c_payload", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);

        object? capturedPayload = null;
        _mockClientProxy
            .Setup(c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((name, args, token) =>
            {
                if (args.Length > 0) capturedPayload = args[0];
            })
            .Returns(Task.CompletedTask);

        await hub.SendMessageAsync("c_payload", "Testing standardized payload");

        Assert.NotNull(capturedPayload);
        var idProp = capturedPayload.GetType().GetProperty("Id")?.GetValue(capturedPayload);
        var messageIdProp = capturedPayload.GetType().GetProperty("MessageId")?.GetValue(capturedPayload);
        var userIdProp = capturedPayload.GetType().GetProperty("UserId")?.GetValue(capturedPayload);
        var senderIdProp = capturedPayload.GetType().GetProperty("SenderId")?.GetValue(capturedPayload);

        Assert.NotNull(idProp);
        Assert.Equal(idProp, messageIdProp);
        Assert.Equal(emp.Id, userIdProp);
        Assert.Equal(emp.Id, senderIdProp);
    }

    [Fact]
    public async Task AddReactionAsync_BroadcastPayload_ContainsBothIdAndMessageId_AndUserIdAndSenderId()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_react_pay", "u_react_pay");
        var channel = new ChatChannel { Id = "c_react_pay", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_react_pay", ChannelId = channel.Id, SenderId = "other" };
        _messages.Save(msg);

        object? capturedPayload = null;
        _mockClientProxy
            .Setup(c => c.SendCoreAsync("ReactionAdded", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((name, args, token) =>
            {
                if (args.Length > 0) capturedPayload = args[0];
            })
            .Returns(Task.CompletedTask);

        await hub.AddReactionAsync(msg.Id, "🚀");

        Assert.NotNull(capturedPayload);
        var idProp = capturedPayload.GetType().GetProperty("Id")?.GetValue(capturedPayload);
        var messageIdProp = capturedPayload.GetType().GetProperty("MessageId")?.GetValue(capturedPayload);
        var userIdProp = capturedPayload.GetType().GetProperty("UserId")?.GetValue(capturedPayload);
        var senderIdProp = capturedPayload.GetType().GetProperty("SenderId")?.GetValue(capturedPayload);

        Assert.Equal("m_react_pay", idProp);
        Assert.Equal("m_react_pay", messageIdProp);
        Assert.Equal(emp.Id, userIdProp);
        Assert.Equal(emp.Id, senderIdProp);
    }

    [Fact]
    public async Task EditMessageAsync_BroadcastPayload_ContainsBothIdAndMessageId()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_edit_pay", "u_edit_pay");
        var channel = new ChatChannel { Id = "c_edit_pay", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_edit_pay", ChannelId = channel.Id, SenderId = emp.Id, Content = "Before" };
        _messages.Save(msg);

        object? capturedPayload = null;
        _mockClientProxy
            .Setup(c => c.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((name, args, token) =>
            {
                if (args.Length > 0) capturedPayload = args[0];
            })
            .Returns(Task.CompletedTask);

        await hub.EditMessageAsync(msg.Id, "After");

        Assert.NotNull(capturedPayload);
        var idProp = capturedPayload.GetType().GetProperty("Id")?.GetValue(capturedPayload);
        var messageIdProp = capturedPayload.GetType().GetProperty("MessageId")?.GetValue(capturedPayload);

        Assert.Equal("m_edit_pay", idProp);
        Assert.Equal("m_edit_pay", messageIdProp);
    }

    [Fact]
    public async Task DeleteMessageAsync_BroadcastPayload_ContainsBothIdAndMessageId()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_del_pay", "u_del_pay");
        var channel = new ChatChannel { Id = "c_del_pay", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_del_pay", ChannelId = channel.Id, SenderId = emp.Id, Content = "To delete" };
        _messages.Save(msg);

        object? capturedPayload = null;
        _mockClientProxy
            .Setup(c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((name, args, token) =>
            {
                if (args.Length > 0) capturedPayload = args[0];
            })
            .Returns(Task.CompletedTask);

        await hub.DeleteMessageAsync(msg.Id);

        Assert.NotNull(capturedPayload);
        var idProp = capturedPayload.GetType().GetProperty("Id")?.GetValue(capturedPayload);
        var messageIdProp = capturedPayload.GetType().GetProperty("MessageId")?.GetValue(capturedPayload);

        Assert.Equal("m_del_pay", idProp);
        Assert.Equal("m_del_pay", messageIdProp);
    }

    [Fact]
    public async Task ChatHub_ThrowsHubException_WithInformativeMessages()
    {
        var (_, hub) = CreateAuthenticatedHub("e_info", "u_info");

        var exNotFound = await Assert.ThrowsAsync<HubException>(() => hub.JoinChannelAsync("nonexistent_id"));
        Assert.Contains("not found", exNotFound.Message, StringComparison.OrdinalIgnoreCase);

        var exMsgNotFound = await Assert.ThrowsAsync<HubException>(() => hub.EditMessageAsync("missing_msg", "text"));
        Assert.Contains("not found", exMsgNotFound.Message, StringComparison.OrdinalIgnoreCase);

        var unauthHub = CreateHub(null);
        var exUnauth = await Assert.ThrowsAsync<HubException>(() => unauthHub.SendMessageAsync("c1", "text"));
        Assert.Contains("authenticated", exUnauth.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EditMessageAsync_WhenUnauthorizedUser_ThrowsHubException()
    {
        var (emp1, hub1) = CreateAuthenticatedHub("e_owner", "u_owner");
        var (emp2, hub2) = CreateAuthenticatedHub("e_intruder", "u_intruder");
        var channel = new ChatChannel { Id = "c_edit_auth", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_edit_auth", ChannelId = channel.Id, SenderId = emp1.Id, Content = "Original" };
        _messages.Save(msg);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub2.EditMessageAsync(msg.Id, "Hacked"));
        Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteMessageAsync_WhenUnauthorizedUser_ThrowsHubException()
    {
        var (emp1, hub1) = CreateAuthenticatedHub("e_del_owner", "u_del_owner");
        var (emp2, hub2) = CreateAuthenticatedHub("e_del_intruder", "u_del_intruder");
        var channel = new ChatChannel { Id = "c_del_auth", ChannelType = ChatChannelType.General, IsDefaultGeneral = true };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_del_auth", ChannelId = channel.Id, SenderId = emp1.Id, Content = "Original" };
        _messages.Save(msg);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub2.DeleteMessageAsync(msg.Id));
        Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddReactionAsync_WhenChannelAccessDenied_ThrowsHubException()
    {
        var (emp, hub) = CreateAuthenticatedHub("e_react_user", "u_react_user");
        var channel = new ChatChannel 
        { 
            Id = "c_react_private", 
            ChannelType = ChatChannelType.Group, 
            CreatedById = "other_user", 
            ParticipantIds = new List<string> { "other_user" } 
        };
        _channels.Save(channel);
        var msg = new ChatMessage { Id = "m_react_auth", ChannelId = channel.Id, SenderId = "other_user", Content = "Private" };
        _messages.Save(msg);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.AddReactionAsync(msg.Id, "👍"));
        Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
