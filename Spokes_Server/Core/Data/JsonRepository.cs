using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
    private readonly ConcurrentDictionary<string, T> _storage = new();
    protected readonly string _basePath;
    protected readonly ILogger? _logger;

    // NEW: Store the search pattern (Default to *.json)
    protected readonly string _searchPattern;

    protected volatile bool _isLoaded;
    protected readonly object _loadLock = new();

    /// <summary>
    /// Thread-safe in-memory cache. Accessing this property guarantees that <see cref="EnsureLoaded"/> has run.
    /// Derived repositories querying cache directly via <see cref="_cache"/> or <see cref="Cache"/> trigger lazy loading automatically.
    /// </summary>
    protected ConcurrentDictionary<string, T> _cache
    {
        get
        {
            EnsureLoaded();
            return _storage;
        }
    }

    /// <summary>
    /// Thread-safe in-memory cache. Accessing this property guarantees that <see cref="EnsureLoaded"/> has run.
    /// </summary>
    protected ConcurrentDictionary<string, T> Cache => _cache;

    /// <summary>
    /// Direct access to underlying cache storage without triggering <see cref="EnsureLoaded"/>.
    /// Used internally by disk loaders, cache clear operations, and bulk pruning routines.
    /// </summary>
    protected ConcurrentDictionary<string, T> RawCache => _storage;

    public JsonRepository(DiskPersistenceService writer, string basePath, string searchPattern = "*.json")
        : this(writer, basePath, searchPattern, null)
    {
    }

    public JsonRepository(DiskPersistenceService writer, string basePath, string searchPattern, ILogger? logger)
    {
        _writer = writer;
        _basePath = basePath;
        _searchPattern = searchPattern;
        _logger = logger;
    }

    public event Action<T>? OnSaved;
    public event Action<T>? OnDeleted;
    public event Action? OnLoaded;

    /// <summary>
    /// Ensures that the repository state has been loaded from disk.
    /// Thread-safe and idempotent; executed lazily on first read operation if not loaded eagerly.
    /// </summary>
    public virtual void EnsureLoaded()
    {
        if (_isLoaded) return;
        lock (_loadLock)
        {
            if (_isLoaded) return;
            LoadFromDisk();
            _isLoaded = true;
        }
    }

    public virtual List<T> GetAll()
    {
        EnsureLoaded();
        return _cache.Values.ToList();
    }

    public virtual T? GetById(string id)
    {
        EnsureLoaded();
        return _cache.TryGetValue(id, out var item) ? item : null;
    }

    public virtual void Save(T item)
    {
        EnsureLoaded();
        _cache[item.Id] = item;
        var path = GetFilePath(item);
        _writer.QueueWrite(path, item);
        RaiseOnSaved(item);
    }

    public virtual void SaveBatch(IEnumerable<T> items)
    {
        EnsureLoaded();
        foreach (var item in items)
        {
            _cache[item.Id] = item;
            var path = GetFilePath(item);
            _writer.QueueWrite(path, item);
            RaiseOnSaved(item);
        }
    }

    protected virtual void RaiseOnSaved(T item) => OnSaved?.Invoke(item);
    protected virtual void RaiseOnLoaded() => OnLoaded?.Invoke();

    public virtual void Delete(string id)
    {
        EnsureLoaded();
        if (_cache.TryRemove(id, out var item))
        {
            var path = GetFilePath(item);
            _writer.QueueDelete(path);
            RaiseOnDeleted(item);
        }
    }

    protected virtual void RaiseOnDeleted(T item)
    {
        OnDeleted?.Invoke(item);
        if (item is IHasAssociatedFiles fileBearer)
        {
            RepositoryFileCascade.HandleEntityDeleted(fileBearer);
        }
    }

    protected abstract string GetFilePath(T item);

    // --- THE SMART LOADER ---
    public virtual void LoadFromDisk()
    {
        lock (_loadLock)
        {
            if (!Directory.Exists(_basePath))
            {
                _isLoaded = true;
                RaiseOnLoaded();
                return;
            }

            string[] files = [];
            try
            {
                // Use the specific pattern defined in the Constructor
                files = Directory.GetFiles(_basePath, _searchPattern, SearchOption.AllDirectories);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "[JsonRepo] Failed to read files from {BasePath}", _basePath);
                return;
            }

            var tempItems = new List<T>();
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
                    _logger?.LogWarning(ex, "[JsonRepo] Failed to deserialize {File}", file);
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "[JsonRepo] Failed to read {File}", file);
                }
            }

            _storage.Clear();
            foreach (var item in tempItems)
            {
                _storage[item.Id] = item;
            }
            _isLoaded = true;
        }
        RaiseOnLoaded();
    }

    // NEW: Clear the cache (Used for Restore)
    public virtual void Clear()
    {
        lock (_loadLock)
        {
            _storage.Clear();
            _isLoaded = true;
        }
        RaiseOnLoaded();
    }
}
