using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Services.Licensing;

namespace Spokes_Server.Core.Services.Core
{
    public class ServerUpdateService : BackgroundService
    {
        private readonly ILogger<ServerUpdateService> _logger;
        private readonly HttpClient _httpClient;
        private readonly Spokes_Server.Aggregate.Database _db;
        private readonly LicenseValidationService _licenseService;
        private readonly VersionMetadata _versionMetadata;
        
        public string? LatestVersion { get; private set; }
        public string CurrentVersion { get; private set; }
        public bool IsUpdateAvailable { get; private set; }
        public string Channel { get; private set; } = "unknown";

        public event Action? OnUpdateAvailable;

        public ServerUpdateService(ILogger<ServerUpdateService> logger, Spokes_Server.Aggregate.Database db, LicenseValidationService licenseService, VersionMetadata versionMetadata)
        {
            _logger = logger;
            _db = db;
            _licenseService = licenseService;
            _versionMetadata = versionMetadata;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Spokes-Update-Checker/1.0");

            CurrentVersion = LicenseValidationService.AppVersion;
            Channel = _versionMetadata.Channel;
        }

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
                if (tagStr.StartsWith(expectedPrefix))
                {
                    string versionStr = tagStr.Substring(expectedPrefix.Length);
                    // Extract version (ignore any pre-release or build metadata after dash or plus)
                    int plusIndex = versionStr.IndexOf('+');
                    if (plusIndex >= 0) versionStr = versionStr.Substring(0, plusIndex);
                    int minusIndex = versionStr.IndexOf('-');
                    if (minusIndex >= 0) versionStr = versionStr.Substring(0, minusIndex);

                    if (Version.TryParse(versionStr, out Version? parsedVersion))
                    {
                        if (highestVersion == null || parsedVersion > highestVersion)
                        {
                            highestVersion = parsedVersion;
                            highestTag = tagStr;
                        }
                    }
                }
            }

            if (highestVersion != null && highestTag != null)
            {
                LatestVersion = highestTag;
                
                string currentCleanStr = CurrentVersion.Replace(expectedPrefix, "");
                int currentPlusIndex = currentCleanStr.IndexOf('+');
                if (currentPlusIndex >= 0) currentCleanStr = currentCleanStr.Substring(0, currentPlusIndex);
                int currentMinusIndex = currentCleanStr.IndexOf('-');
                if (currentMinusIndex >= 0) currentCleanStr = currentCleanStr.Substring(0, currentMinusIndex);

                if (Version.TryParse(currentCleanStr, out Version? currentParsed))
                {
                    if (highestVersion > currentParsed)
                    {
                        if (!IsUpdateAvailable)
                        {
                            IsUpdateAvailable = true;
                            _logger.LogInformation("New update available: {LatestVersion}", LatestVersion);
                            OnUpdateAvailable?.Invoke();

                            // Automated Management Auto-Update Trigger
                            var config = _db.SystemConfigs.Get();
                            if (config != null && config.EnableAutoUpdate && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN")))
                            {
                                var profile = _db.CompanyProfile.Get();
                                var serverConfig = _db.ServerConfigs.GetOrCreateGlobalConfig();
                                var valResult = _licenseService.ValidateLicense(profile?.LicensePayload, serverConfig);
                                string maxVerStr = valResult.MaxAllowedVersion ?? "";
                                
                                bool allowedToUpdate = true;
                                if (Version.TryParse(maxVerStr.TrimStart('v'), out Version? maxVer))
                                {
                                    allowedToUpdate = highestVersion <= maxVer;
                                }

                                // Allow auto-update for security patches regardless of license status
                                bool isLatestSecurityPatch = highestTag?.Contains("-security") == true;
                                
                                if (allowedToUpdate || isLatestSecurityPatch)
                                {
                                    var watchtowerToken = Environment.GetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN");
                                    if (!string.IsNullOrEmpty(watchtowerToken))
                                    {
                                        _logger.LogInformation("Triggering Watchtower auto-update for DigitalOcean deployment...");
                                        try
                                        {
                                            var req = new HttpRequestMessage(HttpMethod.Get, "http://watchtower:8080/v1/update");
                                            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", watchtowerToken);
                                            _ = _httpClient.SendAsync(req, stoppingToken); // Fire and forget since container will be killed
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Failed to trigger Watchtower update.");
                                        }
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("Auto-update skipped because the latest version ({LatestVersion}) exceeds the maximum allowed version by the license ({MaxVerStr}).", LatestVersion, maxVerStr);
                                }
                            }
                        }
                    }
                }
            }
        }
        public async Task TriggerWatchtowerUpdateAsync()
        {
            var token = Environment.GetEnvironmentVariable("WATCHTOWER_HTTP_API_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                _logger.LogInformation("Manually triggering Watchtower auto-update for DigitalOcean deployment...");
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, "http://watchtower:8080/v1/update");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    _ = _httpClient.SendAsync(req); // Fire and forget since container will be killed
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to trigger Watchtower update.");
                }
            }
        }
    }
}
