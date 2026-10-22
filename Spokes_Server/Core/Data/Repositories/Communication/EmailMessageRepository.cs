using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Data.Repositories.Communication;

/// <summary>
/// Repository for managing email messages per employee.
/// NOTE: This class intentionally does not inherit from JsonRepository&lt;EmailMessage&gt; because email messages
/// are partitioned hierarchically on disk by employee ID (Data/Employees/{EmployeeId}/Email/Messages/{MessageId}.json).
/// Decoupling from JsonRepository follows the architectural precedent established in PrivateContactRepository,
/// ensuring all operations support explicit employee scoping while gracefully resolving across index entries on demand.
/// </summary>
public class EmailMessageRepository
{
    private readonly DiskPersistenceService _writer;
    private readonly string _basePath;
    private readonly ILogger<EmailMessageRepository>? _logger;
    private readonly ConcurrentDictionary<string, EmailMessage> _cache = new();

    // The "Lightweight Index" - Maps (Employee + Folder + UID) -> MessageMetadata
    // This allows O(1) checks for existence and flags without loading the full message.
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
        _basePath = Path.Combine(config["DataPath"] ?? "Data", "Employees");
        _logger = logger;
    }

    private string GetFilePath(EmailMessage item) =>
        Path.Combine(_basePath, item.EmployeeId, "Email", "Messages", $"{item.Id}.json");

    // Override LoadFromDisk to NOT load everything. 
    // Instead, we scan directories to build the Index.
    public virtual void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        // Iterate all Employee directories
        var employeeDirs = Directory.GetDirectories(_basePath);
        foreach (var employeeDir in employeeDirs)
        {
            var employeeId = Path.GetFileName(employeeDir);
            var foldersDir = Path.Combine(employeeDir, "Email", "Folders");

            var msgPath = Path.Combine(employeeDir, "Email", "Messages");
            if (!Directory.Exists(msgPath)) continue;

            var indexPath = Path.Combine(employeeDir, "Email", "index.json");

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
                            var key = $"{employeeId}:{kvp.Key}"; // kvp.Key is FolderPath
                            _indices[key] = kvp.Value;
                        }
                        continue; // Success, skip manual scan
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading index for {EmployeeId}", employeeId);
                }
            }

            // Fallback: Scan all JSONs
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

            // Save the rebuilt index
            SaveIndex(employeeId);
        }
    }

    private void UpdateIndex(EmailMessage item)
    {
        var folderKey = $"{item.EmployeeId}:{item.FolderPath}";
        var meta = new MessageMetadata
        {
            Id = item.Id,
            UniqueId = item.UniqueId,
            GlobalMessageId = item.GlobalMessageId,
            Date = item.Date,
            IsRead = item.IsRead,
            IsFlagged = item.IsFlagged,
            IsLocalOnly = item.IsLocalOnly
        };

        var folderIndex = _indices.GetOrAdd(folderKey, _ => new EmailFolderIndex());
        folderIndex.Messages[item.UniqueId] = meta;
    }

    private void SaveIndex(string employeeId)
    {
        var employeeIndices = new Dictionary<string, EmailFolderIndex>();
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:"))
            {
                var folderPath = kvp.Key.Substring(employeeId.Length + 1);
                employeeIndices[folderPath] = kvp.Value;
            }
        }

        var indexPath = Path.Combine(_basePath, employeeId, "Email", "index.json");
        _writer.QueueWrite(indexPath, employeeIndices);
    }

    public virtual void Save(EmailMessage item)
    {
        UpdateIndex(item);

        var path = GetFilePath(item);
        _writer.QueueWrite(path, item);
        _cache[item.Id] = item;

        SaveIndex(item.EmployeeId);
        OnSaved?.Invoke(item);
    }

    public virtual void RenameFolder(string employeeId, string oldFolderPath, string newFolderPath)
    {
        var msgs = GetByFolder(employeeId, oldFolderPath);
        foreach (var msg in msgs)
        {
            msg.FolderPath = newFolderPath;
            Save(msg);
        }

        // Remove old index entirely
        if (_indices.TryRemove($"{employeeId}:{oldFolderPath}", out _))
        {
            SaveIndex(employeeId);
        }
    }

    /// <summary>
    /// Gets a message by ID with explicit employee context (recommended, O(1)).
    /// </summary>
    public virtual EmailMessage? GetById(string employeeId, string messageId) =>
        GetByIdOrLoad(employeeId, messageId);

    /// <summary>
    /// Gets a message by ID across all employees by checking cache and locating it across index entries or disk on demand.
    /// </summary>
    public virtual EmailMessage? GetById(string messageId)
    {
        if (_cache.TryGetValue(messageId, out var item)) return item;

        foreach (var kvp in _indices)
        {
            var meta = kvp.Value.Messages.Values.FirstOrDefault(m => m.Id == messageId);
            if (meta != null)
            {
                var employeeId = kvp.Key.Split(':')[0];
                return GetByIdOrLoad(employeeId, messageId);
            }
        }

        if (Directory.Exists(_basePath))
        {
            foreach (var empDir in Directory.GetDirectories(_basePath))
            {
                var empId = Path.GetFileName(empDir);
                var candidatePath = Path.Combine(empDir, "Email", "Messages", $"{messageId}.json");
                if (File.Exists(candidatePath))
                {
                    return GetByIdOrLoad(empId, messageId);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes a message with explicit employee context.
    /// </summary>
    public virtual void Delete(string employeeId, string messageId)
    {
        _cache.TryRemove(messageId, out _);
        var path = Path.Combine(_basePath, employeeId, "Email", "Messages", $"{messageId}.json");
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to immediately delete email message file {Path}", path);
        }
        _writer.QueueDelete(path);

        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:"))
            {
                var index = kvp.Value;
                var uidToRemove = index.Messages.FirstOrDefault(x => x.Value.Id == messageId).Key;
                if (uidToRemove != 0)
                {
                    index.Messages.TryRemove(uidToRemove, out _);
                    SaveIndex(employeeId);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Deletes a message by ID by resolving employee context from cache, index, or disk.
    /// </summary>
    public virtual void Delete(string messageId)
    {
        if (_cache.TryGetValue(messageId, out var cached))
        {
            Delete(cached.EmployeeId, messageId);
            return;
        }

        foreach (var kvp in _indices)
        {
            var meta = kvp.Value.Messages.Values.FirstOrDefault(m => m.Id == messageId);
            if (meta != null)
            {
                var employeeId = kvp.Key.Split(':')[0];
                Delete(employeeId, messageId);
                return;
            }
        }

        if (Directory.Exists(_basePath))
        {
            foreach (var empDir in Directory.GetDirectories(_basePath))
            {
                var empId = Path.GetFileName(empDir);
                var candidatePath = Path.Combine(empDir, "Email", "Messages", $"{messageId}.json");
                if (File.Exists(candidatePath))
                {
                    Delete(empId, messageId);
                    return;
                }
            }
        }
    }

    public virtual List<EmailMessage> GetAllForEmployee(string employeeId)
    {
        var results = new List<EmailMessage>();
        var seenIds = new HashSet<string>();

        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:"))
            {
                foreach (var meta in kvp.Value.Messages.Values)
                {
                    if (seenIds.Add(meta.Id))
                    {
                        var msg = GetByIdOrLoad(employeeId, meta.Id);
                        if (msg != null) results.Add(msg);
                    }
                }
            }
        }

        var msgDir = Path.Combine(_basePath, employeeId, "Email", "Messages");
        if (Directory.Exists(msgDir))
        {
            foreach (var file in Directory.GetFiles(msgDir, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (seenIds.Add(id))
                {
                    var msg = GetByIdOrLoad(employeeId, id);
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
        foreach (var empDir in Directory.GetDirectories(_basePath))
        {
            var empId = Path.GetFileName(empDir);
            results.AddRange(GetAllForEmployee(empId));
        }
        return results;
    }

    public virtual void Clear()
    {
        _cache.Clear();
        _indices.Clear();
    }

    public bool Exists(string employeeId, string folderPath, uint uid) =>
        _indices.TryGetValue($"{employeeId}:{folderPath}", out var index) && index.Messages.ContainsKey(uid);

    public bool TryClaimUid(string employeeId, string folderPath, uint uid, string messageId, string globalMessageId = "")
    {
        var folderKey = $"{employeeId}:{folderPath}";
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

    public EmailMessage? GetByUid(string employeeId, string folderPath, uint uid)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                return GetByIdOrLoad(employeeId, meta.Id);
            }
        }
        return null;
    }

    /// <summary>
    /// Finds an email message by IMAP UniqueId for a specific employee across any folder using in-memory cache and indices, avoiding global disk scans.
    /// </summary>
    public virtual EmailMessage? GetByUniqueId(string employeeId, uint uniqueId)
    {
        if (uniqueId == 0) return null;

        if (_cache.Values.FirstOrDefault(m => m.EmployeeId == employeeId && m.UniqueId == uniqueId) is { } cached)
            return cached;

        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:", StringComparison.OrdinalIgnoreCase))
            {
                if (kvp.Value.Messages.TryGetValue(uniqueId, out var meta))
                {
                    return GetByIdOrLoad(employeeId, meta.Id);
                }
            }
        }

        var msgDir = Path.Combine(_basePath, employeeId, "Email", "Messages");
        if (!Directory.Exists(msgDir)) return null;

        return GetAllForEmployee(employeeId).FirstOrDefault(m => m.UniqueId == uniqueId);
    }

    public List<EmailMessage> GetAllByGlobalMessageId(string employeeId, string globalMessageId)
    {
        var results = new List<EmailMessage>();
        if (string.IsNullOrEmpty(globalMessageId)) return results;

        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:"))
            {
                var meta = kvp.Value.Messages.Values.FirstOrDefault(m => m.GlobalMessageId == globalMessageId);
                if (meta != null)
                {
                    var msg = GetByIdOrLoad(employeeId, meta.Id);
                    if (msg != null) results.Add(msg);
                }
            }
        }
        return results;
    }

    public EmailMessage? GetByIdOrLoad(string employeeId, string id)
    {
        if (_cache.TryGetValue(id, out var item))
        {
            if (string.IsNullOrEmpty(employeeId) || item.EmployeeId == employeeId)
                return item;
            return null;
        }

        var path = Path.Combine(_basePath, employeeId, "Email", "Messages", $"{id}.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                item = JsonSerializer.Deserialize<EmailMessage>(json);
                if (item != null)
                {
                    _cache[item.Id] = item;
                    if (_cache.Count > 5000)
                    {
                        var keyToRemove = _cache.Keys.FirstOrDefault();
                        if (keyToRemove != null) _cache.TryRemove(keyToRemove, out _);
                    }
                    return item;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading email {Id}", id);
            }
        }
        return null;
    }

    public async Task<EmailMessage?> GetByIdOrLoadAsync(string employeeId, string id, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(id, out var item))
        {
            if (string.IsNullOrEmpty(employeeId) || item.EmployeeId == employeeId)
                return item;
            return null;
        }

        var path = Path.Combine(_basePath, employeeId, "Email", "Messages", $"{id}.json");
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                item = JsonSerializer.Deserialize<EmailMessage>(json);
                if (item != null)
                {
                    _cache[item.Id] = item;
                    if (_cache.Count > 5000)
                    {
                        var keyToRemove = _cache.Keys.FirstOrDefault();
                        if (keyToRemove != null) _cache.TryRemove(keyToRemove, out _);
                    }
                    return item;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[EmailMessageRepo] Error loading email {Id}", id);
            }
        }
        return null;
    }


    public List<EmailMessage> GetByFolder(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            var results = new List<EmailMessage>();
            foreach (var meta in index.Messages.Values)
            {
                var msg = GetByIdOrLoad(employeeId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public List<uint> GetUidsByFolder(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            return index.Messages.Keys.ToList();
        }
        return new List<uint>();
    }

    /// <summary>
    /// Generates the next sequential local UID for messages created or moved locally without server IMAP sync.
    /// Uses structured monotonically increasing sequence starting above any existing non-sentinel UIDs in the folder.
    /// </summary>
    public virtual uint GetNextLocalUid(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index) && index.Messages.Count > 0)
        {
            // Filter out any legacy sentinel UIDs near uint.MaxValue when calculating next local sequence
            var regularUids = index.Messages.Keys.Where(k => k < uint.MaxValue - 1000000).ToList();
            if (regularUids.Count > 0)
            {
                var maxUid = regularUids.Max();
                return maxUid + 1;
            }
        }
        return 1;
    }

    public virtual int GetUnreadCount(string employeeId, string folderPath) =>
        _indices.TryGetValue($"{employeeId}:{folderPath}", out var index) ? index.Messages.Values.Count(m => !m.IsRead) : 0;

    public int GetLocalMessageCount(string employeeId, string folderPath) =>
        _indices.TryGetValue($"{employeeId}:{folderPath}", out var index) ? index.Messages.Count : 0;

    public string? GetIdByUid(string employeeId, string folderPath, uint uid)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                return meta.Id;
            }
        }
        return null;
    }

    public List<EmailMessage> GetByFolderRecent(string employeeId, string folderPath, int count = 50)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            var topMetas = index.Messages.Values
                .OrderByDescending(m => m.Date)
                .ThenByDescending(m => m.UniqueId)
                .Take(count)
                .ToList();

            var results = new List<EmailMessage>();
            foreach (var meta in topMetas)
            {
                var msg = GetByIdOrLoad(employeeId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public List<EmailMessage> GetUnreadByFolderRecent(string employeeId, string folderPath, int count = 10)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            var unreadMetas = index.Messages.Values
                .Where(m => !m.IsRead)
                .OrderByDescending(m => m.Date)
                .ThenByDescending(m => m.UniqueId)
                .Take(count)
                .ToList();

            var results = new List<EmailMessage>();
            foreach (var meta in unreadMetas)
            {
                var msg = GetByIdOrLoad(employeeId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public List<EmailMessage> GetByFolderBeforeDate(string employeeId, string folderPath, DateTime beforeDate, int count = 30)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            var olderMetas = index.Messages.Values
               .Where(m => m.Date < beforeDate)
               .OrderByDescending(m => m.Date)
               .ThenByDescending(m => m.UniqueId)
               .Take(count)
               .ToList();

            var results = new List<EmailMessage>();
            foreach (var meta in olderMetas)
            {
                var msg = GetByIdOrLoad(employeeId, meta.Id);
                if (msg != null) results.Add(msg);
            }
            return results;
        }
        return new List<EmailMessage>();
    }

    public bool TryUpdateFlags(string employeeId, string folderPath, uint uid, bool isRead, bool isFlagged)
    {
        var key = $"{employeeId}:{folderPath}";
        if (_indices.TryGetValue(key, out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                if (meta.IsRead == isRead && meta.IsFlagged == isFlagged)
                {
                    return false;
                }

                var msg = GetByIdOrLoad(employeeId, meta.Id);
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

    public async Task<List<EmailMessage>> SearchAsync(string employeeId, string query, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<EmailMessage>();
        await foreach (var msg in SearchStreamingAsync(employeeId, query, folderPath, progress, cancellationToken))
        {
            results.Add(msg);
        }
        return results.OrderByDescending(m => m.Date).ToList();
    }

    public async IAsyncEnumerable<EmailMessage> SearchStreamingAsync(string employeeId, string query, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        var lowerQuery = query.ToLower();

        var matchingMetas = new List<(string FolderPath, MessageMetadata Meta)>();
        foreach (var kvp in _indices)
        {
            if (kvp.Key.StartsWith($"{employeeId}:"))
            {
                var fPath = kvp.Key.Substring(employeeId.Length + 1);
                if (string.IsNullOrEmpty(folderPath) || fPath == folderPath)
                {
                    foreach (var meta in kvp.Value.Messages.Values)
                    {
                        matchingMetas.Add((fPath, meta));
                    }
                }
            }
        }

        // Sort by date descending to search most recent first
        var sortedMetas = matchingMetas.OrderByDescending(m => m.Meta.Date).ToList();
        var totalMessages = sortedMetas.Count;
        int processedCount = 0;
        progress?.Report((0, totalMessages));

        var mimeFolder = Path.Combine(_basePath, employeeId, "Email", "Mime");

        foreach (var item in sortedMetas)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var meta = item.Meta;
            var id = meta.Id;

            // Optimization: First check if we can find a match in the cache or if we need to load the full message
            EmailMessage? msg = null;
            if (_cache.TryGetValue(id, out var cachedMsg))
            {
                msg = cachedMsg;
            }

            bool isMatch = false;

            // If we don't have the full message, we can still check some metadata if it were available in meta,
            // but for now we mostly rely on loading the message or checking the MIME.

            if (msg == null)
            {
                msg = await GetByIdOrLoadAsync(employeeId, id, cancellationToken);
            }

            if (msg != null)
            {
                isMatch = (msg.Subject?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.FromAddress?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.FromName?.ToLower().Contains(lowerQuery) ?? false) ||
                               (msg.Snippet?.ToLower().Contains(lowerQuery) ?? false);

                if (!isMatch && msg.ToAddresses != null) isMatch = msg.ToAddresses.Any(a => a.ToLower().Contains(lowerQuery));
                if (!isMatch && msg.CcAddresses != null) isMatch = msg.CcAddresses.Any(a => a.ToLower().Contains(lowerQuery));

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

    public bool HasMoreMessages(string employeeId, string folderPath) =>
        _indices.TryGetValue($"{employeeId}:{folderPath}", out var index) && index.Messages.Count > 50;
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
}
