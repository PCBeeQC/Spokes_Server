using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;

namespace Spokes_Server.Controllers;

/// <summary>
/// API controller for managing realtime presence and connection focus state.
/// </summary>
[ApiController]
[Route("spokesapi/presence")]
[Route("spokesapi/push")]
[AllowAnonymous]
[Microsoft.AspNetCore.Cors.EnableCors("AllowPublicApi")]
public class PresenceController : SpokesControllerBase
{
    private readonly PresenceStateService _presence;
    private readonly IUserClientStateService? _clientStateService;
    private readonly ILogger<PresenceController>? _logger;

    public PresenceController(
        PresenceStateService presence,
        IUserClientStateService? clientStateService = null,
        ILogger<PresenceController>? logger = null)
    {
        _presence = presence;
        _clientStateService = clientStateService;
        _logger = logger;
    }

    /// <summary>
    /// Instantly mark a connection offline. Used during pagehide/app termination to bypass TCP timeouts.
    /// AllowAnonymous because during iOS teardown, cookies might not be reliably attached to the sendBeacon request.
    /// The subscriptionId itself serves as a secure, ephemeral one-time token.
    /// </summary>
    [HttpPost("offline")]
    public IActionResult MarkOffline([FromQuery] string subscriptionId)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            _presence.RemoveConnection(subscriptionId);
        }
        return Ok();
    }

    /// <summary>
    /// Instantly mark a connection as unfocused. Used during app suspension to bypass SignalR TCP timeouts without terminating the circuit.
    /// </summary>
    [HttpPost("unfocus")]
    public IActionResult Unfocus([FromQuery] string subscriptionId)
    {
        _logger?.LogDebug("[PresenceController] Unfocus endpoint hit for subscriptionId: {SubscriptionId}", subscriptionId);
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            var userId = _presence.GetUserIdBySubscription(subscriptionId);
            if (!string.IsNullOrEmpty(userId))
            {
                _clientStateService?.FlushToDiskIfUnsent(userId);
            }
            _presence.SetConnectionFocusBySubscription(subscriptionId, false);
            _logger?.LogDebug("[PresenceController] SetConnectionFocusBySubscription called for {SubscriptionId}", subscriptionId);
        }
        return Ok();
    }

    /// <summary>
    /// Updates the explicit presence state (online, away, dnd, offline) for a connection.
    /// Used by Electron OS presence events, system tray status menu, and browser 5-minute idle timers.
    /// </summary>
    [HttpPost("state")]
    public IActionResult SetState([FromQuery] string subscriptionId, [FromQuery] string state)
    {
        if (!string.IsNullOrEmpty(subscriptionId) && !string.IsNullOrEmpty(state))
        {
            var normalized = state.Trim().ToLowerInvariant();
            var parsedState = normalized switch
            {
                "away" or "idle" => ConnectionPresenceState.Idle,
                "dnd" => ConnectionPresenceState.DoNotDisturb,
                "offline" or "invisible" => ConnectionPresenceState.Offline,
                _ => ConnectionPresenceState.Active
            };

            if (parsedState is ConnectionPresenceState.Idle or ConnectionPresenceState.Offline)
            {
                var userId = _presence.GetUserIdBySubscription(subscriptionId);
                if (!string.IsNullOrEmpty(userId))
                {
                    _clientStateService?.FlushToDiskIfUnsent(userId);
                }
            }

            _presence.SetConnectionPresenceState(subscriptionId, parsedState);
        }
        return Ok();
    }
}
