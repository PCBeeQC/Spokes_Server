using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Logging;

namespace Spokes_Server.Core.Services.Communication.Notifications.WebPush;

public class NativeRelayDispatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeviceSessionRepository _sessions;
    private readonly ISystemLogService _systemLog;
    private readonly ILogger<NativeRelayDispatcher>? _logger;
    private readonly SpokesOptions _options;
    private readonly RelayTrialTicketService? _trialTickets;

    public NativeRelayDispatcher(
        IHttpClientFactory httpClientFactory,
        DeviceSessionRepository sessions,
        ISystemLogService systemLog,
        ILogger<NativeRelayDispatcher>? logger = null,
        IOptions<SpokesOptions>? options = null,
        RelayTrialTicketService? trialTickets = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(systemLog);

        _httpClientFactory = httpClientFactory;
        _sessions = sessions;
        _systemLog = systemLog;
        _logger = logger;
        _options = options?.Value ?? new SpokesOptions();
        _trialTickets = trialTickets;
    }

    public virtual async Task<bool> DispatchRelayPushAsync(RelayPushPayload relayPayload, DeviceSession sub, CancellationToken cancellationToken = default)
    {
        try
        {
            await DispatchRelayPushOrThrowAsync(relayPayload, sub, cancellationToken);
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public virtual async Task DispatchRelayPushOrThrowAsync(RelayPushPayload relayPayload, DeviceSession sub, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayPayload);
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Relay-Key", _options.PushRelayKey);

        using var response = await httpClient.PostAsJsonAsync(_options.PushRelayUrl, relayPayload, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            _logger?.LogInformation("Removing expired NativeRelay push subscription {SubscriptionId}", sub.Id);
            _sessions.ClearPushFields(sub);
            throw new DomainException("This device's push token has expired or the app was uninstalled. The subscription has been removed.", "PUSH_TOKEN_EXPIRED");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var relayResponse = ParseRelayResponse(responseContent);

        // The relay returns the trial ticket on both success and trial-expired responses.
        if (relayResponse?.TrialTicket != null)
        {
            StoreTrialTicket(relayResponse.TrialTicket);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = relayResponse?.Code;
            _logger?.LogError("Failed to send NativeRelay push to {RelayUrl}. Status: {Status}, Code: {Code}", _options.PushRelayUrl, response.StatusCode, code);
            _systemLog.LogError("Notifications", string.IsNullOrEmpty(code)
                ? $"Failed to send NativeRelay push to {sub.DeviceType}. Status: {response.StatusCode}"
                : $"Push relay rejected NativeRelay push to {sub.DeviceType}. Status: {response.StatusCode}, Code: {code}");

            if (!string.IsNullOrEmpty(code))
            {
                throw new RelayRejectedException(
                    string.IsNullOrWhiteSpace(relayResponse!.Detail) ? $"Push relay rejected the notification ({code})." : relayResponse.Detail,
                    code);
            }

            throw new HttpRequestException($"Relay Server returned {response.StatusCode}: {responseContent}");
        }

        _logger?.LogDebug("NativeRelay Push notification sent to subscription {SubscriptionId} ({DeviceType})", sub.Id, sub.DeviceType);
    }

    private void StoreTrialTicket(RelayTrialTicket ticket)
    {
        if (_trialTickets == null) return;
        try
        {
            _trialTickets.StoreTicket(ticket);
        }
        catch (Exception ex)
        {
            // Never fail a delivered push because the ticket could not be persisted; the relay re-issues the same ticket next time.
            _logger?.LogWarning(ex, "Failed to persist relay trial ticket.");
        }
    }

    private RelayResponseBody? ParseRelayResponse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            return JsonSerializer.Deserialize<RelayResponseBody>(content);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Push relay response was not JSON.");
            return null;
        }
    }

    /// <summary>Fields the server reads from relay responses (success body or RFC 7807 problem details).</summary>
    private sealed class RelayResponseBody
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }

        [JsonPropertyName("trialTicket")]
        public RelayTrialTicket? TrialTicket { get; set; }
    }
}

