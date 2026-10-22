using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Data;

public class SequenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, int> _sequences;
    private readonly object _lock = new();
    private readonly ILogger<SequenceService>? _logger;
    private bool _isLoaded;

    public SequenceService(IConfiguration config)
        : this(config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public SequenceService(IConfiguration config, ILogger<SequenceService>? logger)
    {
        _logger = logger;
        var dataPath = config["DataPath"] ?? "Data";
        _filePath = Path.Combine(dataPath, "sequences.json");
        _sequences = new();
    }

    internal string FilePath => _filePath;

    /// <summary>
    /// Ensures that sequence state has been loaded from disk.
    /// Thread-safe and idempotent; executed lazily on first read/write operation if not loaded eagerly.
    /// </summary>
    public void EnsureLoaded()
    {
        if (_isLoaded) return;
        lock (_lock)
        {
            if (_isLoaded) return;
            Load();
            _isLoaded = true;
        }
    }

    public int GetNextSequence(string entityType)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_sequences.TryGetValue(entityType, out var current))
            {
                current = 0;
            }

            var next = current + 1;
            _sequences[entityType] = next;
            Save();
            return next;
        }
    }

    public string GenerateNumber(string entityType, string prefix)
    {
        var seq = GetNextSequence(entityType);
        var datePart = DateTime.Today.ToString("yyMM");
        // Format: PREFIX-YYMM-SEQ (e.g. F-2604-0007)
        return $"{prefix}-{datePart}-{seq:D4}";
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (data != null)
            {
                foreach (var kvp in data)
                {
                    _sequences[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading sequences from {FilePath}", _filePath);
        }
    }

    private void Save()
    {
        try
        {
            // Simple atomic write
            var json = JsonSerializer.Serialize(_sequences, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error saving sequences to {FilePath}", _filePath);
        }
    }
}
