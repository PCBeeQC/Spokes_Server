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
    private readonly EmailAccountRepository? _accounts;
    private readonly EmailSendingIdentityRepository? _identities;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailDraftService>? _logger;
    private readonly EmployeeRepository? _employees;

    public EmailDraftService(
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        IEmailCacheMaintenanceService cacheMaintenance,
        EmailAccountRepository? accounts = null,
        EmailSendingIdentityRepository? identities = null,
        ILogger<EmailDraftService>? logger = null,
        EmployeeRepository? employees = null)
    {
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _cacheMaintenance = cacheMaintenance;
        _accounts = accounts;
        _identities = identities;
        _logger = logger;
        _employees = employees;
    }

    private void ValidateActiveNonSuspendedEmployee(string? employeeId)
    {
        if (_employees == null) return;

        if (string.IsNullOrWhiteSpace(employeeId))
        {
            throw new UnauthorizedAccessException("An authenticated employee is required for draft operations.");
        }

        var employee = _employees.GetById(employeeId);
        if (employee == null || !employee.IsActive || employee.IsBanned || employee.IsSuspended)
        {
            throw new UnauthorizedAccessException("Suspended or unauthorized users cannot create, modify, or delete email drafts.");
        }
    }

    private void EnsureDraftCreatorOwnership(EmailMessage draft, string? employeeId)
    {
        if (!string.IsNullOrWhiteSpace(draft.CreatedByEmployeeId))
        {
            if (string.IsNullOrWhiteSpace(employeeId) ||
                !string.Equals(draft.CreatedByEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("You cannot modify or delete another user's draft.");
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(employeeId))
        {
            var account = _accounts?.GetById(draft.AccountId);
            bool isLegacyPersonalOwner = account != null
                ? (!account.IsServerManaged &&
                   !string.IsNullOrWhiteSpace(account.OwnerEmployeeId) &&
                   string.Equals(account.OwnerEmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
                : string.Equals(draft.AccountId, employeeId, StringComparison.OrdinalIgnoreCase);

            if (!isLegacyPersonalOwner)
            {
                throw new UnauthorizedAccessException("You cannot modify or delete another user's draft.");
            }
        }
    }

    public async Task<string> SaveDraftAsync(SaveDraftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var accountId = request.AccountId;
        if (string.IsNullOrWhiteSpace(accountId)) return "";

        ValidateActiveNonSuspendedEmployee(request.EmployeeId);

        var to = request.To ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var body = request.Body ?? string.Empty;
        var attachments = request.Attachments;
        var cc = request.Cc;
        var bcc = request.Bcc;
        var existingDraftId = request.ExistingDraftId;

        string senderEmail = "draft@spokes";
        string senderName = "Draft";

        if (!string.IsNullOrWhiteSpace(request.SendingIdentityId) && _identities != null)
        {
            var identity = _identities.GetById(request.SendingIdentityId);
            if (identity != null)
            {
                senderEmail = identity.EmailAddress;
                senderName = !string.IsNullOrWhiteSpace(identity.DisplayName) ? identity.DisplayName : identity.EmailAddress;
            }
        }
        else if (_accounts != null)
        {
            var account = _accounts.GetById(accountId);
            if (account != null)
            {
                senderEmail = account.EmailAddress;
                senderName = !string.IsNullOrWhiteSpace(account.Name) ? account.Name : account.EmailAddress;
            }
        }

        var folders = _emailFolders.GetByAccount(accountId);
        var drafts = folders.FirstOrDefault(f => f.Path == "LocalDrafts");

        if (drafts == null)
        {
            drafts = new EmailFolder
            {
                AccountId = accountId,
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
            draft = _emailMessages.GetById(accountId, existingDraftId);
            if (draft != null && !string.Equals(draft.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            {
                draft = null;
            }

            if (draft != null)
            {
                if (!string.Equals(draft.FolderPath, "LocalDrafts", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Target message is not a local draft.");
                }

                EnsureDraftCreatorOwnership(draft, request.EmployeeId);
            }
        }

        if (draft == null)
        {
            draft = new EmailMessage
            {
                AccountId = accountId,
                FolderPath = drafts.Path,
                UniqueId = drafts.NextUid++,
                IsRead = true,
                IsFlagged = false,
                IsLocalOnly = true,
                CreatedByEmployeeId = !string.IsNullOrWhiteSpace(request.EmployeeId) ? request.EmployeeId : null
            };

            drafts.TotalCount++;
            _emailFolders.Save(drafts);
        }
        else if (draft.UniqueId == 0)
        {
            draft.UniqueId = drafts.NextUid++;
            _emailFolders.Save(drafts);
        }

        if (string.IsNullOrWhiteSpace(draft.CreatedByEmployeeId) && !string.IsNullOrWhiteSpace(request.EmployeeId))
        {
            draft.CreatedByEmployeeId = request.EmployeeId;
        }

        draft.IsLocalOnly = true;
        draft.Subject = subject;
        draft.FromAddress = senderEmail;
        draft.FromName = senderName;
        draft.SendingIdentityId = request.SendingIdentityId;
        draft.InReplyTo = request.InReplyTo;
        draft.References = request.References ?? [];
        draft.ToAddresses = to.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        draft.CcAddresses = cc?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? [];
        draft.BccAddresses = bcc?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? [];
        draft.ReceivingAddresses = draft.ToAddresses.Concat(draft.CcAddresses).Select(a => a.ToLowerInvariant()).Distinct().ToList();
        draft.Date = DateTimeOffset.Now;
        draft.FolderPath = drafts.Path;

        var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Mime");
        Directory.CreateDirectory(mimeFolder);
        var draftMimePath = Path.Combine(mimeFolder, $"{draft.Id}.eml");

        var draftMime = new MimeMessage();
        if (!string.IsNullOrWhiteSpace(senderEmail) && senderEmail.Contains('@'))
        {
            draftMime.From.Add(new MailboxAddress(senderName, senderEmail));
        }
        if (!string.IsNullOrWhiteSpace(to))
            draftMime.To.AddRange(InternetAddressList.Parse(to));
        if (!string.IsNullOrWhiteSpace(cc))
            draftMime.Cc.AddRange(InternetAddressList.Parse(cc));
        if (!string.IsNullOrWhiteSpace(bcc))
            draftMime.Bcc.AddRange(InternetAddressList.Parse(bcc));
        if (!string.IsNullOrWhiteSpace(request.InReplyTo))
            draftMime.InReplyTo = request.InReplyTo;
        if (request.References != null)
        {
            foreach (var r in request.References.Where(x => !string.IsNullOrWhiteSpace(x)))
                draftMime.References.Add(r);
        }
        draftMime.Subject = subject;
        draftMime.Body = new TextPart(TextFormat.Html) { Text = body };
        await using var draftMimeStream = File.Create(draftMimePath);
        await draftMime.WriteToAsync(draftMimeStream, cancellationToken);

        if (attachments != null)
        {
            var attachmentFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Attachments", draft.Id);
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
        draft.Snippet = textPreview.Length > 100 ? textPreview[..100] : textPreview;

        _emailMessages.Save(draft);

        return draft.Id;
    }

    public Task RemoveDraftAttachmentAsync(
        string accountId,
        string draftId,
        string attachmentId,
        CancellationToken cancellationToken = default) =>
        RemoveDraftAttachmentAsync(accountId, draftId, attachmentId, employeeId: null, cancellationToken);

    public async Task RemoveDraftAttachmentAsync(
        string accountId,
        string draftId,
        string attachmentId,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(employeeId) || _employees != null)
        {
            ValidateActiveNonSuspendedEmployee(employeeId);
        }

        var draft = _emailMessages.GetById(accountId, draftId);
        if (draft == null || !string.Equals(draft.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) return;

        if (!string.Equals(draft.FolderPath, "LocalDrafts", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Target message is not a local draft.");
        }

        if (!string.IsNullOrWhiteSpace(employeeId) || _employees != null)
        {
            EnsureDraftCreatorOwnership(draft, employeeId);
        }

        var attachment = draft.Attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment != null)
        {
            draft.Attachments.Remove(attachment);
            draft.HasAttachments = draft.Attachments.Any();
            _emailMessages.Save(draft);

            try
            {
                var filePath = _cacheMaintenance.GetAttachmentPath(accountId, draftId, attachmentId);
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

    public async Task DeleteDraftAsync(
        string accountId,
        string draftId,
        string? employeeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(draftId))
            return;

        ValidateActiveNonSuspendedEmployee(employeeId);

        var draft = _emailMessages.GetById(accountId, draftId) ?? _emailMessages.GetById(draftId);
        if (draft == null || !string.Equals(draft.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!string.Equals(draft.FolderPath, "LocalDrafts", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cannot delete a non-draft message via DeleteDraftAsync.");
        }

        EnsureDraftCreatorOwnership(draft, employeeId);

        _emailMessages.Delete(accountId, draft.Id);

        var folder = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == "LocalDrafts");
        if (folder != null)
        {
            folder.TotalCount = Math.Max(0, folder.TotalCount - 1);
            if (!draft.IsRead) folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
            _emailFolders.Save(folder);
        }

        await _cacheMaintenance.DeleteLocalMessageFilesAsync(accountId, draft.Id);
    }

    public async Task RepairLocalDraftsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var draftsFolder = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == "LocalDrafts");
        if (draftsFolder == null) return;

        var msgPath = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Messages");
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
