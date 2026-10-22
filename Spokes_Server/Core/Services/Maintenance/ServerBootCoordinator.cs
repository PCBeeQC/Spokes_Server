using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Maintenance;

/// <summary>
/// Coordinates deterministic startup initialization, secret generation, migration execution, and graceful shutdown flushing.
/// </summary>
public static class ServerBootCoordinator
{

    public static async Task InitializeSpokesServerAsync(this IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("ServerBootCoordinator");

        // 1. Initialize repository caches from disk
        var db = services.GetService<Database>();
        db?.Initialize();

        // 2. Register cross-aggregate event coordinators & file cascades
        services.GetService<DatabaseRelationshipCoordinator>()?.RegisterSubscriptions();
        var fileService = services.GetService<IFileService>();
        if (fileService != null)
        {
            RepositoryFileCascade.Initialize(fileService, logger);
        }

        // 3. Execute all pending database and data migrations before accepting requests
        var migrationRunner = services.GetService<IMigrationRunner>();
        if (migrationRunner != null)
        {
            await migrationRunner.RunPendingMigrationsAsync();
        }

        // 4. Initialize server encryption escrow and presence state
        var escrowService = services.GetService<ServerEscrowService>();
        escrowService?.Initialize();

        var presenceState = services.GetService<PresenceStateService>();
        if (presenceState != null && db != null)
        {
            presenceState.InitializeFromSessions(db.DeviceSessions.GetAll());
        }

        if (db == null) return;

        // 4.5 Ensure server config database creation identity (version + id) for licensing and telemetry
        var licenseValidationService = services.GetService<LicenseValidationService>();
        if (licenseValidationService != null)
        {
            var serverConfig = db.ServerConfigs.GetOrCreateGlobalConfig();
            if (licenseValidationService.EnsureDatabaseCreationIdentity(serverConfig))
            {
                db.ServerConfigs.Save(serverConfig);
            }
        }

        // 5. Ensure VAPID keys exist for Web Push notifications
        var activeProfile = db.CompanyProfile.GetOrCreate();
        db.SystemConfigs.GetOrCreate();
        bool profileChanged = false;

        if (string.IsNullOrEmpty(activeProfile.VapidPublicKey))
        {
            var keys = WebPushService.GenerateVapidKeys();
            activeProfile.VapidPublicKey = keys.publicKey;
            activeProfile.VapidPrivateKey = keys.privateKey;
            if (string.IsNullOrEmpty(activeProfile.VapidSubject))
            {
                activeProfile.VapidSubject = "mailto:admin@localhost";
            }
            profileChanged = true;
            logger?.LogInformation("[Bootloader] Auto-generated VAPID keys for push notifications.");
        }

        // 6. Enforce master password configuration
        var configuration = services.GetService<IConfiguration>();
        var envMasterPassword = configuration != null ? configuration["SPOKES_MASTER_PASSWORD"] : Environment.GetEnvironmentVariable("SPOKES_MASTER_PASSWORD");
        if (string.IsNullOrEmpty(envMasterPassword) && string.IsNullOrEmpty(activeProfile.PublicChannelMasterPassword))
        {
            const string errorMessage = "CRITICAL ERROR: The master password is not configured. Please set the SPOKES_MASTER_PASSWORD environment variable to a secure value in your docker-compose.yml and restart the server.";
            logger?.LogCritical("[Bootloader] {Message}", errorMessage);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Bootloader] {errorMessage}");
            Console.ResetColor();
            throw new FatalConfigurationException(errorMessage);
        }

        if (profileChanged)
        {
            db.CompanyProfile.Save(activeProfile);
        }

        // 7. Apply configured currency localization
        if (!string.IsNullOrEmpty(activeProfile?.CurrencySymbol))
        {
            var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
            culture.NumberFormat.CurrencySymbol = activeProfile.CurrencySymbol;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }

        // 8. Apply LiveKit port configuration
        var systemConfigRepo = services.GetService<SystemConfigRepository>();
        var systemConfig = systemConfigRepo?.Get();
        if (systemConfig != null)
        {
            var liveKitService = services.GetService<LiveKitService>();
            liveKitService?.ApplyPortConfiguration(systemConfig);
        }

        // 9. Ensure Casdoor frontend assets are patched for same-origin OIDC redirects if present
        var casdoorAssetsPath = configuration?["CasdoorAssetsPath"] ?? "/app/casdoor/web/build/assets";
        CasdoorAssetPatcher.PatchAssets(casdoorAssetsPath, logger);
    }
}

