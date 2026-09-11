using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Security.Claims;
using WebPushException = WebPush.WebPushException;
using Microsoft.AspNetCore.DataProtection;
using Spokes_Server.Core.Services.Licensing;
using Microsoft.Extensions.DependencyInjection;

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

    public PushController(DeviceSessionRepository sessions, EmployeeRepository employees, IWebPushService webPush, UserService userService)
    {
        _sessions = sessions;
        _employees = employees;
        _webPush = webPush;
        _userService = userService;
    }

    /// <summary>
    /// Get the VAPID public key for client-side subscription.
    /// </summary>
    [HttpGet("vapid-public-key")]
    public async Task<IActionResult> GetVapidPublicKey()
    {
        await Task.CompletedTask;
        var publicKey = _webPush.GetVapidPublicKey();
        if (string.IsNullOrEmpty(publicKey))
        {
            return BadRequest(new { error = "Push notifications not configured" });
        }
        return Ok(new { publicKey });
    }

    /// <summary>
    /// Subscribe to push notifications.
    /// Stamps push fields directly onto the caller's DeviceSession.
    /// </summary>
    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe(
        [FromBody] SubscribeRequest request,
        [FromServices] CompanyProfileRepository? companyProfiles = null,
        [FromServices] ServerConfigRepository? serverConfigs = null,
        [FromServices] LicenseValidationService? licenseValidation = null)
    {
        // For NativeRelay, P256dh and Auth might be empty, so skip those checks if NativeRelay.
        if (string.IsNullOrEmpty(request.Endpoint) ||
            (request.SubscriptionType != "NativeRelay" && (string.IsNullOrEmpty(request.P256dh) || string.IsNullOrEmpty(request.Auth))))
        {
            return BadRequest(new { error = "Invalid subscription data" });
        }

        Console.WriteLine($"[Push/Subscribe] Received subscription for {request.DeviceType} ({request.SubscriptionType}). PublicKey length: {request.PublicKey?.Length ?? 0}");

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var resolvedDeviceId = request.DeviceId;
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
        if (session == null && !string.IsNullOrEmpty(resolvedDeviceId))
        {
            session = _sessions.GetActiveByEmployeeId(userId)
                .FirstOrDefault(s => s.DeviceId == resolvedDeviceId);
        }

        if (session == null)
        {
            return BadRequest(new { error = "No active session found. Please log in again." });
        }


        // Dedup: If another session owned by the same user already has this push endpoint, clear it.
        // Never clear push endpoints on other users' sessions (prevents cross-user DoS).
        var existingWithEndpoint = _sessions.GetByPushEndpoint(request.Endpoint);
        if (existingWithEndpoint != null && existingWithEndpoint.Id != session.Id && existingWithEndpoint.EmployeeId == userId)
        {
            _sessions.ClearPushFields(existingWithEndpoint);
        }

        // Stamp push fields onto the DeviceSession
        session.PushEndpoint = request.Endpoint;
        session.PushP256dh = request.P256dh;
        session.PushAuth = request.Auth;
        session.PushSubscriptionType = !string.IsNullOrEmpty(request.SubscriptionType) ? request.SubscriptionType : "WebPush";
        session.PushPublicKey = request.PublicKey ?? string.Empty;
        session.PushEnabled = true;
        session.PushSubscribedAt = DateTime.UtcNow;
        session.PushUserAgent = Request.Headers.UserAgent.ToString();
        session.DeviceType = !string.IsNullOrEmpty(request.DeviceType) ? request.DeviceType : "Desktop";
        session.IsIdleDetectionEnabled = request.IsIdleDetectionEnabled ?? (request.DeviceType == "Desktop");
        if (!string.IsNullOrEmpty(request.DeviceName))
            session.DeviceName = request.DeviceName;

        await _sessions.SaveAsync(session);

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
                Console.WriteLine($"[Push/Subscribe] Cleaning stale push on session {stale.Id} (same deviceId: {session.DeviceId})");
                _sessions.ClearPushFields(stale);
            }
        }

        bool isNativePushLicensed = true;
        if (session.PushSubscriptionType == "NativeRelay")
        {
            var profilesRepo = companyProfiles ?? HttpContext?.RequestServices?.GetService<CompanyProfileRepository>();
            var configsRepo = serverConfigs ?? HttpContext?.RequestServices?.GetService<ServerConfigRepository>();
            var licenseService = licenseValidation ?? HttpContext?.RequestServices?.GetService<LicenseValidationService>();

            if (profilesRepo != null && configsRepo != null && licenseService != null)
            {
                var profile = profilesRepo.Get();
                var serverConfig = configsRepo.GetOrCreateGlobalConfig();
                var licenseResult = licenseService.ValidateLicense(profile?.LicensePayload, serverConfig);
                isNativePushLicensed = licenseResult.Status != LicenseStatus.HardLock && licenseResult.Status != LicenseStatus.Expired;
            }
        }

        return Ok(new { 
            message = "Subscribed successfully", 
            id = session.Id,
            isNativePushLicensed = isNativePushLicensed
        });
    }

    /// <summary>
    /// Unsubscribe from push notifications.
    /// </summary>
    [HttpPost("unsubscribe")]
    [HttpDelete("unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeRequest request)
    {
        if (string.IsNullOrEmpty(request.Endpoint))
        {
            return BadRequest(new { error = "Endpoint is required" });
        }

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        // Security: Only allow unsubscribing sessions owned by the current user
        var session = _sessions.GetByPushEndpoint(request.Endpoint);
        if (session == null)
        {
            return Ok(new { message = "No subscription found" });
        }

        if (session.EmployeeId != userId)
        {
            // Don't reveal that the subscription exists but belongs to someone else
            return Ok(new { message = "No subscription found" });
        }

        _sessions.ClearPushFields(session);
        await Task.CompletedTask;
        return Ok(new { message = "Unsubscribed successfully" });
    }

    /// <summary>
    /// Get current user's subscription status.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromServices] CompanyProfileRepository? companyProfiles = null,
        [FromServices] ServerConfigRepository? serverConfigs = null,
        [FromServices] LicenseValidationService? licenseValidation = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var sessionsWithPush = _sessions.GetPushEnabledByEmployeeId(userId);
        var employee = await _employees.GetByIdAsync(userId);

        var profilesRepo = companyProfiles ?? HttpContext?.RequestServices?.GetService<CompanyProfileRepository>();
        var configsRepo = serverConfigs ?? HttpContext?.RequestServices?.GetService<ServerConfigRepository>();
        var licenseService = licenseValidation ?? HttpContext?.RequestServices?.GetService<LicenseValidationService>();

        bool isNativeRelayAvailable = true;
        string? status = null;
        string? message = null;

        if (profilesRepo != null && configsRepo != null && licenseService != null)
        {
            var profile = profilesRepo.Get();
            var serverConfig = configsRepo.GetOrCreateGlobalConfig();
            var licenseResult = licenseService.ValidateLicense(profile?.LicensePayload, serverConfig);
            isNativeRelayAvailable = licenseResult.Status != LicenseStatus.HardLock && licenseResult.Status != LicenseStatus.Expired;
            status = licenseResult.Status.ToString();
            message = licenseResult.Message;
        }

        return Ok(new
        {
            isConfigured = _webPush.IsConfigured,
            enabled = employee?.PushNotificationsEnabled ?? true,
            subscriptionCount = sessionsWithPush.Count,
            nativePush = new
            {
                isAvailable = isNativeRelayAvailable,
                status = status,
                message = message
            }
        });
    }

    // --- Device Management ---

    /// <summary>
    /// Get all registered notification devices for the current user.
    /// Returns DeviceSession records directly — no join logic needed.
    /// </summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await Task.CompletedTask;
        var sessions = _sessions.GetActiveByEmployeeId(userId);
        var devices = sessions.Select(s => new
        {
            s.Id,
            s.DeviceType,
            s.DeviceName,
            s.DeviceInfo,
            IsEnabled = s.HasPush && s.PushEnabled,
            s.IsIdleDetectionEnabled,
            s.PushSubscriptionType,
            s.CreatedAt,
            s.LastSeenAt,
            HasPush = s.HasPush,
            Endpoint = s.PushEndpoint ?? string.Empty,
            UserAgent = s.PushUserAgent,
            s.DeviceId
        }).ToList();

        return Ok(devices);
    }

    /// <summary>
    /// Update a notification device (rename, enable/disable).
    /// </summary>
    [HttpPut("devices/{id}")]
    public async Task<IActionResult> UpdateDevice(string id, [FromBody] UpdateDeviceRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var session = await _sessions.GetByIdAsync(id);
        if (session == null || session.EmployeeId != userId)
        {
            return NotFound(new { error = "Device not found" });
        }

        if (request.DeviceName != null)
            session.DeviceName = request.DeviceName;
        if (request.DeviceType != null)
            session.DeviceType = request.DeviceType;
        if (request.IsEnabled.HasValue)
            session.PushEnabled = request.IsEnabled.Value;
        if (request.IsIdleDetectionEnabled.HasValue)
            session.IsIdleDetectionEnabled = request.IsIdleDetectionEnabled.Value;

        await _sessions.SaveAsync(session);
        return Ok(new { message = "Device updated" });
    }

    /// <summary>
    /// Delete a specific notification device (clears push data, does not revoke the session).
    /// </summary>
    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> DeleteDevice(string id)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var session = await _sessions.GetByIdAsync(id);
        if (session == null || session.EmployeeId != userId)
        {
            return NotFound(new { error = "Device not found" });
        }

        _sessions.ClearPushFields(session);
        await Task.CompletedTask;
        return Ok(new { message = "Device deleted" });
    }

    /// <summary>
    /// Send a test notification to a specific device.
    /// </summary>
    [HttpPost("devices/{id}/test")]
    public async Task<IActionResult> SendDeviceTestNotification(string id, 
        [FromServices] Microsoft.AspNetCore.DataProtection.IDataProtectionProvider dataProtection,
        [FromServices] Spokes_Server.Core.Services.Logging.ISystemLogService systemLog)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var sender = await _employees.GetByIdAsync(userId);
        if (sender == null) return Unauthorized();

        var protector = dataProtection.CreateProtector("AvatarPushToken");
        var safeUserId = userId.Replace("|", "");
        var token = protector.Protect($"{safeUserId}|{DateTime.UtcNow.AddHours(48).Ticks}");
        var encodedToken = System.Net.WebUtility.UrlEncode(token);
        var icon = $"/spokesapi/Media/Avatar/{userId}?t={encodedToken}";

        try
        {
            bool wasEncrypted = await _webPush.SendDeviceTestNotificationAsync(id, userId, "Test Notification", $"Rich push notification test from {sender.FirstName}", icon);
            return Ok(new { message = wasEncrypted ? "Test notification sent (Encrypted)" : "Test notification sent (Encrypted Text)" });
        }
        catch (LicenseExpiredException lex)
        {
            systemLog.LogError("Notifications", $"Test push failed for device {id}", lex.Message);
            return BadRequest(new { error = lex.Message, code = "LICENSE_EXPIRED" });
        }
        catch (WebPushException wpe)
        {
            systemLog.LogError("Notifications", $"Test push failed for device {id}", $"WebPush Error: {wpe.Message} (Status: {wpe.StatusCode})");
            return BadRequest(new { error = $"WebPush Error: {wpe.Message} (Status: {wpe.StatusCode})" });
        }
        catch (Exception ex)
        {
            systemLog.LogError("Notifications", $"Test push failed for device {id}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Send a test notification to the current user.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTestNotification()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        await _webPush.SendNotificationAsync(userId, "Test Notification", "Push notifications are working!");
        return Ok(new { message = "Test notification sent" });
    }

    /// <summary>
    /// Get the total unread badge count for the current user.
    /// </summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount([FromServices] NotificationRoutingService routingService)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var result = await routingService.GetBadgeBreakdownAsync(userId);
        return Ok(result);
    }


    private string? GetCurrentUserId()
    {
        var employee = _userService.GetEmployee(User);
        if (employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned) return null;
        return employee.Id;
    }

    /// <summary>
    /// Instantly mark a connection offline. Used during pagehide/app termination to bypass TCP timeouts.
    /// AllowAnonymous because during iOS teardown, cookies might not be reliably attached to the sendBeacon request.
    /// The subscriptionId itself serves as a secure, ephemeral one-time token.
    /// </summary>
    [HttpPost("offline")]
    [AllowAnonymous]
    public async Task<IActionResult> MarkOffline([FromQuery] string subscriptionId, [FromServices] PresenceStateService presence)
    {
        await Task.CompletedTask;
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            presence.RemoveConnection(subscriptionId);
        }
        return Ok();
    }

    /// <summary>
    /// Instantly mark a connection as unfocused. Used during app suspension to bypass SignalR TCP timeouts without terminating the circuit.
    /// </summary>
    [HttpPost("unfocus")]
    [AllowAnonymous]
    public async Task<IActionResult> Unfocus([FromQuery] string subscriptionId, [FromServices] PresenceStateService presence)
    {
        Console.WriteLine($"[PushController] Unfocus endpoint hit for subscriptionId: {subscriptionId}");
        await Task.CompletedTask;
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            presence.SetConnectionFocusBySubscription(subscriptionId, false);
            Console.WriteLine($"[PushController] SetConnectionFocusBySubscription called for {subscriptionId}");
        }
        return Ok();
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
