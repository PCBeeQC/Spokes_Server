namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;

/// <summary>
/// Hosted background service that periodically triggers the company billing inbox check.
/// </summary>
public class CompanyBillInboxBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CompanyBillInboxBackgroundService> _logger;

    public CompanyBillInboxBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<CompanyBillInboxBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CompanyBillInboxBackgroundService started.");

        // Initial delay to let the server finish bootstrapping
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var companyProfileRepo = scope.ServiceProvider.GetService<CompanyProfileRepository>();
                if (string.Equals(companyProfileRepo?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                    continue;
                }

                var inboxService = scope.ServiceProvider.GetRequiredService<CompanyBillInboxService>();
                var settings = inboxService.GetSettings();

                if (settings.Enabled && !string.IsNullOrWhiteSpace(settings.AccountId))
                {
                    _logger.LogInformation("Checking company bill inbox: {Email}", !string.IsNullOrWhiteSpace(settings.EmailAddress) ? settings.EmailAddress : settings.AccountId);
                    var count = await inboxService.CheckInboxAsync(stoppingToken);
                    if (count > 0)
                    {
                        _logger.LogInformation("Company bill inbox ingested {Count} new bill(s)", count);
                        CompanyBillInboxService.NotifyInboxIngested();
                    }
                }

                var delayMinutes = settings.CheckIntervalMinutes > 0 ? settings.CheckIntervalMinutes : 15;
                await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in CompanyBillInboxBackgroundService");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("CompanyBillInboxBackgroundService stopped.");
    }
}
