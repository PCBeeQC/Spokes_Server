using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Coordinated migration runner that executes pending schema and data migrations
/// and tracks their completion in a persistent ledger before request serving begins.
/// </summary>
public class MigrationRunnerService : IMigrationRunner
{
    private readonly IEnumerable<IMigrationStep> _steps;
    private readonly string _dataPath;
    private readonly DiskPersistenceService? _persistence;
    private readonly ILogger<MigrationRunnerService>? _logger;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public MigrationRunnerService(
        IEnumerable<IMigrationStep> steps,
        IConfiguration config,
        DiskPersistenceService? persistence = null,
        ILogger<MigrationRunnerService>? logger = null)
    {
        _steps = steps;
        _dataPath = config["DataPath"] ?? "Data";
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Protected parameterless constructor strictly reserved for mock proxy frameworks (e.g. Moq).
    /// </summary>
    protected MigrationRunnerService()
    {
        _steps = Array.Empty<IMigrationStep>();
        _dataPath = "Data";
        _persistence = null;
        _logger = null;
    }

    private string HistoryFilePath => Path.Combine(_dataPath, "MigrationHistory.json");

    private MigrationHistoryLedger LoadLedger()
    {
        var path = HistoryFilePath;
        if (!File.Exists(path))
        {
            return new MigrationHistoryLedger();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MigrationHistoryLedger>(json) ?? new MigrationHistoryLedger();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MigrationRunner] Failed to read {Path}, starting with empty ledger.", path);
            return new MigrationHistoryLedger();
        }
    }

    private void SaveLedger(MigrationHistoryLedger ledger)
    {
        var path = HistoryFilePath;
        Directory.CreateDirectory(_dataPath);
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(ledger, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Executes all registered migrations that have not yet been applied, recording them in MigrationHistory.json.
    /// </summary>
    public async Task<MigrationRunSummary> RunPendingMigrationsAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("[MigrationRunner] Checking for pending migrations...");

        var ledger = LoadLedger();
        var appliedKeys = new HashSet<string>(ledger.AppliedMigrations.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);

        var steps = _steps.OrderBy(s => s.Order).ToList();
        var summary = new MigrationRunSummary { TotalSteps = steps.Count };

        foreach (var step in steps)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (appliedKeys.Contains(step.MigrationKey))
            {
                summary.SkippedSteps++;
                continue;
            }

            _logger?.LogInformation("[MigrationRunner] Applying migration {Key}: {Description}...", step.MigrationKey, step.Description);

            await step.ExecuteAsync(cancellationToken);

            ledger.AppliedMigrations.Add(new MigrationRecord
            {
                Key = step.MigrationKey,
                Description = step.Description,
                AppliedAtUtc = DateTime.UtcNow
            });
            SaveLedger(ledger);
            appliedKeys.Add(step.MigrationKey);

            summary.ExecutedSteps++;
            summary.AppliedKeys.Add(step.MigrationKey);
            _logger?.LogInformation("[MigrationRunner] Successfully applied {Key}.", step.MigrationKey);
        }

        if (summary.ExecutedSteps > 0 && _persistence != null)
        {
            await _persistence.FlushAllAsync();
        }

        _logger?.LogInformation("[MigrationRunner] Migration pass complete. Total: {Total}, Executed: {Executed}, Skipped: {Skipped}.",
            summary.TotalSteps, summary.ExecutedSteps, summary.SkippedSteps);

        return summary;
    }

    /// <summary>
    /// Returns the history of applied migrations from MigrationHistory.json.
    /// </summary>
    public IReadOnlyList<MigrationRecord> GetAppliedMigrations()
    {
        return LoadLedger().AppliedMigrations.AsReadOnly();
    }
}
