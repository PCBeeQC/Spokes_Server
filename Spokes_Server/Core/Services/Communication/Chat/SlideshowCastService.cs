namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

public class SlideshowCastService : ISlideshowCastService
{
    private readonly ServerConfigRepository _serverConfigs;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly IConfiguration _configuration;
    private readonly SpokesRelayClient _relayClient;
    private readonly ILogger<SlideshowCastService>? _logger;

    public const string DefaultGoogleCastAppId = "5AF5A068";

    public SlideshowCastService(
        ServerConfigRepository serverConfigs,
        CompanyProfileRepository profileRepo,
        SystemConfigRepository systemConfigs,
        IConfiguration configuration,
        SpokesRelayClient relayClient,
        ILogger<SlideshowCastService>? logger = null)
    {
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _relayClient = relayClient ?? throw new ArgumentNullException(nameof(relayClient));
        _logger = logger;
    }

    public bool IsDemoMode() =>
        _configuration.GetValue<bool>("Spokes_DemoMode") ||
        _configuration.GetValue<bool>("Spokes:DemoMode");

    private SlideshowCastInitPayload BuildBaseInitPayload(string currentBaseUri)
    {
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        string fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey) ?? string.Empty;

        string appId = _configuration["Spokes:GoogleCastAppId"]
            ?? _configuration["GoogleCastAppId"]
            ?? Environment.GetEnvironmentVariable("Spokes__GoogleCastAppId")
            ?? Environment.GetEnvironmentVariable("SPOKES_GOOGLE_CAST_APP_ID")
            ?? DefaultGoogleCastAppId;

        return new SlideshowCastInitPayload
        {
            AppId = appId,
            ServerId = config.DatabaseCreationId ?? string.Empty,
            ServerUrl = ResolveServerUrl(currentBaseUri),
            CastTicket = null,
            ServerFingerprint = fingerprint,
            DatabaseCreationVersion = config.DatabaseCreationVersion,
            IsDemo = IsDemoMode()
        };
    }

    public async Task<SlideshowCastInitPayload> GetCastInitPayloadAsync(string currentBaseUri, CancellationToken cancellationToken = default)
    {
        var payload = BuildBaseInitPayload(currentBaseUri);
        if (payload.IsDemo)
        {
            return payload;
        }

        var config = _serverConfigs.GetOrCreateGlobalConfig();
        var profile = _profileRepo.Get();
        var trialTicket = RelayTrialTicketService.GetTicketForCurrentKey(config);
        string issueUrl = _relayClient.ResolveCastIssueTicketUrl();

        try
        {
            var requestBody = new
            {
                serverId = payload.ServerId,
                serverUrl = payload.ServerUrl,
                licensePayload = profile?.LicensePayload,
                serverFingerprint = payload.ServerFingerprint,
                trialTicket,
                databaseCreationVersion = payload.DatabaseCreationVersion,
                isDemo = false
            };

            using var response = await _relayClient.PostJsonAsync(issueUrl, requestBody, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                if (response.TryGetString("castTicket", out var castTicket) && !string.IsNullOrWhiteSpace(castTicket))
                {
                    payload.CastTicket = castTicket;
                    return payload;
                }

                _logger?.LogWarning("Cast ticket relay at {IssueUrl} returned HTTP {StatusCode} without a castTicket.", issueUrl, (int)response.StatusCode);
                payload.AuthorizationErrorCode = "RELAY_INVALID_RESPONSE";
                payload.AuthorizationError = "Relay did not return a valid Cast session ticket.";
                return payload;
            }

            _logger?.LogWarning(
                "Cast ticket relay request failed with HTTP {StatusCode} ({ContentType}, Code={Code}) at {IssueUrl}",
                (int)response.StatusCode,
                response.ContentType ?? "unknown",
                response.Code ?? "none",
                issueUrl);

            if (SpokesRelayClient.IsLicenseProblemCode(response.Code))
            {
                payload.AuthorizationErrorCode = response.Code;
                payload.AuthorizationError = !string.IsNullOrWhiteSpace(response.Detail)
                    ? response.Detail
                    : "A valid Spokes Pass is required to cast photo albums to TV.";
                return payload;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                payload.AuthorizationErrorCode = "RELAY_AUTH_FAILED";
                payload.AuthorizationError = "Relay authentication failed. Please check your server's relay configuration.";
                return payload;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                payload.AuthorizationErrorCode = "RATE_LIMITED";
                payload.AuthorizationError = "Too many cast authorization requests. Please wait a moment and try again.";
                return payload;
            }

            if (response.Json != null && !string.IsNullOrWhiteSpace(response.Detail))
            {
                payload.AuthorizationErrorCode = response.Code ?? "RELAY_ERROR";
                payload.AuthorizationError = response.Detail;
                return payload;
            }

            payload.AuthorizationErrorCode = "RELAY_UNAVAILABLE";
            payload.AuthorizationError = $"Could not authorize Google Cast with the Spokes relay (HTTP {(int)response.StatusCode}). Please try again or check for server updates.";
            return payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to obtain Google Cast session ticket from relay at {IssueUrl}", issueUrl);
            payload.AuthorizationErrorCode = "RELAY_UNREACHABLE";
            payload.AuthorizationError = "Could not reach the Spokes relay service to authorize Google Cast.";
            return payload;
        }
    }

    public string ResolveServerUrl(string currentBaseUri)
    {
        string current = (currentBaseUri ?? string.Empty).Trim().TrimEnd('/');
        var sysConfig = _systemConfigs.Get();
        string publicUrl = (sysConfig?.ServerPublicUrl ?? string.Empty).Trim().TrimEnd('/');

        // 1. If currently accessing via loopback (localhost / 127.0.0.1 / ::1), Chromecast cannot reach it directly.
        // Prefer configured ServerPublicUrl if set and not loopback.
        if (IsLoopback(current) && !string.IsNullOrWhiteSpace(publicUrl) && !IsLoopback(publicUrl))
        {
            _logger?.LogInformation("Resolved loopback '{Current}' to public URL '{PublicUrl}' for Google Cast session.", current, publicUrl);
            return publicUrl;
        }

        // 2. If ServerPublicUrl is HTTPS and current access is HTTP, prefer HTTPS public URL.
        // Google Cast Web Receiver (hosted on https://push.spokes.sh) enforces HTTPS contexts.
        if (current.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(publicUrl)
            && publicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("Upgraded plain HTTP '{Current}' to secure public URL '{PublicUrl}' for Google Cast session.", current, publicUrl);
            return publicUrl;
        }

        // 3. Otherwise, use current access URL (e.g. LAN IP http://192.168.1.50:5000 or current HTTPS domain)
        return string.IsNullOrEmpty(current) ? publicUrl : current;
    }

    private static bool IsLoopback(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var uri = new Uri(url);
            return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1");
        }
    }
}
