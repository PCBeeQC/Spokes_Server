namespace Spokes_Server.Core.Services.Communication.Chat;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Licensing;

public class SlideshowCastService : ISlideshowCastService
{
    private readonly ServerConfigRepository _serverConfigs;
    private readonly CompanyProfileRepository _profileRepo;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly IConfiguration _configuration;
    private readonly SpokesOptions _options;
    private readonly ILogger<SlideshowCastService>? _logger;

    public const string DefaultGoogleCastAppId = "5AF5A068";

    public SlideshowCastService(
        ServerConfigRepository serverConfigs,
        CompanyProfileRepository profileRepo,
        SystemConfigRepository systemConfigs,
        IConfiguration configuration,
        IOptions<SpokesOptions>? options = null,
        ILogger<SlideshowCastService>? logger = null)
    {
        _serverConfigs = serverConfigs ?? throw new ArgumentNullException(nameof(serverConfigs));
        _profileRepo = profileRepo ?? throw new ArgumentNullException(nameof(profileRepo));
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options?.Value ?? new SpokesOptions();
        _logger = logger;
    }

    public SlideshowCastInitPayload GetCastInitPayload(string currentBaseUri)
    {
        var config = _serverConfigs.GetOrCreateGlobalConfig();
        var profile = _profileRepo.Get();

        string fingerprint = RelayTrialTicketService.ComputeFingerprint(config.ServerMasterRsaPublicKey) ?? string.Empty;
        var ticket = RelayTrialTicketService.GetTicketForCurrentKey(config);

        bool isDemo = _configuration.GetValue<bool>("Spokes_DemoMode") 
            || _configuration.GetValue<bool>("Spokes:DemoMode");

        string appId = _configuration["Spokes:GoogleCastAppId"]
            ?? _configuration["GoogleCastAppId"]
            ?? Environment.GetEnvironmentVariable("Spokes__GoogleCastAppId")
            ?? Environment.GetEnvironmentVariable("SPOKES_GOOGLE_CAST_APP_ID")
            ?? DefaultGoogleCastAppId;

        string serverUrl = ResolveServerUrl(currentBaseUri);

        return new SlideshowCastInitPayload
        {
            AppId = appId,
            ServerId = config.DatabaseCreationId ?? string.Empty,
            ServerUrl = serverUrl,
            LicensePayload = profile?.LicensePayload,
            ServerFingerprint = fingerprint,
            TrialTicket = ticket,
            DatabaseCreationVersion = config.DatabaseCreationVersion,
            IsDemo = isDemo
        };
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
