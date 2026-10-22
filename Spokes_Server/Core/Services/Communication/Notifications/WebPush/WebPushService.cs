using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

/// <summary>
/// Service for sending Web Push notifications to users' browsers and mobile devices.
/// Uses VAPID for authentication and coordinates with NativeRelayDispatcher and PushPayloadBuilder.
/// Reads VAPID keys from CompanyProfile and supports device-tier-aware routing based on user presence.
/// </summary>
public class WebPushService : IWebPushService
{
    private readonly DeviceSessionRepository _sessions;
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ServerConfigRepository _serverConfigs;
    private readonly IPushDecisionService _presenceState;
    private readonly NotificationQueueService _notificationQueue;
    private readonly ILogger<WebPushService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISystemLogService _systemLog;
    private readonly LicenseValidationService _licenseValidation;

    private readonly PushTokenService _pushTokenService;
    private readonly PushPayloadBuilder _payloadBuilder;
    private readonly NativeRelayDispatcher _relayDispatcher;
    private readonly Spokes_Server.Core.Configuration.SpokesOptions _spokesOptions;

    [ActivatorUtilitiesConstructor]
    public WebPushService(
        CoreContext coreContext,
        HrContext hrContext,
        IPushDecisionService presenceState,
        NotificationQueueService notificationQueue,
        ILogger<WebPushService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ISystemLogService systemLog,
        LicenseValidationService licenseValidation,
        PushTokenService? pushTokenService = null,
        PushPayloadBuilder? payloadBuilder = null,
        NativeRelayDispatcher? relayDispatcher = null,
        Microsoft.Extensions.Options.IOptions<Spokes_Server.Core.Configuration.SpokesOptions>? spokesOptions = null,
        IDataProtectionProvider? dataProtection = null)
    {
        ArgumentNullException.ThrowIfNull(coreContext);
        ArgumentNullException.ThrowIfNull(hrContext);

        _sessions = coreContext.DeviceSessions;
        _employees = hrContext.Employees;
        _companyProfile = coreContext.CompanyProfile;
        _systemConfigs = coreContext.SystemConfigs;
        _serverConfigs = coreContext.ServerConfigs;
        _presenceState = presenceState;
        _notificationQueue = notificationQueue;
        _logger = logger;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _systemLog = systemLog;
        _licenseValidation = licenseValidation;
        _pushTokenService = pushTokenService ?? new PushTokenService(dataProtection ?? new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        _payloadBuilder = payloadBuilder ?? new PushPayloadBuilder();
        _relayDispatcher = relayDispatcher ?? new NativeRelayDispatcher(httpClientFactory, coreContext.DeviceSessions, systemLog, options: spokesOptions, trialTickets: new RelayTrialTicketService(coreContext.ServerConfigs));
        _spokesOptions = spokesOptions?.Value ?? new Spokes_Server.Core.Configuration.SpokesOptions
        {
            PushRelayKey = configuration["SPOKES_RELAY_KEY"] ?? configuration["Spokes:PushRelayKey"] ?? string.Empty,
            PushRelayUrl = configuration["Spokes:PushRelayUrl"] ?? SpokesConstants.PushRelayUrl
        };
    }

    public string GetVapidPublicKey()
        => _companyProfile.Get()?.VapidPublicKey ?? string.Empty;

    public bool IsConfigured
    {
        get
        {
            var profile = _companyProfile.Get();
            return !string.IsNullOrEmpty(profile?.VapidPublicKey) &&
                   !string.IsNullOrEmpty(profile?.VapidPrivateKey);
        }
    }

    public Task SendNotificationAsync(PushNotificationRequest request) =>
        SendNotificationAsync(request, CancellationToken.None);

    public async Task SendNotificationAsync(PushNotificationRequest request, CancellationToken cancellationToken)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
        {
            _logger.LogDebug("Push notifications not configured, skipping send");
            return;
        }

        var employee = _employees.GetById(request.UserId);
        if (employee == null || !employee.PushNotificationsEnabled)
        {
            _logger.LogDebug("User {UserId} has push notifications disabled", request.UserId);
            return;
        }

        if (!_notificationQueue.IsWithinSchedule(employee))
        {
            _notificationQueue.Enqueue(new QueuedNotification(request));
            _logger.LogDebug("User {UserId} outside schedule, notification queued", request.UserId);
            return;
        }

        await SendPushToSubscriptionsAsync(request, profile, cancellationToken);
    }

