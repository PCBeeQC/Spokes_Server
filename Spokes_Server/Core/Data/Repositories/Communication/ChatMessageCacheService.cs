using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class ChatMessageCacheService
{
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly ConcurrentDictionary<string, int> _messageSizes = new();
    private readonly ConcurrentDictionary<string, DateTime> _messageAccessTimes = new();
    private readonly SemaphoreSlim _pruningSemaphore = new(1, 1);
    private long _totalCacheSizeBytes;

    public ChatMessageCacheService(CompanyProfileRepository companyProfiles)
    {
        _companyProfiles = companyProfiles;
    }

    public long TotalCacheSizeBytes => Math.Max(0, Interlocked.Read(ref _totalCacheSizeBytes));

    public void TrackAccess(ChatMessage item) => TrackAccess(item, DateTime.UtcNow);

    internal void TrackAccess(ChatMessage item, DateTime accessTime)
    {
        _messageAccessTimes[item.Id] = accessTime;
        int estimatedSize = (item.Content?.Length ?? 0) * 2 + 500;

        while (true)
        {
            if (_messageSizes.TryGetValue(item.Id, out var oldSize))
            {
                if (oldSize == estimatedSize)
                {
                    return;
                }

                if (_messageSizes.TryUpdate(item.Id, estimatedSize, oldSize))
                {
                    Interlocked.Add(ref _totalCacheSizeBytes, estimatedSize - oldSize);
                    return;
                }
            }
            else
            {
                if (_messageSizes.TryAdd(item.Id, estimatedSize))
                {
                    Interlocked.Add(ref _totalCacheSizeBytes, estimatedSize);
                    return;
                }
            }
        }
    }

    public void Remove(string id)
    {
        if (_messageSizes.TryRemove(id, out var size))
        {
            Interlocked.Add(ref _totalCacheSizeBytes, -size);
        }
        _messageAccessTimes.TryRemove(id, out _);
    }

    public void Clear()
    {
        _messageSizes.Clear();
        _messageAccessTimes.Clear();
        Interlocked.Exchange(ref _totalCacheSizeBytes, 0);
    }

    public bool IsCacheLimitExceeded()
    {
        var profile = _companyProfiles.Get();
        long maxBytes = (profile?.MaxChatCacheSizeMb ?? 500) * 1024L * 1024L;
        return Interlocked.Read(ref _totalCacheSizeBytes) > maxBytes;
    }

    public void EnforceGlobalCacheSizeLimit(ConcurrentDictionary<string, ChatMessage> cache)
    {
        if (!_pruningSemaphore.Wait(0))
        {
            return; // Already pruning
        }

        try
        {
            var profile = _companyProfiles.Get();
            long maxBytes = (profile?.MaxChatCacheSizeMb ?? 500) * 1024L * 1024L;

            if (Interlocked.Read(ref _totalCacheSizeBytes) <= maxBytes) return;

            // Find oldest messages by LAST ACCESSED TIME (LRU)
            var leastRecentlyUsedIds = _messageAccessTimes
                .OrderBy(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in leastRecentlyUsedIds)
            {
                if (Interlocked.Read(ref _totalCacheSizeBytes) <= maxBytes) break;

                if (cache.TryRemove(id, out _))
                {
                    if (_messageSizes.TryRemove(id, out var size))
                    {
                        Interlocked.Add(ref _totalCacheSizeBytes, -size);
                    }
                    _messageAccessTimes.TryRemove(id, out _);
                }
            }
        }
        finally
        {
            _pruningSemaphore.Release();
        }
    }
}
