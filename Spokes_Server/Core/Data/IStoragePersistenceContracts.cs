namespace Spokes_Server.Core.Data;

/// <summary>
/// Minimal notification contract for persistence health events in the data layer.
/// Inverts upward dependencies from data persistence to maintenance/health services.
/// </summary>
public interface IStorageHealthNotifier
{
    void RecordPersistenceSuccess();
    void RecordPersistenceFailure();
}

/// <summary>
/// Minimal logging contract for persistence failure events in the data layer.
/// Inverts upward dependencies from data persistence to logging services.
/// </summary>
public interface IStoragePersistenceLogger
{
    void Log(string category, string severity, string message, string? details = null);
    void LogError(string category, string message, string? details = null);
}
