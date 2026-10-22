namespace Spokes_Server.Core.Services.Maintenance;

public class DemoResetService : BackgroundService
{
    private readonly ILogger<DemoResetService> _logger;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    protected readonly string _dataPath;

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
                    var backupService = scope.ServiceProvider.GetRequiredService<BackupService>();
                    
                    var result = await backupService.RestoreBackupAsync(snapshotPath, _logger, stoppingToken);
                    if (result.Success)
                    {
                        _logger.LogInformation("[DemoResetService] Demo Environment Reset successfully completed. All state returned to golden snapshot.");
                    }
                    else
                    {
                        _logger.LogError("[DemoResetService] Demo Environment Reset failed: {Error}", result.ErrorMessage);
                    }
                }
                else
                {
                    _logger.LogWarning("[DemoResetService] Golden snapshot not found at {Path}. Skipping reset.", snapshotPath);
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogDebug(ex, "[DemoResetService] Scheduled demo reset was canceled during shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DemoResetService] Fatal error during scheduled demo reset.");
            }
        }
    }
}