    public Task SendClearNotificationAsync(string userId, string threadId) =>
        SendClearNotificationAsync(userId, threadId, CancellationToken.None);

    public async Task SendClearNotificationAsync(string userId, string threadId, CancellationToken cancellationToken)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            return;

        var employee = _employees.GetById(userId);
        if (employee == null || !employee.PushNotificationsEnabled)
            return;

        await SendPushToSubscriptionsAsync(new PushNotificationRequest
        {
            UserId = userId,
            Title = "",
            Body = "",
            Tier = PresenceTier.MobileOnly,
            Category = "clear_notification",
            ThreadId = threadId
        }, profile, cancellationToken);
    }

    public Task SendNotificationDirectAsync(PushNotificationRequest request) =>
        SendNotificationDirectAsync(request, CancellationToken.None);

    public async Task SendNotificationDirectAsync(PushNotificationRequest request, CancellationToken cancellationToken)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            return;

        var employee = _employees.GetById(request.UserId);
        if (employee == null || !employee.PushNotificationsEnabled)
            return;

        await SendPushToSubscriptionsAsync(request, profile, cancellationToken);
    }

    private List<DeviceSession> GetSubscriptionsForTier(string userId, PresenceTier? tier)
    {
        if (tier == PresenceTier.DesktopOnly)
        {
            var subs = _sessions.GetPushEnabledByEmployeeId(userId, DeviceType.Desktop);
            _logger.LogDebug("Sending desktop-only push to {Count} subscriptions for user {UserId}", subs.Count, userId);
            return subs;
        }

        if (tier == PresenceTier.MobileOnly)
        {
            var subs = _sessions.GetPushEnabledByEmployeeId(userId, DeviceType.Mobile);
            _logger.LogDebug("Sending mobile-only push to {Count} subscriptions for user {UserId}", subs.Count, userId);
            return subs;
        }

        var allSubs = _sessions.GetPushEnabledByEmployeeId(userId);
        _logger.LogDebug("Sending push to all {Count} enabled subscriptions for user {UserId}", allSubs.Count, userId);
        return allSubs;
    }

    private async Task SendPushToSubscriptionsAsync(PushNotificationRequest request, CompanyProfile profile, CancellationToken cancellationToken = default)
    {
        var subscriptions = GetSubscriptionsForTier(request.UserId, request.Tier);
        if (!subscriptions.Any())
        {
            _logger.LogDebug("No matching push subscriptions found for user {UserId}", request.UserId);
            return;
        }

        var defaultIcon = _pushTokenService.GetDefaultIconUrl(request.UserId);
        var payloadJson = _payloadBuilder.BuildWebPushPayloadJson(request, profile, defaultIcon);

        var pushClient = new PushServiceClient(_httpClientFactory.CreateClient());
        var vapidAuth = new VapidAuthentication(profile.VapidPublicKey, profile.VapidPrivateKey)
        {
            Subject = profile.VapidSubject
        };

        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        var nativePushStatus = GetNativePushStatus();
        bool isNativeRelayBlocked = !nativePushStatus.IsAvailable;

        var rawPublicUrl = _systemConfigs.Get().ServerPublicUrl;
        string? originUrl = string.IsNullOrWhiteSpace(rawPublicUrl) ? null : rawPublicUrl.TrimEnd('/');

        var pushTasks = subscriptions.Select(async sub =>
        {
            try
            {
                if (request.Category == "clear_notification" && sub.PushSubscriptionType != PushSubscriptionType.NativeRelay)
                {
                    _logger.LogDebug("Skipping clear_notification for WebPush subscription {SubscriptionId} ({DeviceType}) as browser clients do not support silent data pushes", sub.Id, sub.DeviceType);
                    return;
                }

                // Check device vault authorization for encrypted messages
                var effectiveBody = request.GetEffectiveBodyForDevice(sub.HasVaultCookie);
                var deviceRequest = (request.Body == effectiveBody)
                    ? request
                    : new PushNotificationRequest
                    {
                        UserId = request.UserId,
                        Title = request.Title,
                        Body = effectiveBody,
                        Url = request.Url,
                        Icon = request.Icon,
                        Tier = request.Tier,
                        Tag = request.Tag,
                        Actions = request.Actions,
                        Category = request.Category,
                        ThreadId = request.ThreadId,
                        ServerName = request.ServerName,
                        ChannelName = request.ChannelName,
                        IsGroupChat = request.IsGroupChat,
                        Badge = request.Badge,
                        IsSilent = request.IsSilent,
                        Sound = request.Sound,
                        IsEncrypted = request.IsEncrypted,
                        MaskedBody = request.MaskedBody
                    };

                if (sub.PushSubscriptionType == PushSubscriptionType.NativeRelay)
                {
                    if (isNativeRelayBlocked)
                    {
                        _logger.LogDebug("Skipping NativeRelay push for {SubscriptionId} because local license status is {Status}.", sub.Id, nativePushStatus.Status);
                        return;
                    }

                    var relayContext = new PushRelayContext(profile, serverConfig, defaultIcon, originUrl);
                    var relayPayload = _payloadBuilder.BuildRelayPayload(deviceRequest, sub, relayContext);
                    await _relayDispatcher.DispatchRelayPushAsync(relayPayload, sub, cancellationToken);
                }
                else
                {
                    var pushSubscription = new PushSubscription
                    {
                        Endpoint = sub.PushEndpoint,
                        Keys = new Dictionary<string, string>
                        {
                            ["p256dh"] = sub.PushP256dh,
                            ["auth"] = sub.PushAuth
                        }
                    };
                    var devicePayloadJson = (deviceRequest == request)
                        ? payloadJson
                        : _payloadBuilder.BuildWebPushPayloadJson(deviceRequest, profile, defaultIcon);
                    var message = new PushMessage(devicePayloadJson)
                    {
                        Urgency = PushMessageUrgency.High
                    };
                    await pushClient.RequestPushMessageDeliveryAsync(pushSubscription, message, vapidAuth, cancellationToken);
                    _logger.LogDebug("Push notification sent to subscription {SubscriptionId} ({DeviceType})",
                        sub.Id, sub.DeviceType);
                }
            }
            catch (PushServiceClientException ex) when (ex.StatusCode == HttpStatusCode.Gone ||
                                                       ex.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Removing expired push subscription {SubscriptionId}", sub.Id);
                _sessions.ClearPushFields(sub);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push notification to subscription {SubscriptionId}", sub.Id);
                _systemLog.LogError("Notifications", $"Failed to send push notification to {sub.DeviceType}", ex.ToString());
            }
        });

        await Task.WhenAll(pushTasks);
    }

    public async Task<TestNotificationResult> SendDeviceTestNotificationAsync(DeviceTestNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.VapidPublicKey) || string.IsNullOrEmpty(profile.VapidPrivateKey))
            throw new DomainException("VAPID keys not configured on server.", "VAPID_NOT_CONFIGURED");

        var sub = _sessions.GetById(request.SubscriptionId);
        if (sub == null || !sub.HasPush || sub.EmployeeId != request.UserId)
            throw new DomainException("Subscription not found or not owned by user.", "SUBSCRIPTION_NOT_FOUND");

        var defaultIcon = _pushTokenService.GetDefaultIconUrl(request.UserId);
        var employee = _employees.GetById(request.UserId);
        var userSoundChoice = employee?.GetNotificationSound(request.Category);
        var pushSound = NotificationSoundCatalog.GetEffectivePushSound(request.Category, userSoundChoice);

        var testRequest = new PushNotificationRequest
        {
            UserId = request.UserId,
            Title = request.Title,
            Body = request.Body,
            Icon = request.Icon ?? defaultIcon,
            Tag = "test-push",
            Actions = [new { action = "open", title = "Open App" }],
            Category = request.Category,
            Sound = pushSound,
            Url = "/"
        };

        if (sub.PushSubscriptionType == PushSubscriptionType.NativeRelay)
        {
            var nativePushStatus = GetNativePushStatus();
            if (!nativePushStatus.IsAvailable)
            {
                var message = $"Cannot send test notification: {nativePushStatus.Message}";
                if (nativePushStatus.Status != nameof(LicenseStatus.Expired))
                {
                    throw new DomainException(message, "LICENSE_INVALID");
                }
                throw new LicenseExpiredException(message, string.IsNullOrEmpty(profile.LicensePayload) ? "TRIAL_EXPIRED" : "LICENSE_EXPIRED");
            }

            var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
            var relayContext = new PushRelayContext(profile, serverConfig, defaultIcon, null);
            var relayPayload = _payloadBuilder.BuildRelayPayload(testRequest, sub, relayContext);
            await _relayDispatcher.DispatchRelayPushOrThrowAsync(relayPayload, sub, cancellationToken);
            bool isPayloadEncrypted = !string.IsNullOrEmpty(sub.PushPublicKey);
            return TestNotificationResult.Success(isPayloadEncrypted);
        }
        else
        {
            var payloadJson = _payloadBuilder.BuildWebPushPayloadJson(testRequest, profile, defaultIcon);
            var pushClient = new PushServiceClient(_httpClientFactory.CreateClient());
            var vapidAuth = new VapidAuthentication(profile.VapidPublicKey, profile.VapidPrivateKey)
            {
                Subject = profile.VapidSubject
            };

            var pushSubscription = new PushSubscription
            {
                Endpoint = sub.PushEndpoint,
                Keys = new Dictionary<string, string>
                {
                    ["p256dh"] = sub.PushP256dh,
                    ["auth"] = sub.PushAuth
                }
            };
            var message = new PushMessage(payloadJson)
            {
                Urgency = PushMessageUrgency.High
            };

            await pushClient.RequestPushMessageDeliveryAsync(pushSubscription, message, vapidAuth, cancellationToken);
            return TestNotificationResult.Success(isPayloadEncrypted: false);
        }
    }

    public bool IsNativePushAllowed() => GetNativePushStatus().IsAvailable;

    public NativePushAvailability GetNativePushStatus()
    {
        var profile = _companyProfile.Get();
        var serverConfig = _serverConfigs.GetOrCreateGlobalConfig();
        var licenseResult = _licenseValidation.ValidateLicense(profile?.LicensePayload, serverConfig);
        bool isAllowed = LicenseValidationService.IsLicenseActive(licenseResult.Status);
        return new NativePushAvailability(isAllowed, licenseResult.Status.ToString(), licenseResult.Message);
    }

    public static (string publicKey, string privateKey) GenerateVapidKeys()
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var ecParams = ecdsa.ExportParameters(includePrivateParameters: true);

        var publicKeyBytes = new byte[65];
        publicKeyBytes[0] = 0x04;
        Buffer.BlockCopy(ecParams.Q.X!, 0, publicKeyBytes, 1, 32);
        Buffer.BlockCopy(ecParams.Q.Y!, 0, publicKeyBytes, 33, 32);

        var privateKeyBytes = ecParams.D!;

        return (
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(publicKeyBytes),
            Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(privateKeyBytes)
        );
    }
}
