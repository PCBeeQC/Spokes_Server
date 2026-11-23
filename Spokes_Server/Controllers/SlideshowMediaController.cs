namespace Spokes_Server.Controllers;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Identity;

/// <summary>
/// Dedicated controller acting as a secure bridge for active, paired TV slideshow sessions.
/// This controller has NO access to the database or filesystem directly.
/// Media requests are fulfilled exclusively through the active user-scoped session bridge via FileController.
/// When the session ends or expires, all media access immediately ceases.
/// </summary>
[Route("spokesapi/slideshow")]
[ApiController]
[AllowAnonymous]
[Microsoft.AspNetCore.Cors.EnableCors("AllowPublicApi")]
public class SlideshowMediaController : ControllerBase
{
    private readonly ISlideshowSessionService _sessionService;
    private readonly ILogger<SlideshowMediaController> _logger;
    private readonly UserService? _userService;

    public SlideshowMediaController(
        ISlideshowSessionService sessionService,
        ILogger<SlideshowMediaController> logger,
        UserService? userService = null)
    {
        _sessionService = sessionService;
        _logger = logger;
        _userService = userService;
    }

    [HttpGet("{sessionId}/media/{mediaId}")]
    public Task<IActionResult> GetMedia(string sessionId, string mediaId, [FromQuery] string? st = null, CancellationToken cancellationToken = default)
        => ServeBridgeMediaAsync(sessionId, mediaId, thumb: false, st: st, cancellationToken: cancellationToken);

    [HttpGet("{sessionId}/media/{mediaId}/thumb")]
    public Task<IActionResult> GetMediaThumb(string sessionId, string mediaId, [FromQuery] string? st = null, CancellationToken cancellationToken = default)
        => ServeBridgeMediaAsync(sessionId, mediaId, thumb: true, st: st, cancellationToken: cancellationToken);

    private async Task<IActionResult> ServeBridgeMediaAsync(string sessionId, string mediaId, bool thumb, string? st, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(mediaId))
        {
            return BadRequest("Invalid session or media identifier.");
        }

        // 1. Validate active slideshow session and active user bridge
        var session = _sessionService.GetSessionById(sessionId);
        if (session == null || !session.IsPaired || session.FileBridge == null)
        {
            _logger.LogWarning("Unauthorized slideshow media request: session {SessionId} not active or bridge missing", sessionId);
            return StatusCode(403, "Slideshow session is not active.");
        }

        // 2. Verify per-session capability secret (?st=...) or authenticated presenter cookie
        bool hasValidSecret = SlideshowSessionService.SecretsMatch(session.TvSecret, st);
        bool isAuthenticatedHost = !hasValidSecret && IsAuthenticatedSessionHost(session.HostUserId);
        if (!hasValidSecret && !isAuthenticatedHost)
        {
            _logger.LogWarning("Rejected slideshow media request for session {SessionId}: invalid or missing capability token", sessionId);
            return StatusCode(403, "Invalid or missing slideshow capability token.");
        }

        // Keep session alive while presentation is actively streaming
        _sessionService.RecordActivity(sessionId);

        // 3. Delegate directly to the user-scoped bridge (which invokes FileController)
        if (session.FileBridge is UserScopedSlideshowBridge userBridge)
        {
            return await userBridge.GetMediaAsync(mediaId, thumb, ControllerContext, cancellationToken);
        }

        return await session.FileBridge.GetMediaAsync(mediaId, thumb, cancellationToken);
    }

    private bool IsAuthenticatedSessionHost(string? hostUserId)
    {
        if (string.IsNullOrEmpty(hostUserId) || User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (_userService != null)
        {
            var employee = _userService.GetEmployee(User);
            return employee is { IsActive: true, IsBanned: false } &&
                   string.Equals(employee.Id, hostUserId, StringComparison.OrdinalIgnoreCase);
        }

        string? claimEmployeeId = User.FindFirst("EmployeeId")?.Value;
        return !string.IsNullOrEmpty(claimEmployeeId) &&
               string.Equals(claimEmployeeId, hostUserId, StringComparison.OrdinalIgnoreCase);
    }
}
