using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Defines a single unit of schema or data migration with order priority and idempotency key.
/// </summary>
public interface IMigrationStep
{
    /// <summary>
    /// Unique immutable key identifying this migration (e.g., "20260101_EmailEmployeeCentric").
    /// </summary>
    string MigrationKey { get; }

    /// <summary>
    /// Brief human-readable description of the migration.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Execution order priority. Lower numbers execute earlier.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Executes the migration procedure.
    /// </summary>
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
