using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;

namespace Spokes_Server.Core.Services.Licensing;

/// <summary>
/// Parsed response envelope from a Spokes Push Relay HTTP call.
/// Handles both JSON responses (including RFC 7807 ProblemDetails) and non-JSON proxy/WAF responses safely.
/// </summary>
public sealed class SpokesRelayResponse : IDisposable
{
    public HttpStatusCode StatusCode { get; init; }
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    public string? ContentType { get; init; }
    public string RawBody { get; init; } = string.Empty;
    public JsonDocument? Json { get; init; }
    public string? Code { get; init; }
    public string? Detail { get; init; }
    public RelayTrialTicket? TrialTicket { get; init; }

    public bool TryGetString(string propertyName, out string? value)
    {
        value = null;
        if (Json == null)
        {
            return false;
        }

        return SpokesRelayClient.TryGetStringProperty(Json.RootElement, propertyName, out value);
    }

    public void Dispose() => Json?.Dispose();
}

/// <summary>
/// Canonical HTTP client for server-to-relay requests to <c>Spokes_PushRelay</c> (<c>push.spokes.sh</c>).
/// Single source of truth for relay origin/endpoint resolution, <c>X-Relay-Key</c> authentication,
/// structured JSON/non-JSON response parsing, and automatic <see cref="RelayTrialTicket"/> persistence.
/// </summary>
public class SpokesRelayClient
{
    public const string RelayKeyHeaderName = "X-Relay-Key";

    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpokesOptions _options;
    private readonly IConfiguration? _configuration;
    private readonly RelayTrialTicketService? _trialTicketService;
    private readonly ILogger<SpokesRelayClient>? _logger;

    public SpokesRelayClient(
        IHttpClientFactory httpClientFactory,
        IOptions<SpokesOptions>? options = null,
        IConfiguration? configuration = null,
        RelayTrialTicketService? trialTicketService = null,
        ILogger<SpokesRelayClient>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? new SpokesOptions();
        _configuration = configuration;
        _trialTicketService = trialTicketService;
        _logger = logger;
    }

    public static bool IsLicenseProblemCode(string? code) =>
        string.Equals(code, "TRIAL_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "LICENSE_EXPIRED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "LICENSE_INVALID", StringComparison.OrdinalIgnoreCase);

    public string ResolveBaseOrigin()
    {
        string pushRelayUrl = _configuration?["Spokes:PushRelayUrl"]
            ?? _options.PushRelayUrl
            ?? SpokesConstants.PushRelayUrl;

        if (Uri.TryCreate(pushRelayUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        return "https://push.spokes.sh";
    }

    public string ResolveEndpointUrl(string relativePath, string? explicitOverrideConfigKey = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitOverrideConfigKey))
        {
            string? explicitUrl = _configuration?[explicitOverrideConfigKey];
            if (!string.IsNullOrWhiteSpace(explicitUrl))
            {
                return explicitUrl.Trim();
            }
        }

        return $"{ResolveBaseOrigin()}/{relativePath.TrimStart('/')}";
    }

    public string ResolvePushRelayUrl()
    {
        string? configured = _configuration?["Spokes:PushRelayUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return !string.IsNullOrWhiteSpace(_options.PushRelayUrl)
            ? _options.PushRelayUrl.Trim()
            : SpokesConstants.PushRelayUrl;
    }

    public string ResolveGifRelayUrl()
    {
        if (!string.IsNullOrWhiteSpace(_options.GifRelayUrl))
        {
            return _options.GifRelayUrl.Trim();
        }

        string? configured = _configuration?["Spokes:GifRelayUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return SpokesConstants.GifRelayUrl;
    }

    public string ResolveCastIssueTicketUrl() =>
        ResolveEndpointUrl("/api/relay-cast/issue-ticket", "Spokes:CastRelayIssueTicketUrl");

    public string ResolveRelayKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.PushRelayKey))
        {
            return _options.PushRelayKey.Trim();
        }

        string? configured = _configuration?["Spokes:PushRelayKey"]
            ?? _configuration?["SPOKES_RELAY_KEY"]
            ?? Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY");

