using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;

using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using System.Text.Json;
using WebPush;
using WebPushSubscription = WebPush.PushSubscription;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Communication.Notifications;

/// <summary>
/// Service for sending Web Push notifications to users' browsers.
/// Uses VAPID (Voluntary Application Server Identification) for authentication.
/// Reads VAPID keys from CompanyProfile (configurable via Admin UI).
/// Supports device-tier-aware routing based on user presence.
/// </summary>
public class WebPushService : IWebPushService
{
    private readonly DeviceSessionRepository _sessions;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly PresenceStateService _presenceState;
    private readonly NotificationQueueService _notificationQueue;
    private readonly ILogger<WebPushService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly Spokes_Server.Core.Services.Logging.ISystemLogService _systemLog;
    private readonly Spokes_Server.Core.Services.Core.EncryptionService _encryptionService;
    private readonly Spokes_Server.Core.Services.Licensing.LicenseValidationService _licenseValidation;

    public WebPushService(
        DeviceSessionRepository sessions,
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        SystemConfigRepository systemConfigs,
        ServerConfigRepository serverConfigs,
        PresenceStateService presenceState,
        NotificationQueueService notificationQueue,
        ILogger<WebPushService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IDataProtectionProvider dataProtection,
        Spokes_Server.Core.Services.Logging.ISystemLogService systemLog,
        Spokes_Server.Core.Services.Core.EncryptionService encryptionService,
        Spokes_Server.Core.Services.Licensing.LicenseValidationService licenseValidation)
    {
        _sessions = sessions;
        _employees = employees;
        _companyProfile = companyProfile;
        _systemConfigs = systemConfigs;
        _serverConfigs = serverConfigs;
        _presenceState = presenceState;
        _notificationQueue = notificationQueue;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _dataProtection = dataProtection;
        _systemLog = systemLog;
        _encryptionService = encryptionService;
        _licenseValidation = licenseValidation;
    }

    /// <summary>
    /// Get the public VAPID key for client-side subscription.
    /// </summary>
    public string GetVapidPublicKey()
    {
        var profile = _companyProfile.Get();
        return profile?.VapidPublicKey ?? string.Empty;
    }

