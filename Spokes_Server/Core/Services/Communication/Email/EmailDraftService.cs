using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Text;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailDraftService : IEmailDraftService
{
    private readonly EmployeeRepository _employees;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository? _companyProfile;
    private readonly ILogger<EmailDraftService>? _logger;

    public EmailDraftService(
        EmployeeRepository employees,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        IEmailCacheMaintenanceService cacheMaintenance,
        Spokes_Server.Core.Data.Repositories.Core.CompanyProfileRepository? companyProfile = null,
        ILogger<EmailDraftService>? logger = null)
    {
        _employees = employees;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _cacheMaintenance = cacheMaintenance;
        _companyProfile = companyProfile;
        _logger = logger;
    }

    public async Task<string> SaveDraftAsync(SaveDraftRequest request, CancellationToken cancellationToken = default)
    {
        var employeeId = request.EmployeeId;
        var to = request.To ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var body = request.Body ?? string.Empty;
        var attachments = request.Attachments;
        var cc = request.Cc;
        var bcc = request.Bcc;
        var existingDraftId = request.ExistingDraftId;

        string senderEmail;
        string senderName;

        if (employeeId == Spokes_Server.Core.Constants.CompanyBillInboxConstants.SystemEmployeeId)
        {
            var billInbox = _companyProfile?.Get()?.Accounting?.BillInbox;
            senderEmail = !string.IsNullOrEmpty(billInbox?.EmailAddress) ? billInbox.EmailAddress : "facturation@spokes";
            senderName = !string.IsNullOrEmpty(billInbox?.DisplayName) ? billInbox.DisplayName : "Facturation";
        }
        else
        {
            var employee = _employees.GetById(employeeId);
            if (employee == null) return "";
            senderEmail = employee.Email;
            senderName = employee.FullName;
        }

        var folders = _emailFolders.GetByEmployee(employeeId);
        var drafts = folders.FirstOrDefault(f => f.Path == "LocalDrafts");

        if (drafts == null)
        {
            drafts = new EmailFolder
            {
                EmployeeId = employeeId,
                Name = "Local Drafts",
                Path = "LocalDrafts",
                IsDrafts = true,
                TotalCount = 0,
                UnreadCount = 0
            };
            _emailFolders.Save(drafts);
        }

        EmailMessage? draft = null;

        if (!string.IsNullOrEmpty(existingDraftId))
        {
            draft = _emailMessages.GetById(employeeId, existingDraftId);
        }

        if (draft == null)
        {
            draft = new EmailMessage
            {
                EmployeeId = employeeId,
                FolderPath = drafts.Path,
                UniqueId = drafts.NextUid++,
                IsRead = true,
                IsFlagged = false
            };

            drafts.TotalCount++;
            _emailFolders.Save(drafts);
        }
        else if (draft.UniqueId == 0)
        {
            draft.UniqueId = drafts.NextUid++;
            _emailFolders.Save(drafts);
        }

        draft.Subject = subject;
        draft.FromAddress = senderEmail;
        draft.FromName = senderName;
        draft.ToAddresses = to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        draft.CcAddresses = cc?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? new List<string>();
        draft.BccAddresses = bcc?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? new List<string>();
        draft.Date = DateTimeOffset.Now;
        draft.FolderPath = drafts.Path;

        var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, employeeId, "Email", "Mime");
        Directory.CreateDirectory(mimeFolder);
        var draftMimePath = Path.Combine(mimeFolder, $"{draft.Id}.eml");

        var draftMime = new MimeMessage();
        draftMime.From.Add(new MailboxAddress(senderName, senderEmail));
        if (!string.IsNullOrWhiteSpace(to))
            draftMime.To.AddRange(InternetAddressList.Parse(to));
        if (!string.IsNullOrWhiteSpace(cc))
            draftMime.Cc.AddRange(InternetAddressList.Parse(cc));
        if (!string.IsNullOrWhiteSpace(bcc))
            draftMime.Bcc.AddRange(InternetAddressList.Parse(bcc));
        draftMime.Subject = subject;
        draftMime.Body = new TextPart(TextFormat.Html) { Text = body };
        await using var draftMimeStream = File.Create(draftMimePath);
        await draftMime.WriteToAsync(draftMimeStream, cancellationToken);

        if (attachments != null)
        {
            var attachmentFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, employeeId, "Email", "Attachments", draft.Id);
            if (!Directory.Exists(attachmentFolder)) Directory.CreateDirectory(attachmentFolder);

            foreach (var att in attachments)
            {
                var attachmentId = Guid.NewGuid().ToString();
                var filePath = Path.Combine(attachmentFolder, attachmentId);
                await using (var fileStream = File.Create(filePath))
                {
                    await att.CopyToAsync(fileStream, cancellationToken);
                }

                var fileInfo = new FileInfo(filePath);
                draft.Attachments.Add(new EmailAttachmentMeta
                {
                    Id = attachmentId,
                    FileName = att.FileName,
                    ContentType = att.ContentType,
                    Size = att.Size ?? (fileInfo.Exists ? fileInfo.Length : 0)
                });
                draft.HasAttachments = true;
            }
        }

        var textPreview = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(body, "<.*?>", " "));
        textPreview = (textPreview ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        draft.Snippet = textPreview.Length > 100 ? textPreview.Substring(0, 100) : textPreview;

        _emailMessages.Save(draft);

        return draft.Id;
    }

    public async Task RemoveDraftAttachmentAsync(string employeeId, string draftId, string attachmentId, CancellationToken cancellationToken = default)
    {
        var draft = _emailMessages.GetById(employeeId, draftId);
        if (draft == null || draft.EmployeeId != employeeId) return;

        var attachment = draft.Attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment != null)
        {
            draft.Attachments.Remove(attachment);
            draft.HasAttachments = draft.Attachments.Any();
            _emailMessages.Save(draft);

            try
            {
                var filePath = _cacheMaintenance.GetAttachmentPath(employeeId, draftId, attachmentId);
                if (File.Exists(filePath))
                {
                    await Task.Run(() => File.Delete(filePath), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to delete attachment file");
            }
        }
    }

    public async Task RepairLocalDraftsAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var draftsFolder = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.Path == "LocalDrafts");
        if (draftsFolder == null) return;

        var msgPath = Path.Combine(_cacheMaintenance.EmailBodyPath, employeeId, "Email", "Messages");
        if (!Directory.Exists(msgPath)) return;

        var files = await Task.Run(() => Directory.GetFiles(msgPath, "*.json"), cancellationToken);
        bool changed = false;

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var msg = JsonSerializer.Deserialize<EmailMessage>(json);
                if (msg != null && msg.FolderPath == "LocalDrafts" && msg.UniqueId == 0)
                {
                    msg.UniqueId = draftsFolder.NextUid++;
                    _emailMessages.Save(msg);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to repair draft {FilePath}", file);
            }
        }

        if (changed) _emailFolders.Save(draftsFolder);
    }
}
