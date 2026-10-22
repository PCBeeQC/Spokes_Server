using Spokes_Server.Core.Data;

namespace Spokes_Server.Core.Services.Logging;

public interface ISystemLogService : IStoragePersistenceLogger
{
    void LogInfo(string category, string message, string? details = null);

    Task LogAsync(string category, string severity, string message, string? details = null);
    Task LogErrorAsync(string category, string message, string? details = null);
    Task LogInfoAsync(string category, string message, string? details = null);
    
    Task FlushAsync();
    Task<List<SystemLogEntry>> GetLogsAsync(DateTime dateUtc);
    List<DateTime> GetAvailableLogDates();
}
