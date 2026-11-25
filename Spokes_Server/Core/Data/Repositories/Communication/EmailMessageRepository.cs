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
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Repository for managing email messages partitioned by EmailAccount ID
/// (Data/EmailAccounts/{AccountId}/Messages/{MessageId}.json and Data/EmailAccounts/{AccountId}/index.json).
/// </summary>
public class EmailMessageRepository
{
    private readonly DiskPersistenceService _writer;
    private readonly string _basePath;
    private readonly ILogger<EmailMessageRepository>? _logger;
    private readonly ConcurrentDictionary<string, EmailMessage> _cache = new();

    // The "Lightweight Index" - Maps "{AccountId}:{FolderPath}" -> EmailFolderIndex (UID -> MessageMetadata)
    // Allows O(1) checks for existence, flags, and receiving address routing without loading full message JSONs from disk.
    private readonly ConcurrentDictionary<string, EmailFolderIndex> _indices = new();

    public event Action<EmailMessage>? OnSaved;

    public EmailMessageRepository(DiskPersistenceService writer, IConfiguration config)
        : this(writer, config, null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EmailMessageRepository(DiskPersistenceService writer, IConfiguration config, ILogger<EmailMessageRepository>? logger)
    {
        _writer = writer;
        _basePath = Path.Combine(config["DataPath"] ?? "Data", "EmailAccounts");
        _logger = logger;
    }

    private string GetFilePath(EmailMessage item) =>
        Path.Combine(_basePath, item.AccountId, "Messages", $"{item.Id}.json");

    public virtual void LoadFromDisk()
    {
        _cache.Clear();
        _indices.Clear();

        if (!Directory.Exists(_basePath)) return;

        var accountDirs = Directory.GetDirectories(_basePath);
        foreach (var accountDir in accountDirs)
        {
            var accountId = Path.GetFileName(accountDir);
            var msgPath = Path.Combine(accountDir, "Messages");
            if (!Directory.Exists(msgPath)) continue;

            var indexPath = Path.Combine(accountDir, "index.json");

            if (File.Exists(indexPath))
            {
                try
                {
                    var indexJson = File.ReadAllText(indexPath);
                    var loadedIndex = JsonSerializer.Deserialize<Dictionary<string, EmailFolderIndex>>(indexJson);
                    if (loadedIndex != null)
                    {
                        foreach (var kvp in loadedIndex)
                        {
                            var key = $"{accountId}:{kvp.Key}";
                            _indices[key] = kvp.Value;
                        }
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading index for account {AccountId}", accountId);
                }
            }

            var files = Directory.GetFiles(msgPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<EmailMessage>(json);
                    if (item != null)
                    {
                        UpdateIndex(item);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmailMessageRepo] Error scanning message file {File}", file);
                }
            }

            SaveIndex(accountId);
        }
    }

    private void UpdateIndex(EmailMessage item)
    {
        var folderKey = $"{item.AccountId}:{item.FolderPath}";
        var receiving = item.ReceivingAddresses is { Count: > 0 }
            ? item.ReceivingAddresses
            : item.ToAddresses.Concat(item.CcAddresses)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var meta = new MessageMetadata
        {
            Id = item.Id,
            UniqueId = item.UniqueId,
            GlobalMessageId = item.GlobalMessageId,
            Date = item.Date,
            IsRead = item.IsRead,
            IsFlagged = item.IsFlagged,
            IsLocalOnly = item.IsLocalOnly,
            ReceivingAddresses = receiving
        };

        var folderIndex = _indices.GetOrAdd(folderKey, _ => new EmailFolderIndex());
        folderIndex.Messages[item.UniqueId] = meta;
    }

    private void SaveIndex(string accountId)
    {
        var accountIndices = new Dictionary<string, EmailFolderIndex>();
        var prefix = $"{accountId}:";
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                var folderPath = kvp.Key[prefix.Length..];
                accountIndices[folderPath] = kvp.Value;
            }
        }

