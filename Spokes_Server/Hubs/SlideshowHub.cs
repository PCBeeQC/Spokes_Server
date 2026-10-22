namespace Spokes_Server.Hubs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
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
    private readonly FileTokenService _fileTokenService;
    private readonly ScopedKeystoreService _keystoreService;
    private readonly IChatChannelAccessService _chatAccess;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SlideshowHub> _logger;

    public SlideshowHub(
        ISlideshowSessionService sessionService,
        Database db,
        AlbumService albumService,
        UserService userService,
        FileTokenService fileTokenService,
        ScopedKeystoreService keystoreService,
        IChatChannelAccessService chatAccess,
        IServiceProvider serviceProvider,
        ILogger<SlideshowHub> logger)
    {
        _sessionService = sessionService;
        _db = db;
        _albumService = albumService;
        _userService = userService;
        _fileTokenService = fileTokenService;
        _keystoreService = keystoreService;
        _chatAccess = chatAccess;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Called by a TV or external display upon opening /slideshow/pair.
    /// Generates a random, rate-limited pairing code and binds the TV connection.
    /// </summary>
    [AllowAnonymous]
    public async Task<SlideshowSession> RequestPairingCode()
    {
        var session = _sessionService.CreateSession(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{session.SessionId}");
        return session;
    }

    /// <summary>
    /// Rebinds a TV connection after automatic reconnect without disturbing the paired presentation session.
    /// </summary>
    [AllowAnonymous]
    public async Task<bool> RebindTvConnection(string sessionId)
    {
        if (!_sessionService.RebindTvConnection(sessionId, Context.ConnectionId))
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
    [Authorize]
    public async Task<SpokesResult<string>> PairWithTv(string pin, string albumId, int initialIndex = 0)
    {
        var employee = GetCurrentEmployee();
        if (employee == null)
        {
            return SpokesResult<string>.Failure("Authentication required.", "UNAUTHORIZED");
        }

        var album = _db.Albums.GetById(albumId);
        if (album == null)
        {
            return SpokesResult<string>.Failure("Album not found.", "NOT_FOUND");
        }

        bool hasAccess = album.OwnerId == employee.Id ||
                         album.ContributorUserIds.Contains(employee.Id) ||
                         album.SharedWithChannelIds.Any(cId => _chatAccess.GetUsersForChannel(cId).Contains(employee.Id));

        if (!hasAccess)
        {
            _logger.LogWarning("User {UserId} unauthorized to present album {AlbumId}", employee.Id, albumId);
            return SpokesResult<string>.Failure("You do not have permission to view or present this album.", "FORBIDDEN");
        }

        string? plainAlbumKey = null;
        if (album.IsEncrypted)
        {
            string? privateKey = _keystoreService.IsUnlocked ? _keystoreService.GetPrivateKey() : null;
            plainAlbumKey = _albumService.GetPlainAlbumKey(album, employee.Id, privateKey);
        }

        var allowedMedia = album.Media.ToDictionary(m => m.Id, m => System.IO.Path.GetFileName(m.FilePath));
        var bridge = new UserScopedSlideshowBridge(_serviceProvider, employee, album.Id, plainAlbumKey, allowedMedia);

        if (!_sessionService.TryPair(pin, employee.Id, Context.ConnectionId, albumId, out var session, out var error, bridge))
        {
            return SpokesResult<string>.Failure(error ?? "Failed to pair with TV.", "PAIRING_FAILED");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"session_{session!.SessionId}");

        var items = new List<SlideshowMediaItem>();
        var allEmployees = _db.Employees.GetAll();

        foreach (var media in album.Media)
        {
            string url = $"/spokesapi/slideshow/{session.SessionId}/media/{media.Id}";

            var uploader = allEmployees.FirstOrDefault(e => e.Id == media.AddedByUserId);
            string uploaderName = uploader != null ? $"{uploader.FirstName} {uploader.LastName}".Trim() : string.Empty;

            items.Add(new SlideshowMediaItem
            {
                Id = media.Id,
                FileName = media.FileName,
                Url = url,
                ThumbnailUrl = $"{url}/thumb",
                ContentType = media.ContentType,
                ImageWidth = media.ImageWidth,
                ImageHeight = media.ImageHeight,
                DurationSeconds = media.DurationSeconds,
                AddedAt = media.AddedAt,
                AddedByUserName = uploaderName,
                FileSizeBytes = media.FileSizeBytes
            });
        }

        var stagePayload = new SlideshowStagePayload
        {
            SessionId = session.SessionId,
            AlbumId = album.Id,
            AlbumTitle = album.Title,
            InitialIndex = Math.Clamp(initialIndex, 0, Math.Max(0, items.Count - 1)),
            Items = items
        };

        // Notify in-memory and SignalR clients
        _sessionService.NotifySessionStarted(session.SessionId, stagePayload);
        await Clients.Client(session.TvConnectionId).SendAsync("SessionStarted", stagePayload);

        return SpokesResult<string>.Success(session.SessionId);
    }

    /// <summary>
    /// Changes the active slide on the TV stage. Strictly limited to the session host.
    /// </summary>
    public async Task NavigateSlide(string sessionId, int targetIndex)
    {
        if (!_sessionService.ValidateHostCommand(sessionId, Context.ConnectionId, out var session))
        {
            throw new HubException("Unauthorized to navigate this session.");
        }

        _sessionService.NotifySlideChanged(sessionId, targetIndex);
        await Clients.Client(session!.TvConnectionId).SendAsync("SlideChanged", targetIndex);
    }

    /// <summary>
    /// Skips an unwanted or sensitive upcoming photo directly from the phone.
    /// </summary>
    public async Task SkipNext(string sessionId, int nextIndex)
    {
        if (!_sessionService.ValidateHostCommand(sessionId, Context.ConnectionId, out var session))
        {
            throw new HubException("Unauthorized to skip slides in this session.");
        }

        _sessionService.NotifySlideChanged(sessionId, nextIndex);
        await Clients.Client(session!.TvConnectionId).SendAsync("SlideChanged", nextIndex);
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
    /// Called by the TV stage when the HTML5 video duration is loaded or known.
    /// </summary>
    [AllowAnonymous]
    public async Task ReportTvVideoDuration(string sessionId, double duration)
    {
        var session = _sessionService.GetSessionById(sessionId);
        if (session != null && session.TvConnectionId == Context.ConnectionId)
        {
            _sessionService.NotifyTvVideoDurationKnown(sessionId, duration);
            if (!string.IsNullOrEmpty(session.HostConnectionId))
            {
                await Clients.Client(session.HostConnectionId).SendAsync("TvVideoDurationKnown", duration);
            }
        }
    }

    /// <summary>
    /// Called by the TV stage during HTML5 video playback to report actual currentTime and play/pause state.
    /// </summary>
    [AllowAnonymous]
    public async Task ReportTvVideoTime(string sessionId, double currentTime, bool isPlaying, double duration)
    {
        var session = _sessionService.GetSessionById(sessionId);
        if (session != null && session.TvConnectionId == Context.ConnectionId)
        {
            _sessionService.NotifyTvVideoTimeUpdated(sessionId, currentTime, isPlaying, duration);
            if (!string.IsNullOrEmpty(session.HostConnectionId))
            {
                await Clients.Client(session.HostConnectionId).SendAsync("TvVideoTimeUpdated", currentTime, isPlaying, duration);
            }
        }
    }

    /// <summary>
    /// Called by the TV stage when HTML5 video playback completes.
    /// </summary>
    [AllowAnonymous]
    public async Task ReportTvVideoEnded(string sessionId)
    {
        var session = _sessionService.GetSessionById(sessionId);
        if (session != null && session.TvConnectionId == Context.ConnectionId)
        {
            _sessionService.NotifyTvVideoEnded(sessionId);
            if (!string.IsNullOrEmpty(session.HostConnectionId))
            {
                await Clients.Client(session.HostConnectionId).SendAsync("TvVideoEnded");
            }
        }
    }

    /// <summary>
    /// Ends an active slideshow session and clears the TV screen.
    /// </summary>
    public async Task EndSession(string sessionId)
    {
        var session = _sessionService.GetSessionById(sessionId);
        if (session != null)
        {
            if (session.HostConnectionId == Context.ConnectionId || session.TvConnectionId == Context.ConnectionId)
            {
                await Clients.Client(session.TvConnectionId).SendAsync("SessionEnded");
                await Clients.Client(session.HostConnectionId).SendAsync("SessionEnded");
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
        return Context.User == null ? null : _userService.GetEmployee(Context.User);
    }
}
