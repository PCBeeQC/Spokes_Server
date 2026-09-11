using System.Text.Json;

namespace Spokes_Server.Core.Services.Logging;

public class SystemLogService : ISystemLogService
{
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    public SystemLogService(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        string dataPath = config["DataPath"] ?? "Data";
        _logDirectory = Path.Combine(dataPath, "Logs");
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }

    public void LogError(string category, string message, string? details = null)
        => Log(category, "Error", message, details);

    public void LogInfo(string category, string message, string? details = null)
        => Log(category, "Info", message, details);

    public void Log(string category, string severity, string message, string? details = null)
    {
        var entry = new SystemLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Category = category,
            Severity = severity,
            Message = message,
            Details = details
        };

        // Fire and forget appending to avoid blocking callers
        _ = Task.Run(async () =>
        {
            await _lock.WaitAsync();
            try
            {
                var fileName = $"log_{entry.TimestampUtc:yyyy-MM-dd}.jsonl";
                var filePath = Path.Combine(_logDirectory, fileName);
                var jsonLine = JsonSerializer.Serialize(entry, _jsonOptions);
                await File.AppendAllTextAsync(filePath, jsonLine + Environment.NewLine);
            }
            catch
            {
                // Suppress logging errors to avoid infinite loops if disk fails
            }
            finally
            {
                _lock.Release();
            }
        });
    }

    public async Task<List<SystemLogEntry>> GetLogsAsync(DateTime dateUtc)
    {
        var fileName = $"log_{dateUtc:yyyy-MM-dd}.jsonl";
        var filePath = Path.Combine(_logDirectory, fileName);

        if (!File.Exists(filePath))
        {
            return new List<SystemLogEntry>();
        }

        var results = new List<SystemLogEntry>();
        await _lock.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<SystemLogEntry>(line, _jsonOptions);
                    if (entry != null)
                    {
                        results.Add(entry);
                    }
                }
                catch
                {
                    // Ignore corrupted lines
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return results;
    }

    public List<DateTime> GetAvailableLogDates()
    {
        if (!Directory.Exists(_logDirectory)) return new List<DateTime>();

        var files = Directory.GetFiles(_logDirectory, "log_*.jsonl");
        var dates = new List<DateTime>();

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file); // log_2026-08-20
            if (fileName.StartsWith("log_") && fileName.Length == 14)
            {
                var datePart = fileName.Substring(4); // 2026-08-20
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
                {
                    dates.Add(date);
                }
            }
        }

        return dates.OrderByDescending(d => d).ToList();
    }
}
