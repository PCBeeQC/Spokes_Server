using System;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Security;
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
public class PresenceController : SpokesControllerBase
{
    public const string PresenceTokenPurpose = "SpokesPresenceBeacon";
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromDays(7);

    private readonly PresenceStateService _presence;
    private readonly IUserClientStateService? _clientStateService;
    private readonly ILogger<PresenceController>? _logger;
    private readonly IDataProtectionProvider? _dataProtection;
    private readonly SystemConfigRepository? _systemConfigs;
    private readonly EmployeeRepository? _employees;
    private readonly TimeProvider _timeProvider;

    private sealed record PresenceBeaconTokenPayload(string SubscriptionId, string EmployeeId, long ExpiresUnixSeconds);

    public sealed class PresenceUpdateRequest
    {
        public string? SubscriptionId { get; set; }
        public string? State { get; set; }
        public string? PresenceToken { get; set; }
        public long? Sequence { get; set; }
    }

    public PresenceController(
        PresenceStateService presence,
        IUserClientStateService? clientStateService = null,
        ILogger<PresenceController>? logger = null,
        IDataProtectionProvider? dataProtection = null,
        SystemConfigRepository? systemConfigs = null,
        EmployeeRepository? employees = null,
        TimeProvider? timeProvider = null)
    {
        _presence = presence;
        _clientStateService = clientStateService;
        _logger = logger;
        _dataProtection = dataProtection;
        _systemConfigs = systemConfigs;
        _employees = employees;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string CreatePresenceToken(
        IDataProtectionProvider dataProtection,
        string subscriptionId,
        string employeeId,
        TimeSpan? lifetime = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(employeeId);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var expiresAt = now.Add(lifetime ?? DefaultTokenLifetime).ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new PresenceBeaconTokenPayload(subscriptionId, employeeId, expiresAt));
        return dataProtection.CreateProtector(PresenceTokenPurpose).Protect(payload);
    }

    /// <summary>
    /// Instantly mark a connection offline. Used during pagehide/app termination to bypass TCP timeouts.
    /// AllowAnonymous because during iOS teardown or >24h idle tabs, cookies may be omitted;
    /// requires hardened same-origin headers plus either an authenticated owner session or a signed presenceToken.
    /// </summary>
    [HttpPost("offline")]
    public IActionResult MarkOffline(
        [FromQuery] string? subscriptionId = null,
        [FromBody] PresenceUpdateRequest? body = null,
        [FromQuery] string? presenceToken = null,
        [FromQuery] long? seq = null)
    {
        if (!TryResolveAndAuthorize(subscriptionId, body, presenceToken, out var resolvedSubId, out var failureResult))
        {
            return failureResult!;
        }

        if (!string.IsNullOrEmpty(resolvedSubId))
        {
            var resolvedSeq = body?.Sequence ?? seq;
            _presence.MarkConnectionOffline(resolvedSubId, resolvedSeq);
        }
        return Ok();
    }

    /// <summary>
    /// Instantly mark a connection as unfocused. Used during app suspension to bypass SignalR TCP timeouts without terminating the circuit.
    /// </summary>
    [HttpPost("unfocus")]
    public IActionResult Unfocus(
        [FromQuery] string? subscriptionId = null,
        [FromBody] PresenceUpdateRequest? body = null,
        [FromQuery] string? presenceToken = null,
        [FromQuery] long? seq = null)
    {
        if (!TryResolveAndAuthorize(subscriptionId, body, presenceToken, out var resolvedSubId, out var failureResult))
        {
            return failureResult!;
        }

        _logger?.LogDebug("[PresenceController] Unfocus endpoint hit for subscriptionId: {SubscriptionId}", resolvedSubId);
        if (!string.IsNullOrEmpty(resolvedSubId))
        {
            var resolvedSeq = body?.Sequence ?? seq;
            if (_presence.SetConnectionFocusBySubscription(resolvedSubId, false, resolvedSeq))
            {
                StopActiveTypingForSubscription(resolvedSubId);
            }
            _logger?.LogDebug("[PresenceController] SetConnectionFocusBySubscription called for {SubscriptionId}", resolvedSubId);
        }
        return Ok();
    }

    /// <summary>
    /// Updates the explicit presence state (online, away, dnd, offline) for a connection.
    /// Used by Electron OS presence events, system tray status menu, and browser 5-minute idle timers.
    /// </summary>
    [HttpPost("state")]
    public IActionResult SetState(
        [FromQuery] string? subscriptionId = null,
        [FromQuery] string? state = null,
        [FromBody] PresenceUpdateRequest? body = null,
        [FromQuery] string? presenceToken = null,
        [FromQuery] long? seq = null)
    {
        var resolvedState = !string.IsNullOrWhiteSpace(body?.State) ? body.State : state;
        if (!TryResolveAndAuthorize(subscriptionId, body, presenceToken, out var resolvedSubId, out var failureResult,
                requireSubscriptionAuthWhenStateProvided: !string.IsNullOrEmpty(resolvedState)))
        {
            return failureResult!;
        }

        if (!string.IsNullOrEmpty(resolvedSubId) && !string.IsNullOrEmpty(resolvedState))
        {
            var normalized = resolvedState.Trim().ToLowerInvariant();
            var parsedState = normalized switch
            {
                "away" or "idle" => ConnectionPresenceState.Idle,
                "dnd" => ConnectionPresenceState.DoNotDisturb,
                "offline" or "invisible" => ConnectionPresenceState.Offline,
                _ => ConnectionPresenceState.Active
            };

            var resolvedSeq = body?.Sequence ?? seq;
            if (_presence.SetConnectionPresenceState(resolvedSubId, parsedState, resolvedSeq) &&
                parsedState is ConnectionPresenceState.Idle or ConnectionPresenceState.Offline)
            {
                StopActiveTypingForSubscription(resolvedSubId);
            }
        }
        return Ok();
    }

