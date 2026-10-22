namespace Spokes_Server.Controllers;

using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Communication.Chat;

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

    public SlideshowMediaController(
        ISlideshowSessionService sessionService,
        ILogger<SlideshowMediaController> logger)
    {
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpGet("{sessionId}/media/{mediaId}")]
    public IActionResult GetMedia(string sessionId, string mediaId)
        => ServeBridgeMedia(sessionId, mediaId, thumb: false);

    [HttpGet("{sessionId}/media/{mediaId}/thumb")]
    public IActionResult GetMediaThumb(string sessionId, string mediaId)
        => ServeBridgeMedia(sessionId, mediaId, thumb: true);

    private IActionResult ServeBridgeMedia(string sessionId, string mediaId, bool thumb)
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

        // Keep session alive while presentation is actively streaming
        _sessionService.RecordActivity(sessionId);

        // 2. Delegate directly to the user-scoped bridge (which invokes FileController)
        if (session.FileBridge is UserScopedSlideshowBridge userBridge)
        {
            return userBridge.GetMedia(mediaId, thumb, ControllerContext);
        }

        return session.FileBridge.GetMedia(mediaId, thumb);
    }
}
