namespace Spokes_Server.Tests.Hubs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Hubs;
using Xunit;

public class SlideshowHubTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly Mock<Database> _mockDb;
    private readonly AlbumRepository _albums;
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly SlideshowSessionService _sessionService;
    private readonly FileTokenService _fileTokenService;
    private readonly ScopedKeystoreService _keystoreService;
    private readonly AlbumService _albumService;
    private readonly UserService _userService;
    private readonly Mock<IChatChannelAccessService> _mockChatAccess;
    private readonly Mock<IHubCallerClients> _mockClients;
    private readonly Mock<ISingleClientProxy> _mockSingleClientProxy;
    private readonly Mock<IClientProxy> _mockGroupProxy;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly Mock<HubCallerContext> _mockContext;

    public SlideshowHubTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_SlideshowHub_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _channels = new ChatChannelRepository(_writer, mockConfig.Object);
        _employees = new EmployeeRepository(_writer, mockConfig.Object);
        _albums = new AlbumRepository(_writer, mockConfig.Object);
        var openIdAccounts = new OpenIdAccountRepository(_writer, mockConfig.Object);

        _mockDb = new Mock<Database>();
        _mockDb.Setup(d => d.Albums).Returns(_albums);
        _mockDb.Setup(d => d.Employees).Returns(_employees);

        _sessionService = new SlideshowSessionService();

        var mockDataProtection = new Mock<IDataProtectionProvider>();
        var mockProtector = new Mock<IDataProtector>();
        mockDataProtection.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(mockProtector.Object);
        mockProtector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns((byte[] b) => b);
        mockProtector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns((byte[] b) => b);

        _keystoreService = new ScopedKeystoreService(mockDataProtection.Object);
        _fileTokenService = new FileTokenService(mockDataProtection.Object, new MemoryCache(new MemoryCacheOptions()));

        var companyProfiles = new CompanyProfileRepository(_writer, mockConfig.Object);
        var serverConfigs = new ServerConfigRepository(_writer, mockConfig.Object);
        var escrow = new Mock<ServerEscrowService>(companyProfiles, serverConfigs, new Mock<ICryptoService>().Object, new Mock<ILogger<ServerEscrowService>>().Object, mockConfig.Object);
        _albumService = new AlbumService(_albums, _channels, new Mock<ICryptoService>().Object, escrow.Object, _employees);

        var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        _userService = new UserService(mockAuthState.Object, _employees, openIdAccounts);
        _mockChatAccess = new Mock<IChatChannelAccessService>();

        _mockClients = new Mock<IHubCallerClients>();
        _mockSingleClientProxy = new Mock<ISingleClientProxy>();
        _mockGroupProxy = new Mock<IClientProxy>();
        _mockGroups = new Mock<IGroupManager>();
        _mockContext = new Mock<HubCallerContext>();

        _mockClients.Setup(c => c.Client(It.IsAny<string>())).Returns(_mockSingleClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockGroupProxy.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
        _writer.Dispose();
    }

    private SlideshowHub CreateHub(string? userId = "user-1", string connectionId = "conn-1")
    {
        if (userId != null)
        {
            var claims = new[] { new Claim("sub", userId), new Claim("EmployeeId", userId) };
            var identity = new ClaimsIdentity(claims, "TestAuthType");
            var principal = new ClaimsPrincipal(identity);
            _mockContext.Setup(c => c.User).Returns(principal);
        }
        else
        {
            _mockContext.Setup(c => c.User).Returns((ClaimsPrincipal?)null);
        }

        _mockContext.Setup(c => c.ConnectionId).Returns(connectionId);

        return new SlideshowHub(
            _sessionService,
            _mockDb.Object,
            _albumService,
            _userService,
            _fileTokenService,
            _keystoreService,
            _mockChatAccess.Object,
            new ServiceCollection().BuildServiceProvider(),
            new Mock<ILogger<SlideshowHub>>().Object)
        {
            Context = _mockContext.Object,
            Clients = _mockClients.Object,
            Groups = _mockGroups.Object
        };
    }

    [Fact]
    public async Task RequestPairingCode_GeneratesSessionAndAddsToGroup()
    {
        var hub = CreateHub("user-1", "tv-conn-123");

        var session = await hub.RequestPairingCode();

        Assert.NotNull(session);
        Assert.Equal(6, session.PairingPin.Length);
        Assert.Equal("tv-conn-123", session.TvConnectionId);
        _mockGroups.Verify(g => g.AddToGroupAsync("tv-conn-123", $"session_{session.SessionId}", default), Times.Once);
    }

    [Fact]
    public async Task PairWithTv_Fails_WhenUnauthenticated()
    {
        var hub = CreateHub(userId: null, connectionId: "host-conn-1");

        var result = await hub.PairWithTv("123456", "album-1");

        Assert.False(result.IsSuccess);
        Assert.Equal("UNAUTHORIZED", result.Code);
    }

    [Fact]
    public async Task PairWithTv_Succeeds_ForAlbumOwner()
    {
        var emp = new Employee { Id = "owner-1", FirstName = "Alice", LastName = "Smith" };
        _employees.Save(emp);

        var album = new Album
        {
            Id = "album-owner",
            Title = "Vacation Photos",
            OwnerId = "owner-1",
            Media = [new AlbumMedia { Id = "m1", FileName = "beach.jpg", FilePath = "/spokesapi/files/albums/album-owner/beach.jpg", ContentType = "image/jpeg" }]
        };
        _albums.Save(album);

        // TV creates session
        var tvSession = _sessionService.CreateSession("tv-screen-conn");
        string pin = tvSession.PairingPin;

        var hub = CreateHub(userId: "owner-1", connectionId: "host-phone-conn");

        var result = await hub.PairWithTv(pin, "album-owner", initialIndex: 0);

        Assert.True(result.IsSuccess);
        Assert.Equal(tvSession.SessionId, result.Value);

        // TV client should have received "SessionStarted"
        _mockClients.Verify(c => c.Client("tv-screen-conn"), Times.AtLeastOnce);
        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("SessionStarted", It.Is<object[]>(args => args.Length == 1 && args[0] is SlideshowStagePayload), default), Times.Once);
    }

    [Fact]
    public async Task NavigateSlide_SucceedsForHost_ThrowsForNonHost()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var hostHub = CreateHub("owner-1", "host-conn-verified");
        await hostHub.NavigateSlide(session.SessionId, 3);

        // Verified host successfully dispatches slide change to TV
        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("SlideChanged", It.Is<object[]>(args => (int)args[0] == 3), default), Times.Once);

        // Non-host caller throws HubException
        var imposterHub = CreateHub("imposter", "imposter-conn");
        await Assert.ThrowsAsync<HubException>(() => imposterHub.NavigateSlide(session.SessionId, 5));
    }

    [Fact]
    public async Task SyncZoom_DispatchesToTvClient()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var hostHub = CreateHub("owner-1", "host-conn-verified");
        await hostHub.SyncZoom(session.SessionId, 2.0, 50.0, -30.0);

        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("ZoomUpdated", It.Is<object[]>(args => (double)args[0] == 2.0), default), Times.Once);
    }

    [Fact]
    public async Task SyncVideoControl_DispatchesToTvClient_WithLooping()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var hostHub = CreateHub("owner-1", "host-conn-verified");
        await hostHub.SyncVideoControl(session.SessionId, isPlaying: true, currentTime: 15.2, isMuted: false, isLooping: true);

        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("VideoControlUpdated", It.Is<object[]>(args => 
            (bool)args[0] == true &&
            Math.Abs((double)args[1] - 15.2) < 0.01 &&
            (bool)args[2] == false &&
            (bool)args[3] == true
        ), default), Times.Once);
    }

    [Fact]
    public async Task EndSession_NotifiesBothPeers()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var hostHub = CreateHub("owner-1", "host-conn-verified");
        await hostHub.EndSession(session.SessionId);

        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("SessionEnded", It.IsAny<object[]>(), default), Times.AtLeastOnce);
        Assert.Null(_sessionService.GetSessionById(session.SessionId));
    }

    [Fact]
    public async Task RebindTvConnection_UpdatesTvConnectionId_AndAddsToGroup()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var tvHub = CreateHub(userId: null, connectionId: "tv-reconnected-conn");
        bool result = await tvHub.RebindTvConnection(session.SessionId);

        Assert.True(result);
        var updated = _sessionService.GetSessionById(session.SessionId);
        Assert.NotNull(updated);
        Assert.Equal("tv-reconnected-conn", updated.TvConnectionId);
        _mockGroups.Verify(g => g.AddToGroupAsync("tv-reconnected-conn", $"session_{session.SessionId}", default), Times.Once);
    }

    [Fact]
    public async Task OnDisconnectedAsync_WhenPairedTvDisconnects_DoesNotNotifyHost()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var tvHub = CreateHub(userId: null, connectionId: "tv-screen-conn");
        await tvHub.OnDisconnectedAsync(null);

        // Host should NOT receive SessionEnded because the TV is allowed to reconnect
        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("SessionEnded", It.IsAny<object[]>(), default), Times.Never);
        Assert.NotNull(_sessionService.GetSessionById(session.SessionId));
    }

    [Fact]
    public async Task OnDisconnectedAsync_WhenHostDisconnects_NotifiesTvAndEndsSession()
    {
        var session = _sessionService.CreateSession("tv-screen-conn");
        _sessionService.TryPair(session.PairingPin, "owner-1", "host-conn-verified", "album-1", out _, out _);

        var hostHub = CreateHub(userId: "owner-1", connectionId: "host-conn-verified");
        await hostHub.OnDisconnectedAsync(null);

        // TV should receive SessionEnded
        _mockSingleClientProxy.Verify(p => p.SendCoreAsync("SessionEnded", It.IsAny<object[]>(), default), Times.Once);
        Assert.Null(_sessionService.GetSessionById(session.SessionId));
    }
}