    private bool TryResolveAndAuthorize(
        string? subscriptionId,
        PresenceUpdateRequest? body,
        string? presenceToken,
        out string? resolvedSubId,
        out IActionResult? failureResult,
        bool requireSubscriptionAuthWhenStateProvided = true)
    {
        resolvedSubId = !string.IsNullOrWhiteSpace(body?.SubscriptionId) ? body.SubscriptionId : subscriptionId;
        var resolvedToken = !string.IsNullOrWhiteSpace(body?.PresenceToken) ? body.PresenceToken : presenceToken;
        return TryAuthorizePresenceRequest(resolvedSubId, resolvedToken, out failureResult, requireSubscriptionAuthWhenStateProvided);
    }

    private void StopActiveTypingForSubscription(string subscriptionId)
    {
        var userId = _presence.GetUserIdBySubscription(subscriptionId);
        if (!string.IsNullOrEmpty(userId))
        {
            _clientStateService?.StopActiveTyping(userId);
        }
    }

    private string? GetConfiguredPublicUrl()
    {
        var repo = _systemConfigs ?? HttpContext?.RequestServices?.GetService<SystemConfigRepository>();
        return repo?.Get()?.ServerPublicUrl;
    }

    private EmployeeRepository? GetEmployees() =>
        _employees ?? HttpContext?.RequestServices?.GetService<EmployeeRepository>();

    private bool TryAuthorizePresenceRequest(
        string? subscriptionId,
        string? presenceToken,
        out IActionResult? failureResult,
        bool requireSubscriptionAuthWhenStateProvided = true)
    {
        failureResult = null;
        bool hasVerifiedToken = !string.IsNullOrEmpty(subscriptionId) &&
                                IsValidPresenceTokenForSubscription(subscriptionId, presenceToken);

        if (!ServerUrlResolver.IsTrustedPresenceBeaconRequest(HttpContext, GetConfiguredPublicUrl(), hasVerifiedToken))
        {
            failureResult = ForbiddenError("Cross-origin presence updates are not permitted.");
            return false;
        }

        if (string.IsNullOrEmpty(subscriptionId) || !requireSubscriptionAuthWhenStateProvided)
        {
            return true;
        }

        if (!hasVerifiedToken && !IsAuthenticatedCookieOwnerForSubscription(subscriptionId))
        {
            failureResult = ForbiddenError("Subscription does not belong to the authenticated user.");
            return false;
        }

        return true;
    }

    private bool IsAuthenticatedCookieOwnerForSubscription(string subscriptionId)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var callerId = User.FindFirst("EmployeeId")?.Value;
        if (string.IsNullOrWhiteSpace(callerId) || User.HasClaim("PendingApproval", "true"))
        {
            return false;
        }

        var employees = GetEmployees();
        var employee = employees?.GetById(callerId);
        if (employee != null && (!employee.IsActive || employee.IsBanned))
        {
            return false;
        }

        var subscriptionUserId = _presence.GetUserIdBySubscription(subscriptionId);
        if (!string.IsNullOrEmpty(subscriptionUserId))
        {
            return string.Equals(callerId, subscriptionUserId, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private bool IsValidPresenceTokenForSubscription(string subscriptionId, string? presenceToken)
    {
        if (string.IsNullOrWhiteSpace(presenceToken))
        {
            return false;
        }

        var provider = _dataProtection ?? HttpContext?.RequestServices?.GetService<IDataProtectionProvider>();
        if (provider == null)
        {
            return false;
        }

        try
        {
            var raw = provider.CreateProtector(PresenceTokenPurpose).Unprotect(presenceToken);
            var payload = JsonSerializer.Deserialize<PresenceBeaconTokenPayload>(raw);
            var nowUnix = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (payload == null ||
                payload.ExpiresUnixSeconds < nowUnix ||
                !string.Equals(payload.SubscriptionId, subscriptionId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(payload.EmployeeId))
            {
                return false;
            }

            var employees = GetEmployees();
            var employee = employees?.GetById(payload.EmployeeId);
            if (employee != null && (!employee.IsActive || employee.IsBanned))
            {
                return false;
            }

            var subscriptionUserId = _presence.GetUserIdBySubscription(subscriptionId);
            return string.IsNullOrEmpty(subscriptionUserId) ||
                   string.Equals(payload.EmployeeId, subscriptionUserId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
