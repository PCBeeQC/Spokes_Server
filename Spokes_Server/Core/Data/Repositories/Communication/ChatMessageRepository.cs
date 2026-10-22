using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Repository for chat messages with lazy loading support.
/// On startup, only loads messages from the last 7 days.
/// Older messages are loaded on-demand when users scroll back or search.
/// </summary>
public class ChatMessageRepository : JsonRepository<ChatMessage>, IChatMessageLookup
{
    private const int InitialLoadCount = 55;
    private const int CacheCheckBatchThreshold = 50;

    private readonly CompanyProfileRepository _companyProfiles;
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
        _companyProfiles = null!;
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
        _companyProfiles = companyProfiles;
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

    private void EnsureDirectoryExists(ChatMessage item)
    {
        try
        {
            var path = GetFilePath(item);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
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
        EnsureDirectoryExists(item);
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
            if (!string.IsNullOrEmpty(dir) && checkedDirs.Add(dir) && !Directory.Exists(dir))
            {
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
                try
                {
                    var json = File.ReadAllText(orphanedPath);
                    msg = JsonSerializer.Deserialize<ChatMessage>(json);
                }
                catch (JsonException ex)
                {
                    _logger?.LogWarning(ex, "[ChatMessageRepo] Failed to deserialize orphaned message from disk {OrphanedPath}", orphanedPath);
                }
                catch (IOException ex)
                {
                    _logger?.LogWarning(ex, "[ChatMessageRepo] IO error reading orphaned message from disk {OrphanedPath}", orphanedPath);
                }
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

        if (_cache.TryRemove(id, out var item))
        {
            _indexService.RemoveFromIndex(item);
            filePath = GetFilePath(item);
        }
        else
        {
            var (channelId, _) = _indexService.FindIndexEntry(id);
            if (!string.IsNullOrEmpty(channelId))
            {
                _indexService.RemoveFromIndex(new ChatMessage { Id = id, ChannelId = channelId });
                filePath = Path.Combine(_basePath, channelId, "messages", $"{id}.json");
            }
            else
            {
                var orphanedPath = GetFilePath(new ChatMessage { Id = id, ChannelId = string.Empty });
                if (File.Exists(orphanedPath))
                {
                    filePath = orphanedPath;
                }
            }
        }

        if (filePath != null)
        {
            _writer.QueueDelete(filePath);
        }

        _cacheService.Remove(id);
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
        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
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

    public async Task<ChatMessage?> GetMessageFromDiskAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        var filePath = Path.Combine(_basePath, channelId, "messages", $"{messageId}.json");
        if (File.Exists(filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
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
                        var filePath = Path.Combine(_basePath, channelId, "messages", $"{entry.Id}.json");
                        if (File.Exists(filePath))
                        {
                            try
                            {
                                var msgJson = File.ReadAllText(filePath);
                                var item = JsonSerializer.Deserialize<ChatMessage>(msgJson);
                                if (item != null)
                                {
                                    _cache[item.Id] = item;
                                    _cacheService.TrackAccess(item);
                                }
                            }
                            catch (JsonException ex)
                            {
                                _logger?.LogWarning(ex, "[ChatMessageRepo] Failed to deserialize {FilePath}", filePath);
                            }
                            catch (IOException ex)
                            {
                                _logger?.LogWarning(ex, "[ChatMessageRepo] IO error reading {FilePath}", filePath);
                            }
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

    private void EnsureIndexBuilt(string channelId) =>
        _indexService.EnsureIndexBuilt(channelId);

    public override List<ChatMessage> GetAll()
    {
        var results = new Dictionary<string, ChatMessage>();

        // 1. Seed with in-memory cache
        foreach (var kvp in _cache)
        {
            results[kvp.Key] = kvp.Value;
        }

        // 2. Ensure channel indexes on disk are built
        _indexService.EnsureAllChannelIndexesBuilt();

        // 3. Query index snapshot for all messages across all channels
        var snapshot = _indexService.GetChannelIndexesSnapshot();
        foreach (var (channelId, entries) in snapshot)
        {
            foreach (var entry in entries)
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
            if (!results.ContainsKey(item.Id))
            {
                results[item.Id] = item;
            }
        }

        // 5. Add any remaining items in _cache (in case any were added during disk scan)
        foreach (var kvp in _cache)
        {
            if (!results.ContainsKey(kvp.Key))
            {
                results[kvp.Key] = kvp.Value;
            }
        }

        return results.Values.ToList();
    }

    public List<ChatMessage> GetAllForSender(string senderId)
    {
        if (string.IsNullOrEmpty(senderId)) return [];

        var results = new Dictionary<string, ChatMessage>();

        // 1. Seed with in-memory cache for this sender
        foreach (var kvp in _cache)
        {
            if (kvp.Value.SenderId == senderId)
            {
                results[kvp.Key] = kvp.Value;
            }
        }

        // 2. Ensure channel indexes on disk are built
        _indexService.EnsureAllChannelIndexesBuilt();

        // 3. Query index snapshot for sender
        var snapshot = _indexService.GetChannelIndexesSnapshot();
        foreach (var (channelId, entries) in snapshot)
        {
            var senderEntries = entries.Where(e => e.SenderId == senderId).ToList();
            foreach (var entry in senderEntries)
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

        // 4. Scan orphaned messages on disk for this sender
        foreach (var item in LoadOrphanedMessagesFromDisk())
        {
            if (item.SenderId == senderId && !results.ContainsKey(item.Id))
            {
                results[item.Id] = item;
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
            ChatMessage? item = null;
            try
            {
                var json = File.ReadAllText(file);
                item = JsonSerializer.Deserialize<ChatMessage>(json);
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "[ChatMessageRepo] Failed to deserialize orphaned message {File}", file);
            }
            catch (IOException ex)
            {
                _logger?.LogWarning(ex, "[ChatMessageRepo] IO error reading orphaned message {File}", file);
            }

            if (item != null)
            {
                yield return item;
            }
        }
    }

    public List<ChatMessage> GetByChannel(string channelId, int? limit = null)
    {
        var results = new List<ChatMessage>();
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var query = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt);

            if (limit.HasValue)
            {
                var targetEntries = query.Take(limit.Value).ToList();
                bool addedToCache = false;
                foreach (var entry in targetEntries)
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
            }
            else
            {
                var targetEntries = query.ToList();
                foreach (var entry in targetEntries)
                {
                    if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                    {
                        results.Add(cachedMsg);
                    }
                }
            }
        }
        return results;
    }

    public async Task<List<ChatMessage>> GetByChannelAsync(string channelId, int? limit = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ChatMessage>();
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var query = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt);

            if (limit.HasValue)
            {
                var targetEntries = query.Take(limit.Value).ToList();
                bool addedToCache = false;
                foreach (var entry in targetEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                    {
                        results.Add(cachedMsg);
                    }
                    else
                    {
                        var msg = await GetMessageFromDiskAsync(channelId, entry.Id, cancellationToken);
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
            }
            else
            {
                var targetEntries = query.ToList();
                foreach (var entry in targetEntries)
                {
                    if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                    {
                        results.Add(cachedMsg);
                    }
                }
            }
        }
        return results;
    }

    public List<ChatMessage> GetCachedByChannel(string channelId, int? limit = null)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count == 0) return [];

        var query = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt);
        var targetEntries = limit.HasValue ? query.Take(limit.Value) : query;
        var results = new List<ChatMessage>();
        foreach (var entry in targetEntries)
        {
            if (_cache.TryGetValue(entry.Id, out var cachedMsg))
            {
                results.Add(cachedMsg);
            }
        }
        return results;
    }

    public List<ChatMessage> GetByChannelBefore(string channelId, DateTime before, int count = 50)
    {
        var results = new List<ChatMessage>();
        bool addedToCache = false;
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            var targetEntries = channelIndex
                .Where(e => !e.IsDeleted && e.SentAt < before)
                .OrderByDescending(e => e.SentAt)
                .Take(count)
                .ToList();

            foreach (var entry in targetEntries)
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
        }
        if (addedToCache)
        {
            NotifyCacheItemAdded();
        }
        return results;
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

    public List<ChatMessage> GetReplies(string parentMessageId)
    {
        var results = new List<ChatMessage>();
        var snapshot = _indexService.GetChannelIndexesSnapshot();
        foreach (var kvp in snapshot)
        {
            var channelId = kvp.Key;
            var replyIds = kvp.Value
                .Where(e => !e.IsDeleted && e.ReplyToId == parentMessageId)
                .Select(e => e.Id)
                .ToList();

            foreach (var id in replyIds)
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
        return results.OrderBy(m => m.SentAt).ToList();
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

    public int GetMessageCount(string channelId)
    {
        var channelIndex = _indexService.GetIndexForChannel(channelId);
        if (channelIndex.Count > 0)
        {
            return channelIndex.Count(e => !e.IsDeleted);
        }
        return _cache.Values.Count(m => m.ChannelId == channelId && !m.IsDeleted);
    }

    public int GetTotalMessageCount(string channelId)
    {
        EnsureIndexBuilt(channelId);
        return GetMessageCount(channelId);
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
}
