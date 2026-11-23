using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Re-runs push subscription migration with case-insensitive directory discovery so Linux hosts
/// that missed lowercase <c>pushsubscriptions/</c> during the original run clean up and merge leftover files.
/// </summary>
public class PushSubscriptionsCaseFixMigrationStep : IMigrationStep
{
    private readonly DeviceSessionRepository _deviceSessions;
    private readonly string _dataPath;
    private readonly ILogger<PushSubscriptionsCaseFixMigrationStep>? _logger;

    public string MigrationKey => "20261112_PushSubscriptionsCaseFix";
    public string Description => "Merges and cleans up any remaining lowercase or mixed-case pushsubscriptions directories";
    public int Order => 115;

    public PushSubscriptionsCaseFixMigrationStep(
        DeviceSessionRepository deviceSessions,
        IConfiguration config,
        ILogger<PushSubscriptionsCaseFixMigrationStep>? logger = null)
    {
        _deviceSessions = deviceSessions ?? throw new ArgumentNullException(nameof(deviceSessions));
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        PushDeviceSessionMigrationStep.MigratePushSubscriptionDirectories(_deviceSessions, _dataPath, _logger);
        return Task.CompletedTask;
    }
}
