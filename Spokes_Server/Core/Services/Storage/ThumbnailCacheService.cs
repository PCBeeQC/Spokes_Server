using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Storage;

public sealed record CachedThumbnailEntry(
    byte[] RawBytes,
    DateTimeOffset LastWriteUtc,
    string ETag,
    string ContentType);

/// <summary>
/// Bounded in-memory LRU cache of raw on-disk thumbnail bytes (plaintext for unencrypted files,
/// ciphertext for encrypted channels/albums). Plaintext of encrypted files is never cached in
/// shared memory; callers decrypt per-request only after full authorization and key resolution.
/// Eliminates repeated stat()/open()/read() syscalls on slow or network-backed storage volumes.
/// </summary>
public sealed class ThumbnailCacheService
{
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024; // 64 MB
    public const int DefaultMaxEntryBytes = 1024 * 1024;        // 1 MB

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed class CacheNode
    {
        public required string Key { get; init; }
        public required CachedThumbnailEntry Entry { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheNode>> _entries = new(PathComparer);
    private readonly LinkedList<CacheNode> _lru = new();
    private readonly ConcurrentDictionary<string, byte> _warmingPaths = new(PathComparer);
    private readonly SemaphoreSlim _warmGate = new(2, 2);

    private long _currentBytes;
    private long _invalidationEpoch;

    public long MaxTotalBytes { get; }
    public int MaxEntryBytes { get; }

    public ThumbnailCacheService(
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxEntryBytes = DefaultMaxEntryBytes)
    {
        MaxTotalBytes = maxTotalBytes > 0 ? maxTotalBytes : DefaultMaxTotalBytes;
        MaxEntryBytes = maxEntryBytes > 0 ? maxEntryBytes : DefaultMaxEntryBytes;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public long CurrentBytes
    {
        get
        {
            lock (_lock)
            {
                return _currentBytes;
            }
        }
    }

    public static string BuildETag(DateTimeOffset lastWriteUtc, long length) =>
        $"\"{lastWriteUtc.Ticks:x}_{length:x}\"";

    public static bool IsThumbnailFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        return fileName.EndsWith("_thumb.jpg", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("_thumb_sm.jpg", StringComparison.OrdinalIgnoreCase);
    }

    public long CaptureEpoch() => Interlocked.Read(ref _invalidationEpoch);

    public bool TryGet(string? physicalPath, out CachedThumbnailEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(physicalPath)) return false;

        var key = NormalizeKey(physicalPath);
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                return false;
            }

            if (_lru.First != node)
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
            }

            entry = node.Value.Entry;
            return true;
        }
    }

    public bool Set(string? physicalPath, byte[] rawBytes, DateTimeOffset lastWriteUtc, string contentType) =>
        SetInternal(physicalPath, rawBytes, lastWriteUtc, contentType, expectedEpoch: null);

    public bool TrySetIfUnchanged(
        string? physicalPath,
        byte[] rawBytes,
        DateTimeOffset lastWriteUtc,
        string contentType,
        long expectedEpoch) =>
        SetInternal(physicalPath, rawBytes, lastWriteUtc, contentType, expectedEpoch);

    private bool SetInternal(
        string? physicalPath,
        byte[] rawBytes,
        DateTimeOffset lastWriteUtc,
        string contentType,
        long? expectedEpoch)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) ||
            rawBytes == null ||
            rawBytes.Length == 0 ||
            rawBytes.Length > MaxEntryBytes ||
            rawBytes.Length > MaxTotalBytes)
        {
            return false;
        }

        var key = NormalizeKey(physicalPath);
        var normalizedLastWrite = lastWriteUtc.ToUniversalTime();
        var cached = new CachedThumbnailEntry(
            rawBytes,
            normalizedLastWrite,
            BuildETag(normalizedLastWrite, rawBytes.Length),
            string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType);

        lock (_lock)
        {
            if (expectedEpoch.HasValue && Interlocked.Read(ref _invalidationEpoch) != expectedEpoch.Value)
            {
                return false;
            }

            if (_entries.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Value.Entry.RawBytes.Length;
                existing.Value.Entry = cached;
                _currentBytes += rawBytes.Length;
                if (_lru.First != existing)
                {
                    _lru.Remove(existing);
                    _lru.AddFirst(existing);
                }
            }
            else
            {
                var node = new LinkedListNode<CacheNode>(new CacheNode
                {
                    Key = key,
                    Entry = cached
                });
                _lru.AddFirst(node);
                _entries[key] = node;
                _currentBytes += rawBytes.Length;
            }

            EvictOverLimitLocked();
            return true;
        }
    }

    /// <summary>
    /// Warms an existing thumbnail from disk asynchronously (used after a cold 304 Not Modified so
    /// subsequent If-None-Match revalidations and downloads hit RAM with zero disk syscalls).
    /// </summary>
    public void WarmFromDiskInBackground(string? physicalPath, DateTimeOffset expectedLastWriteUtc, long expectedLength, string contentType)
    {
        if (string.IsNullOrWhiteSpace(physicalPath) || expectedLength <= 0 || expectedLength > MaxEntryBytes)
        {
            return;
        }

        var key = NormalizeKey(physicalPath);
        if (!_warmingPaths.TryAdd(key, 0))
        {
            return;
        }

        long epoch = CaptureEpoch();
        _ = Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                await _warmGate.WaitAsync().ConfigureAwait(false);
                acquired = true;

                if (CaptureEpoch() != epoch || TryGet(key, out _))
                {
                    return;
                }

                var rawBytes = await File.ReadAllBytesAsync(key).ConfigureAwait(false);
                if (rawBytes.Length == 0 || rawBytes.Length > MaxEntryBytes)
                {
                    return;
                }

                // Only populate if length still matches the 304 stat we observed, preventing
                // mismatched ETags if the file was modified mid-read.
                if (rawBytes.Length == expectedLength)
                {
                    TrySetIfUnchanged(key, rawBytes, expectedLastWriteUtc, contentType, epoch);
                }
            }
            catch
            {
                // Best-effort background warm; ignore transient I/O errors or deleted files.
            }
            finally
            {
                if (acquired) _warmGate.Release();
                _warmingPaths.TryRemove(key, out _);
            }
        });
    }

    public void Invalidate(string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath)) return;

        var key = NormalizeKey(physicalPath);
        lock (_lock)
        {
            Interlocked.Increment(ref _invalidationEpoch);
            RemoveKeyLocked(key);
        }
    }

    public void InvalidateFileAndThumbnails(string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath)) return;

        var fullPath = NormalizeKey(physicalPath);
        var dir = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        var fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);

        lock (_lock)
        {
            Interlocked.Increment(ref _invalidationEpoch);
            RemoveKeyLocked(fullPath);

            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(fileName))
            {
                RemoveKeyLocked(Path.Combine(dir, FileService.GetThumbnailFileName(fileName, small: false)));
                RemoveKeyLocked(Path.Combine(dir, FileService.GetThumbnailFileName(fileName, small: true)));
                if (!string.IsNullOrEmpty(fileNameNoExt))
                {
                    RemoveKeyLocked(Path.Combine(dir, $"{fileNameNoExt}_thumb.jpg"));
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _invalidationEpoch);
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }

    private void RemoveKeyLocked(string key)
    {
        if (_entries.Remove(key, out var node))
        {
            _lru.Remove(node);
            _currentBytes = Math.Max(0, _currentBytes - node.Value.Entry.RawBytes.Length);
        }
    }

    private void EvictOverLimitLocked()
    {
        while (_currentBytes > MaxTotalBytes && _lru.Last != null)
        {
            var oldest = _lru.Last;
            _lru.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            _currentBytes = Math.Max(0, _currentBytes - oldest.Value.Entry.RawBytes.Length);
        }
    }

    private static string NormalizeKey(string physicalPath)
    {
        try
        {
            return Path.GetFullPath(physicalPath);
        }
        catch
        {
            return physicalPath;
        }
    }
}