        var indexPath = Path.Combine(_basePath, accountId, "index.json");
        _writer.QueueWrite(indexPath, accountIndices);
    }

    public virtual void Save(EmailMessage item)
    {
        UpdateIndex(item);

        var path = GetFilePath(item);
        _writer.QueueWrite(path, item);
        _cache[item.Id] = item;

        SaveIndex(item.AccountId);
        OnSaved?.Invoke(item);
    }

    public virtual void RenameFolder(string accountId, string oldFolderPath, string newFolderPath)
    {
        var msgs = GetByFolder(accountId, oldFolderPath);
        foreach (var msg in msgs)
        {
            msg.FolderPath = newFolderPath;
            Save(msg);
        }

        if (_indices.TryRemove($"{accountId}:{oldFolderPath}", out _))
        {
            SaveIndex(accountId);
        }
    }

    public virtual EmailMessage? GetById(string accountId, string messageId)
    {
        if (TryGetCachedMessage(accountId, messageId, out var item)) return item;

        var path = Path.Combine(_basePath, accountId, "Messages", $"{messageId}.json");
        if (!File.Exists(path)) return null;

        try
        {
            return DeserializeAndCacheMessage(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading email {Id}", messageId);
            return null;
        }
    }

    private string? ResolveAccountId(string messageId)
    {
        if (_cache.TryGetValue(messageId, out var item)) return item.AccountId;

        foreach (var kvp in _indices)
        {
            var meta = kvp.Value.Messages.Values.FirstOrDefault(m => m.Id == messageId);
            if (meta != null)
            {
                return kvp.Key.Split(':')[0];
            }
        }

        if (Directory.Exists(_basePath))
        {
            foreach (var accDir in Directory.GetDirectories(_basePath))
            {
                var accId = Path.GetFileName(accDir);
                var candidatePath = Path.Combine(accDir, "Messages", $"{messageId}.json");
                if (File.Exists(candidatePath))
                {
                    return accId;
                }
            }
        }

        return null;
    }

    public virtual EmailMessage? GetById(string messageId)
    {
        if (_cache.TryGetValue(messageId, out var item)) return item;

        var accountId = ResolveAccountId(messageId);
        return accountId != null ? GetById(accountId, messageId) : null;
    }

    public virtual void Delete(string accountId, string messageId) =>
        DeleteMany(accountId, [messageId]);

    public virtual int DeleteMany(string accountId, IEnumerable<string> messageIds)
    {
        if (string.IsNullOrWhiteSpace(accountId) || messageIds == null)
        {
            return 0;
        }

        var idsToRemove = messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (idsToRemove.Count == 0)
        {
            return 0;
        }

        foreach (var messageId in idsToRemove)
        {
            _cache.TryRemove(messageId, out _);
            var path = Path.Combine(_basePath, accountId, "Messages", $"{messageId}.json");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to immediately delete email message file {Path}", path);
            }
            _writer.QueueDelete(path);
        }

        bool indexChanged = false;
        var prefix = $"{accountId}:";
        foreach (var kvp in _indices)
        {
            if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var index = kvp.Value;
            foreach (var entry in index.Messages)
            {
                if (idsToRemove.Contains(entry.Value.Id))
                {
                    if (index.Messages.TryRemove(entry.Key, out _))
                    {
                        indexChanged = true;
                    }
                }
            }
        }

        if (indexChanged)
        {
            SaveIndex(accountId);
        }

        return idsToRemove.Count;
    }

    public virtual void Delete(string messageId)
    {
        var accountId = ResolveAccountId(messageId);
        if (accountId != null)
        {
            Delete(accountId, messageId);
        }
    }

    public virtual void DeleteByAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;

        var prefix = $"{accountId}:";
        foreach (var key in _indices.Keys.ToList())
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_indices.TryRemove(key, out var folderIndex))
                {
                    foreach (var meta in folderIndex.Messages.Values)
                    {
                        _cache.TryRemove(meta.Id, out _);
                    }
                }
            }
        }

        foreach (var kvp in _cache.ToList())
        {
            if (string.Equals(kvp.Value.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }

        var accountDir = Path.Combine(_basePath, accountId);
        try
        {
            if (Directory.Exists(accountDir))
            {
                Directory.Delete(accountDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to immediately delete email account directory {AccountDir}", accountDir);
        }

        _writer.QueueDeleteDirectory(accountDir);
    }

    public virtual List<EmailMessage> GetAllForAccount(string accountId)
    {
        var results = new List<EmailMessage>();
        var seenIds = new HashSet<string>();
        var prefix = $"{accountId}:";

        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                foreach (var meta in kvp.Value.Messages.Values)
                {
                    if (seenIds.Add(meta.Id))
                    {
                        var msg = GetById(accountId, meta.Id);
                        if (msg != null) results.Add(msg);
                    }
                }
            }
        }

        var msgDir = Path.Combine(_basePath, accountId, "Messages");
        if (Directory.Exists(msgDir))
        {
            foreach (var file in Directory.GetFiles(msgDir, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (seenIds.Add(id))
                {
                    var msg = GetById(accountId, id);
                    if (msg != null) results.Add(msg);
                }
            }
        }

        return results;
    }

    public virtual List<EmailMessage> GetAll()
    {
        if (!Directory.Exists(_basePath)) return _cache.Values.ToList();

        var results = new List<EmailMessage>();
        foreach (var accDir in Directory.GetDirectories(_basePath))
        {
            var accId = Path.GetFileName(accDir);
            results.AddRange(GetAllForAccount(accId));
        }
        return results;
    }

    public virtual void Clear()
    {
        _cache.Clear();
        _indices.Clear();
    }

    public bool Exists(string accountId, string folderPath, uint uid) =>
        _indices.TryGetValue($"{accountId}:{folderPath}", out var index) && index.Messages.ContainsKey(uid);

    public bool TryClaimUid(string accountId, string folderPath, uint uid, string messageId, string globalMessageId = "")
    {
        var folderKey = $"{accountId}:{folderPath}";
        var folderIndex = _indices.GetOrAdd(folderKey, _ => new EmailFolderIndex());

        var placeholder = new MessageMetadata
        {
            Id = messageId,
            UniqueId = uid,
            GlobalMessageId = globalMessageId,
            Date = DateTimeOffset.UtcNow,
            IsRead = false,
            IsFlagged = false
        };

        return folderIndex.Messages.TryAdd(uid, placeholder);
    }

    public virtual EmailMessage? GetByUniqueId(string accountId, uint uniqueId)
    {
        if (uniqueId == 0) return null;

        if (_cache.Values.FirstOrDefault(m => m.AccountId == accountId && m.UniqueId == uniqueId) is { } cached)
            return cached;

        var prefix = $"{accountId}:";
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (kvp.Value.Messages.TryGetValue(uniqueId, out var meta))
                {
                    return GetById(accountId, meta.Id);
                }
            }
        }

        var msgDir = Path.Combine(_basePath, accountId, "Messages");
        if (!Directory.Exists(msgDir)) return null;

        return GetAllForAccount(accountId).FirstOrDefault(m => m.UniqueId == uniqueId);
    }

    public List<EmailMessage> GetAllByGlobalMessageId(string accountId, string globalMessageId)
    {
        var results = new List<EmailMessage>();
        if (string.IsNullOrEmpty(globalMessageId)) return results;

        var prefix = $"{accountId}:";
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                var meta = kvp.Value.Messages.Values.FirstOrDefault(m => m.GlobalMessageId == globalMessageId);
                if (meta != null)
                {
                    var msg = GetById(accountId, meta.Id);
                    if (msg != null) results.Add(msg);
                }
            }
        }
        return results;
    }

    private bool TryGetCachedMessage(string accountId, string id, out EmailMessage? item)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            item = string.IsNullOrEmpty(accountId) || cached.AccountId == accountId ? cached : null;
            return true;
        }
        item = null;
        return false;
    }

    private EmailMessage? DeserializeAndCacheMessage(string json)
    {
        var item = JsonSerializer.Deserialize<EmailMessage>(json);
        if (item == null) return null;

        _cache[item.Id] = item;
        if (_cache.Count > 5000)
        {
            var keyToRemove = _cache.Keys.FirstOrDefault();
            if (keyToRemove != null) _cache.TryRemove(keyToRemove, out _);
        }
        return item;
    }

    private async Task<EmailMessage?> GetByIdOrLoadAsync(string accountId, string id, CancellationToken cancellationToken = default)
    {
        if (TryGetCachedMessage(accountId, id, out var item)) return item;

        var path = Path.Combine(_basePath, accountId, "Messages", $"{id}.json");
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return DeserializeAndCacheMessage(json);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading email {Id}", id);
            return null;
        }
    }

    public IReadOnlyCollection<MessageMetadata> GetFolderMetadata(string accountId, string folderPath)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
        {
            return index.Messages.Values.ToList();
        }
        return Array.Empty<MessageMetadata>();
    }

    public List<EmailMessage> GetByFolder(string accountId, string folderPath, Func<MessageMetadata, bool>? filter = null)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
        {
            var results = new List<EmailMessage>();
            var metas = filter != null ? index.Messages.Values.Where(filter) : index.Messages.Values;
            foreach (var meta in metas)
            {
                var msg = GetById(accountId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public List<uint> GetUidsByFolder(string accountId, string folderPath)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
        {
            return index.Messages.Keys.ToList();
        }
        return new List<uint>();
    }

    public virtual uint GetNextLocalUid(string accountId, string folderPath)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index) && index.Messages.Count > 0)
        {
            var regularUids = index.Messages.Keys.Where(k => k < uint.MaxValue - 1000000).ToList();
            if (regularUids.Count > 0)
            {
                var maxUid = regularUids.Max();
                return maxUid + 1;
            }
        }
        return 1;
    }

    public virtual int GetUnreadCount(string accountId, string folderPath, Func<MessageMetadata, bool>? filter = null)
    {
        if (!_indices.TryGetValue($"{accountId}:{folderPath}", out var index)) return 0;
        return filter != null
            ? index.Messages.Values.Count(m => !m.IsRead && filter(m))
            : index.Messages.Values.Count(m => !m.IsRead);
    }

    public int GetLocalMessageCount(string accountId, string folderPath, Func<MessageMetadata, bool>? filter = null)
    {
        if (!_indices.TryGetValue($"{accountId}:{folderPath}", out var index)) return 0;
        return filter != null
            ? index.Messages.Values.Count(filter)
            : index.Messages.Count;
    }

    public string? GetIdByUid(string accountId, string folderPath, uint uid)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                return meta.Id;
            }
        }
        return null;
    }

    public List<EmailMessage> GetByFolderRecent(string accountId, string folderPath, int count = 50, Func<MessageMetadata, bool>? filter = null)
    {
        if (_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
        {
            var source = filter != null ? index.Messages.Values.Where(filter) : index.Messages.Values;
            var topMetas = source
                .OrderByDescending(m => m.Date)
                .ThenByDescending(m => m.UniqueId)
                .Take(count)
                .ToList();

            var results = new List<EmailMessage>();
            foreach (var meta in topMetas)
            {
                var msg = GetById(accountId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public List<EmailMessage> GetUnreadByFolderRecent(string accountId, string folderPath, int count = 10, Func<MessageMetadata, bool>? filter = null) =>
        GetByFolderRecent(
            accountId,
            folderPath,
            count,
            filter != null ? m => !m.IsRead && filter(m) : static m => !m.IsRead);

    public List<EmailMessage> GetByFolderBeforeDate(string accountId, string folderPath, DateTime beforeDate, int count = 30, Func<MessageMetadata, bool>? filter = null) =>
        GetByFolderRecent(
            accountId,
            folderPath,
            count,
            filter != null ? m => m.Date < beforeDate && filter(m) : m => m.Date < beforeDate);

    public List<EmailMessage> GetRecentAcrossFolders(
        IEnumerable<(string AccountId, string FolderPath, Func<MessageMetadata, bool>? FolderFilter)> folderScopes,
        int count = 50,
        DateTime? beforeDateUtc = null,
        Func<MessageMetadata, bool>? globalFilter = null)
    {
        var candidates = new List<(string AccountId, MessageMetadata Meta)>();

        foreach (var (accountId, folderPath, folderFilter) in folderScopes)
        {
            if (!_indices.TryGetValue($"{accountId}:{folderPath}", out var index))
                continue;

            foreach (var meta in index.Messages.Values)
            {
                if (beforeDateUtc.HasValue && meta.Date >= beforeDateUtc.Value)
                    continue;
                if (globalFilter != null && !globalFilter(meta))
                    continue;
                if (folderFilter != null && !folderFilter(meta))
                    continue;

                candidates.Add((accountId, meta));
            }
        }

        var ordered = candidates
            .OrderByDescending(c => c.Meta.Date)
            .ThenByDescending(c => c.Meta.UniqueId);

        var results = new List<EmailMessage>(Math.Min(count, candidates.Count));
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGlobalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (accountId, meta) in ordered)
        {
            if (!seenIds.Add(meta.Id))
                continue;

            if (!string.IsNullOrWhiteSpace(meta.GlobalMessageId) && !seenGlobalIds.Add($"{accountId}:{meta.GlobalMessageId}"))
                continue;

            var msg = GetById(accountId, meta.Id);
            if (msg != null)
            {
                results.Add(msg);
                if (results.Count >= count)
                    break;
            }
        }

        return results;
    }

    public bool TryUpdateFlags(string accountId, string folderPath, uint uid, bool isRead, bool isFlagged)
    {
        var key = $"{accountId}:{folderPath}";
        if (_indices.TryGetValue(key, out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                if (meta.IsRead == isRead && meta.IsFlagged == isFlagged)
                {
                    return false;
                }

                var msg = GetById(accountId, meta.Id);
                if (msg != null)
                {
                    msg.IsRead = isRead;
                    msg.IsFlagged = isFlagged;
                    Save(msg);
                    return true;
                }
            }
        }
        return false;
    }

    public async Task<List<EmailMessage>> SearchAsync(string accountId, string query, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default, Func<MessageMetadata, bool>? filter = null)
    {
        var results = new List<EmailMessage>();
        await foreach (var msg in SearchStreamingAsync(accountId, query, folderPath, progress, cancellationToken, filter))
        {
            results.Add(msg);
        }
        return results.OrderByDescending(m => m.Date).ToList();
    }

    public async IAsyncEnumerable<EmailMessage> SearchStreamingAsync(string accountId, string query, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default, Func<MessageMetadata, bool>? filter = null)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        var lowerQuery = query.ToLower();

        var matchingMetas = new List<(string FolderPath, MessageMetadata Meta)>();
        var prefix = $"{accountId}:";
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                var fPath = kvp.Key[prefix.Length..];
                if (string.IsNullOrEmpty(folderPath) || fPath == folderPath)
                {
                    foreach (var meta in kvp.Value.Messages.Values)
                    {
                        if (filter == null || filter(meta))
                        {
                            matchingMetas.Add((fPath, meta));
                        }
                    }
                }
            }
        }

        var sortedMetas = matchingMetas.OrderByDescending(m => m.Meta.Date).ToList();
        var totalMessages = sortedMetas.Count;
        int processedCount = 0;
        progress?.Report((0, totalMessages));

        var mimeFolder = Path.Combine(_basePath, accountId, "Mime");

        foreach (var item in sortedMetas)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var meta = item.Meta;
            var id = meta.Id;

            var msg = await GetByIdOrLoadAsync(accountId, id, cancellationToken);

            if (msg != null)
            {
                bool isMatch = (msg.Subject?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.FromAddress?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.FromName?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.Snippet?.ToLower().Contains(lowerQuery) ?? false);

                if (!isMatch && msg.ToAddresses != null) isMatch = msg.ToAddresses.Any(a => a.ToLower().Contains(lowerQuery));
                if (!isMatch && msg.CcAddresses != null) isMatch = msg.CcAddresses.Any(a => a.ToLower().Contains(lowerQuery));
                if (!isMatch && msg.ReceivingAddresses != null) isMatch = msg.ReceivingAddresses.Any(a => a.ToLower().Contains(lowerQuery));

                if (!isMatch)
                {
                    var mimeFilePath = Path.Combine(mimeFolder, $"{id}.eml");
                    if (File.Exists(mimeFilePath))
                    {
                        try
                        {
                            var mime = await MimeKit.MimeMessage.LoadAsync(mimeFilePath, cancellationToken);
                            var bodyText = mime.TextBody ?? "";
                            if (string.IsNullOrWhiteSpace(bodyText) && !string.IsNullOrWhiteSpace(mime.HtmlBody))
                            {
                                bodyText = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(mime.HtmlBody, "<.*?>", " "));
                            }
                            if (bodyText.ToLower().Contains(lowerQuery)) isMatch = true;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "[EmailMessageRepo] Error reading MIME body for search {Id}", id);
                        }
                    }
                }

                if (isMatch)
                {
                    yield return msg;
                }
            }

            var currentProgress = Interlocked.Increment(ref processedCount);
            if (currentProgress % 5 == 0 || currentProgress == totalMessages) progress?.Report((currentProgress, totalMessages));
        }
    }

    public bool HasMoreMessages(string accountId, string folderPath, Func<MessageMetadata, bool>? filter = null) =>
        GetLocalMessageCount(accountId, folderPath, filter) > 50;
}

public class EmailFolderIndex
{
    public ConcurrentDictionary<uint, MessageMetadata> Messages { get; set; } = new();
}

public class MessageMetadata
{
    public string Id { get; set; } = "";
    public uint UniqueId { get; set; }
    public string GlobalMessageId { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsLocalOnly { get; set; }
    public List<string> ReceivingAddresses { get; set; } = [];
}
