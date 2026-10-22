using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Maintenance;

public class ServerUpdateService : BackgroundService
{
    private readonly ILogger<ServerUpdateService> _logger;
    private readonly HttpClient _httpClient;
    private readonly CoreContext _core;
    private readonly LicenseValidationService _licenseService;
    private readonly VersionMetadata _versionMetadata;
    private readonly IConfiguration? _configuration;
    
    public string? LatestVersion { get; private set; }
    public string CurrentVersion { get; private set; }
    public bool IsUpdateAvailable { get; private set; }
    public string Channel { get; private set; } = "unknown";

    public event Action? OnUpdateAvailable;

    [ActivatorUtilitiesConstructor]
    public ServerUpdateService(
        ILogger<ServerUpdateService> logger,
        CoreContext coreContext,
        LicenseValidationService licenseService,
        VersionMetadata versionMetadata,
        IHttpClientFactory httpClientFactory,
        IConfiguration? configuration = null)
        : this(logger, coreContext, licenseService, versionMetadata, configuration, httpClientFactory?.CreateClient("ServerUpdate") ?? new HttpClient())
    {
    }

    public ServerUpdateService(
        ILogger<ServerUpdateService> logger,
        CoreContext coreContext,
        LicenseValidationService licenseService,
        VersionMetadata versionMetadata,
        IConfiguration? configuration = null)
        : this(logger, coreContext, licenseService, versionMetadata, configuration, (HttpClient?)null)
    {
    }

    internal ServerUpdateService(
        ILogger<ServerUpdateService> logger,
        CoreContext coreContext,
        LicenseValidationService licenseService,
        VersionMetadata versionMetadata,
        IConfiguration? configuration,
        HttpClient? httpClient)
    {
        _logger = logger;
        _core = coreContext;
        _licenseService = licenseService;
        _versionMetadata = versionMetadata;
        _configuration = configuration;
        _httpClient = httpClient ?? new HttpClient();
        if (httpClient == null && !_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Spokes-Update-Checker/1.0");
        }

        CurrentVersion = LicenseValidationService.AppVersion;
        Channel = _versionMetadata.Channel;
    }

    private string? GetWatchtowerToken() =>
        _configuration != null ? _configuration["WATCHTOWER_HTTP_API_TOKEN"] : Environment.GetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (Channel == "unknown")
            {
                _logger.LogInformation("Update check disabled for development build (version: {Version})", CurrentVersion);
                return;
            }

