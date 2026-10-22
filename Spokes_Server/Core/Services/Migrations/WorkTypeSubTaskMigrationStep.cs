using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Ensures every work type / task has at least one default 'General' sub-task.
/// Tracked by MigrationRunnerService and recorded in MigrationHistory.json so it executes only once rather than scanning on every boot.
/// </summary>
public class WorkTypeSubTaskMigrationStep : IMigrationStep
{
    private readonly WorkTypeRepository _workTypeRepo;
    private readonly ILogger<WorkTypeSubTaskMigrationStep>? _logger;

    public string MigrationKey => "20260106_WorkTypeDefaultSubTask";
    public string Description => "Ensures every work type has at least one default 'General' sub-task";
    public int Order => 60;

    public WorkTypeSubTaskMigrationStep(WorkTypeRepository workTypeRepo, ILogger<WorkTypeSubTaskMigrationStep>? logger = null)
    {
        _workTypeRepo = workTypeRepo;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migrated = 0;

        foreach (var workType in _workTypeRepo.GetAll().Where(w => w.SubTasks.Count == 0))
        {
            if (cancellationToken.IsCancellationRequested) break;

            workType.SubTasks.Add(new SubTask
            {
                Name = "General"
            });
            _workTypeRepo.Save(workType);
            migrated++;
        }

        if (migrated > 0)
        {
            _logger?.LogInformation("[WorkTypeSubTaskMigrationStep] Added default sub-task to {Count} work type(s).", migrated);
        }

        return Task.CompletedTask;
    }
}
