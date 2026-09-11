using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Collections.Concurrent;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class EmailMessageRepository : JsonRepository<EmailMessage>
{
    // The "Lightweight Index" - Maps (Employee + Folder + UID) -> MessageMetadata
    // This allows O(1) checks for existence and flags without loading the full message.
    private readonly ConcurrentDictionary<string, EmailFolderIndex> _indices = new();

    public EmailMessageRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Employees"), "EmailMessages.json")
    {
    }

    protected override string GetFilePath(EmailMessage item)
    {
        // Data/Employees/{EmployeeId}/Email/Messages/{Id}.json
        return Path.Combine(_basePath, item.EmployeeId, "Email", "Messages", $"{item.Id}.json");
    }

    // Override LoadFromDisk to NOT load everything. 
    // Instead, we scan directories to build the Index.
    public override void LoadFromDisk()
    {
        if (!Directory.Exists(_basePath)) return;

        // Iterate all Employee directories
        var employeeDirs = Directory.GetDirectories(_basePath);
        foreach (var employeeDir in employeeDirs)
        {
            var employeeId = Path.GetFileName(employeeDir);
            var foldersDir = Path.Combine(employeeDir, "Email", "Folders");

            // We need to know which folders exist to organize the index
            if (Directory.Exists(foldersDir))
            {
                // We'll trust the folder structure for now.
            }

            var msgPath = Path.Combine(employeeDir, "Email", "Messages");
            if (!Directory.Exists(msgPath)) continue;

            var indexPath = Path.Combine(employeeDir, "Email", "index.json");

            if (File.Exists(indexPath))
            {
                try
                {
                    var indexJson = File.ReadAllText(indexPath);
                    var loadedIndex = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, EmailFolderIndex>>(indexJson);
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
                    Console.WriteLine($"[EmailFolderRepo] Error loading index for {employeeId}: {ex.Message}");
                }
            }

            // Fallback: Scan all JSONs
            var files = Directory.GetFiles(msgPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = System.Text.Json.JsonSerializer.Deserialize<EmailMessage>(json);
                    if (item != null)
                    {
                        UpdateIndex(item);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmailMessageRepo] Error scanning message file {file}: {ex.Message}");
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
            IsFlagged = item.IsFlagged
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

    public override void Save(EmailMessage item)
    {
        UpdateIndex(item);

        var path = GetFilePath(item);
        _writer.QueueWrite(path, item);
        _cache[item.Id] = item;

        SaveIndex(item.EmployeeId);
    }

    public void RenameFolder(string employeeId, string oldFolderPath, string newFolderPath)
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

    public override void Delete(string id)
    {
        throw new NotSupportedException("Use Delete(messageId, employeeId) instead to prevent state leaks and ensure correct file paths.");
    }

    public void Delete(string messageId, string employeeId)
    {
        _cache.TryRemove(messageId, out _);
        var path = Path.Combine(_basePath, employeeId, "Email", "Messages", $"{messageId}.json");
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

    public bool Exists(string employeeId, string folderPath, uint uid)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            return index.Messages.ContainsKey(uid);
        }
        return false;
    }

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

    public EmailMessage? GetByUniqueId(string employeeId, string folderPath, uint uid)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            if (index.Messages.TryGetValue(uid, out var meta))
            {
                return GetByIdOrLoad(meta.Id, employeeId);
            }
        }
        return null;
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
                    var msg = GetByIdOrLoad(meta.Id, employeeId);
                    if (msg != null) results.Add(msg);
                }
            }
        }
        return results;
    }

    public EmailMessage? GetByIdOrLoad(string id, string employeeId)
    {
        if (_cache.TryGetValue(id, out var item)) return item;

        var path = Path.Combine(_basePath, employeeId, "Email", "Messages", $"{id}.json");
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                item = System.Text.Json.JsonSerializer.Deserialize<EmailMessage>(json);
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
                Console.WriteLine($"[EmailMessageRepo] Error loading email {id}: {ex.Message}");
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
                var msg = GetByIdOrLoad(meta.Id, employeeId);
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

    public virtual int GetUnreadCount(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            return index.Messages.Values.Count(m => !m.IsRead);
        }
        return 0;
    }

    public int GetLocalMessageCount(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            return index.Messages.Count;
        }
        return 0;
    }

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
                var msg = GetByIdOrLoad(meta.Id, employeeId);
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
                var msg = GetByIdOrLoad(meta.Id, employeeId);
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

                var msg = GetByIdOrLoad(meta.Id, employeeId);
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

    public async Task<List<EmailMessage>> SearchAsync(string employeeId, string query, string dataPath, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<EmailMessage>();
        await foreach (var msg in SearchStreamingAsync(employeeId, query, dataPath, folderPath, progress, cancellationToken))
        {
            results.Add(msg);
        }
        return results.OrderByDescending(m => m.Date).ToList();
    }

    public async IAsyncEnumerable<EmailMessage> SearchStreamingAsync(string employeeId, string query, string dataPath, string? folderPath = null, IProgress<(int processed, int total)>? progress = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
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

        var mimeFolder = Path.Combine(dataPath, "Employees", employeeId, "Email", "Mime");

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
                // Try to load just enough to check metadata? No, let's load it but maybe we can optimize this later.
                msg = GetByIdOrLoad(id, employeeId);
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
                            using var stream = File.OpenRead(mimeFilePath);
                            var mime = MimeKit.MimeMessage.Load(stream);
                            var bodyText = mime.TextBody ?? "";
                            if (string.IsNullOrWhiteSpace(bodyText) && !string.IsNullOrWhiteSpace(mime.HtmlBody))
                            {
                                bodyText = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(mime.HtmlBody, "<.*?>", " "));
                            }
                            if (bodyText.ToLower().Contains(lowerQuery)) isMatch = true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[EmailFolderRepo] Error reading MIME body for search {id}: {ex.Message}");
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




    public bool HasMoreMessages(string employeeId, string folderPath)
    {
        if (_indices.TryGetValue($"{employeeId}:{folderPath}", out var index))
        {
            return index.Messages.Count > 50;
        }
        return false;
    }
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
}


