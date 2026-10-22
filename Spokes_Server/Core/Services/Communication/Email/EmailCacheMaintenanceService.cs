using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailCacheMaintenanceService : IEmailCacheMaintenanceService
{
    private readonly EmployeeRepository _employees;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EmailSyncStateService _syncStateService;
    private readonly ILogger<EmailCacheMaintenanceService>? _logger;
    private readonly string _emailBodyPath;

    public EmailCacheMaintenanceService(
        EmployeeRepository employees,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EmailSyncStateService syncStateService,
        IConfiguration config,
        ILogger<EmailCacheMaintenanceService>? logger = null)
    {
        _employees = employees;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _syncStateService = syncStateService;
        _logger = logger;

        var dataPath = (config != null ? config["DataPath"] : "Data") ?? "Data";
        _emailBodyPath = Path.Combine(dataPath, "Employees");

        // Ensure Windows-1252 codepage is available (not included in .NET Core/5+ by default)
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public string EmailBodyPath => _emailBodyPath;

    public string GetAttachmentPath(string requestingEmployeeId, string messageId, string attachmentId)
    {
        return Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Attachments", messageId, attachmentId);
    }

    public string? GetMimeFilePath(string employeeId, string messageId)
    {
        var path = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime", $"{messageId}.eml");
        return File.Exists(path) ? path : null;
    }

    public async Task<string> GetEmailBodyAsync(string requestingEmployeeId, string messageId, CancellationToken cancellationToken = default)
    {
        var mimePath = Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Mime", $"{messageId}.eml");
        if (File.Exists(mimePath))
            return await ExtractBodyFromMimeAsync(mimePath, cancellationToken);

        var legacyHtmlPath = Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Bodies", $"{messageId}.html");
        if (File.Exists(legacyHtmlPath))
            return await ReadHtmlWithEncodingDetectionAsync(legacyHtmlPath, _logger, cancellationToken);

        return "";
    }

    public static async Task<string> ExtractBodyFromMimeAsync(string mimePath, CancellationToken cancellationToken = default)
    {
        var mime = await MimeMessage.LoadAsync(mimePath, cancellationToken);
        var body = mime.HtmlBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            var text = mime.TextBody ?? "";
            body = $"<div style=\"white-space:pre-wrap;font-family:sans-serif;\">{System.Net.WebUtility.HtmlEncode(text)}</div>";
        }

        foreach (var part in mime.BodyParts.OfType<MimePart>())
        {
            if (!string.IsNullOrEmpty(part.ContentId) && part.Content != null)
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
                var cleanId = part.ContentId.Trim('<', '>');
                body = body.Replace($"cid:{cleanId}", dataUri);
            }
        }
        return body;
    }

    public static async Task<string> ReadHtmlWithEncodingDetectionAsync(string filePath, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8, cancellationToken);

        bool needsFix = false;
        foreach (char c in text)
        {
            if (c >= '\u0080' && c <= '\u009F')
            {
                needsFix = true;
                break;
            }
        }

        if (needsFix)
        {
            char[] win1252Map = new char[]
            {
                '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
                '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
                '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
                '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
            };

            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= '\u0080' && c <= '\u009F')
                    sb.Append(win1252Map[c - '\u0080']);
                else
                    sb.Append(c);
            }
            text = sb.ToString();

            try
            {
                await File.WriteAllTextAsync(filePath, text, System.Text.Encoding.UTF8, cancellationToken);
                logger?.LogDebug("Fixed Windows-1252 character mapping in: {FileName}", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to repair charsets during read");
            }
        }

        return text;
    }

    public async Task<string> GetSignatureAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_emailBodyPath, employeeId, "Email", "Signature.html");
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, cancellationToken);

        var employee = _employees.GetById(employeeId);
        if (employee != null && !string.IsNullOrEmpty(employee.SignatureHtml))
        {
            return employee.SignatureHtml;
        }

        return "";
    }

    public async Task SaveSignatureAsync(string employeeId, string html, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_emailBodyPath, employeeId, "Email");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Signature.html");
        await File.WriteAllTextAsync(path, html ?? string.Empty, cancellationToken);

        var employee = _employees.GetById(employeeId);
        if (employee != null && !string.IsNullOrEmpty(employee.SignatureHtml))
        {
            employee.SignatureHtml = string.Empty;
            _employees.Save(employee);
        }
    }

    public async Task RepairEmailInstallationAsync(string employeeId, Func<string, CancellationToken, Task> syncEmployeeCallback, CancellationToken cancellationToken = default)
    {
        _syncStateService.SetState(employeeId, "Preparing Repair...");

        var folders = _emailFolders.GetByEmployee(employeeId);
        var draftIds = new HashSet<string>();

        var draftFolder = folders.FirstOrDefault(f => f.Path == "LocalDrafts");
        if (draftFolder != null)
        {
            var uids = _emailMessages.GetUidsByFolder(employeeId, "LocalDrafts");
            foreach (var uid in uids)
            {
                var id = _emailMessages.GetIdByUid(employeeId, "LocalDrafts", uid);
                if (id != null) draftIds.Add(id);
            }
        }

        _syncStateService.SetState(employeeId, "Wiping physical cache...");

        await Task.Run(() =>
        {
            var mimeDir = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime");
            if (Directory.Exists(mimeDir))
            {
                try { Directory.Delete(mimeDir, true); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete mime directory"); }
            }

            var attDir = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments");
            if (Directory.Exists(attDir))
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(attDir))
                    {
                        var msgId = Path.GetFileName(dir);
                        if (!draftIds.Contains(msgId))
                        {
                            Directory.Delete(dir, true);
                        }
                    }
                }
                catch (Exception ex) { _logger?.LogWarning(ex, "Failed to wipe attachments directory"); }
            }

            var msgDir = Path.Combine(_emailBodyPath, employeeId, "Email", "Messages");
            if (Directory.Exists(msgDir))
            {
                foreach (var file in Directory.GetFiles(msgDir, "*.json"))
                {
                    var msgId = Path.GetFileNameWithoutExtension(file);
                    if (!draftIds.Contains(msgId))
                    {
                        try { File.Delete(file); } catch (Exception ex) { _logger?.LogWarning(ex, "Failed to delete message file {FilePath}", file); }
                    }
                }
            }
        }, cancellationToken);

        foreach (var folder in folders)
        {
            if (folder.Path == "LocalDrafts") continue;

            _syncStateService.SetState(employeeId, $"Clearing {folder.Name}...");

            var uids = _emailMessages.GetUidsByFolder(employeeId, folder.Path);
            foreach (var uid in uids)
            {
                var id = _emailMessages.GetIdByUid(employeeId, folder.Path, uid);
                if (id != null)
                {
                    _emailMessages.Delete(employeeId, id);
                }
            }

            folder.TotalCount = 0;
            folder.UnreadCount = 0;
            _emailFolders.Save(folder);
        }

        _syncStateService.SetState(employeeId, "Starting fresh sync...");
        await syncEmployeeCallback(employeeId, cancellationToken);
    }

    public async Task DeleteLocalMessageFilesAsync(string employeeId, string messageId)
    {
        try
        {
            var mimePath = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime", $"{messageId}.eml");
            if (File.Exists(mimePath)) await Task.Run(() => File.Delete(mimePath));

            var attachmentFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments", messageId);
            if (Directory.Exists(attachmentFolder)) await Task.Run(() => Directory.Delete(attachmentFolder, true));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to cleanup files for {MessageId}", messageId);
        }
    }
}
