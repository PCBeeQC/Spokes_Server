using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Security;
using Microsoft.Extensions.Logging;
using Lib.Net.Http.WebPush;
using System.Text.Json.Serialization;

namespace Spokes_Server.Controllers;

/// <summary>
/// API controller for managing Web Push subscriptions and notification devices.
/// </summary>
[ApiController]
[Route("spokesapi/push")]
[Authorize]
[Microsoft.AspNetCore.Cors.EnableCors("AllowPublicApi")]
public class PushController : SpokesControllerBase
{
    private readonly DeviceSessionRepository _sessions;
    private readonly EmployeeRepository _employees;
    private readonly IWebPushService _webPush;
    private readonly UserService _userService;
    private readonly ILogger<PushController>? _logger;
    private readonly IPushLicenseEligibilityService _licenseEligibility;
    private readonly PushTokenService? _pushTokenService;
    private readonly ISystemLogService? _systemLog;
    private readonly INotificationRoutingService? _routingService;

    [ActivatorUtilitiesConstructor]
    public PushController(
        DeviceSessionRepository sessions,
        EmployeeRepository employees,
        IWebPushService webPush,
        UserService userService,
        IPushLicenseEligibilityService licenseEligibility,
        ILogger<PushController>? logger = null,
        IDataProtectionProvider? dataProtection = null,
        ISystemLogService? systemLog = null,
        INotificationRoutingService? routingService = null,
        PushTokenService? pushTokenService = null)
    {
        _sessions = sessions;
        _employees = employees;
        _webPush = webPush;
        _userService = userService;
        _licenseEligibility = licenseEligibility ?? throw new System.ArgumentNullException(nameof(licenseEligibility));
        _logger = logger;
        _pushTokenService = pushTokenService ?? (dataProtection != null ? new PushTokenService(dataProtection) : null);
        _systemLog = systemLog;
        _routingService = routingService;
    }



    /// <summary>
    /// Get the VAPID public key for client-side subscription.
    /// </summary>
    [HttpGet("vapid-public-key")]
    public IActionResult GetVapidPublicKey()
    {
        var publicKey = _webPush.GetVapidPublicKey();
        if (string.IsNullOrEmpty(publicKey))
        {
            return BadRequestError("Push notifications not configured");
        }
        return Ok(new VapidPublicKeyResponse(publicKey));
    }

