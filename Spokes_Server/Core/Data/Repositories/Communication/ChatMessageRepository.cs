using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Concurrent;
using System.Text.Json;
using System.IO;
using Microsoft.Extensions.Configuration;
using System.Linq;
using System.Collections.Generic;
using System;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;


namespace Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Repository for chat messages with lazy loading support.
/// On startup, only loads messages from the last 7 days.
/// Older messages are loaded on-demand when users scroll back or search.
/// </summary>
public class ChatMessageRepository : JsonRepository<ChatMessage>
{


    // In-memory cache for channel previews (latest per channel) used by sidebar
    // to avoid reading messages from disk or polluting the main message _cache.
    private readonly ConcurrentDictionary<string, ChannelPreviewSummary> _channelPreviews = new();
    private readonly ConcurrentDictionary<string, ChatMessage> _previewCache = new();

    // NEW: Master Index for all channels
    private readonly ConcurrentDictionary<string, List<ChatIndexEntry>> _channelIndexes = new();
    private readonly object _indexLock = new(); // For thread-safe index updates
    private readonly System.Threading.SemaphoreSlim _pruningSemaphore = new(1, 1);

    // How many messages per channel to load on startup
    private const int InitialLoadCount = 55;

    // Cache tracking
    private readonly ConcurrentDictionary<string, int> _messageSizes = new();
    private readonly ConcurrentDictionary<string, DateTime> _messageAccessTimes = new();
    private long _totalCacheSizeBytes = 0;
    private readonly CompanyProfileRepository _companyProfiles;

