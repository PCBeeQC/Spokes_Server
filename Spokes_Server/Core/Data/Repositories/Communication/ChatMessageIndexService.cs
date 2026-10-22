using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class ChatMessageIndexService
{
    private readonly DiskPersistenceService _writer;
    private readonly string _basePath;
    private readonly Func<string, ChatMessage?>? _messageLookup;
    private readonly ILogger<ChatMessageIndexService>? _logger;

    private readonly ConcurrentDictionary<string, List<ChatIndexEntry>> _channelIndexes = new();
    private readonly ConcurrentDictionary<string, ChannelPreviewSummary> _channelPreviews = new();
    private readonly ConcurrentDictionary<string, ChatMessage> _previewCache = new();
    private readonly object _indexLock = new();

    public ChatMessageIndexService(
        DiskPersistenceService writer,
        string basePath,
        IChatMessageLookup? messageLookup = null,
        ILogger<ChatMessageIndexService>? logger = null)
        : this(writer, basePath, messageLookup != null ? messageLookup.GetCachedMessage : (Func<string, ChatMessage?>?)null, logger)
    {
    }

    public ChatMessageIndexService(
        DiskPersistenceService writer,
        string basePath,
        Func<string, ChatMessage?>? messageLookup,
        ILogger<ChatMessageIndexService>? logger = null)
    {
        _writer = writer;
        _basePath = basePath;
        _messageLookup = messageLookup;
        _logger = logger;
    }

    public void Clear()
    {
        lock (_indexLock)
        {
            _channelIndexes.Clear();
            _channelPreviews.Clear();
            _previewCache.Clear();
        }
    }

    public IReadOnlyCollection<string> AllChannelIds => _channelIndexes.Keys.ToList();

    public Dictionary<string, List<ChatIndexEntry>> GetChannelIndexesSnapshot()
    {
        lock (_indexLock)
        {
            return _channelIndexes.ToDictionary(k => k.Key, v => v.Value.ToList());
        }
    }

    public (string? ChannelId, ChatIndexEntry? Entry) FindIndexEntry(string messageId)
    {
        lock (_indexLock)
        {
            foreach (var kvp in _channelIndexes)
            {
                var match = kvp.Value.FirstOrDefault(e => e.Id == messageId);
                if (match != null)
                {
                    return (kvp.Key, match);
                }
            }
            return (null, null);
        }
    }

    public List<ChatIndexEntry> GetIndexForChannel(string channelId)
    {
        lock (_indexLock)
        {
            return _channelIndexes.TryGetValue(channelId, out var idx) ? idx.ToList() : [];
        }
    }

    public bool HasMessagesBefore(string channelId, DateTime before)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
            {
                return channelIndex.Any(e => !e.IsDeleted && e.SentAt < before);
            }
            return false;
        }
    }

    public int GetTotalChannelMessageCount(string channelId)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
            {
                return channelIndex.Count(e => !e.IsDeleted);
            }
            return 0;
        }
    }

    public DateTime? GetLatestMessageTimestamp(string channelId)
    {
        if (_channelPreviews.TryGetValue(channelId, out var preview))
        {
            return preview.SentAt;
        }

        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
            {
                var latest = channelIndex
                    .Where(e => !e.IsDeleted)
                    .OrderByDescending(e => e.SentAt)
                    .FirstOrDefault();
                return latest?.SentAt;
            }
        }

        return null;
    }

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
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
            {
                var entry = channelIndex
                    .Where(e => !e.IsDeleted && (blockedUserIds == null || !blockedUserIds.Contains(e.SenderId)))
                    .OrderByDescending(e => e.SentAt)
                    .FirstOrDefault();

                if (entry != null)
                {
                    var cachedMsg = _messageLookup?.Invoke(entry.Id);
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
        }

        return null;
    }

    public void RefreshChannelPreview(string channelId, ChatMessage? cachedMsg = null)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
            {
                var latestEntry = channelIndex
                    .Where(e => !e.IsDeleted)
                    .OrderByDescending(e => e.SentAt)
                    .FirstOrDefault();

                if (latestEntry != null)
                {
                    if (cachedMsg == null && _messageLookup != null)
                    {
                        cachedMsg = _messageLookup(latestEntry.Id);
                    }

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
    }

    private void ApplyIndexUpdate(ChatMessage item, ChatMessage? cachedMsg = null)
    {
        var channelIndex = _channelIndexes.GetOrAdd(item.ChannelId, _ => new List<ChatIndexEntry>());
        var existing = channelIndex.FirstOrDefault(x => x.Id == item.Id);
        var previewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30);
        var albumIds = item.MentionedAlbumIds != null && item.MentionedAlbumIds.Count > 0 ? item.MentionedAlbumIds.ToList() : new List<string>();
        if (existing != null)
        {
            existing.SentAt = item.SentAt;
            existing.IsDeleted = item.IsDeleted;
            existing.IsEncrypted = item.IsEncrypted;
            existing.SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant();
            existing.ReplyToId = item.ReplyToId;
            existing.PreviewText = previewText;
            existing.MentionedAlbumIds = albumIds;
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
                PreviewText = previewText,
                MentionedAlbumIds = albumIds
            });
        }
        RefreshChannelPreview(item.ChannelId, cachedMsg);
    }

    public void UpdateIndex(ChatMessage item, ChatMessage? cachedMsg = null)
    {
        lock (_indexLock)
        {
            ApplyIndexUpdate(item, cachedMsg);
            SaveMasterIndex();
        }
    }

    public void RemoveFromIndex(ChatMessage item)
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

    public void SaveMasterIndex()
    {
        var indexPath = Path.Combine(_basePath, "index.json");
        Dictionary<string, List<ChatIndexEntry>> serializableIndex;
        lock (_indexLock)
        {
            serializableIndex = _channelIndexes.ToDictionary(k => k.Key, v => v.Value.ToList());
        }
        _writer.QueueWrite(indexPath, serializableIndex);
    }

    /// <summary>
    /// Ensures that an index exists for the specified channel by scanning its messages directory on disk
    /// if the channel is not yet indexed in memory.
    /// </summary>
    public void EnsureIndexBuilt(string channelId)
    {
        if (_channelIndexes.ContainsKey(channelId)) return;

        lock (_indexLock)
        {
            if (_channelIndexes.ContainsKey(channelId)) return;

            _channelIndexes[channelId] = LoadIndexEntriesFromDisk(channelId);
            RefreshChannelPreview(channelId);
            SaveMasterIndex();
        }
    }

    private List<ChatIndexEntry> LoadIndexEntriesFromDisk(string channelId)
    {
        var channelDir = Path.Combine(_basePath, channelId);
        var messagesDir = Path.Combine(channelDir, "messages");
        var channelIndexList = new List<ChatIndexEntry>();

        if (!Directory.Exists(messagesDir))
        {
            return channelIndexList;
        }

        var files = Directory.GetFiles(messagesDir, "*.json");
        foreach (var file in files)
        {
            var item = TryReadMessageFromFile(file);
            if (item != null)
            {
                channelIndexList.Add(CreateChatIndexEntry(item));
            }
        }

        return channelIndexList;
    }

    private ChatMessage? TryReadMessageFromFile(string file)
    {
        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<ChatMessage>(json);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "[ChatMessageIndexService] Failed to deserialize message {File}", file);
            return null;
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "[ChatMessageIndexService] IO error reading message {File}", file);
            return null;
        }
    }

    /// <summary>
    /// Ensures indexes for all channel directories on disk (excluding orphaned) are built in memory.
    /// </summary>
    public void EnsureAllChannelIndexesBuilt()
    {
        if (!Directory.Exists(_basePath)) return;

        var channelDirs = Directory.GetDirectories(_basePath);
        foreach (var dir in channelDirs)
        {
            var channelId = Path.GetFileName(dir);
            if (channelId != "orphaned")
            {
                EnsureIndexBuilt(channelId);
            }
        }
    }

    /// <summary>
    /// Scans all channel folders from disk, parses message JSON, formats previews,
    /// reconstructs all channel index entries, refreshes channel previews, persists index.json,
    /// and returns the most recent messages (within 7 days, up to initialLoadCount per channel)
    /// for cache seeding.
    /// </summary>
    public List<ChatMessage> RebuildMasterIndexFromDisk(int initialLoadCount = 55)
    {
        if (!Directory.Exists(_basePath)) return [];

        var recentMessages = new List<ChatMessage>();
        var channelDirs = Directory.GetDirectories(_basePath);

        foreach (var channelDir in channelDirs)
        {
            var channelId = Path.GetFileName(channelDir);
            if (channelId == "orphaned") continue;

            var messagesDir = Path.Combine(channelDir, "messages");
            if (!Directory.Exists(messagesDir)) continue;

            var channelIndexList = new List<ChatIndexEntry>();
            var deserializedMessages = new Dictionary<string, ChatMessage>();
            var files = Directory.GetFiles(messagesDir, "*.json");

            foreach (var file in files)
            {
                var item = TryReadMessageFromFile(file);
                if (item != null)
                {
                    channelIndexList.Add(CreateChatIndexEntry(item));
                    deserializedMessages[item.Id] = item;
                }
            }

            // Only return full bodies for the most recent initialLoadCount messages within 7 days
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var targetEntries = channelIndexList
                .Where(e => !e.IsDeleted && e.SentAt > cutoff)
                .OrderByDescending(e => e.SentAt)
                .Take(initialLoadCount)
                .ToList();

            foreach (var entry in targetEntries)
            {
                if (deserializedMessages.TryGetValue(entry.Id, out var item))
                {
                    recentMessages.Add(item);
                }
            }

            SetChannelIndex(channelId, channelIndexList);
            RefreshChannelPreview(channelId);
        }

        SaveMasterIndex();
        return recentMessages;
    }

    private static ChatIndexEntry CreateChatIndexEntry(ChatMessage item)
    {
        return new ChatIndexEntry
        {
            Id = item.Id,
            ChannelId = item.ChannelId,
            SenderId = item.SenderId,
            SentAt = item.SentAt,
            IsDeleted = item.IsDeleted,
            IsEncrypted = item.IsEncrypted,
            SearchText = item.IsEncrypted ? null : item.Content?.ToLowerInvariant(),
            ReplyToId = item.ReplyToId,
            PreviewText = item.IsEncrypted ? null : ChatPreviewFormatter.FormatAndTruncatePreview(item.Content, item.Attachments, 30),
            MentionedAlbumIds = item.MentionedAlbumIds != null && item.MentionedAlbumIds.Count > 0 ? item.MentionedAlbumIds.ToList() : []
        };
    }

    public void EnsureIndexBuilt(string channelId, Func<string, List<ChatMessage>> channelMessagesLoader)
    {
        if (_channelIndexes.ContainsKey(channelId)) return;

        lock (_indexLock)
        {
            if (_channelIndexes.ContainsKey(channelId)) return;

            var messages = channelMessagesLoader(channelId);
            var channelIndexList = new List<ChatIndexEntry>();
            foreach (var item in messages)
            {
                channelIndexList.Add(CreateChatIndexEntry(item));
            }
            _channelIndexes[channelId] = channelIndexList;
            RefreshChannelPreview(channelId);
            SaveMasterIndex();
        }
    }

    public bool TryLoadMasterIndex(out Dictionary<string, List<ChatIndexEntry>>? loadedIndex)
    {
        loadedIndex = null;
        var indexPath = Path.Combine(_basePath, "index.json");
        if (!File.Exists(indexPath)) return false;

        try
        {
            var json = File.ReadAllText(indexPath);
            loadedIndex = JsonSerializer.Deserialize<Dictionary<string, List<ChatIndexEntry>>>(json);
            if (loadedIndex != null)
            {
                lock (_indexLock)
                {
                    foreach (var kvp in loadedIndex)
                    {
                        _channelIndexes[kvp.Key] = kvp.Value.ToList();
                    }
                }
                return true;
            }
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "[ChatMessageIndexService] Failed to deserialize master index: {Message}", ex.Message);
        }
        catch (IOException ex)
        {
            _logger?.LogError(ex, "[ChatMessageIndexService] Failed to read master index: {Message}", ex.Message);
        }
        return false;
    }

    public void SetChannelIndex(string channelId, List<ChatIndexEntry> entries)
    {
        lock (_indexLock)
        {
            _channelIndexes[channelId] = entries.ToList();
        }
    }

    public void TriggerBackgroundPreviewBackfill(Func<string, string, ChatMessage?> diskMessageLoader)
    {
        _ = Task.Run(() =>
        {
            try
            {
                List<(string ChannelId, string EntryId)> candidates;
                lock (_indexLock)
                {
                    candidates = _channelIndexes
                        .SelectMany(kvp => kvp.Value
                            .Where(e => !e.IsDeleted && !e.IsEncrypted && e.PreviewText == null)
                            .OrderByDescending(e => e.SentAt)
                            .Take(1)
                            .Select(e => (kvp.Key, e.Id)))
                        .ToList();
                }

                bool anyUpdated = false;
                foreach (var (channelId, entryId) in candidates)
                {
                    var msg = diskMessageLoader(channelId, entryId);
                    if (msg != null)
                    {
                        var preview = ChatPreviewFormatter.FormatAndTruncatePreview(msg.Content, msg.Attachments, 30);
                        lock (_indexLock)
                        {
                            if (_channelIndexes.TryGetValue(channelId, out var channelIndex))
                            {
                                var entry = channelIndex.FirstOrDefault(e => e.Id == entryId);
                                if (entry != null)
                                {
                                    entry.PreviewText = preview;
                                    anyUpdated = true;
                                }
                            }
                            RefreshChannelPreview(channelId, msg);
                        }
                    }
                }
                if (anyUpdated)
                {
                    SaveMasterIndex();
                }
            }
            catch (OperationCanceledException)
            {
                // Permit cancellation without error logging
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[ChatMessageIndexService] Preview backfill error: {Message}", ex.Message);
            }
        });
    }

    public ChatMessage? GetCachedPreview(string channelId)
    {
        return _previewCache.TryGetValue(channelId, out var msg) ? msg : null;
    }

    public void SetCachedPreview(string channelId, ChatMessage message)
    {
        _previewCache[channelId] = message;
    }

    public int GetIndexPosition(string channelId, string messageId)
    {
        lock (_indexLock)
        {
            if (_channelIndexes.TryGetValue(channelId, out var index))
            {
                var ordered = index.OrderByDescending(e => e.SentAt).ToList();
                var pos = ordered.FindIndex(e => e.Id == messageId);
                return pos >= 0 ? pos : 0;
            }
            return 0;
        }
    }
}