    /// <summary>
    /// Subscribe to push notifications.
    /// Stamps push fields directly onto the caller's DeviceSession.
    /// </summary>
    [HttpPost("subscribe")]
    public IActionResult Subscribe([FromBody] SubscribeRequest request)
    {
        // For NativeRelay, P256dh and Auth might be empty, so skip those checks if NativeRelay.
        if (string.IsNullOrEmpty(request.Endpoint) ||
            (request.SubscriptionType != PushSubscriptionType.NativeRelay && (string.IsNullOrEmpty(request.P256dh) || string.IsNullOrEmpty(request.Auth))))
        {
            return BadRequestError("Invalid subscription data");
        }

        _logger?.LogDebug("[Push/Subscribe] Received subscription for {DeviceType} ({SubscriptionType}). PublicKey length: {KeyLength}", request.DeviceType, request.SubscriptionType, request.PublicKey?.Length ?? 0);

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var sessionId = User.FindFirst("SessionId")?.Value;

        // Find the caller's DeviceSession using multiple strategies
        DeviceSession? session = null;

        // Strategy 1: SessionId claim (most reliable for authenticated requests)
        if (!string.IsNullOrEmpty(sessionId))
        {
            session = _sessions.GetById(sessionId);
            // Verify session isn't revoked or expired and belongs to caller
            if (session != null && (session.RevokedAt != null || session.ExpiresAt <= DateTime.UtcNow || session.EmployeeId != userId))
            {
                session = null;
            }
        }

        // Strategy 2: DeviceId + UserId (fallback)
        if (session == null && !string.IsNullOrEmpty(request.DeviceId))
        {
            session = _sessions.GetActiveByEmployeeId(userId)
                .FirstOrDefault(s => s.DeviceId == request.DeviceId);
        }

        if (session == null)
        {
            return BadRequestError("No active session found. Please log in again.");
        }


        // Dedup: If another session owned by the same user already has this push endpoint, clear it.
        // Never clear push endpoints on other users' sessions (prevents cross-user DoS).
        var existingWithEndpoint = _sessions.GetByPushEndpoint(request.Endpoint);
        if (existingWithEndpoint != null && existingWithEndpoint.Id != session.Id && existingWithEndpoint.EmployeeId == userId)
        {
            _sessions.ClearPushFields(existingWithEndpoint);
        }

        var subscriptionType = !string.IsNullOrEmpty(request.SubscriptionType) ? request.SubscriptionType : PushSubscriptionType.WebPush;

        // Gate NativeRelay registrations: require an active Spokes Pass or valid trial ticket
        if (subscriptionType == PushSubscriptionType.NativeRelay)
        {
            if (!_licenseEligibility.IsNativePushAllowed())
            {
                return ForbiddenError("Native push notifications require an active Spokes Pass.", "PASS_EXPIRED");
            }
        }

        // Stamp push fields onto the DeviceSession
        session.PushEndpoint = request.Endpoint;
        session.PushP256dh = request.P256dh;
        session.PushAuth = request.Auth;
        session.PushSubscriptionType = subscriptionType;
        if (!string.IsNullOrWhiteSpace(request.PublicKey))
        {
            session.PushPublicKey = request.PublicKey.Trim();
        }
        else
        {
            session.PushPublicKey ??= string.Empty;
        }
        session.PushEnabled = true;
        session.PushSubscribedAt = DateTime.UtcNow;
        session.PushUserAgent = Request.Headers.UserAgent.ToString();

        bool isExplicitDesktop = (!string.IsNullOrEmpty(request.DeviceType) && request.DeviceType.Equals(DeviceType.Desktop, StringComparison.OrdinalIgnoreCase))
            || DeviceDetectionHelper.IsDesktopClient(session.PushUserAgent);

        bool isCapacitor;
        if (request.IsCapacitor == true)
        {
            isCapacitor = true;
        }
        else if (isExplicitDesktop)
        {
            isCapacitor = false;
        }
        else
        {
            isCapacitor = session.IsCapacitorApp
                || session.PushUserAgent.Contains("Capacitor", StringComparison.OrdinalIgnoreCase)
                || (subscriptionType == PushSubscriptionType.NativeRelay && request.IsCapacitor != false && string.Equals(session.DeviceType, DeviceType.Mobile, StringComparison.OrdinalIgnoreCase));
        }

        if (isCapacitor)
        {
            session.IsCapacitor = true;
            session.DeviceType = DeviceType.Mobile;
            session.IsIdleDetectionEnabled = false;
        }
        else
        {
            session.IsCapacitor = false;
            session.DeviceType = !string.IsNullOrEmpty(request.DeviceType) ? request.DeviceType : DeviceType.Desktop;
            session.IsIdleDetectionEnabled = request.IsIdleDetectionEnabled ?? (request.DeviceType == DeviceType.Desktop);
        }

        if (!string.IsNullOrEmpty(request.DeviceName))
            session.DeviceName = request.DeviceName;

        _sessions.Save(session);

        // Clean up stale push subscriptions from old sessions on the same device.
        // With stable device IDs (ANDROID_ID / iOS Keychain UUID), matching by deviceId
        // reliably identifies the same physical device across reinstalls.
        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            var staleSessions = _sessions.GetPushEnabledByEmployeeId(userId)
                .Where(s => s.Id != session.Id
                    && s.DeviceId == session.DeviceId)
                .ToList();

            foreach (var stale in staleSessions)
            {
                _logger?.LogDebug("[Push/Subscribe] Cleaning stale push on session {SessionId} (same deviceId: {DeviceId})", stale.Id, session.DeviceId);
                _sessions.ClearPushFields(stale);
            }
        }

