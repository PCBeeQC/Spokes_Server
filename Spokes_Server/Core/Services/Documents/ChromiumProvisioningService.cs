using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Documents;

/// <summary>
/// Background service that ensures Chromium is fetched and ready for PDF generation via BrowserManager.
/// </summary>
public class ChromiumProvisioningService : BackgroundService
{
    private readonly IBrowserManager _browserManager;
    private readonly ILogger<ChromiumProvisioningService> _logger;

    public ChromiumProvisioningService(IBrowserManager browserManager, ILogger<ChromiumProvisioningService> logger)
    {
        _browserManager = browserManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[ChromiumProvisioning] Verifying Chromium for PDF generation in background via BrowserManager...");
            await _browserManager.EnsureProvisionedAsync(stoppingToken);
            _logger.LogInformation("[ChromiumProvisioning] Chromium ready for PDF generation.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChromiumProvisioning] Failed to provision Chromium.");
        }
    }
}