    public ChatMessageRepository(DiskPersistenceService writer, IConfiguration config, CompanyProfileRepository companyProfiles)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "chat"), "*.json")
    {
        _companyProfiles = companyProfiles;
    }

    private void TrackCacheAccess(ChatMessage item)
    {
        _messageAccessTimes[item.Id] = DateTime.UtcNow;
        int estimatedSize = (item.Content?.Length ?? 0) * 2 + 500;
        var addedSize = _messageSizes.AddOrUpdate(item.Id, estimatedSize, (k, oldSize) =>
        {
            Interlocked.Add(ref _totalCacheSizeBytes, -oldSize);
            return estimatedSize;
        });
        Interlocked.Add(ref _totalCacheSizeBytes, addedSize);
    }

    protected override string GetFilePath(ChatMessage item)
    {
        // Store messages in a 'messages' subfolder within the channel folder
        if (string.IsNullOrEmpty(item.ChannelId))
        {
            // Fallback to a 'orphaned' folder if ChannelId is missing, to avoid errors
            return Path.Combine(_basePath, "orphaned", $"{item.Id}.json");
        }

        var channelDir = Path.Combine(_basePath, item.ChannelId, "messages");
        return Path.Combine(channelDir, $"{item.Id}.json");
    }

    public override void Save(ChatMessage item)
    {
        // Robustness: Ensure the directory exists immediately (don't wait for background service)
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
            Console.WriteLine($"[ChatMessageRepo] IO Error during directory sync: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[ChatMessageRepo] Access Denied during directory sync: {ex.Message}");
        }

        UpdateIndex(item);
        base.Save(item);
        TrackCacheAccess(item);
        EnforceGlobalCacheSizeLimit();
    }

    public override async Task SaveAsync(ChatMessage item)
    {
        // Robustness: Ensure the directory exists immediately (don't wait for background service)
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
            Console.WriteLine($"[ChatMessageRepo] IO Error during directory sync: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[ChatMessageRepo] Access Denied during directory sync: {ex.Message}");
        }

        await UpdateIndexAsync(item);
        await base.SaveAsync(item);
        TrackCacheAccess(item);
        EnforceGlobalCacheSizeLimit();
    }

    public override void Delete(string id)
    {
        if (_cache.TryGetValue(id, out var item))
        {
            RemoveFromIndex(item);
        }
        base.Delete(id);
        if (_messageSizes.TryRemove(id, out var size))
        {
            Interlocked.Add(ref _totalCacheSizeBytes, -size);
        }
        _messageAccessTimes.TryRemove(id, out _);
    }

    public override async Task DeleteAsync(string id)
    {
        if (_cache.TryGetValue(id, out var item))
        {
            await RemoveFromIndexAsync(item);
        }
        await base.DeleteAsync(id);
        if (_messageSizes.TryRemove(id, out var size))
        {
            Interlocked.Add(ref _totalCacheSizeBytes, -size);
        }
        _messageAccessTimes.TryRemove(id, out _);
    }

    private void SaveMasterIndex()
    {
        var indexPath = Path.Combine(_basePath, "index.json");
        var serializableIndex = _channelIndexes.ToDictionary(k => k.Key, v => v.Value);
        _writer.QueueWrite(indexPath, serializableIndex);
    }

    private async Task SaveMasterIndexAsync()
    {
        var indexPath = Path.Combine(_basePath, "index.json");
        var serializableIndex = _channelIndexes.ToDictionary(k => k.Key, v => v.Value);
        await _writer.QueueWriteAsync(indexPath, serializableIndex);
    }

    private void UpdateIndex(ChatMessage item)
    {
        lock (_indexLock)
        {
            var channelIndex = _channelIndexes.GetOrAdd(item.ChannelId, _ => new List<ChatIndexEntry>());
            var existing = channelIndex.FirstOrDefault(x => x.Id == item.Id);
            var previewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30);
            if (existing != null)
            {
                existing.SentAt = item.SentAt;
                existing.IsDeleted = item.IsDeleted;
                existing.IsEncrypted = item.IsEncrypted;
                existing.SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant();
                existing.ReplyToId = item.ReplyToId;
                existing.PreviewText = previewText;
            }
            else
            {
                channelIndex.Add(new ChatIndexEntry
                {
                    Id = item.Id,
                    ChannelId = item.ChannelId,
                    SenderId = item.SenderId,
                    SentAt = item.SentAt,
                    IsDeleted = item.IsDeleted,
                    IsEncrypted = item.IsEncrypted,
                    SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant(),
                    ReplyToId = item.ReplyToId,
                    PreviewText = previewText
                });
            }
            RefreshChannelPreview(item.ChannelId);
            SaveMasterIndex();
        }
    }

    private async Task UpdateIndexAsync(ChatMessage item)
    {
        lock (_indexLock)
        {
            var channelIndex = _channelIndexes.GetOrAdd(item.ChannelId, _ => new List<ChatIndexEntry>());
            var existing = channelIndex.FirstOrDefault(x => x.Id == item.Id);
            var previewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30);
            if (existing != null)
            {
                existing.SentAt = item.SentAt;
                existing.IsDeleted = item.IsDeleted;
                existing.IsEncrypted = item.IsEncrypted;
                existing.SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant();
                existing.ReplyToId = item.ReplyToId;
                existing.PreviewText = previewText;
            }
            else
            {
                channelIndex.Add(new ChatIndexEntry
                {
                    Id = item.Id,
                    ChannelId = item.ChannelId,
                    SenderId = item.SenderId,
                    SentAt = item.SentAt,
                    IsDeleted = item.IsDeleted,
                    IsEncrypted = item.IsEncrypted,
                    SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant(),
                    ReplyToId = item.ReplyToId,
                    PreviewText = previewText
                });
            }
            RefreshChannelPreview(item.ChannelId);
        }
        await SaveMasterIndexAsync();
    }

    private void RemoveFromIndex(ChatMessage item)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(item.ChannelId, out var channelIndex))
            {
                channelIndex.RemoveAll(x => x.Id == item.Id);
                RefreshChannelPreview(item.ChannelId);
                SaveMasterIndex();
            }
        }
    }

    private async Task RemoveFromIndexAsync(ChatMessage item)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(item.ChannelId, out var channelIndex))
            {
                channelIndex.RemoveAll(x => x.Id == item.Id);
                RefreshChannelPreview(item.ChannelId);
            }
        }
        await SaveMasterIndexAsync();
    }

    public void RefreshChannelPreview(string channelId)
    {
        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
        {
            var latestEntry = channelIndex
                .Where(e => !e.IsDeleted)
                .OrderByDescending(e => e.SentAt)
                .FirstOrDefault();

            if (latestEntry != null)
            {
                _cache.TryGetValue(latestEntry.Id, out var cachedMsg);

                _channelPreviews[channelId] = new ChannelPreviewSummary
                {
                    MessageId = latestEntry.Id,
                    ChannelId = channelId,
                    SenderId = latestEntry.SenderId,
                    SentAt = latestEntry.SentAt,
                    PreviewText = latestEntry.PreviewText ?? (latestEntry.IsEncrypted ? null : latestEntry.SearchText),
                    IsEncrypted = latestEntry.IsEncrypted,
                    EncryptedContent = latestEntry.IsEncrypted ? cachedMsg?.Content : null,
                    Attachments = cachedMsg?.Attachments
                };
            }
            else
            {
                _channelPreviews.TryRemove(channelId, out _);
            }
        }
        else
        {
            _channelPreviews.TryRemove(channelId, out _);
        }
    }

    /// <summary>
    /// Returns the in-memory preview summary for a channel without loading messages from disk.
    /// If blockedUserIds is provided and the latest message was sent by a blocked user, finds the latest non-blocked message from the in-memory index.
    /// </summary>
    public ChannelPreviewSummary? GetChannelPreviewSummary(string channelId, IReadOnlyList<string>? blockedUserIds = null)
    {
        if (_channelPreviews.TryGetValue(channelId, out var preview))
        {
            if (blockedUserIds == null || !blockedUserIds.Contains(preview.SenderId))
            {
                return preview;
            }
        }

        // Fallback when latest message is from a blocked user: query in-memory index
        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
        {
            var entry = channelIndex
                .Where(e => !e.IsDeleted && (blockedUserIds == null || !blockedUserIds.Contains(e.SenderId)))
                .OrderByDescending(e => e.SentAt)
                .FirstOrDefault();

            if (entry != null)
            {
                _cache.TryGetValue(entry.Id, out var cachedMsg);
                return new ChannelPreviewSummary
                {
                    MessageId = entry.Id,
                    ChannelId = channelId,
                    SenderId = entry.SenderId,
                    SentAt = entry.SentAt,
                    PreviewText = entry.PreviewText ?? (entry.IsEncrypted ? null : entry.SearchText),
                    IsEncrypted = entry.IsEncrypted,
                    EncryptedContent = entry.IsEncrypted ? cachedMsg?.Content : null,
                    Attachments = cachedMsg?.Attachments
                };
            }
        }

        return null;
    }

    public List<ChatIndexEntry> GetIndexForChannel(string channelId)
    {
        return _channelIndexes.TryGetValue(channelId, out var idx) ? idx : new List<ChatIndexEntry>();
    }

    /// <summary>
    /// Reads a specific message directly from disk, bypassing the cache.
    /// Used for streaming decryption searches to avoid OOM.
    /// </summary>
    public ChatMessage? GetMessageFromDisk(string channelId, string messageId)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        var filePath = Path.Combine(_basePath, channelId, "messages", $"{messageId}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = File.ReadAllText(filePath);
            var msg = JsonSerializer.Deserialize<ChatMessage>(json);
            if (msg != null)
            {
                TrackCacheAccess(msg);
            }
            return msg;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ChatMessage?> GetMessageFromDiskAsync(string channelId, string messageId)
    {
        if (_cache.TryGetValue(messageId, out var cached)) return cached;

        var filePath = Path.Combine(_basePath, channelId, "messages", $"{messageId}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var msg = JsonSerializer.Deserialize<ChatMessage>(json);
            if (msg != null)
            {
                TrackCacheAccess(msg);
            }
            return msg;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Initial load - uses master index.json if available, otherwise generates it.
    /// Only loads full message bodies for the last 7 days.
    /// </summary>
    public override void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        var indexPath = Path.Combine(_basePath, "index.json");
        bool indexLoaded = false;

        if (File.Exists(indexPath))
        {
            try
            {
                var json = File.ReadAllText(indexPath);
                var loadedIndex = JsonSerializer.Deserialize<Dictionary<string, List<ChatIndexEntry>>>(json);
                if (loadedIndex != null)
                {
                    foreach (var kvp in loadedIndex)
                    {
                        _channelIndexes[kvp.Key] = kvp.Value;
                    }
                    indexLoaded = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChatMessageRepo] Failed to read master index. Falling back. {ex.Message}");
            }
        }

        if (indexLoaded)
        {
            // FAST PATH: Use index to load recent messages
            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var kvp in _channelIndexes)
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
                                TrackCacheAccess(item);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ChatMessageRepo] Failed to deserialize {filePath}: {ex.Message}");
                        }
                    }
                }

                // Immediately initialize in-memory channel preview from index
                RefreshChannelPreview(channelId);
            }

            // Asynchronously backfill PreviewText for legacy unmigrated indexes in background
            TriggerBackgroundPreviewBackfill();
            return;
        }

        // SLOW PATH: Fallback to reading all files and generating the index
        var channelDirs = Directory.GetDirectories(_basePath);
        foreach (var channelDir in channelDirs)
        {
            var channelId = Path.GetFileName(channelDir);
            var messagesDir = Path.Combine(channelDir, "messages");
            if (!Directory.Exists(messagesDir)) continue;

            var channelIndexList = new List<ChatIndexEntry>();
            var deserializedMessages = new Dictionary<string, ChatMessage>();
            var files = Directory.GetFiles(messagesDir, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<ChatMessage>(json);
                    if (item != null)
                    {
                        channelIndexList.Add(new ChatIndexEntry
                        {
                            Id = item.Id,
                            ChannelId = item.ChannelId,
                            SenderId = item.SenderId,
                            SentAt = item.SentAt,
                            IsDeleted = item.IsDeleted,
                            IsEncrypted = item.IsEncrypted,
                            SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant(),
                            ReplyToId = item.ReplyToId,
                            PreviewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30)
                        });

                        deserializedMessages[item.Id] = item;
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[ChatMessageRepo] Failed to deserialize {file}: {ex.Message}");
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[ChatMessageRepo] Failed to read {file}: {ex.Message}");
                }
            }

            // Only load full bodies for the most recent InitialLoadCount messages within 7 days
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var targetEntries = channelIndexList
                .Where(e => !e.IsDeleted && e.SentAt > cutoff)
                .OrderByDescending(e => e.SentAt)
                .Take(InitialLoadCount)
                .ToList();

            foreach (var entry in targetEntries)
            {
                if (deserializedMessages.TryGetValue(entry.Id, out var item))
                {
                    _cache[item.Id] = item;
                    TrackCacheAccess(item);
                }
            }

            _channelIndexes[channelId] = channelIndexList;
            RefreshChannelPreview(channelId);
        }

        SaveMasterIndex();
    }

    private void TriggerBackgroundPreviewBackfill()
    {
        _ = Task.Run(() =>
        {
            try
            {
                bool anyUpdated = false;
                foreach (var kvp in _channelIndexes)
                {
                    var channelId = kvp.Key;
                    var channelIndex = kvp.Value;
                    var latest = channelIndex
                        .Where(e => !e.IsDeleted && !e.IsEncrypted && e.PreviewText == null)
                        .OrderByDescending(e => e.SentAt)
                        .FirstOrDefault();

                    if (latest != null)
                    {
                        var msg = GetMessageFromDisk(channelId, latest.Id);
                        if (msg != null)
                        {
                            latest.PreviewText = ChatPreviewFormatter.FormatAndTruncatePreview(msg.Content, msg.Attachments, 30);
                            anyUpdated = true;
                            RefreshChannelPreview(channelId);
                        }
                    }
                }
                if (anyUpdated)
                {
                    SaveMasterIndex();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ChatMessageRepo] Preview backfill error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Ensures that the master index is populated for the given channel.
    /// </summary>
    private void EnsureIndexBuilt(string channelId)
    {
        if (!_channelIndexes.ContainsKey(channelId))
        {
            lock (_indexLock)
            {
                if (!_channelIndexes.ContainsKey(channelId))
                {
                    // Scan disk for messages in this channel to build the index
                    var channelDir = Path.Combine(_basePath, channelId);
                    var messagesDir = Path.Combine(channelDir, "messages");
                    if (Directory.Exists(messagesDir))
                    {
                        var channelIndexList = new List<ChatIndexEntry>();
                        var files = Directory.GetFiles(messagesDir, "*.json");
                        foreach (var file in files)
                        {
                            try
                            {
                                var json = File.ReadAllText(file);
                                var item = JsonSerializer.Deserialize<ChatMessage>(json);
                                if (item != null)
                                {
                                    channelIndexList.Add(new ChatIndexEntry
                                    {
                                        Id = item.Id,
                                        ChannelId = item.ChannelId,
                                        SenderId = item.SenderId,
                                        SentAt = item.SentAt,
                                        IsDeleted = item.IsDeleted,
                                        IsEncrypted = item.IsEncrypted,
                                        SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant(),
                                        ReplyToId = item.ReplyToId,
                                        PreviewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30)
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ChatMessageRepo] Failed to deserialize message {file}: {ex.Message}");
                            }
                        }
                        _channelIndexes[channelId] = channelIndexList;
                        RefreshChannelPreview(channelId);
                        SaveMasterIndex();
                    }
                }
            }
        }
    }



    /// <summary>
    /// Get messages for a specific channel, ordered by sent time descending.
    /// Uses the index to dynamically load any missing messages from disk.
    /// </summary>
    public List<ChatMessage> GetByChannel(string channelId, int? limit = null)
    {
        var results = new List<ChatMessage>();
        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
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
                    _ = Task.Run(() => EnforceGlobalCacheSizeLimit());
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

    /// <summary>
    /// Get messages for a channel before a specific time (for pagination/infinite scroll).
    /// Uses the index to dynamically load any missing messages from disk.
    /// </summary>
    public List<ChatMessage> GetByChannelBefore(string channelId, DateTime before, int count = 50)
    {
        var results = new List<ChatMessage>();
        bool addedToCache = false;
        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
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
            _ = Task.Run(() => EnforceGlobalCacheSizeLimit());
        }
        return results;
    }

    /// <summary>
    /// Get the most recent message in a channel (for channel previews).
    /// Uses the index to find the latest message without scanning disk.
    /// </summary>
    public ChatMessage? GetLatestInChannel(string channelId)
    {
        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
        {
            var latestEntry = channelIndex.Where(e => !e.IsDeleted).OrderByDescending(e => e.SentAt).FirstOrDefault();
            if (latestEntry != null)
            {
                if (_cache.TryGetValue(latestEntry.Id, out var cachedMsg)) return cachedMsg;
                if (_previewCache.TryGetValue(channelId, out var previewMsg) && previewMsg.Id == latestEntry.Id) return previewMsg;

                var msg = GetMessageFromDisk(channelId, latestEntry.Id);
                if (msg != null)
                {
                    _previewCache[channelId] = msg;
                    return msg;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Get thread replies for a parent message.
    /// </summary>
    public List<ChatMessage> GetReplies(string parentMessageId)
    {
        var results = new List<ChatMessage>();
        foreach (var kvp in _channelIndexes)
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

    /// <summary>
    /// Finds the index position of a message (newest is 0).
    /// </summary>
    public int GetIndexPosition(string channelId, string messageId)
    {
        if (_channelIndexes.TryGetValue(channelId, out var index))
        {
            var ordered = index.OrderByDescending(e => e.SentAt).ToList();
            var pos = ordered.FindIndex(e => e.Id == messageId);
            return pos >= 0 ? pos : 0;
        }
        return 0;
    }

    /// <summary>
    /// Searches messages in a channel.
    /// </summary>
    public List<ChatMessage> Search(string channelId, string query)
    {
        EnsureIndexBuilt(channelId);

        var lowerQuery = query.ToLowerInvariant();
        var results = new List<ChatMessage>();

        if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
        {
            var matchingIds = channelIndex
                .Where(e => !e.IsDeleted && !e.IsEncrypted && e.SearchText != null && e.SearchText.Contains(lowerQuery))
                .OrderByDescending(e => e.SentAt)
                .Take(100)
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
                    var filePath = Path.Combine(_basePath, channelId, "messages", $"{id}.json");
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            var json = File.ReadAllText(filePath);
                            var msg = JsonSerializer.Deserialize<ChatMessage>(json);
                            if (msg != null) results.Add(msg);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ChatMessageRepo] Search failed to read/parse message file {filePath}: {ex.Message}");
                        }
                    }
                }
            }
        }
        return results.OrderByDescending(m => m.SentAt).ToList();
    }

    /// <summary>
    /// Get message count for a channel (for statistics).
    /// </summary>
    public int GetMessageCount(string channelId)
    {
        return _channelIndexes.TryGetValue(channelId, out var idx) ? idx.Count(e => !e.IsDeleted) : 0;
    }

    /// <summary>
    /// Get accurate message count.
    /// </summary>
    public int GetTotalMessageCount(string channelId)
    {
        EnsureIndexBuilt(channelId);
        return GetMessageCount(channelId);
    }

    /// <summary>
    /// Get the count of unread messages in a channel since a given timestamp.
    /// Used for persistent unread notification badges.
    /// </summary>
    /// <param name="channelId">The channel to count messages in</param>
    /// <param name="since">Count messages sent after this timestamp</param>
    /// <param name="excludeUserId">Optional: exclude messages from this user (typically the current user)</param>
    public int GetUnreadCountSince(string channelId, DateTime since, string? excludeUserId = null)
    {
        if (_channelIndexes.TryGetValue(channelId, out var idx))
        {
            var count = idx.Count(e => !e.IsDeleted && e.SentAt > since && (excludeUserId == null || e.SenderId != excludeUserId));
            if (count > 0) return count;
        }

        // Fallback: check in-memory cache (zero disk I/O)
        return _cache.Values.Count(m =>
            m.ChannelId == channelId &&
            !m.IsDeleted &&
            m.SentAt > since &&
            (excludeUserId == null || m.SenderId != excludeUserId));
    }

    /// <summary>
    /// Get the actual unread messages in a channel since a given timestamp.
    /// Used for persistent unread notification evaluations.
    /// </summary>
    public List<ChatMessage> GetUnreadMessagesSince(string channelId, DateTime since, string? excludeUserId = null)
    {
        var results = new List<ChatMessage>();
        var loadedIds = new HashSet<string>();
        bool addedToCache = false;

        // 1. Gather unread messages from the index
        if (_channelIndexes.TryGetValue(channelId, out var idx))
        {
            var targetEntries = idx
                .Where(e => !e.IsDeleted && e.SentAt > since && (excludeUserId == null || e.SenderId != excludeUserId))
                .ToList();

            foreach (var entry in targetEntries)
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
                        results.Add(msg);
                        loadedIds.Add(msg.Id);
                        addedToCache = true;
                    }
                }
            }
        }

        // 2. Fallback/Union: Include matching messages present in the in-memory cache
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
            _ = Task.Run(() => EnforceGlobalCacheSizeLimit());
        }

        return results;
    }

    /// <summary>
    /// Get the actual unread messages in a channel since a given timestamp asynchronously.
    /// Used for persistent unread notification evaluations.
    /// </summary>
    public async Task<List<ChatMessage>> GetUnreadMessagesSinceAsync(string channelId, DateTime since, string? excludeUserId = null)
    {
        var results = new List<ChatMessage>();
        var loadedIds = new HashSet<string>();
        bool addedToCache = false;

        // 1. Gather unread messages from the index
        if (_channelIndexes.TryGetValue(channelId, out var idx))
        {
            var targetEntries = idx
                .Where(e => !e.IsDeleted && e.SentAt > since && (excludeUserId == null || e.SenderId != excludeUserId))
                .ToList();

            foreach (var entry in targetEntries)
            {
                if (_cache.TryGetValue(entry.Id, out var cachedMsg))
                {
                    results.Add(cachedMsg);
                    loadedIds.Add(cachedMsg.Id);
                }
                else
                {
                    var msg = await GetMessageFromDiskAsync(channelId, entry.Id);
                    if (msg != null)
                    {
                        _cache[msg.Id] = msg;
                        results.Add(msg);
                        loadedIds.Add(msg.Id);
                        addedToCache = true;
                    }
                }
            }
        }

        // 2. Fallback/Union: Include matching messages present in the in-memory cache
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
            _ = Task.Run(() => EnforceGlobalCacheSizeLimit());
        }

        return results;
    }
    /// <summary>
    /// Prune the global cache to limit memory usage based on MB size.
    /// Keeps the cache size under the limit defined in CompanyProfile.
    /// </summary>
    public void EnforceGlobalCacheSizeLimit()
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

                if (_cache.TryRemove(id, out _))
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