        return Ok(new PushSubscribeResponse("Subscribed successfully", session.Id, true));
    }

    /// <summary>
    /// Unsubscribe from push notifications.
    /// </summary>
    [HttpPost("unsubscribe")]
    [HttpDelete("unsubscribe")]
    public IActionResult Unsubscribe([FromBody] UnsubscribeRequest request)
    {
        if (string.IsNullOrEmpty(request.Endpoint))
        {
            return BadRequestError("Endpoint is required");
        }

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        // Security: Only allow unsubscribing sessions owned by the current user
        var session = _sessions.GetByPushEndpoint(request.Endpoint);
        if (session == null || session.EmployeeId != userId)
        {
            // Don't reveal whether the subscription exists or belongs to someone else
            return Ok(new PushMessageResponse("No subscription found"));
        }

        _sessions.ClearPushFields(session);
        return Ok(new PushMessageResponse("Unsubscribed successfully"));
    }

    /// <summary>
    /// Get current user's subscription status.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var sessionsWithPush = _sessions.GetPushEnabledByEmployeeId(userId);
        var employee = _employees.GetById(userId);

        var nativeStatus = _licenseEligibility.GetNativePushStatus();

        return Ok(new PushStatusResponse(
            _webPush.IsConfigured,
            employee?.PushNotificationsEnabled ?? true,
            sessionsWithPush.Count,
            new NativePushStatusResponse(nativeStatus.IsAvailable, nativeStatus.Status ?? string.Empty, nativeStatus.Message ?? string.Empty)));
    }

    // --- Device Management ---

    /// <summary>
    /// Get all registered notification devices for the current user.
    /// Returns DeviceSession records directly — no join logic needed.
    /// </summary>
    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var sessions = _sessions.GetActiveByEmployeeId(userId);
        var devices = sessions.Select(s => new DeviceListItemResponse(
            s.Id,
            s.DeviceType,
            s.DeviceName,
            s.DeviceInfo,
            s.HasPush && s.PushEnabled,
            s.IsIdleDetectionEnabled,
            s.PushSubscriptionType,
            s.CreatedAt,
            s.LastSeenAt,
            s.HasPush,
            s.PushEndpoint ?? string.Empty,
            s.PushUserAgent,
            s.DeviceId
        )).ToList();

        return Ok(devices);
    }

    /// <summary>
    /// Update a notification device (rename, enable/disable).
    /// </summary>
    [HttpPut("devices/{id}")]
    public IActionResult UpdateDevice(string id, [FromBody] UpdateDeviceRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var session = _sessions.GetById(id);
        if (session == null || session.EmployeeId != userId)
        {
            return NotFoundError("Device not found");
        }

        if (request.DeviceName != null)
            session.DeviceName = request.DeviceName;
        if (request.DeviceType != null)
            session.DeviceType = request.DeviceType;
        if (request.IsEnabled.HasValue)
            session.PushEnabled = request.IsEnabled.Value;
        if (request.IsIdleDetectionEnabled.HasValue)
            session.IsIdleDetectionEnabled = request.IsIdleDetectionEnabled.Value;

        if (session.IsCapacitorApp)
        {
            session.IsIdleDetectionEnabled = false;
        }

        _sessions.Save(session);
        return Ok(new PushMessageResponse("Device updated"));
    }

    /// <summary>
    /// Delete a specific notification device (clears push data, does not revoke the session).
    /// </summary>
    [HttpDelete("devices/{id}")]
    public IActionResult DeleteDevice(string id)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var session = _sessions.GetById(id);
        if (session == null || session.EmployeeId != userId)
        {
            return NotFoundError("Device not found");
        }

        _sessions.ClearPushFields(session);
        return Ok(new PushMessageResponse("Device deleted"));
    }

    /// <summary>
    /// Send a test notification to a specific device.
    /// </summary>
    [HttpPost("devices/{id}/test")]
    public async Task<IActionResult> SendDeviceTestNotification(string id, [FromQuery] string category = "chat")
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var sender = _employees.GetById(userId);
        if (sender == null) return UnauthorizedError("Authentication required.", "UNAUTHORIZED");

        var icon = _pushTokenService?.GetAvatarUrl(userId) ?? $"/spokesapi/Media/Avatar/{userId}";

        try
        {
            var testResult = await _webPush.SendDeviceTestNotificationAsync(new DeviceTestNotificationRequest
            {
                SubscriptionId = id,
                UserId = userId,
                Title = "Test Notification",
                Body = $"Rich push notification test from {sender.FirstName}",
                Icon = icon,
                Category = category
            });
            return Ok(new PushMessageResponse(testResult.IsPayloadEncrypted ? "Test notification sent (Encrypted)" : "Test notification sent (Encrypted Text)"));
        }
        catch (DomainException dex) when (!string.IsNullOrEmpty(dex.ErrorCode))
        {
            _systemLog?.LogError("Notifications", $"Test push failed for device {id}", $"{dex.ErrorCode}: {dex.Message}");
            return BadRequestError(dex.Message, dex.ErrorCode);
        }
        catch (PushServiceClientException wpe)
        {
            _systemLog?.LogError("Notifications", $"Test push failed for device {id}", $"WebPush Error: {wpe.Message} (Status: {wpe.StatusCode})");
            return BadRequestError($"WebPush Error: {wpe.Message} (Status: {wpe.StatusCode})");
        }
        catch (Exception ex)
        {
            _systemLog?.LogError("Notifications", $"Test push failed for device {id}", ex.Message);
            return BadRequestError(ex.Message);
        }
    }

    /// <summary>
    /// Send a test notification to the current user.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTestNotification([FromQuery] string category = "chat")
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        var sender = _employees.GetById(userId);
        var userSoundChoice = sender?.GetNotificationSound(category);
        var sound = NotificationSoundCatalog.GetEffectivePushSound(category, userSoundChoice);

        await _webPush.SendNotificationAsync(new PushNotificationRequest
        {
            UserId = userId,
            Title = "Test Notification",
            Body = "Push notifications are working!",
            Category = category,
            Sound = sound
        });
        return Ok(new PushMessageResponse("Test notification sent"));
    }

    /// <summary>
    /// Get the total unread badge count for the current user.
    /// </summary>
    [HttpGet("unread-count")]
    public IActionResult GetUnreadCount()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return UnauthorizedError("Authentication required.", "UNAUTHORIZED");
        }

        if (_routingService == null)
        {
            return StatusCodeError(StatusCodes.Status500InternalServerError, "Notification routing service not available.", "SERVICE_UNAVAILABLE");
        }

        var result = _routingService.GetBadgeBreakdown(userId);
        return Ok(result);
    }


    private string? GetCurrentUserId()
    {
        var employee = _userService.GetEmployee(User);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return null;
        return employee.Id;
    }
}