    /// <summary>
    /// Check if push notifications are configured.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var profile = _companyProfile.Get();
            return !string.IsNullOrEmpty(profile?.VapidPublicKey) &&
                   !string.IsNullOrEmpty(profile?.VapidPrivateKey);
        }
    }

    /// <summary>
    /// Determine which notification tier to use based on user presence.
    /// </summary>
    public PresenceTier DetermineNotificationTier(string userId)
    {
        // 1. User has app in focus anywhere (with push enabled) -> Only local UI, NO push notifications
        if (_presenceState.IsUserFocusedForPush(userId))
            return PresenceTier.None;

        // 2. User has a desktop tab open (with push enabled)...
        if (_presenceState.HasActiveDesktopConnectionForPush(userId))
        {
            // If they walked away over 1 minute ago, blast the phone!
            if (_presenceState.IsUserAwayFromDesktopForPush(userId))
                return PresenceTier.All;

            // Under 1 minute of inactivity, hold phone back.
            return PresenceTier.DesktopOnly;
        }

        // 3. Totally offline
        return PresenceTier.All;
    }

    /// <summary>
    /// Send a push notification to a user's subscribed browsers,
    /// optionally filtered by presence tier.
    /// </summary>
    public async Task SendNotificationAsync(string userId, string title, string body,
        string? url = null, string? icon = null, PresenceTier? tier = null, string? tag = null, object[]? actions = null, string category = "chat", string? threadId = null, string? serverName = null, string? channelName = null, bool isGroupChat = false, int? badge = null, bool isSilent = false)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
        {
            _logger.LogDebug("Push notifications not configured, skipping send");
            return;
        }

        // Check if user has push notifications enabled
        var employee = _employees.GetById(userId);
        if (employee == null || !employee.PushNotificationsEnabled)
        {
            _logger.LogDebug("User {UserId} has push notifications disabled", userId);
            return;
        }

        // Schedule gate: queue if outside user's active hours
        if (!_notificationQueue.IsWithinSchedule(employee))
        {
            _notificationQueue.Enqueue(new QueuedNotification
            {
                UserId = userId,
                Title = title,
                Body = body,
                Url = url,
                Icon = icon,
                Tag = tag,
                Actions = actions,
                Tier = tier,
                QueuedAtUtc = DateTime.UtcNow,
                ThreadId = threadId,
                ServerName = serverName,
                ChannelName = channelName,
                IsGroupChat = isGroupChat,
                Badge = badge
            });
            _logger.LogDebug("User {UserId} outside schedule, notification queued", userId);
            return;
        }



        await SendPushToSubscriptionsAsync(userId, title, body, url, icon, tier, tag, actions, profile, category, threadId, serverName, channelName, isGroupChat, badge, isSilent);
    }

    /// <summary>
    /// Send a silent push notification to clear delivered notifications for a specific channel on user devices.
    /// </summary>
    public async Task SendClearNotificationAsync(string userId, string threadId)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            return;

        var employee = _employees.GetById(userId);
        if (employee == null || !employee.PushNotificationsEnabled)
            return;

        await SendPushToSubscriptionsAsync(userId, "", "", null, null, PresenceTier.MobileOnly, null, null, profile, "clear_notification", threadId, null, null, false, 0, false);
    }

    /// <summary>
    /// Send a push notification directly, bypassing the schedule gate.
    /// Used by the background flush service for queued notifications.
    /// </summary>
    public async Task SendNotificationDirectAsync(string userId, string title, string body,
        string? url = null, string? icon = null, PresenceTier? tier = null, string? tag = null, object[]? actions = null, string category = "chat", string? threadId = null, string? serverName = null, string? channelName = null, bool isGroupChat = false, int? badge = null, bool isSilent = false)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            return;

        var employee = _employees.GetById(userId);
        if (employee == null || !employee.PushNotificationsEnabled)
            return;



        await SendPushToSubscriptionsAsync(userId, title, body, url, icon, tier, tag, actions, profile, category, threadId, serverName, channelName, isGroupChat, badge, isSilent);
    }

    /// <summary>
    /// Core push delivery logic shared by both SendNotificationAsync and SendNotificationDirectAsync.
    /// </summary>
    private async Task SendPushToSubscriptionsAsync(string userId, string title, string body,
        string? url, string? icon, PresenceTier? tier, string? tag, object[]? actions, Models.Core.CompanyProfile profile, string category, string? threadId, string? serverName, string? channelName, bool isGroupChat, int? badge, bool isSilent)
    {
        List<DeviceSession> subscriptions;
        if (tier == PresenceTier.DesktopOnly)
        {
            subscriptions = _sessions.GetPushEnabledByEmployeeId(userId, "Desktop");
            _logger.LogDebug("Sending desktop-only push to {Count} subscriptions for user {UserId}",
                subscriptions.Count, userId);
        }
        else if (tier == PresenceTier.MobileOnly)
        {
            subscriptions = _sessions.GetPushEnabledByEmployeeId(userId, "Mobile");
            _logger.LogDebug("Sending mobile-only push to {Count} subscriptions for user {UserId}",
                subscriptions.Count, userId);
        }
        else
        {
            subscriptions = _sessions.GetPushEnabledByEmployeeId(userId);
            _logger.LogDebug("Sending push to all {Count} enabled subscriptions for user {UserId}",
                subscriptions.Count, userId);
        }

        if (!subscriptions.Any())
        {
            _logger.LogDebug("No matching push subscriptions found for user {UserId}", userId);
            return;
        }

        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var token = protector.Protect($"{userId}|{DateTime.UtcNow.AddHours(48).Ticks}");
        var encodedToken = System.Net.WebUtility.UrlEncode(token);
        var defaultIcon = $"/spokesapi/Media/Icon?t={encodedToken}";

        var payload = new
        {
            title,
            body,
            icon = icon ?? defaultIcon,
            badge = defaultIcon,
            tag = tag,
            actions = actions,
            isSilent = isSilent,
            data = new { url = url ?? "/chat", threadId = threadId, serverName = serverName, channelName = channelName, isGroupChat = isGroupChat }
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        var pushClient = new WebPushClient();
        pushClient.SetVapidDetails(profile.VapidSubject, profile.VapidPublicKey, profile.VapidPrivateKey);

        var options = new Dictionary<string, object>
        {
            { "headers", new Dictionary<string, object> { { "urgency", "high" } } }
        };

        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        var licenseResult = _licenseValidation.ValidateLicense(profile.LicensePayload, serverConfig);
        bool isNativeRelayBlocked = licenseResult.Status == Spokes_Server.Core.Services.Licensing.LicenseStatus.HardLock || licenseResult.Status == Spokes_Server.Core.Services.Licensing.LicenseStatus.Expired;

        foreach (var sub in subscriptions)
        {
            try
            {
                if (category == "clear_notification" && sub.PushSubscriptionType != "NativeRelay")
                {
                    _logger.LogDebug("Skipping clear_notification for WebPush subscription {SubscriptionId} ({DeviceType}) as browser clients do not support silent data pushes", sub.Id, sub.DeviceType);
                    continue;
                }

                if (sub.PushSubscriptionType == "NativeRelay")
                {
                    if (isNativeRelayBlocked)
                    {
                        _logger.LogDebug("Skipping NativeRelay push for {SubscriptionId} because local license status is {Status}.", sub.Id, licenseResult.Status);
                        continue;
                    }

                    var httpClient = _httpClientFactory.CreateClient();
                    httpClient.DefaultRequestHeaders.Add("X-Relay-Key", SpokesConstants.PushRelayKey);

                    string? absoluteImageUrl = null;
                    string? originUrl = _systemConfigs.Get().ServerPublicUrl?.TrimEnd('/');

                    string serverId = serverConfig.DatabaseCreationId;
                    string serverIdSignature = serverConfig.DatabaseCreationIdSignature;
                    string licensePayload = profile.LicensePayload;
                    string databaseCreationVersion = serverConfig.DatabaseCreationVersion;
                    string databaseCreationSignature = serverConfig.DatabaseCreationSignature;
                    string keyHash = _encryptionService.KeyHash;

                    if (!string.IsNullOrEmpty(icon) && !string.IsNullOrEmpty(originUrl))
                    {
                        absoluteImageUrl = $"{originUrl}{icon}";
                    }

                    object relayPayload;
                    if (!string.IsNullOrEmpty(sub.PushPublicKey))
                    {
                        var innerPayload = new
                        {
                            Title = title,
                            Body = body,
                            Url = url ?? "/chat",
                            Category = category,
                            ImageUrl = absoluteImageUrl,
                            OriginUrl = originUrl,
                            ThreadId = threadId,
                            ServerName = serverName,
                            ChannelName = channelName,
                            IsGroupChat = isGroupChat,
                            ServerIconUrl = originUrl != null ? $"{originUrl}{defaultIcon}" : defaultIcon,
                            IsSilent = isSilent
                        };
                        var innerPayloadBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(innerPayload));

                        var aesKey = new byte[32];
                        RandomNumberGenerator.Fill(aesKey);
                        using var aesGcm = new AesGcm(aesKey, 16);

                        var nonce = new byte[12];
                        RandomNumberGenerator.Fill(nonce);

                        var ciphertext = new byte[innerPayloadBytes.Length];
                        var authTag = new byte[16];

                        aesGcm.Encrypt(nonce, innerPayloadBytes, ciphertext, authTag);

                        using var rsa = RSA.Create();
                        var keyBytes = Convert.FromBase64String(sub.PushPublicKey);
                        try
                        {
                            rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                        }
                        catch (System.Security.Cryptography.CryptographicException)
                        {
                            rsa.ImportRSAPublicKey(keyBytes, out _);
                        }
                        var encryptedKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);

                        relayPayload = new
                        {
                            Token = sub.PushEndpoint,
                            EncryptedKey = Convert.ToBase64String(encryptedKey),
                            Nonce = Convert.ToBase64String(nonce),
                            Ciphertext = Convert.ToBase64String(ciphertext),
                            Tag = Convert.ToBase64String(authTag),
                            IsEncrypted = true,
                            Category = category,
                            Sound = category == "chat" ? "SpokesNotif1.wav" : "default",
                            ServerId = serverId,
                            ServerIdSignature = serverIdSignature,
                            DatabaseCreationVersion = databaseCreationVersion,
                            DatabaseCreationSignature = databaseCreationSignature,
                            KeyHash = keyHash,
                            LicensePayload = licensePayload,
                            ThreadId = threadId,
                            ServerName = serverName,
                            ChannelName = channelName,
                            IsGroupChat = isGroupChat,
                            ServerIconUrl = originUrl != null ? $"{originUrl}{defaultIcon}" : defaultIcon,
                            Badge = badge,
                            IsSilent = isSilent
                        };
                    }
                    else
                    {
                        relayPayload = new
                        {
                            Token = sub.PushEndpoint,
                            Title = title,
                            Body = body,
                            Url = url ?? "/chat",
                            Category = category,
                            ImageUrl = absoluteImageUrl,
                            OriginUrl = originUrl,
                            Sound = category == "chat" ? "SpokesNotif1.wav" : "default",
                            ServerId = serverId,
                            ServerIdSignature = serverIdSignature,
                            DatabaseCreationVersion = databaseCreationVersion,
                            DatabaseCreationSignature = databaseCreationSignature,
                            KeyHash = keyHash,
                            LicensePayload = licensePayload,
                            ThreadId = threadId,
                            ServerName = serverName,
                            ChannelName = channelName,
                            IsGroupChat = isGroupChat,
                            ServerIconUrl = originUrl != null ? $"{originUrl}{defaultIcon}" : defaultIcon,
                            Badge = badge,
                            IsSilent = isSilent
                        };
                    }
                    var response = await httpClient.PostAsJsonAsync(SpokesConstants.PushRelayUrl, relayPayload);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
                        {
                            _logger.LogInformation("Removing expired NativeRelay push subscription {SubscriptionId}", sub.Id);
                            _sessions.ClearPushFields(sub);
                        }
                        else
                        {
                            _logger.LogError("Failed to send NativeRelay push to {RelayUrl}. Status: {Status}", SpokesConstants.PushRelayUrl, response.StatusCode);
                            _systemLog.LogError("Notifications", $"Failed to send NativeRelay push to {sub.DeviceType}. Status: {response.StatusCode}");
                        }
                    }
                    else
                    {
                        _logger.LogDebug("NativeRelay Push notification sent to subscription {SubscriptionId} ({DeviceType})", sub.Id, sub.DeviceType);
                    }
                }
                else
                {
                    var pushSubscription = new WebPushSubscription(sub.PushEndpoint, sub.PushP256dh, sub.PushAuth);
                    await pushClient.SendNotificationAsync(pushSubscription, payloadJson, options);
                    _logger.LogDebug("Push notification sent to subscription {SubscriptionId} ({DeviceType})",
                        sub.Id, sub.DeviceType);
                }
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                                               ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Removing expired push subscription {SubscriptionId}", sub.Id);
                _sessions.ClearPushFields(sub);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push notification to subscription {SubscriptionId}", sub.Id);
                _systemLog.LogError("Notifications", $"Failed to send push notification to {sub.DeviceType}", ex.ToString());
            }
        }
    }

    /// <summary>
    /// Send a test notification to a specific device, ignoring all rules.
    /// </summary>
    public async Task<bool> SendDeviceTestNotificationAsync(string subscriptionId, string userId, string title, string body, string? icon = null, string category = "chat")
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            throw new InvalidOperationException("VAPID keys not configured on server.");

        var sub = _sessions.GetById(subscriptionId);
        if (sub == null || !sub.HasPush || sub.EmployeeId != userId)
            throw new InvalidOperationException("Subscription not found or not owned by user.");

        var protector = _dataProtection.CreateProtector("AvatarPushToken");
        var token = protector.Protect($"{userId}|{DateTime.UtcNow.AddHours(48).Ticks}");
        var encodedToken = System.Net.WebUtility.UrlEncode(token);
        var defaultIcon = $"/spokesapi/Media/Icon?t={encodedToken}";

        var payload = new
        {
            title,
            body,
            icon = icon ?? defaultIcon,
            badge = defaultIcon,
            tag = "test-push",
            actions = new object[] { new { action = "open", title = "Open App" } },
            data = new { url = "/" }
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        var pushClient = new WebPushClient();
        pushClient.SetVapidDetails(profile.VapidSubject, profile.VapidPublicKey, profile.VapidPrivateKey);

        var options = new Dictionary<string, object>
        {
            { "headers", new Dictionary<string, object> { { "urgency", "high" } } }
        };

        if (sub.PushSubscriptionType == "NativeRelay")
        {
            var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
            var licenseResult = _licenseValidation.ValidateLicense(profile.LicensePayload, serverConfig);
            if (licenseResult.Status == LicenseStatus.HardLock || licenseResult.Status == LicenseStatus.Expired)
            {
                throw new LicenseExpiredException("Cannot send test notification: Your server license has expired.");
            }

            string serverId = serverConfig.DatabaseCreationId;
            string serverIdSignature = serverConfig.DatabaseCreationIdSignature;
            string licensePayload = profile.LicensePayload;
            string databaseCreationVersion = serverConfig.DatabaseCreationVersion;
            string databaseCreationSignature = serverConfig.DatabaseCreationSignature;
            string keyHash = _encryptionService.KeyHash;

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Relay-Key", SpokesConstants.PushRelayKey);
            object relayPayload;
            if (!string.IsNullOrEmpty(sub.PushPublicKey))
            {
                var innerPayload = new
                {
                    Title = title,
                    Body = body,
                    Url = "/",
                    Category = category
                };
                var innerPayloadBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(innerPayload));

                var aesKey = new byte[32];
                RandomNumberGenerator.Fill(aesKey);
                using var aesGcm = new AesGcm(aesKey, 16);

                var nonce = new byte[12];
                RandomNumberGenerator.Fill(nonce);

                var ciphertext = new byte[innerPayloadBytes.Length];
                var tag = new byte[16];

                aesGcm.Encrypt(nonce, innerPayloadBytes, ciphertext, tag);

                using var rsa = RSA.Create();
                var keyBytes = Convert.FromBase64String(sub.PushPublicKey);
                try
                {
                    rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    rsa.ImportRSAPublicKey(keyBytes, out _);
                }
                var encryptedKey = rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);

                relayPayload = new
                {
                    Token = sub.PushEndpoint,
                    EncryptedKey = Convert.ToBase64String(encryptedKey),
                    Nonce = Convert.ToBase64String(nonce),
                    Ciphertext = Convert.ToBase64String(ciphertext),
                    Tag = Convert.ToBase64String(tag),
                    IsEncrypted = true,
                    Category = category,
                    Sound = category == "chat" ? "SpokesNotif1.wav" : "default",
                    ServerId = serverId,
                    ServerIdSignature = serverIdSignature,
                    DatabaseCreationVersion = databaseCreationVersion,
                    DatabaseCreationSignature = databaseCreationSignature,
                    KeyHash = keyHash,
                    LicensePayload = licensePayload
                };
            }
            else
            {
                relayPayload = new
                {
                    Token = sub.PushEndpoint,
                    Title = title,
                    Body = body,
                    Url = "/",
                    Category = category,
                    Sound = category == "chat" ? "SpokesNotif1.wav" : "default",
                    ServerId = serverId,
                    ServerIdSignature = serverIdSignature,
                    DatabaseCreationVersion = databaseCreationVersion,
                    DatabaseCreationSignature = databaseCreationSignature,
                    KeyHash = keyHash,
                    LicensePayload = licensePayload
                };
            }
            var response = await httpClient.PostAsJsonAsync(SpokesConstants.PushRelayUrl, relayPayload);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Gone)
                {
                    _sessions.ClearPushFields(sub);
                    throw new Exception("This device's push token has expired or the app was uninstalled. The subscription has been removed.");
                }
                
                var responseContent = await response.Content.ReadAsStringAsync();
                throw new Exception($"Relay Server returned {response.StatusCode}: {responseContent}");
            }
            return !string.IsNullOrEmpty(sub.PushPublicKey);
        }
        else
        {
            var pushSubscription = new WebPushSubscription(sub.PushEndpoint, sub.PushP256dh, sub.PushAuth);
            await pushClient.SendNotificationAsync(pushSubscription, payloadJson, options);
            return false;
        }
    }

    /// <summary>
    /// Generate new VAPID keys. Call this once to get keys for configuration.
    /// </summary>
    public static (string publicKey, string privateKey) GenerateVapidKeys()
    {
        var keys = VapidHelper.GenerateVapidKeys();
        return (keys.PublicKey, keys.PrivateKey);
    }
}
