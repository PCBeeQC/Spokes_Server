using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Authoritative migration runner that executes pending schema and data migrations
/// and tracks their completion in a persistent ledger before request serving begins.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Executes all registered migrations that have not yet been applied.
    /// </summary>
    Task<MigrationRunSummary> RunPendingMigrationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all migration records recorded in the persistent ledger.
    /// </summary>
    IReadOnlyList<MigrationRecord> GetAppliedMigrations();
}
