namespace Spokes_Server.Core.Services.Logging;

public interface ISystemLogService
{
    void Log(string category, string severity, string message, string? details = null);
    void LogError(string category, string message, string? details = null);
    void LogInfo(string category, string message, string? details = null);
    
    Task<List<SystemLogEntry>> GetLogsAsync(DateTime dateUtc);
    List<DateTime> GetAvailableLogDates();
}
