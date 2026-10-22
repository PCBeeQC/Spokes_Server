using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Identity;

/// <summary>
/// Background service that syncs Casdoor branding on startup when built-in Casdoor is enabled.
/// </summary>
public class CasdoorBrandingBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<CasdoorBrandingBackgroundService> _logger;
    private readonly TimeSpan _startupDelay;

    public CasdoorBrandingBackgroundService(
        IServiceProvider serviceProvider,
        SystemConfigRepository systemConfigs,
        ILogger<CasdoorBrandingBackgroundService> logger,
        TimeSpan? startupDelay = null)
    {
        _serviceProvider = serviceProvider;
        _systemConfigs = systemConfigs;
        _logger = logger;
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var systemConfig = _systemConfigs.Get();

            if (!systemConfig.IsSetupComplete || systemConfig.ProviderType != IdpType.BuiltInCasdoor)
            {
                return;
            }

            // Give Casdoor time to initialize
            await Task.Delay(_startupDelay, stoppingToken);

            using var scope = _serviceProvider.CreateScope();
            var casdoorService = scope.ServiceProvider.GetRequiredService<CasdoorProvisioningService>();
            await casdoorService.SyncBrandingAsync();
            _logger.LogInformation("[CasdoorBranding] Casdoor branding synced automatically on boot.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CasdoorBranding] Failed to sync Casdoor branding on boot.");
        }
    }
}