            _logger.LogInformation("Starting ServerUpdateService on channel '{Channel}'. Current Version: {Version}", Channel, CurrentVersion);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckForUpdatesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to check for server updates.");
                }

                // Check every 24 hours
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }

        public async Task CheckForUpdatesAsync(CancellationToken stoppingToken = default)
        {
            if (Channel == "unknown") return;

            string repoName = Channel switch
            {
                "test" => "spokes-test",
                "beta" => "spokes-beta",
                _ => "spokes"
            };

            string scope = $"repository:pcbeeqc/{repoName}:pull";
            string tokenUrl = $"https://ghcr.io/token?scope={scope}";

            // 1. Get anonymous token
            var tokenResponse = await _httpClient.GetAsync(tokenUrl, stoppingToken);
            tokenResponse.EnsureSuccessStatusCode();
            
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync(stoppingToken);
            using var doc = JsonDocument.Parse(tokenJson);
            if (!doc.RootElement.TryGetProperty("token", out var tokenElement))
            {
                _logger.LogWarning("Failed to extract token from GHCR response.");
                return;
            }
            
            string token = tokenElement.GetString() ?? "";

            // 2. Fetch tags
            string tagsUrl = $"https://ghcr.io/v2/pcbeeqc/{repoName}/tags/list";
            using var request = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var tagsResponse = await _httpClient.SendAsync(request, stoppingToken);
            tagsResponse.EnsureSuccessStatusCode();

            var tagsJson = await tagsResponse.Content.ReadAsStringAsync(stoppingToken);
            using var tagsDoc = JsonDocument.Parse(tagsJson);
            
            if (!tagsDoc.RootElement.TryGetProperty("tags", out var tagsElement))
            {
                return;
            }

            string expectedPrefix = Channel switch
            {
                "test" => "test-v",
                "beta" => "beta-v",
                _ => "server-v"
            };

            Version? highestVersion = null;
            string? highestTag = null;

            foreach (var tag in tagsElement.EnumerateArray())
            {
                string tagStr = tag.GetString() ?? "";
                if (tagStr.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                    && TryParseVersion(tagStr, expectedPrefix, out var parsedVersion)
                    && parsedVersion != null)
                {
                    if (highestVersion == null || parsedVersion > highestVersion)
                    {
                        highestVersion = parsedVersion;
                        highestTag = tagStr;
                    }
                }
            }

            if (highestVersion == null || highestTag == null)
                return;

            LatestVersion = highestTag;

            if (!TryParseVersion(CurrentVersion, expectedPrefix, out var currentParsed) || currentParsed == null)
                return;

            if (highestVersion <= currentParsed)
                return;

            if (IsUpdateAvailable)
                return;

            IsUpdateAvailable = true;
            _logger.LogInformation("New update available: {LatestVersion}", LatestVersion);
            OnUpdateAvailable?.Invoke();

            TryTriggerAutoUpdate(highestVersion, highestTag, stoppingToken);
        }

    internal static bool TryParseVersion(string versionRaw, string prefix, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(versionRaw)) return false;

        string cleanStr = versionRaw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? versionRaw[prefix.Length..]
            : versionRaw;

        int plusIndex = cleanStr.IndexOf('+');
        if (plusIndex >= 0) cleanStr = cleanStr[..plusIndex];
        int minusIndex = cleanStr.IndexOf('-');
        if (minusIndex >= 0) cleanStr = cleanStr[..minusIndex];

        return Version.TryParse(cleanStr, out version);
    }

    private void TryTriggerAutoUpdate(Version highestVersion, string highestTag, CancellationToken stoppingToken)
    {
        var config = _core.SystemConfigs.Get();
        var watchtowerToken = GetWatchtowerToken();
        if (config == null || !config.EnableAutoUpdate || string.IsNullOrEmpty(watchtowerToken))
            return;

        if (!IsUpdatePermittedByLicense(highestVersion, highestTag, out var maxVerStr))
        {
            _logger.LogInformation("Auto-update skipped because the latest version ({LatestVersion}) exceeds the maximum allowed version by the license ({MaxVerStr}).", LatestVersion, maxVerStr);
            return;
        }

        SendWatchtowerUpdateRequest(watchtowerToken, stoppingToken);
    }

    private bool IsUpdatePermittedByLicense(Version targetVersion, string targetTag, out string maxVerStr)
    {
        maxVerStr = string.Empty;

        // Allow auto-update for security patches regardless of license status
        if (targetTag.Contains("-security", StringComparison.OrdinalIgnoreCase))
            return true;

        var profile = _core.CompanyProfile.Get();
        var serverConfig = _core.ServerConfigs.GetOrCreateGlobalConfig();
        var valResult = _licenseService.ValidateLicense(profile?.LicensePayload, serverConfig);
        maxVerStr = valResult.MaxAllowedVersion ?? string.Empty;

        if (Version.TryParse(maxVerStr.TrimStart('v'), out var maxVer))
        {
            return targetVersion <= maxVer;
        }

        return true;
    }

    private void SendWatchtowerUpdateRequest(string token, CancellationToken stoppingToken = default)
    {
        _logger.LogInformation("Triggering Watchtower auto-update for DigitalOcean deployment...");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "http://watchtower:8080/v1/update");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _ = _httpClient.SendAsync(req, stoppingToken); // Fire and forget since container will be killed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger Watchtower update.");
        }
    }

    public Task TriggerWatchtowerUpdateAsync()
    {
        var token = GetWatchtowerToken();
        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogInformation("Manually triggering Watchtower auto-update for DigitalOcean deployment...");
            SendWatchtowerUpdateRequest(token);
        }
        return Task.CompletedTask;
    }
}
