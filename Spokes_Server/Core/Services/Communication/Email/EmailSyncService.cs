using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using MimeKit;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailSyncService : IEmailSyncService
{
    private static readonly string[] ForwardingHeaderNames =
    [
        "Delivered-To",
        "X-Forwarded-To",
        "X-Forwarded-For",
        "X-Original-To",
        "Envelope-To",
        "X-Envelope-To",
        "Resent-To"
    ];

    private static readonly Regex EmailTokenRegex = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReceivedForRegex = new(
        @"\bfor\s+<?([a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,})>?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly EmailAccountRepository _accounts;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EmailSyncStateService _syncStateService;
    private readonly INotificationRoutingService _notificationRouting;
    private readonly IEmailDraftService _draftService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailSyncService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _accountSyncLocks = new();

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public EmailSyncService(
        EmailAccountRepository accounts,
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
        _accounts = accounts;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _syncStateService = syncStateService;
        _notificationRouting = notificationRouting;
        _draftService = draftService;
        _cacheMaintenance = cacheMaintenance;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts);
    }

    public Task SyncAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
        ExecuteWithAccountSyncLockAsync(
            accountId,
            "SyncAccountAsync",
            "Starting Sync...",
            "Connecting to Server...",
            useDetailedConnectionFailure: true,
            "Error connecting",
            $"Email sync failed for account {accountId}",
            beforeConnectAsync: async (acc, ct) => await _draftService.RepairLocalDraftsAsync(acc.Id, ct),
            syncAction: async (acc, client, isInitialSync, ct) =>
            {
                _syncStateService.SetState(acc.Id, "Fetching Folders...");
                var personalNamespaces = client.PersonalNamespaces;
                var folders = await client.GetFoldersAsync(personalNamespaces[0], false, ct);

                foreach (var folder in folders)
                {
                    try
                    {
                        _syncStateService.SetState(acc.Id, $"Syncing Folder: {folder.Name}...");
                        await SyncFolderAsync(folder, acc, isInitialSync, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to sync folder {FolderPath} for account {AccountId}", folder.FullName, acc.Id);
                    }
                }

                acc.LastSyncedAt = DateTime.UtcNow;
                _accounts.Save(acc);
            },
            cancellationToken);

    public Task SyncSingleFolderAsync(string accountId, string folderPath, CancellationToken cancellationToken = default) =>
        ExecuteWithAccountSyncLockAsync(
            accountId,
            $"SyncSingleFolderAsync (folder {folderPath})",
            initialState: null,
            $"Connecting to sync {folderPath}...",
            useDetailedConnectionFailure: false,
            "Error syncing folder",
            $"Single Folder Sync Failed for {folderPath}",
            beforeConnectAsync: null,
            syncAction: async (acc, client, isInitialSync, ct) =>
            {
                var folder = await client.GetFolderAsync(folderPath, ct);
                _syncStateService.SetState(acc.Id, $"Syncing Folder: {folder.Name}...");
                await SyncFolderAsync(folder, acc, isInitialSync, ct);

                if (!acc.LastSyncedAt.HasValue &&
                    (folder.Attributes.HasFlag(FolderAttributes.Inbox) || folder.Name.Equals("Inbox", StringComparison.OrdinalIgnoreCase)))
                {
                    acc.LastSyncedAt = DateTime.UtcNow;
                    _accounts.Save(acc);
                }
            },
            cancellationToken);

    private async Task ExecuteWithAccountSyncLockAsync(
        string accountId,
        string lockOperationName,
        string? initialState,
        string connectingState,
        bool useDetailedConnectionFailure,
        string errorStatePrefix,
        string errorLogContext,
        Func<EmailAccount, CancellationToken, Task>? beforeConnectAsync,
        Func<EmailAccount, ImapClient, bool, CancellationToken, Task> syncAction,
        CancellationToken cancellationToken)
    {
        if (string.Equals(_companyProfile.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
            return;

        var account = _accounts.GetById(accountId);
        if (account == null) return;

        var syncLock = _accountSyncLocks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        if (!await syncLock.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken))
        {
            _logger?.LogWarning("{Operation} skipped for account {AccountId} due to lock timeout", lockOperationName, accountId);
            return;
        }

        try
        {
            bool isInitialAccountSync = !account.LastSyncedAt.HasValue;

            if (initialState != null)
            {
                _syncStateService.SetState(accountId, initialState);
            }

            if (beforeConnectAsync != null)
            {
                await beforeConnectAsync(account, cancellationToken);
            }

            try
            {
                _syncStateService.SetState(accountId, connectingState);

                using var connectionResult = await _imapFactory.ConnectAsync(account, cancellationToken);
                if (!connectionResult.IsSuccess)
                {
                    var state = connectionResult.Status switch
                    {
                        ImapConnectionStatus.MissingCredentials => "Missing Credentials",
                        ImapConnectionStatus.DecryptionError => "Decryption Error",
                        ImapConnectionStatus.ServerNotConfigured => "Server Not Configured",
                        _ => useDetailedConnectionFailure
                            ? $"Connection Failed: {connectionResult.ErrorMessage}"
                            : "Server Not Configured"
                    };
                    _syncStateService.SetState(accountId, state);
                    return;
                }

                var client = connectionResult.Client!;
                await syncAction(account, client, isInitialAccountSync, cancellationToken);

                await client.DisconnectAsync(true, cancellationToken);
                _syncStateService.SetState(accountId, "Idle");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "{ErrorContext} ({Email})", errorLogContext, account.EmailAddress);
                _syncStateService.SetState(accountId, $"{errorStatePrefix}: {ex.Message}");
            }
        }
        finally
        {
            syncLock.Release();
        }
    }

    internal async Task SyncFolderAsync(IMailFolder imapFolder, EmailAccount account, bool isInitialAccountSync, CancellationToken cancellationToken = default)
    {
        var accountId = account.Id;
        var existingFolders = _emailFolders.GetByAccount(accountId);
        var localFolder = existingFolders.FirstOrDefault(f => f.Path == imapFolder.FullName);
        bool hadExistingLocalFolder = localFolder != null;

        var parentPath = EmailFolderService.GetParentPath(imapFolder);
        bool isRootInbox = string.IsNullOrEmpty(parentPath) &&
                           (imapFolder.Attributes.HasFlag(FolderAttributes.Inbox) ||
                            imapFolder.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase));

        if (localFolder == null)
        {
            localFolder = new EmailFolder
            {
                AccountId = accountId,
                Name = imapFolder.Name,
                Path = imapFolder.FullName,
                ParentPath = parentPath,
                Delimiter = imapFolder.DirectorySeparator.ToString(),
                IsSelectable = !imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect),
                IsInbox = isRootInbox,
                IsSent = imapFolder.Attributes.HasFlag(FolderAttributes.Sent),
                IsTrash = imapFolder.Attributes.HasFlag(FolderAttributes.Trash),
                IsDrafts = imapFolder.Attributes.HasFlag(FolderAttributes.Drafts),
                IsArchive = imapFolder.Attributes.HasFlag(FolderAttributes.Archive),
                IsJunk = imapFolder.Attributes.HasFlag(FolderAttributes.Junk)
            };

            _emailFolders.Save(localFolder);
        }
        else
        {
            bool modified = false;
            if (localFolder.ParentPath != parentPath) { localFolder.ParentPath = parentPath; modified = true; }
            if (localFolder.Delimiter != imapFolder.DirectorySeparator.ToString()) { localFolder.Delimiter = imapFolder.DirectorySeparator.ToString(); modified = true; }
            bool isSelectable = !imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect);
            if (localFolder.IsSelectable != isSelectable) { localFolder.IsSelectable = isSelectable; modified = true; }
            if (localFolder.IsInbox != isRootInbox) { localFolder.IsInbox = isRootInbox; modified = true; }
            if (modified) _emailFolders.Save(localFolder);
        }

        if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
        {
            // Non-selectable container folder (such as [Gmail]); contains no messages and cannot be opened.
            return;
        }

        await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var items = await imapFolder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope, cancellationToken);
        var itemsDescending = items.OrderByDescending(i => i.UniqueId).ToList();

        var serverUids = new HashSet<uint>(items.Select(i => i.UniqueId.Id));
        var localUids = _emailMessages.GetUidsByFolder(accountId, localFolder.Path);
        bool isInitialFolderSync = !hadExistingLocalFolder && localUids.Count == 0;
        var missingUids = localUids.Where(uid => !serverUids.Contains(uid)).ToList();
        bool flagsOrDeletionsChanged = false;

        foreach (var missingUid in missingUids)
        {
            var localId = _emailMessages.GetIdByUid(accountId, localFolder.Path, missingUid);
            if (localId != null && await HandleRemovedServerMessageAsync(accountId, localId, localFolder, requireNonLocalOnly: true))
            {
                flagsOrDeletionsChanged = true;
            }
        }

        foreach (var item in itemsDescending)
        {
            var uid = item.UniqueId.Id;
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;

            if (isDeleted)
            {
                if (_emailMessages.Exists(accountId, localFolder.Path, uid))
                {
                    var localId = _emailMessages.GetIdByUid(accountId, localFolder.Path, uid);
                    if (localId != null && await HandleRemovedServerMessageAsync(accountId, localId, localFolder, requireNonLocalOnly: false))
                    {
                        flagsOrDeletionsChanged = true;
                    }
                }
                continue;
            }

            var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
            var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;

            if (_emailMessages.TryUpdateFlags(accountId, localFolder.Path, uid, isRead, isFlagged))
            {
                flagsOrDeletionsChanged = true;
            }
        }

        int syncedCount = 0;

        foreach (var item in itemsDescending)
        {
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;
            if (isDeleted) continue;

            if (_emailMessages.Exists(accountId, localFolder.Path, item.UniqueId.Id))
                continue;

            try
            {
                var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
                var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;
                var envelopeId = item.Envelope?.MessageId ?? string.Empty;

                var otherCopies = _emailMessages.GetAllByGlobalMessageId(accountId, envelopeId);
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
                            await _cacheMaintenance.DeleteLocalMessageFilesAsync(accountId, tCopy.Id);
                            _emailMessages.Delete(accountId, tCopy.Id);
                        }
                        else if (tCopy.IsLocalOnly)
                        {
                            await _cacheMaintenance.DeleteLocalMessageFilesAsync(accountId, tCopy.Id);
                            _emailMessages.Delete(accountId, tCopy.Id);
                        }
                    }
                }

                var message = await imapFolder.GetMessageAsync(item.UniqueId, cancellationToken);
                var emailObj = await SaveMessageAsync(message, item.UniqueId, envelopeId, localFolder, accountId, isRead, isFlagged, cancellationToken);
                if (emailObj != null)
                {
                    syncedCount++;

                    bool isRecentEnoughToNotify = !account.LastSyncedAt.HasValue ||
                                                  emailObj.Date >= account.LastSyncedAt.Value.AddMinutes(-15);

                    if (!isInitialAccountSync && !isInitialFolderSync && !isRead && localFolder.IsInbox && isRecentEnoughToNotify)
                    {
                        _syncStateService.NotifyNewEmail(accountId, emailObj);
                        _ = SafeDispatchNotificationAsync(() => _notificationRouting.RouteEmailNotificationAsync(emailObj), "email notification");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error fetching message {UniqueId}", item.UniqueId);
            }
        }

        int previousUnreadCount = localFolder.UnreadCount;
        localFolder.TotalCount = imapFolder.Count;
        localFolder.UnreadCount = _emailMessages.GetUnreadCount(accountId, localFolder.Path);
        _emailFolders.Save(localFolder);

        if (syncedCount > 0 || flagsOrDeletionsChanged || localFolder.UnreadCount != previousUnreadCount)
        {
            _syncStateService.NotifyUnreadCountChanged(accountId);
        }
    }

    public static List<string> ExtractReceivingAddresses(MimeMessage mimeMessage)
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var to in mimeMessage.To.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(to.Address))
                addresses.Add(to.Address.Trim().ToLowerInvariant());
        }

        foreach (var cc in mimeMessage.Cc.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(cc.Address))
                addresses.Add(cc.Address.Trim().ToLowerInvariant());
        }

        if (mimeMessage.Headers != null)
        {
            foreach (var headerName in ForwardingHeaderNames)
            {
                foreach (var header in mimeMessage.Headers.Where(h => string.Equals(h.Field, headerName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.IsNullOrWhiteSpace(header.Value)) continue;

                    if (InternetAddressList.TryParse(header.Value, out var parsedList))
                    {
                        foreach (var mb in parsedList.Mailboxes)
                        {
                            if (!string.IsNullOrWhiteSpace(mb.Address))
                                addresses.Add(mb.Address.Trim().ToLowerInvariant());
                        }
                    }

                    foreach (Match match in EmailTokenRegex.Matches(header.Value))
                    {
                        addresses.Add(match.Value.Trim().ToLowerInvariant());
                    }
                }
            }

            foreach (var header in mimeMessage.Headers.Where(h => string.Equals(h.Field, "Received", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(header.Value)) continue;

                foreach (Match match in ReceivedForRegex.Matches(header.Value))
                {
                    if (match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                    {
                        addresses.Add(match.Groups[1].Value.Trim().ToLowerInvariant());
                    }
                }
            }
        }

        return addresses.OrderBy(a => a).ToList();
    }

    public static (string? UnsubscribeUrl, string? UnsubscribeMailto) ExtractUnsubscribeInfo(MimeMessage mimeMessage)
    {
        string? url = null;
        string? mailto = null;

        var raw = mimeMessage.Headers?["List-Unsubscribe"];
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);

        var bracketMatches = Regex.Matches(raw, @"<([^>]+)>");
        var candidates = bracketMatches.Count > 0
            ? bracketMatches.Select(m => m.Groups[1].Value.Trim()).ToList()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        foreach (var candidate in candidates)
        {
            if (url == null && (candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                url = candidate;
            }
            else if (mailto == null && candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                mailto = candidate;
            }
        }

        return (url, mailto);
    }

    private async Task<EmailMessage?> SaveMessageAsync(MimeMessage mimeMessage, UniqueId uid, string envelopeMessageId, EmailFolder folder, string accountId, bool isRead, bool isFlagged, CancellationToken cancellationToken = default)
    {
        var messageId = Guid.NewGuid().ToString();
        var actualMessageId = !string.IsNullOrEmpty(envelopeMessageId) ? envelopeMessageId : (mimeMessage.MessageId ?? "");
        if (!_emailMessages.TryClaimUid(accountId, folder.Path, uid.Id, messageId, actualMessageId))
        {
            var existingId = _emailMessages.GetIdByUid(accountId, folder.Path, uid.Id);
            if (existingId != null)
            {
                var existing = _emailMessages.GetById(accountId, existingId);
                if (existing != null) return existing;
            }
            return null;
        }

        var (unsubUrl, unsubMailto) = ExtractUnsubscribeInfo(mimeMessage);

        var email = new EmailMessage
        {
            Id = messageId,
            AccountId = accountId,
            FolderPath = folder.Path,
            UniqueId = uid.Id,
            GlobalMessageId = actualMessageId,

            Subject = mimeMessage.Subject ?? "(No Subject)",
            FromAddress = mimeMessage.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            FromName = mimeMessage.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            Date = mimeMessage.Date,

            InReplyTo = mimeMessage.InReplyTo,
            References = mimeMessage.References?.ToList() ?? [],
            ReplyToAddresses = mimeMessage.ReplyTo.Mailboxes.Select(m => m.Address).Where(a => !string.IsNullOrWhiteSpace(a)).ToList(),
            ReceivingAddresses = ExtractReceivingAddresses(mimeMessage),
            UnsubscribeUrl = unsubUrl,
            UnsubscribeMailto = unsubMailto,

            IsRead = isRead,
            IsFlagged = isFlagged
        };

        foreach (var to in mimeMessage.To.Mailboxes) email.ToAddresses.Add(to.Address);
        foreach (var cc in mimeMessage.Cc.Mailboxes) email.CcAddresses.Add(cc.Address);

        var textPreview = mimeMessage.TextBody;
        if (string.IsNullOrWhiteSpace(textPreview) && !string.IsNullOrWhiteSpace(mimeMessage.HtmlBody))
        {
            textPreview = System.Net.WebUtility.HtmlDecode(Regex.Replace(mimeMessage.HtmlBody, "<.*?>", " "));
        }
        textPreview = (textPreview ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        email.Snippet = textPreview.Length > 100 ? textPreview[..100] : textPreview;

        var attachmentFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Attachments", email.Id);
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

        var mimeFolder = Path.Combine(_cacheMaintenance.EmailBodyPath, accountId, "Mime");
        Directory.CreateDirectory(mimeFolder);
        var mimePath = Path.Combine(mimeFolder, $"{email.Id}.eml");
        await using var mimeStream = File.Create(mimePath);
        await mimeMessage.WriteToAsync(mimeStream, cancellationToken);

        _emailMessages.Save(email);
        return email;
    }

    private void MoveMessageToLocalTrash(string accountId, EmailMessage message)
    {
        var folders = _emailFolders.GetByAccount(accountId);
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

        _emailMessages.Delete(accountId, message.Id);

        message.FolderPath = trashFolder.Path;
        message.IsLocalOnly = true;
        message.UniqueId = _emailMessages.GetNextLocalUid(accountId, trashFolder.Path);

        _emailMessages.Save(message);

        trashFolder.TotalCount++;
        if (!message.IsRead) trashFolder.UnreadCount++;
        _emailFolders.Save(trashFolder);
    }

    private async Task<bool> HandleRemovedServerMessageAsync(string accountId, string localId, EmailFolder localFolder, bool requireNonLocalOnly)
    {
        var msg = _emailMessages.GetById(accountId, localId);
        if (msg == null || (requireNonLocalOnly && msg.IsLocalOnly))
            return false;

        var otherCopies = _emailMessages.GetAllByGlobalMessageId(accountId, msg.GlobalMessageId)
            .Where(m => m.Id != msg.Id && m.FolderPath != localFolder.Path).ToList();

        if (otherCopies.Any())
        {
            await _cacheMaintenance.DeleteLocalMessageFilesAsync(accountId, localId);
            _emailMessages.Delete(accountId, localId);
            return true;
        }

        if (!localFolder.IsTrash)
        {
            MoveMessageToLocalTrash(accountId, msg);
            return true;
        }

        return false;
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation) =>
        NotificationDispatchHelper.SafeDispatchAsync(routeAction, operation, _logger);
}
