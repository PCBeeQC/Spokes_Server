using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Logging;

public class SystemLogService : ISystemLogService, IAsyncDisposable, IDisposable
{
    private readonly string _logDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<SystemLogService>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<SystemLogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private bool _disposed;


    public SystemLogService(IConfiguration config, ILogger<SystemLogService>? logger = null, TimeProvider? timeProvider = null)
    {
        string dataPath = config["DataPath"] ?? "Data";
        _logDirectory = Path.Combine(dataPath, "Logs");
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _channel = Channel.CreateUnbounded<SystemLogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        _processingTask = Task.Run(ProcessQueueAsync);
    }

    public void LogError(string category, string message, string? details = null)
        => Log(category, "Error", message, details);

    public void LogInfo(string category, string message, string? details = null)
        => Log(category, "Info", message, details);

    public void Log(string category, string severity, string message, string? details = null)
    {
        var entry = new SystemLogEntry
        {
            TimestampUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Category = category,
            Severity = severity,
            Message = message,
            Details = details
        };

        if (!_channel.Writer.TryWrite(entry))
        {
            _logger?.LogWarning("[SystemLogService] Failed to enqueue log entry for category {Category}", category);
        }
    }

    public Task LogErrorAsync(string category, string message, string? details = null)
        => LogAsync(category, "Error", message, details);

    public Task LogInfoAsync(string category, string message, string? details = null)
        => LogAsync(category, "Info", message, details);

    public async Task LogAsync(string category, string severity, string message, string? details = null)
    {
        Log(category, severity, message, details);
        await FlushAsync();
    }

    public async Task FlushAsync()
    {
        await DrainChannelAsync();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                await DrainChannelAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful cancellation on shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SystemLogService] Background processing failed: {Message}", ex.Message);
        }
    }

    private async Task DrainChannelAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            while (_channel.Reader.TryRead(out var entry))
            {
                await WriteEntryToFileAsync(entry);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteEntryToFileAsync(SystemLogEntry entry)
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            var fileName = $"log_{entry.TimestampUtc:yyyy-MM-dd}.jsonl";
            var filePath = Path.Combine(_logDirectory, fileName);
            var jsonLine = JsonSerializer.Serialize(entry, _jsonOptions);
            await File.AppendAllTextAsync(filePath, jsonLine + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Fallback to logger to avoid unmonitored silent errors
            _logger?.LogError(ex, "[SystemLogService] Failed to write log entry: {Message}", ex.Message);
        }
    }

    public async Task<List<SystemLogEntry>> GetLogsAsync(DateTime dateUtc)
    {
        await FlushAsync();

        var fileName = $"log_{dateUtc:yyyy-MM-dd}.jsonl";
        var filePath = Path.Combine(_logDirectory, fileName);

        await _writeLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
            {
                return [];
            }

            List<SystemLogEntry> results = [];
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
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "[SystemLogService] Skipping corrupted log entry in {FilePath}: {Message}", filePath, ex.Message);
                }
            }

            return results;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public List<DateTime> GetAvailableLogDates()
    {
        if (!Directory.Exists(_logDirectory)) return [];

        var files = Directory.GetFiles(_logDirectory, "log_*.jsonl");
        List<DateTime> dates = [];

        foreach (var file in files)
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.Length == 0) continue;

            var fileName = Path.GetFileNameWithoutExtension(file); // log_2026-08-20
            if (fileName.StartsWith("log_") && fileName.Length == 14)
            {
                var datePart = fileName[4..]; // 2026-08-20
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", null, DateTimeStyles.None, out var date))
                {
                    dates.Add(date.Date);
                }
            }
        }

        return dates.OrderByDescending(d => d).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try
        {
            await _processingTask;
        }
        catch (Exception) { }

        await FlushAsync();
        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try
        {
            _processingTask.GetAwaiter().GetResult();
        }
        catch (Exception) { }

        _writeLock.Wait();
        try
        {
            while (_channel.Reader.TryRead(out var entry))
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                var fileName = $"log_{entry.TimestampUtc:yyyy-MM-dd}.jsonl";
                var filePath = Path.Combine(_logDirectory, fileName);
                var jsonLine = JsonSerializer.Serialize(entry, _jsonOptions);
                File.AppendAllText(filePath, jsonLine + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SystemLogService] Failed to flush log entry during synchronous dispose: {Message}", ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }

        _cts.Cancel();
        _cts.Dispose();
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