        return configured?.Trim() ?? string.Empty;
    }

    public virtual async Task<SpokesRelayResponse> PostJsonAsync<TRequest>(
        string url,
        TRequest payload,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(payload);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string relayKey = ResolveRelayKey();
        if (!string.IsNullOrEmpty(relayKey))
        {
            request.Headers.TryAddWithoutValidation(RelayKeyHeaderName, relayKey);
        }

        return await SendAndParseAsync(request, cancellationToken, timeout);
    }

    public virtual async Task<SpokesRelayResponse> GetAsync(
        string url,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await SendAndParseAsync(request, cancellationToken, timeout);
    }

    private async Task<SpokesRelayResponse> SendAndParseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var httpClient = _httpClientFactory.CreateClient();

        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeout.HasValue)
        {
            timeoutCts!.CancelAfter(timeout.Value);
        }

        var effectiveCt = timeoutCts?.Token ?? cancellationToken;
        using var response = await httpClient.SendAsync(request, effectiveCt);

        string rawBody = response.Content != null
            ? await response.Content.ReadAsStringAsync(effectiveCt)
            : string.Empty;
        string? contentType = response.Content?.Headers.ContentType?.MediaType;

        JsonDocument? doc = null;
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                doc = JsonDocument.Parse(rawBody);
            }
            catch (JsonException ex)
            {
                _logger?.LogDebug(ex, "Spokes relay response from {Url} was not valid JSON (HTTP {StatusCode}, Content-Type {ContentType}).",
                    request.RequestUri, (int)response.StatusCode, contentType ?? "unknown");
                doc = null;
            }
        }

        string? code = null;
        string? detail = null;
        RelayTrialTicket? trialTicket = null;

        if (doc != null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            code = ExtractCode(doc.RootElement);
            detail = ExtractDetail(doc.RootElement);
            trialTicket = ExtractTrialTicket(doc.RootElement);

            if (trialTicket != null)
            {
                PersistTrialTicket(trialTicket);
            }
        }

        return new SpokesRelayResponse
        {
            StatusCode = response.StatusCode,
            ContentType = contentType,
            RawBody = rawBody,
            Json = doc,
            Code = code,
            Detail = detail,
            TrialTicket = trialTicket
        };
    }

    private void PersistTrialTicket(RelayTrialTicket ticket)
    {
        if (_trialTicketService == null || string.IsNullOrWhiteSpace(ticket.Fingerprint))
        {
            return;
        }

        try
        {
            _trialTicketService.StoreTicket(ticket);
        }
        catch (Exception ex)
        {
            // Never fail a relay operation because the ticket mirror could not be written; the relay re-issues it next time.
            _logger?.LogWarning(ex, "Failed to persist relay trial ticket.");
        }
    }

    private static string? ExtractCode(JsonElement root)
    {
        if (TryGetStringProperty(root, "code", out var code) && !string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        if (TryGetPropertyIgnoreCase(root, "extensions", out var ext) &&
            ext.ValueKind == JsonValueKind.Object &&
            TryGetStringProperty(ext, "code", out var extCode) &&
            !string.IsNullOrWhiteSpace(extCode))
        {
            return extCode;
        }

        return null;
    }

    private static string? ExtractDetail(JsonElement root)
    {
        if ((TryGetStringProperty(root, "detail", out var detail) ||
             TryGetStringProperty(root, "error", out detail) ||
             TryGetStringProperty(root, "title", out detail)) &&
            !string.IsNullOrWhiteSpace(detail))
        {
            return detail;
        }

        return null;
    }

    private RelayTrialTicket? ExtractTrialTicket(JsonElement root)
    {
        bool found = TryGetPropertyIgnoreCase(root, "trialTicket", out var ticketElement);
        if (!found &&
            TryGetPropertyIgnoreCase(root, "extensions", out var ext) &&
            ext.ValueKind == JsonValueKind.Object)
        {
            found = TryGetPropertyIgnoreCase(ext, "trialTicket", out ticketElement);
        }

        if (!found || ticketElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var ticket = ticketElement.Deserialize<RelayTrialTicket>(CaseInsensitiveJsonOptions);
            return ticket != null && !string.IsNullOrWhiteSpace(ticket.Fingerprint) ? ticket : null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to deserialize returned relay trial ticket.");
            return null;
        }
    }

    internal static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetStringProperty(JsonElement obj, string name, out string? value)
    {
        value = null;
        if (TryGetPropertyIgnoreCase(obj, name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return true;
        }

        return false;
    }
}
