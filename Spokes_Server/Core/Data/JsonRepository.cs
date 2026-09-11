using System.Collections.Concurrent;
using System.Text.Json; // Make sure this is using System.Text.Json
using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Core.Data;

/// <summary>
/// Abstract base repository using JSON file persistence with in-memory caching.
/// NOTE: The C# assembly structure intentionally keeps repositories and models within a unified Core layer.
/// While scripting languages (like Python/JS) suffer from circular import failures, C# compiles namespace references
/// globally at compile-time. Bi-directional references between domain entities and repositories are standard DDD
/// aggregates, and separating them into abstract interfaces would add severe over-engineering boilerplate to this monolithic application.
/// </summary>
public abstract class JsonRepository<T> where T : class, IDataEntity
{
    protected readonly DiskPersistenceService _writer;
    protected readonly ConcurrentDictionary<string, T> _cache = new();
    protected readonly string _basePath;

    // NEW: Store the search pattern (Default to *.json)
    protected readonly string _searchPattern;

    public JsonRepository(DiskPersistenceService writer, string basePath, string searchPattern = "*.json")
    {
        _writer = writer;
        _basePath = basePath;
        _searchPattern = searchPattern;
    }

    public event Action<T>? OnSaved;

    public virtual List<T> GetAll() => _cache.Values.ToList();

    public virtual Task<List<T>> GetAllAsync() => Task.FromResult(GetAll());

    public virtual T? GetById(string id)
    {
        return _cache.TryGetValue(id, out var item) ? item : null;
    }

    public virtual Task<T?> GetByIdAsync(string id) => Task.FromResult(GetById(id));

    public virtual void Save(T item)
    {
        
        _cache[item.Id] = item;
        var path = GetFilePath(item);
        _writer.QueueWrite(path, item);
        OnSaved?.Invoke(item);
    }

    public virtual async Task SaveAsync(T item)
    {
        
        _cache[item.Id] = item;
        var path = GetFilePath(item);
        await _writer.QueueWriteAsync(path, item);
        OnSaved?.Invoke(item);
    }

    public virtual void Delete(string id)
    {
        if (_cache.TryRemove(id, out var item))
        {
            var path = GetFilePath(item);
            _writer.QueueDelete(path);
        }
    }

    public virtual async Task DeleteAsync(string id)
    {
        if (_cache.TryRemove(id, out var item))
        {
            var path = GetFilePath(item);
            await _writer.QueueDeleteAsync(path);
        }
    }

    protected abstract string GetFilePath(T item);

    // --- THE SMART LOADER ---
    public virtual void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        string[] files = Array.Empty<string>();
        try
        {
            // Use the specific pattern defined in the Constructor
            files = Directory.GetFiles(_basePath, _searchPattern, SearchOption.AllDirectories);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[JsonRepo] Failed to read files from {_basePath}: {ex.Message}");
            return;
        }

        var tempItems = new System.Collections.Generic.List<T>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<T>(json);
                if (item != null)
                {
                    tempItems.Add(item);
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"[JsonRepo] Failed to deserialize {file}: {ex.Message}");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[JsonRepo] Failed to read {file}: {ex.Message}");
            }
        }
        
        _cache.Clear();
        foreach (var item in tempItems)
        {
            _cache[item.Id] = item;
        }
    }

    // NEW: Clear the cache (Used for Restore)
    public virtual void Clear()
    {
        _cache.Clear();
    }
}
