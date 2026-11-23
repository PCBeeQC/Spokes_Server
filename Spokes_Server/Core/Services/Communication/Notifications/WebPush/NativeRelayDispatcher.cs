using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public class NativeRelayDispatcher
{
    private readonly SpokesRelayClient _relayClient;
    private readonly DeviceSessionRepository _sessions;
    private readonly ISystemLogService _systemLog;
    private readonly ILogger<NativeRelayDispatcher>? _logger;

    public NativeRelayDispatcher(
        SpokesRelayClient relayClient,
        DeviceSessionRepository sessions,
        ISystemLogService systemLog,
        ILogger<NativeRelayDispatcher>? logger = null)
    {
        _relayClient = relayClient ?? throw new ArgumentNullException(nameof(relayClient));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _systemLog = systemLog ?? throw new ArgumentNullException(nameof(systemLog));
        _logger = logger;
    }

    public virtual async Task<bool> DispatchRelayPushAsync(RelayPushPayload relayPayload, DeviceSession sub, CancellationToken cancellationToken = default)
    {
        try
        {
            await DispatchRelayPushOrThrowAsync(relayPayload, sub, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public virtual async Task DispatchRelayPushOrThrowAsync(RelayPushPayload relayPayload, DeviceSession sub, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayPayload);

        string relayUrl = _relayClient.ResolvePushRelayUrl();
        using var response = await _relayClient.PostJsonAsync(relayUrl, relayPayload, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            _logger?.LogInformation("Removing expired NativeRelay push subscription {SubscriptionId}", sub.Id);
            _sessions.ClearPushFields(sub);
            throw new DomainException("This device's push token has expired or the app was uninstalled. The subscription has been removed.", "PUSH_TOKEN_EXPIRED");
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = response.Code;
            _logger?.LogError("Failed to send NativeRelay push to {RelayUrl}. Status: {Status}, Code: {Code}", relayUrl, response.StatusCode, code);
            _systemLog.LogError("Notifications", string.IsNullOrEmpty(code)
                ? $"Failed to send NativeRelay push to {sub.DeviceType}. Status: {response.StatusCode}"
                : $"Push relay rejected NativeRelay push to {sub.DeviceType}. Status: {response.StatusCode}, Code: {code}");

            if (!string.IsNullOrEmpty(code))
            {
                throw new RelayRejectedException(
                    string.IsNullOrWhiteSpace(response.Detail) ? $"Push relay rejected the notification ({code})." : response.Detail,
                    code);
            }

            throw new HttpRequestException($"Relay Server returned {response.StatusCode}: {response.RawBody}");
        }

        _logger?.LogDebug("NativeRelay Push notification sent to subscription {SubscriptionId} ({DeviceType})", sub.Id, sub.DeviceType);
    }
}
