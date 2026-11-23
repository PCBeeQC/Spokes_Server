namespace Spokes_Server.Hubs;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Security;

/// <summary>
/// Real-time SignalR Hub for TV slideshow presentation sync, pairing, and remote control.
/// </summary>
public class SlideshowHub : Hub
{
    private readonly ISlideshowSessionService _sessionService;
    private readonly Database _db;
    private readonly AlbumService _albumService;
    private readonly UserService _userService;
    private readonly ScopedKeystoreService _keystoreService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SlideshowHub> _logger;

    public SlideshowHub(
        ISlideshowSessionService sessionService,
        Database db,
        AlbumService albumService,
        UserService userService,
        ScopedKeystoreService keystoreService,
        IServiceProvider serviceProvider,
        ILogger<SlideshowHub> logger)
    {
        _sessionService = sessionService;
        _db = db;
        _albumService = albumService;
        _userService = userService;
        _keystoreService = keystoreService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Called by a TV or external display upon opening /slideshow/pair or Cast Receiver.
    /// Generates a random, rate-limited pairing code and binds the TV connection.
    /// </summary>
    [AllowAnonymous]
    public async Task<SlideshowSession> RequestPairingCode()
    {
        string? creatorIp = Context.Features?
            .Get<Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature>()?
            .HttpContext?.Connection.RemoteIpAddress?.ToString();

        foreach (var oldSessionId in _sessionService.GetUnpairedSessionIdsByTvConnection(Context.ConnectionId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session_{oldSessionId}");
        }

        SlideshowSession session;
        try
        {
            session = _sessionService.CreateSession(Context.ConnectionId, SlideshowDisplayKind.SignalRCastReceiver, creatorIp);
        }
        catch (InvalidOperationException ex)
        {
            throw new HubException(ex.Message);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{session.SessionId}");
        return session;
    }

    /// <summary>
    /// Rebinds a TV connection after automatic reconnect without disturbing the paired presentation session.
    /// Requires the per-session TvSecret capability token issued during RequestPairingCode.
    /// </summary>
    [AllowAnonymous]
    public async Task<bool> RebindTvConnection(string sessionId, string tvSecret)
    {
        if (!_sessionService.RebindTvConnection(sessionId, Context.ConnectionId, tvSecret))
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{sessionId}");
        return true;
    }

    /// <summary>
    /// Called by the presenter's phone to pair with the TV screen.
    /// Requires authentication and validates album access permissions.
    /// </summary>
    [Authorize(Policy = AppPermissions.Albums.View)]
    public async Task<SpokesResult<string>> PairWithTv(string pin, string albumId, int initialIndex = 0)
    {
        var employee = Context.User == null ? null : _userService.GetEmployee(Context.User);
        if (employee == null)
        {
            return SpokesResult<string>.Failure("Authentication required.", "UNAUTHORIZED");
        }

        if (!employee.IsActiveUnsuspended)
        {
            return SpokesResult<string>.Failure("User account is not permitted to present.", "FORBIDDEN");
        }

        if (!employee.HasPermission(AppPermissions.Albums.View))
        {
            _logger.LogWarning("User {UserId} missing Albums.View permission to present album {AlbumId}", employee.Id, albumId);
            return SpokesResult<string>.Failure("You do not have permission to view or present this album.", "FORBIDDEN");
        }

        var album = _db.Albums.GetById(albumId);
        if (album == null)
        {
            return SpokesResult<string>.Failure("Album not found.", "NOT_FOUND");
        }

        if (!_albumService.CanUserViewAlbum(album, employee.Id))
        {
            _logger.LogWarning("User {UserId} unauthorized to present album {AlbumId}", employee.Id, albumId);
            return SpokesResult<string>.Failure("You do not have permission to view or present this album.", "FORBIDDEN");
        }

        string? plainAlbumKey = null;
        if (album.IsEncrypted)
        {
            string? privateKey = _keystoreService.IsUnlocked ? _keystoreService.GetPrivateKey() : null;
            plainAlbumKey = _albumService.GetPlainAlbumKey(album, employee.Id, privateKey);
            if (string.IsNullOrEmpty(plainAlbumKey))
            {
                return SpokesResult<string>.Failure("Unlock your Vault before presenting an encrypted album.", "VAULT_LOCKED");
            }
        }

        var allowedMedia = album.Media.ToDictionary(m => m.Id, m => System.IO.Path.GetFileName(m.FilePath));
        var bridge = new UserScopedSlideshowBridge(_serviceProvider, employee, album.Id, plainAlbumKey, allowedMedia);

        if (!_sessionService.TryPair(pin, employee.Id, Context.ConnectionId, albumId, out var session, out var error, bridge))
        {
            return SpokesResult<string>.Failure(error ?? "Failed to pair with TV.", "PAIRING_FAILED");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{session!.SessionId}");

        var stagePayload = SlideshowStagePayload.Create(
            session.SessionId,
            session.TvSecret,
            album,
            initialIndex,
            SlideshowSessionService.AppendSecretQuery);

        // Notify in-memory and SignalR clients
        _sessionService.NotifySessionStarted(session.SessionId, stagePayload);
        await Clients.Client(session.TvConnectionId).SendAsync("SessionStarted", stagePayload);

        return SpokesResult<string>.Success(session.SessionId);
    }

    /// <summary>
    /// Changes the active slide on the TV stage. Strictly limited to the session host.
    /// </summary>
    public Task NavigateSlide(string sessionId, int targetIndex) =>
        BroadcastSlideChangeAsync(sessionId, targetIndex, "Unauthorized to navigate this session.");

    /// <summary>
    /// Skips an unwanted or sensitive upcoming photo directly from the phone.
    /// </summary>
    public Task SkipNext(string sessionId, int nextIndex) =>
        BroadcastSlideChangeAsync(sessionId, nextIndex, "Unauthorized to skip slides in this session.");

    private async Task BroadcastSlideChangeAsync(string sessionId, int targetIndex, string unauthorizedMessage)
    {
        if (!_sessionService.ValidateHostCommand(sessionId, Context.ConnectionId, out var session))
        {
            throw new HubException(unauthorizedMessage);
        }

        _sessionService.NotifySlideChanged(sessionId, targetIndex);
        await Clients.Client(session!.TvConnectionId).SendAsync("SlideChanged", targetIndex);
    }

    /// <summary>
    /// Broadcasts synchronized pinch-to-zoom / pan coordinates to the TV stage.
    /// </summary>
    public async Task SyncZoom(string sessionId, double scale, double x, double y, bool isPanning = false)
    {
        if (!_sessionService.ValidateHostCommand(sessionId, Context.ConnectionId, out var session))
        {
            return;
        }

        _sessionService.NotifyZoomUpdated(sessionId, scale, x, y, isPanning);
        await Clients.Client(session!.TvConnectionId).SendAsync("ZoomUpdated", scale, x, y, isPanning);
    }

    /// <summary>
    /// Broadcasts video playback controls (play/pause, seek, mute, loop) to the TV stage.
    /// </summary>
    public async Task SyncVideoControl(string sessionId, bool isPlaying, double currentTime, bool isMuted, bool isLooping = true)
    {
        if (!_sessionService.ValidateHostCommand(sessionId, Context.ConnectionId, out var session))
        {
            return;
        }

        _sessionService.NotifyVideoControlUpdated(sessionId, isPlaying, currentTime, isMuted, isLooping);
        await Clients.Client(session!.TvConnectionId).SendAsync("VideoControlUpdated", isPlaying, currentTime, isMuted, isLooping);
    }

    /// <summary>
    /// Ends an active slideshow session and clears the TV screen.
    /// </summary>
    public async Task EndSession(string sessionId)
    {
        var session = _sessionService.GetSessionById(sessionId);
        if (session != null)
        {
            var callerEmployee = GetCurrentEmployee();
            bool isHostUser = callerEmployee != null && !string.IsNullOrEmpty(session.HostUserId) &&
                              string.Equals(callerEmployee.Id, session.HostUserId, StringComparison.OrdinalIgnoreCase);

            bool isHostConn = !string.IsNullOrEmpty(session.HostConnectionId) &&
                              !string.IsNullOrEmpty(Context.ConnectionId) &&
                              session.HostConnectionId == Context.ConnectionId;
            bool isTvConn = !string.IsNullOrEmpty(session.TvConnectionId) &&
                            !string.IsNullOrEmpty(Context.ConnectionId) &&
                            session.TvConnectionId == Context.ConnectionId;

            if (isHostConn || isTvConn || isHostUser)
            {
                if (!string.IsNullOrEmpty(session.TvConnectionId))
                {
                    await Clients.Client(session.TvConnectionId).SendAsync("SessionEnded");
                }
                if (!string.IsNullOrEmpty(session.HostConnectionId))
                {
                    await Clients.Client(session.HostConnectionId).SendAsync("SessionEnded");
                }
                _sessionService.EndSession(sessionId);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var session = _sessionService.HandleDisconnect(Context.ConnectionId);
        if (session != null && session.IsPaired)
        {
            string remainingConnection = session.TvConnectionId == Context.ConnectionId 
                ? session.HostConnectionId 
                : session.TvConnectionId;

            if (!string.IsNullOrEmpty(remainingConnection))
            {
                await Clients.Client(remainingConnection).SendAsync("SessionEnded");
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Employee? GetCurrentEmployee()
    {
        var emp = Context.User == null ? null : _userService.GetEmployee(Context.User);
        if (emp == null || !emp.IsActive || emp.IsSuspended || emp.IsBanned)
        {
            return null;
        }
        return emp;
    }
}
