using Spokes_Server.Aggregate;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Core.Services.Core;

public class DemoResetService : BackgroundService
{
    private readonly ILogger<DemoResetService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly string _dataPath;

    public DemoResetService(ILogger<DemoResetService> logger, IServiceProvider services, IConfiguration config)
    {
        _logger = logger;
        _services = services;
        _config = config;
        _dataPath = config["DataPath"] ?? "Data";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue<bool>("Spokes_DemoMode"))
        {
            // Do not run if not in Demo Mode
            return;
        }

        _logger.LogInformation("[DemoResetService] Demo Mode is ACTIVE. Server will reset from golden snapshot every hour.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval (1 hour)
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);

                _logger.LogInformation("[DemoResetService] Initiating scheduled Demo Environment Reset...");

                var snapshotPath = Path.Combine(_dataPath, "Backups", "demo_golden_snapshot.zip");
                
                if (File.Exists(snapshotPath))
                {
                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<Database>();
                    
                    // Restore backup synchronously as required by the method signature
                    db.RestoreBackup(snapshotPath, _logger);
                    
                    _logger.LogInformation("[DemoResetService] Demo Environment Reset successfully completed. All state returned to golden snapshot.");
                }
                else
                {
                    _logger.LogWarning("[DemoResetService] Golden snapshot not found at {Path}. Skipping reset.", snapshotPath);
                }
            }
            catch (TaskCanceledException)
            {
                // Shutting down gracefully
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DemoResetService] Fatal error during scheduled demo reset.");
            }
        }
    }
}