public class SubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
    public string? SubscriptionType { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceName { get; set; }
    public bool? IsIdleDetectionEnabled { get; set; }
    public bool? IsCapacitor { get; set; }
    public string? PublicKey { get; set; }
    public string? DeviceId { get; set; }
}

public class UnsubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
}

public class UpdateDeviceRequest
{
    public string? DeviceName { get; set; }
    public string? DeviceType { get; set; }
    public bool? IsEnabled { get; set; }
    public bool? IsIdleDetectionEnabled { get; set; }
}

public record PushSubscribeResponse(string Message, string Id, bool isNativePushLicensed);

public record PushMessageResponse(string Message);

public record DeviceListItemResponse(
    string Id,
    string? DeviceType,
    string? DeviceName,
    string? DeviceInfo,
    bool IsEnabled,
    bool IsIdleDetectionEnabled,
    string? PushSubscriptionType,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    bool HasPush,
    string Endpoint,
    string? UserAgent,
    string? DeviceId);

public record VapidPublicKeyResponse([property: JsonPropertyName("publicKey")] string publicKey);

public record NativePushStatusResponse(
    [property: JsonPropertyName("isAvailable")] bool isAvailable,
    [property: JsonPropertyName("status")] string status,
    [property: JsonPropertyName("message")] string message);

public record PushStatusResponse(
    [property: JsonPropertyName("isConfigured")] bool isConfigured,
    [property: JsonPropertyName("enabled")] bool enabled,
    [property: JsonPropertyName("subscriptionCount")] int subscriptionCount,
    [property: JsonPropertyName("nativePush")] NativePushStatusResponse nativePush);
