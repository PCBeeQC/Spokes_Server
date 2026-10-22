using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailSyncService : IEmailSyncService
{
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EncryptionService _encryptionService;
    private readonly EmailSyncStateService _syncStateService;
    private readonly INotificationRoutingService _notificationRouting;
    private readonly IEmailDraftService _draftService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailSyncService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public EmailSyncService(
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        EmailSyncStateService syncStateService,
        INotificationRoutingService notificationRouting,
        IEmailDraftService draftService,
        IEmailCacheMaintenanceService cacheMaintenance,
        ILogger<EmailSyncService>? logger = null,
        IImapConnectionFactory? imapFactory = null)
    {
        _employees = employees;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _encryptionService = encryptionService;
        _syncStateService = syncStateService;
        _notificationRouting = notificationRouting;
        _draftService = draftService;
        _cacheMaintenance = cacheMaintenance;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, employees, encryptionService);
    }

    public async Task SyncEmployeeAsync(string employeeId, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return;

        _syncStateService.SetState(employeeId, "Starting Sync...");

        await _draftService.RepairLocalDraftsAsync(employeeId, cancellationToken);

        try
        {
            _syncStateService.SetState(employeeId, "Connecting to Server...");

            using var connectionResult = await _imapFactory.ConnectAsync(employee, cancellationToken);
            if (!connectionResult.IsSuccess)
            {
                var state = connectionResult.Status switch
                {
                    ImapConnectionStatus.MissingCredentials => "Missing Credentials",
                    ImapConnectionStatus.DecryptionError => "Decryption Error",
                    _ => "Server Not Configured"
                };
                _syncStateService.SetState(employeeId, state);
                return;
            }

            var client = connectionResult.Client!;

            _syncStateService.SetState(employeeId, "Fetching Folders...");
            var personalNamespaces = client.PersonalNamespaces;
            var folders = await client.GetFoldersAsync(personalNamespaces[0], false, cancellationToken);

            foreach (var folder in folders)
            {
                _syncStateService.SetState(employeeId, $"Syncing Folder: {folder.Name}...");
                await SyncFolderAsync(client, folder, employeeId, cancellationToken);
            }

            await client.DisconnectAsync(true, cancellationToken);
            _syncStateService.SetState(employeeId, "Idle");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Email sync failed for {Email}", employee.Email);
            _syncStateService.SetState(employeeId, $"Error connecting: {ex.Message}");
        }
    }

    public async Task SyncSingleFolderAsync(string employeeId, string folderPath, CancellationToken cancellationToken = default)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return;

        try
        {
            _syncStateService.SetState(employeeId, $"Connecting to sync {folderPath}...");

            using var connectionResult = await _imapFactory.ConnectAsync(employee, cancellationToken);
            if (!connectionResult.IsSuccess)
            {
                var state = connectionResult.Status switch
                {
                    ImapConnectionStatus.MissingCredentials => "Missing Credentials",
                    ImapConnectionStatus.DecryptionError => "Decryption Error",
                    _ => "Server Not Configured"
                };
                _syncStateService.SetState(employeeId, state);
                return;
            }

            var client = connectionResult.Client!;
            var folder = await client.GetFolderAsync(folderPath, cancellationToken);
            _syncStateService.SetState(employeeId, $"Syncing Folder: {folder.Name}...");
            await SyncFolderAsync(client, folder, employeeId, cancellationToken);

            await client.DisconnectAsync(true, cancellationToken);
            _syncStateService.SetState(employeeId, "Idle");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Single Folder Sync Failed for {FolderPath}", folderPath);
            _syncStateService.SetState(employeeId, $"Error syncing folder: {ex.Message}");
        }
    }

    private async Task SyncFolderAsync(ImapClient client, IMailFolder imapFolder, string employeeId, CancellationToken cancellationToken = default)
    {
        var existingFolders = _emailFolders.GetByEmployee(employeeId);
        var localFolder = existingFolders.FirstOrDefault(f => f.Path == imapFolder.FullName);

        if (localFolder == null)
        {
            localFolder = new EmailFolder
            {
                EmployeeId = employeeId,
                Name = imapFolder.Name,
                Path = imapFolder.FullName,
                ParentPath = GetParentPath(imapFolder),
                Delimiter = imapFolder.DirectorySeparator.ToString(),
                IsInbox = imapFolder.Attributes.HasFlag(FolderAttributes.Inbox),
                IsSent = imapFolder.Attributes.HasFlag(FolderAttributes.Sent),
                IsTrash = imapFolder.Attributes.HasFlag(FolderAttributes.Trash),
                IsDrafts = imapFolder.Attributes.HasFlag(FolderAttributes.Drafts),
                IsArchive = imapFolder.Attributes.HasFlag(FolderAttributes.Archive),
                IsJunk = imapFolder.Attributes.HasFlag(FolderAttributes.Junk)
            };
            if (imapFolder.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase)) localFolder.IsInbox = true;

            _emailFolders.Save(localFolder);
        }
        else
        {
            bool modified = false;
            var parentPath = GetParentPath(imapFolder);
            if (localFolder.ParentPath != parentPath) { localFolder.ParentPath = parentPath; modified = true; }
            if (localFolder.Delimiter != imapFolder.DirectorySeparator.ToString()) { localFolder.Delimiter = imapFolder.DirectorySeparator.ToString(); modified = true; }
            if (modified) _emailFolders.Save(localFolder);
        }

        await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var items = await imapFolder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope, cancellationToken);
        var itemsDescending = items.OrderByDescending(i => i.UniqueId).ToList();

        var serverUids = new HashSet<uint>(items.Select(i => i.UniqueId.Id));
        var localUids = _emailMessages.GetUidsByFolder(employeeId, localFolder.Path);
        var missingUids = localUids.Where(uid => !serverUids.Contains(uid)).ToList();

        foreach (var missingUid in missingUids)
        {
            var localId = _emailMessages.GetIdByUid(employeeId, localFolder.Path, missingUid);
            if (localId != null)
            {
                var msg = _emailMessages.GetByIdOrLoad(employeeId, localId);
                if (msg != null && !msg.IsLocalOnly)
                {
                    var otherCopies = _emailMessages.GetAllByGlobalMessageId(employeeId, msg.GlobalMessageId)
                        .Where(m => m.Id != msg.Id && m.FolderPath != localFolder.Path).ToList();

                    if (otherCopies.Any())
                    {
                        await _cacheMaintenance.DeleteLocalMessageFilesAsync(employeeId, localId);
                        _emailMessages.Delete(employeeId, localId);
                    }
                    else if (!localFolder.IsTrash)
                    {
                        MoveMessageToLocalTrash(employeeId, msg);
                    }
                }
            }
        }

        foreach (var item in itemsDescending)
        {
            var uid = item.UniqueId.Id;
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;

            if (isDeleted)
            {
                if (_emailMessages.Exists(employeeId, localFolder.Path, uid))
                {
                    var localId = _emailMessages.GetIdByUid(employeeId, localFolder.Path, uid);
                    if (localId != null)
                    {
                        var msg = _emailMessages.GetByIdOrLoad(employeeId, localId);
                        if (msg != null)
                        {
                            var otherCopies = _emailMessages.GetAllByGlobalMessageId(employeeId, msg.GlobalMessageId)
                                .Where(m => m.Id != msg.Id && m.FolderPath != localFolder.Path).ToList();

                            if (otherCopies.Any())
                            {
                                await _cacheMaintenance.DeleteLocalMessageFilesAsync(employeeId, localId);
                                _emailMessages.Delete(employeeId, localId);
                            }
                            else if (!localFolder.IsTrash)
                            {
                                MoveMessageToLocalTrash(employeeId, msg);
                            }
                        }
                    }
                }
                continue;
            }

            var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
            var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;

            _emailMessages.TryUpdateFlags(employeeId, localFolder.Path, uid, isRead, isFlagged);
        }

        int syncedCount = 0;

        foreach (var item in itemsDescending)
        {
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;
            if (isDeleted) continue;

            if (_emailMessages.Exists(employeeId, localFolder.Path, item.UniqueId.Id))
                continue;

            try
            {
                var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
                var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;
                var envelopeId = item.Envelope?.MessageId ?? string.Empty;

                var otherCopies = _emailMessages.GetAllByGlobalMessageId(employeeId, envelopeId);
                var trashCopies = otherCopies.Where(c =>
                {
                    var f = existingFolders.FirstOrDefault(folder => folder.Path == c.FolderPath);
                    return f != null && (f.IsTrash || f.Name.Contains("Trash", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("Deleted", StringComparison.OrdinalIgnoreCase));
                }).ToList();

                bool currentIsTrash = localFolder.IsTrash || localFolder.Name.Contains("Trash", StringComparison.OrdinalIgnoreCase) || localFolder.Name.Contains("Deleted", StringComparison.OrdinalIgnoreCase);

                if (trashCopies.Any())
                {
                    foreach (var tCopy in trashCopies)
                    {
                        if (!currentIsTrash)
                        {
                            await _cacheMaintenance.DeleteLocalMessageFilesAsync(employeeId, tCopy.Id);
                            _emailMessages.Delete(employeeId, tCopy.Id);
                        }
                        else if (tCopy.IsLocalOnly)
                        {
                            await _cacheMaintenance.DeleteLocalMessageFilesAsync(employeeId, tCopy.Id);
                            _emailMessages.Delete(employeeId, tCopy.Id);
                        }
                    }
                }

                var message = await imapFolder.GetMessageAsync(item.UniqueId, cancellationToken);
                var emailObj = await SaveMessageAsync(message, item.UniqueId, envelopeId, localFolder, employeeId, isRead, isFlagged, cancellationToken);
                if (emailObj != null)
                {
                    syncedCount++;

                    if (!isRead && localFolder.IsInbox)
                    {
                        _syncStateService.NotifyNewEmail(employeeId, emailObj);
                        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteEmailNotificationAsync(emailObj), "email notification");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error fetching message {UniqueId}", item.UniqueId);
            }
        }

        if (syncedCount > 0)
        {
            _syncStateService.NotifyUnreadCountChanged(employeeId);
        }

        localFolder.TotalCount = imapFolder.Count;
        localFolder.UnreadCount = _emailMessages.GetUnreadCount(employeeId, localFolder.Path);
        _emailFolders.Save(localFolder);
    }

    private async Task<EmailMessage?> SaveMessageAsync(MimeMessage mimeMessage, UniqueId uid, string envelopeMessageId, EmailFolder folder, string employeeId, bool isRead, bool isFlagged, CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString();
        var actualMessageId = !string.IsNullOrEmpty(envelopeMessageId) ? envelopeMessageId : (mimeMessage.MessageId ?? "");
        if (!_emailMessages.TryClaimUid(employeeId, folder.Path, uid.Id, messageId, actualMessageId))
        {
            var existingId = _emailMessages.GetIdByUid(employeeId, folder.Path, uid.Id);
            if (existingId != null)
            {
                var existing = _emailMessages.GetByIdOrLoad(employeeId, existingId);
                if (existing != null) return existing;
            }
            return null;
        }

        var email = new EmailMessage
        {
            Id = messageId,
            EmployeeId = employeeId,
            FolderPath = folder.Path,
            UniqueId = uid.Id,
            GlobalMessageId = actualMessageId,

            Subject = mimeMessage.Subject ?? "(No Subject)",
            FromAddress = mimeMessage.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            FromName = mimeMessage.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            Date = mimeMessage.Date,

            IsRead = isRead,
            IsFlagged = isFlagged
        };

        foreach (var to in mimeMessage.To.Mailboxes) email.ToAddresses.Add(to.Address);
        foreach (var cc in mimeMessage.Cc.Mailboxes) email.CcAddresses.Add(cc.Address);

        var textPreview = mimeMessage.TextBody;
        if (string.IsNullOrWhiteSpace(textPreview) && !string.IsNullOrWhiteSpace(mimeMessage.HtmlBody))
        {
            textPreview = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(mimeMessage.HtmlBody, "<.*?>", " "));
        }
        textPreview = (textPreview ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        email.Snippet = textPreview.Length > 100 ? textPreview.Substring(0, 100) : textPreview;

        var attachmentFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, employeeId, "Email", "Attachments", email.Id);
        var allAttachments = mimeMessage.Attachments.OfType<MimePart>().ToList();

        foreach (var bodyPart in mimeMessage.BodyParts.OfType<MimePart>())
        {
            if (bodyPart.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase))
            {
                if (!allAttachments.Contains(bodyPart))
                {
                    if (string.IsNullOrEmpty(bodyPart.FileName))
                    {
                        bodyPart.FileName = "invite.ics";
                    }
                    allAttachments.Add(bodyPart);
                }
            }
        }

        if (allAttachments.Any() && !Directory.Exists(attachmentFolder))
        {
            Directory.CreateDirectory(attachmentFolder);
        }

        foreach (var part in allAttachments)
        {
            long size = 0;
            var attachmentId = Guid.NewGuid().ToString();
            var filePath = Path.Combine(attachmentFolder, attachmentId);

            try
            {
                if (part.Content != null)
                {
                    using var stream = part.Content.Open();
                    using var fileStream = File.Create(filePath);
                    await stream.CopyToAsync(fileStream, cancellationToken);
                    size = fileStream.Length;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Attachment save failed: {AttachmentId}", attachmentId);
            }

            email.Attachments.Add(new EmailAttachmentMeta
            {
                Id = attachmentId,
                FileName = part.FileName ?? "unknown_file",
                ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                Size = size
            });
            email.HasAttachments = true;
        }

        var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, employeeId, "Email", "Mime");
        Directory.CreateDirectory(mimeFolder);
        var mimePath = Path.Combine(mimeFolder, $"{email.Id}.eml");
        await using var mimeStream = File.Create(mimePath);
        await mimeMessage.WriteToAsync(mimeStream, cancellationToken);

        _emailMessages.Save(email);
        return email;
    }

    private void MoveMessageToLocalTrash(string employeeId, EmailMessage message)
    {
        var folders = _emailFolders.GetByEmployee(employeeId);
        var trashFolder = folders.FirstOrDefault(f => f.IsTrash)
            ?? folders.FirstOrDefault(f => f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted", StringComparison.OrdinalIgnoreCase));

        if (trashFolder == null)
        {
            _logger?.LogWarning("Could not find local Trash folder to move missing message {MessageId}", message.Id);
            return;
        }

        var oldFolder = folders.FirstOrDefault(f => f.Path == message.FolderPath);
        if (oldFolder != null)
        {
            oldFolder.TotalCount = Math.Max(0, oldFolder.TotalCount - 1);
            if (!message.IsRead) oldFolder.UnreadCount = Math.Max(0, oldFolder.UnreadCount - 1);
            _emailFolders.Save(oldFolder);
        }

        _emailMessages.Delete(employeeId, message.Id);

        message.FolderPath = trashFolder.Path;
        message.IsLocalOnly = true;
        message.UniqueId = _emailMessages.GetNextLocalUid(employeeId, trashFolder.Path);

        _emailMessages.Save(message);

        trashFolder.TotalCount++;
        if (!message.IsRead) trashFolder.UnreadCount++;
        _emailFolders.Save(trashFolder);
    }

    private string GetParentPath(IMailFolder folder)
    {
        if (string.IsNullOrEmpty(folder.FullName)) return string.Empty;
        var sep = folder.DirectorySeparator.ToString();
        var lastIdx = folder.FullName.LastIndexOf(sep, StringComparison.Ordinal);
        return lastIdx > 0 ? folder.FullName.Substring(0, lastIdx) : string.Empty;
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation)
    {
        return Task.Run(async () =>
        {
            try
            {
                await routeAction();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to dispatch {Operation} in background", operation);
            }
        });
    }
}

