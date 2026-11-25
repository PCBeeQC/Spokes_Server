using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Result of loading a page of channel messages, including cache vs. disk performance telemetry.
/// </summary>
public sealed record ChannelMessageLoadResult(
    List<ChatMessage> Messages,
    int CacheHits,
    int DiskReads,
    double IndexLookupMs,
    double DiskReadMs,
    bool HasMore);

/// <summary>
/// Repository for chat messages with lazy loading support.
/// On startup, only loads messages from the last 7 days.
/// Older messages are loaded on-demand when users scroll back or search.
/// </summary>
public class ChatMessageRepository : JsonRepository<ChatMessage>, IChatMessageLookup
{
    private const int InitialLoadCount = 55;
    private const int CacheCheckBatchThreshold = 50;

    private readonly ChatMessageIndexService _indexService;
    private readonly ChatMessageCacheService _cacheService;

    private int _cacheAdditionsCounter;
    private int _pruningTaskScheduled;

    private void NotifyCacheItemAdded(int count = 1)
    {
        if (Interlocked.Add(ref _cacheAdditionsCounter, count) >= CacheCheckBatchThreshold)
        {
            Interlocked.Exchange(ref _cacheAdditionsCounter, 0);
            TriggerBackgroundCachePruning();
        }
    }

    private void TriggerBackgroundCachePruning()
    {
        if (!_cacheService.IsCacheLimitExceeded())
            return;

        if (Interlocked.CompareExchange(ref _pruningTaskScheduled, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _cacheService.EnforceGlobalCacheSizeLimit(_cache);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[ChatMessageRepo] Error during background cache pruning");
                }
                finally
                {
                    Interlocked.Exchange(ref _pruningTaskScheduled, 0);
                }
            });
        }
    }

    /// <summary>
    /// Protected parameterless constructor strictly reserved for mock proxy frameworks (e.g. Moq).
    /// </summary>
    protected ChatMessageRepository()
        : base(null!, "Data", "*.json", null)
    {
        _cacheService = null!;
        _indexService = null!;
        _isLoaded = true;
    }

    public ChatMessageRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        CompanyProfileRepository companyProfiles)
        : this(writer, config, companyProfiles, null, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public ChatMessageRepository(
        DiskPersistenceService writer,
        IConfiguration config,
        CompanyProfileRepository companyProfiles,
        ChatMessageCacheService? cacheService = null,
        ILogger<ChatMessageRepository>? logger = null)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "chat"), "*.json", logger)
    {
        _cacheService = cacheService ?? new ChatMessageCacheService(companyProfiles);
        _indexService = new ChatMessageIndexService(writer, _basePath, this);
        _isLoaded = true;
    }


    public ChatMessage? GetCachedMessage(string messageId)
    {
        return _cache.TryGetValue(messageId, out var msg) ? msg : null;
    }

    public override void EnsureLoaded()
    {
        // ChatMessageRepository relies on partitioned indexes and on-demand message loading
    }

    public ChatMessageIndexService IndexService => _indexService;
    public ChatMessageCacheService CacheService => _cacheService;

    protected override string GetFilePath(ChatMessage item)
    {
        if (string.IsNullOrEmpty(item.ChannelId))
        {
            return Path.Combine(_basePath, "orphaned", $"{item.Id}.json");
        }

        var channelDir = Path.Combine(_basePath, item.ChannelId, "messages");
        return Path.Combine(channelDir, $"{item.Id}.json");
    }

    private void EnsureDirectoryExists(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "[ChatMessageRepo] IO Error during directory sync");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogError(ex, "[ChatMessageRepo] Access Denied during directory sync");
        }
    }

    public override void Save(ChatMessage item)
    {
        if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString();
        EnsureDirectoryExists(Path.GetDirectoryName(GetFilePath(item)));
        _indexService.UpdateIndex(item, item);
        base.Save(item);
        _cacheService.TrackAccess(item);
        TriggerBackgroundCachePruning();
    }

    public override void SaveBatch(IEnumerable<ChatMessage> items)
    {
        var itemList = items as IList<ChatMessage> ?? items.ToList();
        if (itemList.Count == 0) return;

        EnsureLoaded();
        var checkedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in itemList)
        {
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString();
            var path = GetFilePath(item);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && checkedDirs.Add(dir))
            {
                EnsureDirectoryExists(dir);
            }

            _indexService.UpdateIndex(item, item);
            _cache[item.Id] = item;
            _writer.QueueWrite(path, item);
            RaiseOnSaved(item);
            _cacheService.TrackAccess(item);
        }

        NotifyCacheItemAdded(itemList.Count);
        TriggerBackgroundCachePruning();
    }

    public override ChatMessage? GetById(string id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;

        var (channelId, _) = _indexService.FindIndexEntry(id);
        ChatMessage? msg = null;

        if (!string.IsNullOrEmpty(channelId))
        {
            msg = GetMessageFromDisk(channelId, id);
        }
        else
        {
            var orphanedPath = GetFilePath(new ChatMessage { Id = id, ChannelId = string.Empty });
            if (File.Exists(orphanedPath))
            {
                msg = ChatMessageIndexService.TryReadMessageFile(orphanedPath, _logger, "[ChatMessageRepo]");
            }
        }

        if (msg != null)
        {
            _cache[msg.Id] = msg;
            _cacheService.TrackAccess(msg);
            NotifyCacheItemAdded();
        }

        return msg;
    }

    /// <summary>
    /// Retrieves a message by ID, checking cache first, falling back to direct disk loading via channel ID or orphaned storage.
    /// Follows the scope-container-first convention (channelId, messageId) consistent with EmailMessageRepository (employeeId, messageId).
    /// </summary>
    public ChatMessage? GetByIdOrLoad(string? channelId, string messageId)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        if (!string.IsNullOrEmpty(channelId))
        {
            var msg = GetMessageFromDisk(channelId, messageId);
            if (msg != null)
            {
                _cache[msg.Id] = msg;
                _cacheService.TrackAccess(msg);
                NotifyCacheItemAdded();
                return msg;
            }
        }

        return GetById(messageId);
    }

    /// <summary>
    /// Retrieves a message by ID across all channels, checking cache first, falling back to index and orphaned storage.
    /// </summary>
    public ChatMessage? GetByIdOrLoad(string messageId) => GetByIdOrLoad(null, messageId);

    public override void Delete(string id)
    {
        string? filePath = null;
        ChatMessage? itemToDelete = null;

        if (_cache.TryRemove(id, out var item))
        {
            _indexService.RemoveFromIndex(item);
            filePath = GetFilePath(item);
            itemToDelete = item;
        }
        else
        {
            var (channelId, _) = _indexService.FindIndexEntry(id);
            if (!string.IsNullOrEmpty(channelId))
            {
                itemToDelete = GetMessageFromDisk(channelId, id) ?? new ChatMessage { Id = id, ChannelId = channelId };
                _indexService.RemoveFromIndex(itemToDelete);
                filePath = Path.Combine(_basePath, channelId, "messages", $"{id}.json");
            }
            else
            {
                var orphanedPath = GetFilePath(new ChatMessage { Id = id, ChannelId = string.Empty });
                if (File.Exists(orphanedPath))
                {
                    itemToDelete = ChatMessageIndexService.TryReadMessageFile(orphanedPath, _logger, "[ChatMessageRepo]")
                        ?? new ChatMessage { Id = id, ChannelId = string.Empty };
                    filePath = orphanedPath;
                }
            }
        }

        if (filePath != null)
        {
            _writer.QueueDelete(filePath);
        }

        _cacheService.Remove(id);

        if (itemToDelete != null)
        {
            RaiseOnDeleted(itemToDelete);
        }
    }

    public void RefreshChannelPreview(string channelId) =>
        _indexService.RefreshChannelPreview(channelId);

    public ChannelPreviewSummary? GetChannelPreviewSummary(string channelId, IReadOnlyList<string>? blockedUserIds = null) =>
        _indexService.GetChannelPreviewSummary(channelId, blockedUserIds);

    public List<ChatIndexEntry> GetIndexForChannel(string channelId) =>
        _indexService.GetIndexForChannel(channelId);

    public virtual DateTime? GetLatestMessageTimestamp(string channelId) =>
        _indexService.GetLatestMessageTimestamp(channelId);

    public ChatMessage? GetMessageFromDisk(string channelId, string messageId)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        var filePath = Path.Combine(_basePath, channelId, "messages", $"{messageId}.json");
        return File.Exists(filePath)
            ? ChatMessageIndexService.TryReadMessageFile(filePath, _logger, "[ChatMessageRepo]")
            : null;
    }

    public async Task<ChatMessage?> GetMessageFromDiskAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        var filePath = Path.Combine(_basePath, channelId, "messages", $"{messageId}.json");
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<ChatMessage>(json);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "[ChatMessageRepo] Failed to deserialize message from disk {FilePath}", filePath);
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "[ChatMessageRepo] IO error reading message from disk {FilePath}", filePath);
            }
        }
        return null;
    }

    public override void Clear()
    {
        lock (_loadLock)
        {
            base.Clear();
            _cacheService.Clear();
            _indexService.Clear();
        }
    }

    public override void LoadFromDisk()
    {
        lock (_loadLock)
        {
            if (!Directory.Exists(_basePath))
            {
                RawCache.Clear();
                _cacheService.Clear();
                _indexService.Clear();
                _isLoaded = true;
                return;
            }

            _isLoaded = true;
            _cache.Clear();
            _cacheService.Clear();
            _indexService.Clear();

            if (_indexService.TryLoadMasterIndex(out var loadedIndex) && loadedIndex != null)
            {
                // FAST PATH: Use index to load recent messages
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var kvp in loadedIndex)
                {
                    var channelId = kvp.Key;
                    var channelIndex = kvp.Value;

                    var targetEntries = channelIndex
                        .Where(e => !e.IsDeleted && e.SentAt > cutoff)
                        .OrderByDescending(e => e.SentAt)
                        .Take(InitialLoadCount)
                        .ToList();

                    foreach (var entry in targetEntries)
                    {
                        var item = GetMessageFromDisk(channelId, entry.Id);
                        if (item != null)
                        {
                            _cache[item.Id] = item;
                            _cacheService.TrackAccess(item);
                        }
                    }

                    // Immediately initialize in-memory channel preview from index
                    _indexService.RefreshChannelPreview(channelId);
                }

                // Asynchronously backfill PreviewText for legacy unmigrated indexes in background
                _indexService.TriggerBackgroundPreviewBackfill(GetMessageFromDisk);
                TriggerBackgroundCachePruning();
                return;
            }

            // SLOW PATH: Delegate fallback disk scanning and index reconstruction to ChatMessageIndexService
            var recentMessages = _indexService.RebuildMasterIndexFromDisk(InitialLoadCount);
            foreach (var item in recentMessages)
            {
                _cache[item.Id] = item;
                _cacheService.TrackAccess(item);
            }
            TriggerBackgroundCachePruning();
        }
    }

    /// <summary>
    /// Fires a non-blocking background pre-warm of the newest messages per channel so first channel opens
    /// after server restart hit RAM instead of waiting on cold disk reads (even for channels inactive > 7 days).
    /// </summary>
    public void StartBackgroundChannelHeadPreWarm(int messagesPerChannel = 12)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await PreWarmChannelHeadsAsync(messagesPerChannel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[ChatMessageRepo] Background channel head pre-warm stopped with error.");
            }
        });
    }

    /// <summary>
    /// Loads up to <paramref name="messagesPerChannel"/> newest uncached messages per channel from disk
    /// (most recently active channels first) while respecting the configured global chat cache RAM budget.
    /// </summary>
    public async Task<int> PreWarmChannelHeadsAsync(int messagesPerChannel = 12, CancellationToken cancellationToken = default)
    {
        if (messagesPerChannel <= 0 || _cacheService.IsCacheLimitExceeded())
        {
            return 0;
        }

        var snapshot = _indexService.GetChannelIndexesSnapshot();
        if (snapshot.Count == 0)
        {
            return 0;
        }

        var channelWork = new List<(DateTime LatestSentAt, string ChannelId, List<string> MissingMessageIds)>();
        foreach (var (channelId, entries) in snapshot)
        {
            if (entries == null || entries.Count == 0) continue;

            var newestEntries = entries
                .Where(e => !e.IsDeleted)
                .OrderByDescending(e => e.SentAt)
                .Take(messagesPerChannel)
                .ToList();

            if (newestEntries.Count == 0) continue;

            var missingIds = new List<string>();
            foreach (var entry in newestEntries)
            {
                if (!_cache.ContainsKey(entry.Id))
                {
                    missingIds.Add(entry.Id);
                }
            }

            if (missingIds.Count > 0)
            {
                channelWork.Add((newestEntries[0].SentAt, channelId, missingIds));
            }
        }

        if (channelWork.Count == 0)
        {
            return 0;
        }

        channelWork.Sort((a, b) => b.LatestSentAt.CompareTo(a.LatestSentAt));

        var flatTargets = new List<(string ChannelId, string MessageId)>();
        foreach (var (_, channelId, missingIds) in channelWork)
        {
            foreach (var messageId in missingIds)
            {
                flatTargets.Add((channelId, messageId));
            }
        }

        var sw = Stopwatch.StartNew();
        int loadedCount = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 3),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(flatTargets, parallelOptions, async (target, ct) =>
        {
            if (ct.IsCancellationRequested || _cacheService.IsCacheLimitExceeded())
            {
                return;
            }

            if (_cache.ContainsKey(target.MessageId))
            {
                return;
            }

            var msg = await GetMessageFromDiskAsync(target.ChannelId, target.MessageId, ct).ConfigureAwait(false);
            if (msg != null)
            {
                _cache[msg.Id] = msg;
                _cacheService.TrackAccess(msg);
                Interlocked.Increment(ref loadedCount);
            }
        }).ConfigureAwait(false);

        sw.Stop();
        if (loadedCount > 0)
        {
            _logger?.LogInformation(
                "[ChatMessageRepo] Background head pre-warm loaded {LoadedCount} messages across {ChannelCount} channels in {ElapsedMs:F0}ms.",
                loadedCount, channelWork.Count, sw.Elapsed.TotalMilliseconds);
        }

        return loadedCount;
    }

    private void EnsureIndexBuilt(string channelId) =>
        _indexService.EnsureIndexBuilt(channelId);

    public override List<ChatMessage> GetAll() => CollectMessages(senderId: null);

    public List<ChatMessage> GetAllForSender(string senderId) =>
        string.IsNullOrEmpty(senderId) ? [] : CollectMessages(senderId);

    private List<ChatMessage> CollectMessages(string? senderId)
    {
        var results = new Dictionary<string, ChatMessage>();
        bool filterBySender = !string.IsNullOrEmpty(senderId);

        // 1. Seed with in-memory cache
        foreach (var kvp in _cache)
        {
            if (!filterBySender || kvp.Value.SenderId == senderId)
            {
                results[kvp.Key] = kvp.Value;
            }
        }

        // 2. Ensure channel indexes on disk are built
        _indexService.EnsureAllChannelIndexesBuilt();

        // 3. Query index snapshot across all channels
        var snapshot = _indexService.GetChannelIndexesSnapshot();
        foreach (var (channelId, entries) in snapshot)
        {
            IEnumerable<ChatIndexEntry> targetEntries = filterBySender
                ? entries.Where(e => e.SenderId == senderId).ToList()
                : entries;
            foreach (var entry in targetEntries)
            {
                if (!results.ContainsKey(entry.Id))
                {
                    if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                    {
                        results[entry.Id] = cachedMsg;
                    }
                    else
                    {
                        var msg = GetMessageFromDisk(channelId, entry.Id);
                        if (msg != null)
                        {
                            results[entry.Id] = msg;
                        }
                    }
                }
            }
        }

        // 4. Scan orphaned messages on disk
        foreach (var item in LoadOrphanedMessagesFromDisk())
        {
            if ((!filterBySender || item.SenderId == senderId) && !results.ContainsKey(item.Id))
            {
                results[item.Id] = item;
            }
        }

        // 5. Add any remaining items in _cache (in case any were added during disk scan)
        foreach (var kvp in _cache)
        {
            if ((!filterBySender || kvp.Value.SenderId == senderId) && !results.ContainsKey(kvp.Key))
            {
                results[kvp.Key] = kvp.Value;
            }
        }

        return results.Values.ToList();
    }

    private IEnumerable<ChatMessage> LoadOrphanedMessagesFromDisk()
    {
        if (!Directory.Exists(_basePath)) yield break;
        var orphanedDir = Path.Combine(_basePath, "orphaned");
        if (!Directory.Exists(orphanedDir)) yield break;

        var files = Directory.GetFiles(orphanedDir, "*.json");
        foreach (var file in files)
        {
            var item = ChatMessageIndexService.TryReadMessageFile(file, _logger, "[ChatMessageRepo]");
            if (item != null)
            {
                yield return item;
            }
        }
    }

    private List<ChatIndexEntry>? GetOrderedChannelEntries(string channelId, int? limit)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count == 0) return null;

        var query = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt);
        return (limit.HasValue ? query.Take(limit.Value) : query).ToList();
    }

    private List<ChatMessage> GetCachedMessagesForEntries(IEnumerable<ChatIndexEntry> entries)
    {
        var results = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            if (_cache.TryGetValue(entry.Id, out var cachedMsg))
            {
                results.Add(cachedMsg);
            }
        }
        return results;
    }

    private List<ChatMessage> LoadMessagesForEntries(string channelId, IReadOnlyCollection<ChatIndexEntry> entries)
    {
        var results = new List<ChatMessage>(entries.Count);
        bool addedToCache = false;
        foreach (var entry in entries)
        {
            if (_cache.TryGetValue(entry.Id, out var cachedMsg))
            {
                results.Add(cachedMsg);
            }
            else
            {
                var msg = GetMessageFromDisk(channelId, entry.Id);
                if (msg != null)
                {
                    _cache[msg.Id] = msg;
                    results.Add(msg);
                    addedToCache = true;
                }
            }
        }
        if (addedToCache)
        {
            NotifyCacheItemAdded();
        }
        return results;
    }

    private Task<ChannelMessageLoadResult> LoadMessagesForEntriesWithStatsAsync(
        string channelId,
        IReadOnlyList<ChatIndexEntry> entries,
        bool hasMore,
        double indexLookupMs,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return Task.FromResult(new ChannelMessageLoadResult([], 0, 0, indexLookupMs, 0, hasMore));
        }

        // Fast path: check in-memory cache first. When every entry is already cached, return synchronously
        // without hopping onto the thread pool or capturing a Blazor synchronization context.
        var slots = new ChatMessage?[entries.Count];
        List<int>? missingIndices = null;
        int cacheHits = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            if (_cache.TryGetValue(entries[i].Id, out var cachedMsg))
            {
                slots[i] = cachedMsg;
                cacheHits++;
            }
            else
            {
                (missingIndices ??= []).Add(i);
            }
        }

        if (missingIndices == null)
        {
            var fastList = new List<ChatMessage>(entries.Count);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null) fastList.Add(slots[i]!);
            }
            return Task.FromResult(new ChannelMessageLoadResult(fastList, cacheHits, 0, indexLookupMs, 0, hasMore));
        }

        // Cold path: offload all uncached File.Exists / open / read / JSON deserialize work off the
        // Blazor circuit SynchronizationContext onto the thread pool with bounded parallelism so
        // slow disk or NAS volumes never freeze UI interaction or Back navigation.
        return LoadMissingEntriesOffCircuitAsync(
            channelId,
            entries,
            slots,
            missingIndices,
            cacheHits,
            hasMore,
            indexLookupMs,
            cancellationToken);
    }

    private Task<ChannelMessageLoadResult> LoadMissingEntriesOffCircuitAsync(
        string channelId,
        IReadOnlyList<ChatIndexEntry> entries,
        ChatMessage?[] slots,
        List<int> missingIndices,
        int cacheHits,
        bool hasMore,
        double indexLookupMs,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var diskSw = Stopwatch.StartNew();
            int diskReads = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(missingIndices, parallelOptions, async (slotIndex, ct) =>
            {
                var entry = entries[slotIndex];
                var msg = await GetMessageFromDiskAsync(channelId, entry.Id, ct).ConfigureAwait(false);
                if (msg != null)
                {
                    _cache[msg.Id] = msg;
                    _cacheService.TrackAccess(msg);
                    slots[slotIndex] = msg;
                    Interlocked.Increment(ref diskReads);
                }
            }).ConfigureAwait(false);

            diskSw.Stop();
            var diskMs = diskSw.Elapsed.TotalMilliseconds;

            if (diskReads > 0)
            {
                NotifyCacheItemAdded(diskReads);
            }

            var results = new List<ChatMessage>(entries.Count);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null) results.Add(slots[i]!);
            }

            if (indexLookupMs + diskMs >= 200)
            {
                _logger?.LogWarning(
                    "[Perf][ChatMessageRepo] Slow channel load: channel={ChannelId} loaded={Count} cacheHits={CacheHits} diskReads={DiskReads} indexMs={IndexMs:F1} diskMs={DiskMs:F1}",
                    channelId, results.Count, cacheHits, diskReads, indexLookupMs, diskMs);
            }

            return new ChannelMessageLoadResult(results, cacheHits, diskReads, indexLookupMs, diskMs, hasMore);
        }, cancellationToken);
    }

    public List<ChatMessage> GetByChannel(string channelId, int? limit = null)
    {
        var targetEntries = GetOrderedChannelEntries(channelId, limit);
        if (targetEntries == null) return [];
        if (!limit.HasValue) return GetCachedMessagesForEntries(targetEntries);

        return LoadMessagesForEntries(channelId, targetEntries);
    }

    public Task<ChannelMessageLoadResult> GetByChannelWithStatsAsync(string channelId, int? limit = null, CancellationToken cancellationToken = default)
    {
        var indexSw = Stopwatch.StartNew();
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count == 0)
        {
            indexSw.Stop();
            return Task.FromResult(new ChannelMessageLoadResult([], 0, 0, indexSw.Elapsed.TotalMilliseconds, 0, false));
        }

        var ordered = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt);
        if (!limit.HasValue)
        {
            var allEntries = ordered.ToList();
            indexSw.Stop();
            var cachedOnly = GetCachedMessagesForEntries(allEntries);
            return Task.FromResult(new ChannelMessageLoadResult(cachedOnly, cachedOnly.Count, 0, indexSw.Elapsed.TotalMilliseconds, 0, false));
        }

        var taken = ordered.Take(limit.Value + 1).ToList();
        bool hasMore = taken.Count > limit.Value;
        if (hasMore)
        {
            taken.RemoveAt(taken.Count - 1);
        }
        indexSw.Stop();

        return LoadMessagesForEntriesWithStatsAsync(channelId, taken, hasMore, indexSw.Elapsed.TotalMilliseconds, cancellationToken);
    }

    public async Task<List<ChatMessage>> GetByChannelAsync(string channelId, int? limit = null, CancellationToken cancellationToken = default)
    {
        var result = await GetByChannelWithStatsAsync(channelId, limit, cancellationToken).ConfigureAwait(false);
        return result.Messages;
    }

    public List<ChatMessage> GetByChannelBefore(string channelId, DateTime before, int count = 50)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count == 0) return [];

        var targetEntries = channelIndex
            .Where(e => !e.IsDeleted && e.SentAt < before)
            .OrderByDescending(e => e.SentAt)
            .Take(count)
            .ToList();

        return LoadMessagesForEntries(channelId, targetEntries);
    }

    public Task<ChannelMessageLoadResult> GetByChannelBeforeWithStatsAsync(string channelId, DateTime before, int count = 50, CancellationToken cancellationToken = default)
    {
        var indexSw = Stopwatch.StartNew();
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count == 0)
        {
            indexSw.Stop();
            return Task.FromResult(new ChannelMessageLoadResult([], 0, 0, indexSw.Elapsed.TotalMilliseconds, 0, false));
        }

        var taken = channelIndex
            .Where(e => !e.IsDeleted && e.SentAt < before)
            .OrderByDescending(e => e.SentAt)
            .Take(count + 1)
            .ToList();
        bool hasMore = taken.Count > count;
        if (hasMore)
        {
            taken.RemoveAt(taken.Count - 1);
        }
        indexSw.Stop();

        return LoadMessagesForEntriesWithStatsAsync(channelId, taken, hasMore, indexSw.Elapsed.TotalMilliseconds, cancellationToken);
    }

    public async Task<List<ChatMessage>> GetByChannelBeforeAsync(string channelId, DateTime before, int count = 50, CancellationToken cancellationToken = default)
    {
        var result = await GetByChannelBeforeWithStatsAsync(channelId, before, count, cancellationToken).ConfigureAwait(false);
        return result.Messages;
    }

    public ChatMessage? GetLatestInChannel(string channelId)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var latestEntry = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt).FirstOrDefault();
            if (latestEntry != null)
            {
                if (_cache.TryGetValue(latestEntry.Id, out var cachedMsg)) return cachedMsg;
                var previewMsg = _indexService.GetCachedPreview(channelId);
                if (previewMsg != null && previewMsg.Id == latestEntry.Id) return previewMsg;

                var msg = GetMessageFromDisk(channelId, latestEntry.Id);
                if (msg != null)
                {
                    _indexService.SetCachedPreview(channelId, msg);
                    return msg;
                }
            }
        }
        return null;
    }

    public virtual int? ResolveAttachmentKeyEpoch(string channelId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        const string smallThumbSuffix = "_thumb_sm.jpg";
        const string thumbSuffix = "_thumb.jpg";
        var targetName = fileName;
        var baseName = fileName.EndsWith(smallThumbSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^smallThumbSuffix.Length]
            : fileName.EndsWith(thumbSuffix, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^thumbSuffix.Length]
                : fileName;

        var channelIndex = _indexService.GetIndexForChannel(channelId);
        foreach (var entry in channelIndex
            .Where(e => !e.IsDeleted && (e.HasImages || e.HasFiles))
            .OrderByDescending(e => e.SentAt))
        {
            var msg = _cache.TryGetValue(entry.Id, out var cached) ? cached : GetMessageFromDisk(channelId, entry.Id);
            if (msg?.Attachments == null) continue;

            foreach (var att in msg.Attachments)
            {
                if (string.IsNullOrWhiteSpace(att.FilePath)) continue;
                var attPath = att.FilePath;
                var qIdx = attPath.IndexOf('?');
                if (qIdx >= 0) attPath = attPath[..qIdx];
                var attName = Path.GetFileName(attPath.TrimEnd('/'));
                if (string.Equals(attName, targetName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attName, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    return att.KeyEpoch;
                }
            }
        }

        return null;
    }

    public int GetIndexPosition(string channelId, string messageId) =>
        _indexService.GetIndexPosition(channelId, messageId);

    public List<ChatMessage> Search(string channelId, string query)
    {
        EnsureIndexBuilt(channelId);

        var lowerQuery = query.ToLowerInvariant();
        var results = new List<ChatMessage>();

        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var matchingIds = channelIndex
                .Where(e => !e.IsDeleted && !e.IsEncrypted && e.SearchText != null && e.SearchText.Contains(lowerQuery))
                .OrderByDescending(e => e.SentAt)
                .Select(e => e.Id)
                .ToList();

            foreach (var id in matchingIds)
            {
                if (_cache.TryGetValue(id, out var cachedMsg))
                {
                    results.Add(cachedMsg);
                }
                else
                {
                    var msg = GetMessageFromDisk(channelId, id);
                    if (msg != null)
                    {
                        _cache[msg.Id] = msg;
                        results.Add(msg);
                    }
                }
            }
        }
        return results;
    }

    public bool HasMessagesBefore(string channelId, DateTime before)
    {
        EnsureIndexBuilt(channelId);
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            return channelIndex.Any(e => !e.IsDeleted && e.SentAt < before);
        }
        return _cache.Values.Any(m => m.ChannelId == channelId && !m.IsDeleted && m.SentAt < before);
    }

    public int GetUnreadCountSince(string channelId, DateTime since, string? excludeUserId = null)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var count = channelIndex.Count(e => !e.IsDeleted && e.SentAt > since && (excludeUserId == null || e.SenderId != excludeUserId));
            if (count > 0) return count;
        }

        // Fallback: check in-memory cache (zero disk I/O)
        return _cache.Values.Count(m =>
            m.ChannelId == channelId &&
            !m.IsDeleted &&
            m.SentAt > since &&
            (excludeUserId == null || m.SenderId != excludeUserId));
    }

    public List<ChatMessage> GetUnreadMessagesSince(string channelId, DateTime since, string? excludeUserId = null)
    {
        EnsureIndexBuilt(channelId);

        var channelIndex = _indexService.GetIndexForChannel(channelId);
        var results = new List<ChatMessage>();
        var loadedIds = new HashSet<string>();
        bool addedToCache = false;

        if (channelIndex.Count > 0)
        {
            var unreadEntries = channelIndex
                .Where(e => !e.IsDeleted && e.SentAt > since && (excludeUserId == null || e.SenderId != excludeUserId))
                .OrderBy(e => e.SentAt)
                .ToList();

            foreach (var entry in unreadEntries)
            {
                if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                {
                    results.Add(cachedMsg);
                    loadedIds.Add(cachedMsg.Id);
                }
                else
                {
                    var msg = GetMessageFromDisk(channelId, entry.Id);
                    if (msg != null)
                    {
                        _cache[msg.Id] = msg;
                        _cacheService.TrackAccess(msg);
                        results.Add(msg);
                        loadedIds.Add(msg.Id);
                        addedToCache = true;
                    }
                }
            }
        }

        // Fallback/Union: Include matching messages present in the in-memory cache
        var cachedMatches = _cache.Values.Where(m =>
            m.ChannelId == channelId &&
            !m.IsDeleted &&
            m.SentAt > since &&
            (excludeUserId == null || m.SenderId != excludeUserId));

        foreach (var msg in cachedMatches)
        {
            if (loadedIds.Add(msg.Id))
            {
                results.Add(msg);
            }
        }

        if (addedToCache)
        {
            NotifyCacheItemAdded();
        }

        return results;
    }

    public void EnforceGlobalCacheSizeLimit()
    {
        _cacheService.EnforceGlobalCacheSizeLimit(_cache);
    }

    public List<ChatMessage> GetAllByChannelIncludingCold(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return [];
        }

        var resultsById = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        foreach (var cached in _cache.Values)
        {
            if (string.Equals(cached.ChannelId, channelId, StringComparison.Ordinal))
            {
                resultsById[cached.Id] = cached;
            }
        }

        var messagesDir = Path.Combine(_basePath, channelId, "messages");
        if (Directory.Exists(messagesDir))
        {
            foreach (var filePath in Directory.EnumerateFiles(messagesDir, "*.json"))
            {
                var messageId = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrEmpty(messageId) || resultsById.ContainsKey(messageId))
                {
                    continue;
                }

                var msg = GetMessageFromDisk(channelId, messageId);
                if (msg != null)
                {
                    resultsById[msg.Id] = msg;
                }
            }
        }

        return resultsById.Values.ToList();
    }

    public void PurgeChannelDirectory(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        var cachedIds = _cache.Values
            .Where(m => string.Equals(m.ChannelId, channelId, StringComparison.Ordinal))
            .Select(m => m.Id)
            .ToList();

        foreach (var id in cachedIds)
        {
            _cache.TryRemove(id, out _);
            _cacheService.Remove(id);
        }

        _indexService.RemoveChannelIndex(channelId);

        var channelDir = Path.Combine(_basePath, channelId);
        if (Directory.Exists(channelDir))
        {
            _writer.QueueDeleteDirectory(channelDir);
        }
    }
}
