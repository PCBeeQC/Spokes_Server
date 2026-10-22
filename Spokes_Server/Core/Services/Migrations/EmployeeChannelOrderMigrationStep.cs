using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Cleans up legacy direct message channel IDs from employee CustomChannelOrder collections.
/// </summary>
public class EmployeeChannelOrderMigrationStep : IMigrationStep
{
    private readonly EmployeeRepository _employeeRepo;
    private readonly ChatChannelRepository _channelRepo;
    private readonly ILogger<EmployeeChannelOrderMigrationStep>? _logger;

    public string MigrationKey => "20260107_EmployeeDirectChannelOrderCleanup";
    public string Description => "Cleans up direct message channel IDs from employee CustomChannelOrder";
    public int Order => 85;

    public EmployeeChannelOrderMigrationStep(
        EmployeeRepository employeeRepo,
        ChatChannelRepository channelRepo,
        ILogger<EmployeeChannelOrderMigrationStep>? logger = null)
    {
        _employeeRepo = employeeRepo;
        _channelRepo = channelRepo;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var directIds = _channelRepo.GetAll()
            .Where(c => c.ChannelType == ChatChannelType.Direct)
            .Select(c => c.Id)
            .ToHashSet();

        if (directIds.Count == 0) return Task.CompletedTask;

        int cleanedCount = 0;
        foreach (var employee in _employeeRepo.GetAll())
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (employee.UsesCustomSidebarOrder && employee.CustomChannelOrder.Count > 0)
            {
                bool modified = false;
                foreach (var dId in directIds)
                {
                    if (employee.CustomChannelOrder.Remove(dId))
                    {
                        modified = true;
                    }
                }

                if (modified)
                {
                    _employeeRepo.Save(employee);
                    cleanedCount++;
                }
            }
        }

        if (cleanedCount > 0)
        {
            _logger?.LogInformation("[EmployeeChannelOrderMigrationStep] Cleaned direct channels from custom channel order for {Count} employee(s).", cleanedCount);
        }

        return Task.CompletedTask;
    }
}
