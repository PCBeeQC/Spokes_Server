using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Identity;

public class CasdoorProvisioningService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly string _dataPath;
    private readonly IConfiguration _configuration;
    private readonly CasdoorConfigTemplateGenerator _templateGenerator;
    private readonly CasdoorApiClient _apiClient;
    private readonly CasdoorDatabaseMigrator _databaseMigrator;
    private readonly ILogger<CasdoorProvisioningService>? _logger;

    internal CasdoorProvisioningService(
        CompanyProfileRepository companyProfiles,
        SystemConfigRepository systemConfigs,
        IConfiguration configuration,
        CasdoorConfigTemplateGenerator? templateGenerator = null,
        CasdoorApiClient? apiClient = null,
        CasdoorDatabaseMigrator? databaseMigrator = null,
        ILogger<CasdoorProvisioningService>? logger = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _companyProfiles = companyProfiles;
        _systemConfigs = systemConfigs;
        _configuration = configuration;
        _dataPath = _configuration["DataPath"] ?? "Data";
        _templateGenerator = templateGenerator ?? new CasdoorConfigTemplateGenerator();
        _apiClient = apiClient ?? (httpClientFactory != null ? new CasdoorApiClient(httpClientFactory) : new CasdoorApiClient());
        _databaseMigrator = databaseMigrator ?? new CasdoorDatabaseMigrator();
        _logger = logger;
    }

    [ActivatorUtilitiesConstructor]
    public CasdoorProvisioningService(
        CoreContext coreContext,
        IConfiguration configuration,
        CasdoorApiClient? apiClient = null,
        CasdoorDatabaseMigrator? databaseMigrator = null,
        CasdoorConfigTemplateGenerator? templateGenerator = null,
        ILogger<CasdoorProvisioningService>? logger = null,
        IHttpClientFactory? httpClientFactory = null)
        : this(coreContext.CompanyProfile, coreContext.SystemConfigs, configuration, templateGenerator, apiClient, databaseMigrator, logger, httpClientFactory)
    {
    }

    public async Task<(string BuiltInPassword, string SpokesPassword, string ClientId, string ClientSecret)> ProvisionAsync(
        string publicUrl, string builtInUsername, string builtInPassword, string spokesUsername, string spokesPassword)
    {
        // Casdoor lowercases usernames on insert (isUsernameLowered=true in app.conf).
        // We must match that in init_data.json to avoid UNIQUE constraint crashes on restart.
        builtInUsername = builtInUsername.ToLowerInvariant();
        spokesUsername = spokesUsername.ToLowerInvariant();

        var hashedBuiltInPassword = BCrypt.Net.BCrypt.HashPassword(builtInPassword);
        var hashedSpokesPassword = BCrypt.Net.BCrypt.HashPassword(spokesPassword);

        var persistentCasdoorDir = Path.Combine(_dataPath, "casdoor");
        var persistentConfDir = Path.Combine(persistentCasdoorDir, "conf");
        if (!Directory.Exists(persistentConfDir)) Directory.CreateDirectory(persistentConfDir);

        var ephemeralCasdoorDir = "/app/casdoor";
        var ephemeralConfDir = Path.Combine(ephemeralCasdoorDir, "conf");
        if (!Directory.Exists(ephemeralConfDir)) Directory.CreateDirectory(ephemeralConfDir);

        // 1. Generate app.conf
        var appConf = _templateGenerator.GenerateAppConf(publicUrl, _dataPath, "prod");
        await File.WriteAllTextAsync(Path.Combine(persistentConfDir, "app.conf"), appConf);
        await File.WriteAllTextAsync(Path.Combine(ephemeralConfDir, "app.conf"), appConf);

        // 2. Generate Application Credentials
        var clientId = GenerateRandomString(20);
        var clientSecret = GenerateRandomString(40);

        var profile = _companyProfiles.Get();
        var sysConfig = _systemConfigs.Get();
        var companyName = string.IsNullOrWhiteSpace(profile?.CompanyName) ? "Spokes" : profile.CompanyName;
        var logoUrl = $"{publicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/logo.png";
        var faviconUrl = $"{publicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/icon.png";
        var fixedLogoUrl = $"{publicUrl.TrimEnd('/')}/spokes-org-logo.png";
        var fixedFaviconUrl = $"{publicUrl.TrimEnd('/')}/spokes-org-icon.png";

        // 3. Generate init_data.json
        var initParams = new CasdoorInitDataParameters(
            publicUrl,
            builtInUsername,
            hashedBuiltInPassword,
            spokesUsername,
            hashedSpokesPassword,
            clientId,
            clientSecret,
            companyName,
            logoUrl,
            faviconUrl,
            fixedLogoUrl,
            fixedFaviconUrl);

        var initJson = _templateGenerator.GenerateInitDataJson(initParams);
        await File.WriteAllTextAsync(Path.Combine(persistentCasdoorDir, "init_data.json"), initJson);
        await File.WriteAllTextAsync(Path.Combine(ephemeralCasdoorDir, "init_data.json"), initJson);

        // 4. Update System Config
        var config = _systemConfigs.Get();
        config.ProviderType = IdpType.BuiltInCasdoor;
        config.Authority = publicUrl.TrimEnd('/');
        config.ClientId = clientId;
        config.ClientSecret = clientSecret;
        config.IsSetupComplete = true;
        _systemConfigs.Save(config);

        // 4.5 Create indicator file for entrypoint.sh
        await File.WriteAllTextAsync(Path.Combine(_dataPath, "casdoor.enabled"), "true");

        // 4.6 Ensure Casdoor frontend assets are patched for same-origin OIDC redirects
        var casdoorAssetsPath = _configuration["CasdoorAssetsPath"] ?? "/app/casdoor/web/build/assets";
        CasdoorAssetPatcher.PatchAssets(casdoorAssetsPath, _logger);

        // 5. Start Casdoor via Supervisor
        var supervisorStarted = false;
        try
        {
            var psi = new ProcessStartInfo("supervisorctl", "start casdoor")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                _logger?.LogInformation("[Casdoor Provisioning] Supervisor start output: {Output}", output);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger?.LogWarning("[Casdoor Provisioning] Supervisor start error: {Error}", error);
                }
                supervisorStarted = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Casdoor Provisioning] Failed to start supervisorctl");
        }

        // 6. Forcefully update the built-in admin password and branding via SQLite
        var dbPath = Path.Combine(_dataPath, "casdoor.db");
        var migrationOptions = new CasdoorBrandingMigrationOptions
        {
            DbPath = dbPath,
            BuiltInUsername = builtInUsername,
            HashedBuiltInPassword = hashedBuiltInPassword,
            SpokesUsername = spokesUsername,
            HashedSpokesPassword = hashedSpokesPassword,
            CompanyName = companyName,
            LogoUrl = logoUrl,
            FaviconUrl = faviconUrl,
            FixedLogoUrl = fixedLogoUrl,
            FixedFaviconUrl = fixedFaviconUrl,
            ClientId = clientId,
            ClientSecret = clientSecret
        };

        // When Casdoor is started in Docker/production, poll until the database is ready
        // and initialized with the required tables and rows.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var migrationCompleted = false;

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(dbPath))
            {
                try
                {
                    var rows = await _databaseMigrator.UpdateAdminCredentialsAndBrandingAsync(migrationOptions);
                    if (rows > 0)
                    {
                        _logger?.LogInformation("[Casdoor Provisioning] SQLite migration applied {Rows} record updates after {Elapsed:0.0}s.", rows, stopwatch.Elapsed.TotalSeconds);
                        migrationCompleted = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[Casdoor Provisioning] Waiting for Casdoor database to initialize (elapsed: {Elapsed:0.0}s)...", stopwatch.Elapsed.TotalSeconds);
                }
            }
            else if (!supervisorStarted)
            {
                // In environments without Casdoor supervisor running, do not block waiting for db
                break;
            }

            await Task.Delay(500);
        }

        if (!migrationCompleted && (supervisorStarted || File.Exists(dbPath)))
        {
            _logger?.LogWarning("[Casdoor Provisioning] SQLite migration did not update any records within {Elapsed:0.0}s.", stopwatch.Elapsed.TotalSeconds);
        }

        return (builtInPassword, spokesPassword, clientId, clientSecret);
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
    }

    private async Task SyncAppConfOriginAsync(string newPublicUrl)
    {
        var persistentConfDir = Path.Combine(_dataPath, "casdoor", "conf");
        if (!Directory.Exists(persistentConfDir)) Directory.CreateDirectory(persistentConfDir);

        var ephemeralConfDir = "/app/casdoor/conf";
        if (!Directory.Exists(ephemeralConfDir)) Directory.CreateDirectory(ephemeralConfDir);

        var appConf = _templateGenerator.GenerateAppConf(newPublicUrl, _dataPath, "dev");
        await File.WriteAllTextAsync(Path.Combine(persistentConfDir, "app.conf"), appConf);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(ephemeralConfDir, "app.conf"), appConf);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[Casdoor Sync] Could not write ephemeral app.conf");
        }

        _logger?.LogInformation("[Casdoor Sync] app.conf origin updated to '{NewPublicUrl}'.", newPublicUrl);
    }

    public virtual async Task SyncBrandingAsync()
    {
        var profile = _companyProfiles.Get();
        var sysConfig = _systemConfigs.Get();
        if (sysConfig.ProviderType != IdpType.BuiltInCasdoor) return;

        // Ensure Casdoor frontend assets are patched for same-origin OIDC redirects
        var casdoorAssetsPath = _configuration["CasdoorAssetsPath"] ?? "/app/casdoor/web/build/assets";
        CasdoorAssetPatcher.PatchAssets(casdoorAssetsPath, _logger);

        // Sync app.conf origin with current ServerPublicUrl
        if (!string.IsNullOrWhiteSpace(sysConfig.ServerPublicUrl))
        {
            await SyncAppConfOriginAsync(sysConfig.ServerPublicUrl.TrimEnd('/'));
        }

        var companyName = string.IsNullOrWhiteSpace(profile?.CompanyName) ? "Spokes" : profile.CompanyName;
        var logoUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/logo.png";
        var faviconUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/branding/{sysConfig.PublicBrandingToken}/icon.png";

        var fixedLogoUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/spokes-org-logo.png";
        var fixedFaviconUrl = $"{sysConfig.ServerPublicUrl.TrimEnd('/')}/spokes-org-icon.png";

        var clientId = sysConfig.ClientId;
        var clientSecret = sysConfig.ClientSecret;

        // 1. Sync Spokes Application Branding (Always via API)
        try
        {
            var success = await _apiClient.UpdateApplicationBrandingAsync(new CasdoorApplicationBrandingRequest
            {
                Credentials = new CasdoorConnectionCredentials
                {
                    BaseUrl = "http://127.0.0.1:8000",
                    ClientId = clientId,
                    ClientSecret = clientSecret
                },
                CompanyName = companyName,
                LogoUrl = logoUrl,
                FaviconUrl = faviconUrl,
                ServerPublicUrl = sysConfig.ServerPublicUrl
            });

            if (success)
            {
                // Casdoor is healthy — remove init_data.json so it's never re-processed on restart.
                // The data is already in casdoor.db; the JSON file is redundant after first boot.
                var persistentInitData = Path.Combine(_dataPath, "casdoor", "init_data.json");
                var ephemeralInitData = "/app/casdoor/init_data.json";
                if (File.Exists(persistentInitData))
                {
                    File.Delete(persistentInitData);
                    _logger?.LogInformation("[Casdoor Sync] Removed persistent init_data.json (no longer needed after first boot).");
                }
                if (File.Exists(ephemeralInitData))
                {
                    File.Delete(ephemeralInitData);
                    _logger?.LogInformation("[Casdoor Sync] Removed ephemeral init_data.json.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Casdoor Sync] Failed to sync application via API");
        }

        // 2. Sync Organization (One-Time Only via DB)
        if (!sysConfig.IsCasdoorSanitized)
        {
            var dbPath = Path.Combine(_dataPath, "casdoor.db");
            if (!File.Exists(dbPath)) return;

            var sanitized = await _databaseMigrator.SanitizeOrganizationAsync(new CasdoorOrganizationSanitizationOptions
            {
                DbPath = dbPath,
                CompanyName = companyName,
                FixedLogoUrl = fixedLogoUrl,
                FixedFaviconUrl = fixedFaviconUrl
            });
            if (sanitized)
            {
                sysConfig.IsCasdoorSanitized = true;
                _systemConfigs.Save(sysConfig);
                _logger?.LogInformation("[Casdoor Sync] First-run sanitization applied to Casdoor UI elements.");
            }
        }
    }
}
