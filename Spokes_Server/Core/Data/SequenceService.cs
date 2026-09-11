using System.Collections.Concurrent;
using System.Text.Json;

namespace Spokes_Server.Core.Data;

public class SequenceService
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, int> _sequences;
    private readonly object _lock = new();

    public SequenceService(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? "Data";
        _filePath = Path.Combine(dataPath, "sequences.json");
        _sequences = new ConcurrentDictionary<string, int>();

        Load();
    }

    public int GetNextSequence(string entityType)
    {
        lock (_lock)
        {
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
            Console.WriteLine($"Error loading sequences: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            // Simple atomic write
            var json = JsonSerializer.Serialize(_sequences, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving sequences: {ex.Message}");
        }
    }
}
