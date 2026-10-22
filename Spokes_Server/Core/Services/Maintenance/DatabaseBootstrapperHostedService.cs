namespace Spokes_Server.Core.Services.Maintenance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;

/// <summary>
/// Hosted lifecycle service that asynchronously initializes database caches, encryption escrow,
/// and presence state before the HTTP server listener starts accepting requests, and flushes
/// state during host shutdown.
/// </summary>
public class DatabaseBootstrapperHostedService : IHostedLifecycleService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseBootstrapperHostedService> _logger;

    public DatabaseBootstrapperHostedService(
        IServiceProvider serviceProvider,
        ILogger<DatabaseBootstrapperHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[DatabaseBootstrapper] Initializing Spokes subsystems before application listeners start...");
        await _serviceProvider.InitializeSpokesServerAsync();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[DatabaseBootstrapper] Executing application shutdown hooks...");
            var presenceState = _serviceProvider.GetService<PresenceStateService>();
            presenceState?.FlushActiveSessionsToDatabase();

            var diskService = _serviceProvider.GetServices<IHostedService>().OfType<DiskPersistenceService>().FirstOrDefault();
            if (diskService != null)
            {
                await diskService.FlushAllAsync();
            }
        }
        catch (ObjectDisposedException)
        {
            // Root service provider may be disposing concurrently during host termination
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
