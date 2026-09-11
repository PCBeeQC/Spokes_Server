using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Microsoft.AspNetCore.Components.Forms; // For IBrowserFile

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailService
{
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;

    private readonly EncryptionService _encryptionService;
    private readonly IConfiguration _config;
    private readonly EmailSyncStateService _syncStateService;
    private readonly PresenceStateService _presenceState;
    private readonly NotificationRoutingService _notificationRouting;
    private readonly string _emailBodyPath;

    public EmailService(
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        IConfiguration config,
        EmailSyncStateService syncStateService,
        PresenceStateService presenceState,
        NotificationRoutingService notificationRouting)
    {
        _employees = employees;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _encryptionService = encryptionService;
        _config = config;
        _syncStateService = syncStateService;
        _presenceState = presenceState;
        _notificationRouting = notificationRouting;

        var dataPath = (_config != null ? _config["DataPath"] : "Data") ?? "Data";
        _emailBodyPath = Path.Combine(dataPath, "Employees");
        // We don't create _emailBodyPath here because it's a parent of dynamic employee folders

        // Ensure Windows-1252 codepage is available (not included in .NET Core/5+ by default)
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public virtual async Task SyncEmployeeAsync(string employeeId)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return;

        _syncStateService.SetState(employeeId, "Starting Sync...");

        await RepairLocalDraftsAsync(employeeId);

        // Check credentials
        var username = employee.Email;
        var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            _syncStateService.SetState(employeeId, "Missing Credentials");
            return;
        }

        // Check Server Settings
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.EmailSettings.ImapHost))
        {
            _syncStateService.SetState(employeeId, "Server Not Configured");
            return;
        }

        var settings = profile.EmailSettings;

        try
        {
            using var client = new ImapClient();

            // Accept all certs if needed (dev mode), otherwise use default validation
            // client.ServerCertificateValidationCallback = (s,c,h,e) => true; 

            _syncStateService.SetState(employeeId, "Connecting to Server...");

            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(username, password);

            // 1. Sync Folders
            _syncStateService.SetState(employeeId, "Fetching Folders...");
            var personalNamespaces = client.PersonalNamespaces;
            var folders = await client.GetFoldersAsync(personalNamespaces[0]);

            foreach (var folder in folders)
            {
                _syncStateService.SetState(employeeId, $"Syncing Folder: {folder.Name}...");
                await SyncFolderAsync(client, folder, employeeId);
            }

            await client.DisconnectAsync(true);
            _syncStateService.SetState(employeeId, "Idle");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailSync] Failed for {employee.Email}: {ex.Message}");
            _syncStateService.SetState(employeeId, $"Error connecting: {ex.Message}");
            // Log error to some system log?
        }
    }

    public virtual async Task SyncSingleFolderAsync(string employeeId, string folderPath)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return;

        var username = employee.Email;
        var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
        var settings = _companyProfile.Get()?.EmailSettings;

        if (settings == null || string.IsNullOrEmpty(username)) return;

        try
        {
            using var client = new ImapClient();
            _syncStateService.SetState(employeeId, $"Connecting to sync {folderPath}...");
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(username, password);

            var folder = await client.GetFolderAsync(folderPath);
            _syncStateService.SetState(employeeId, $"Syncing Folder: {folder.Name}...");
            await SyncFolderAsync(client, folder, employeeId);

            await client.DisconnectAsync(true);
            _syncStateService.SetState(employeeId, "Idle");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailSync] Single Folder Sync Failed: {ex.Message}");
            _syncStateService.SetState(employeeId, $"Error syncing folder: {ex.Message}");
        }
    }

    private async Task SyncFolderAsync(ImapClient client, IMailFolder imapFolder, string employeeId)
    {
        // 1. Update/Create Local Folder
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
            // Special case for generic "Inbox" name matching if attributes missing
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

        // 2. Open Folder
        await imapFolder.OpenAsync(FolderAccess.ReadWrite); // Open ReadWrite to allow setting flags

        // 3. Sync Messages (New & Flags)
        // Strategy: Get All MessageSummaries (UID + Flags + Envelope) for the folder.
        var items = await imapFolder.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.Envelope);

        // We want to process from Newest to Oldest
        var itemsDescending = items.OrderByDescending(i => i.UniqueId).ToList();

        // PART 0: HANDLE MISSING / MOVED / DELETED MESSAGES
        var serverUids = new HashSet<uint>(items.Select(i => i.UniqueId.Id));
        var localUids = _emailMessages.GetUidsByFolder(employeeId, localFolder.Path);
        var missingUids = localUids.Where(uid => !serverUids.Contains(uid)).ToList();

        foreach (var missingUid in missingUids)
        {
            var localId = _emailMessages.GetIdByUid(employeeId, localFolder.Path, missingUid);
            if (localId != null)
            {
                var msg = _emailMessages.GetByIdOrLoad(localId, employeeId);
                if (msg != null && msg.UniqueId < (uint.MaxValue - 1000000))
                {
                    var otherCopies = _emailMessages.GetAllByGlobalMessageId(employeeId, msg.GlobalMessageId)
                        .Where(m => m.Id != msg.Id && m.FolderPath != localFolder.Path).ToList();

                    if (otherCopies.Any())
                    {
                        await DeleteLocalMessageFilesAsync(localId, employeeId);
                        _emailMessages.Delete(localId, employeeId);
                    }
                    else if (!localFolder.IsTrash)
                    {
                        await MoveMessageToLocalTrashAsync(msg, employeeId);
                    }
                }
            }
        }

        // PART A: SYNC FLAGS (Fast, process ALL)
        // Optimization: Use efficient TryUpdateFlags which checks RAM index first.
        foreach (var item in itemsDescending)
        {
            var uid = item.UniqueId.Id;
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;

            if (isDeleted)
            {
                // If it's deleted on server, we MUST remove it locally so it doesn't linger 
                // (e.g., if we missed the DeleteMessageAsync call or another client deleted it)
                if (_emailMessages.Exists(employeeId, localFolder.Path, uid))
                {
                    var localId = _emailMessages.GetIdByUid(employeeId, localFolder.Path, uid);
                    if (localId != null)
                    {
                        var msg = _emailMessages.GetByIdOrLoad(localId, employeeId);
                        if (msg != null)
                        {
                            var otherCopies = _emailMessages.GetAllByGlobalMessageId(employeeId, msg.GlobalMessageId)
                                .Where(m => m.Id != msg.Id && m.FolderPath != localFolder.Path).ToList();

                            if (otherCopies.Any())
                            {
                                await DeleteLocalMessageFilesAsync(localId, employeeId);
                                _emailMessages.Delete(localId, employeeId);
                            }
                            else if (!localFolder.IsTrash)
                            {
                                await MoveMessageToLocalTrashAsync(msg, employeeId);
                            }
                        }
                    }
                }
                continue; // Skip flag update since we just processed it
            }

            var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
            var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;

            // This will only load the message from disk IF the flags have changed.
            _emailMessages.TryUpdateFlags(employeeId, localFolder.Path, uid, isRead, isFlagged);
        }

        // PART B: DOWNLOAD NEW MESSAGES
        int syncedCount = 0;

        foreach (var item in itemsDescending)
        {
            var isDeleted = item.Flags?.HasFlag(MessageFlags.Deleted) ?? false;
            if (isDeleted) continue; // Do not download messages marked as deleted

            if (_emailMessages.Exists(employeeId, localFolder.Path, item.UniqueId.Id))
                continue;

            try
            {
                var isRead = item.Flags?.HasFlag(MessageFlags.Seen) ?? false;
                var isFlagged = item.Flags?.HasFlag(MessageFlags.Flagged) ?? false;
                var envelopeId = item.Envelope?.MessageId ?? string.Empty;

                // DEDUPLICATION: Check if this exists in the local Trash folder
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
                            // Moving out of Trash into a normal folder. Purge trash copies.
                            await DeleteLocalMessageFilesAsync(tCopy.Id, employeeId);
                            _emailMessages.Delete(tCopy.Id, employeeId);
                        }
                        else if (tCopy.UniqueId >= (uint.MaxValue - 1000000))
                        {
                            // Syncing Trash folder, and found a dummy copy. Purge it to make way for the real server copy.
                            await DeleteLocalMessageFilesAsync(tCopy.Id, employeeId);
                            _emailMessages.Delete(tCopy.Id, employeeId);
                        }
                    }
                }

                var message = await imapFolder.GetMessageAsync(item.UniqueId);
                var emailObj = await SaveMessageAsync(message, item.UniqueId, envelopeId, localFolder, employeeId, isRead, isFlagged);
                syncedCount++;

                if (!isRead && localFolder.IsInbox && emailObj != null)
                {
                    _syncStateService.NotifyNewEmail(employeeId, emailObj);

                    // Defer push notification decisions to the central router
                    _ = _notificationRouting.RouteEmailNotificationAsync(emailObj);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailSync] Error fetching message {item.UniqueId}: {ex.Message}");
            }
        }

        if (syncedCount > 0)
        {
            _syncStateService.NotifyUnreadCountChanged(employeeId);
        }

        // Update folder counts AFTER all sync operations
        // UnreadCount from local index (single source of truth), TotalCount from server
        localFolder.TotalCount = imapFolder.Count;
        localFolder.UnreadCount = _emailMessages.GetUnreadCount(employeeId, localFolder.Path);
        _emailFolders.Save(localFolder);
    }

    private async Task<EmailMessage> SaveMessageAsync(MimeMessage mimeMessage, UniqueId uid, string envelopeMessageId, EmailFolder folder, string employeeId, bool isRead, bool isFlagged)
    {
        // Generate ID first so we can use it for atomic claim
        var messageId = Guid.NewGuid().ToString();

        // ATOMIC DEDUP: TryClaimUid uses ConcurrentDictionary.TryAdd — only one thread wins
        var actualMessageId = !string.IsNullOrEmpty(envelopeMessageId) ? envelopeMessageId : (mimeMessage.MessageId ?? "");
        if (!_emailMessages.TryClaimUid(employeeId, folder.Path, uid.Id, messageId, actualMessageId))
        {
            // Another thread already claimed this UID — return their message
            var existingId = _emailMessages.GetIdByUid(employeeId, folder.Path, uid.Id);
            if (existingId != null)
            {
                var existing = _emailMessages.GetByIdOrLoad(existingId, employeeId);
                if (existing != null) return existing;
            }
            // Fallback: claim was made but message not saved yet (very unlikely). Skip.
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

        // Snippet (Body Preview)
        var textPreview = mimeMessage.TextBody;
        if (string.IsNullOrWhiteSpace(textPreview) && !string.IsNullOrWhiteSpace(mimeMessage.HtmlBody))
        {
            textPreview = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(mimeMessage.HtmlBody, "<.*?>", " "));
        }
        textPreview = (textPreview ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        email.Snippet = textPreview.Length > 100 ? textPreview.Substring(0, 100) : textPreview;

        // Attachments
        var attachmentFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments", email.Id);

        var allAttachments = mimeMessage.Attachments.OfType<MimePart>().ToList();

        // Also capture inline calendar invites (iTIP / Teams / Outlook) which are often not marked as "attachments"
        foreach (var bodyPart in mimeMessage.BodyParts.OfType<MimePart>())
        {
            if (bodyPart.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase))
            {
                if (!allAttachments.Contains(bodyPart))
                {
                    // Ensure it has a filename so the UI displays it cleanly
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
                    await stream.CopyToAsync(fileStream);
                    size = fileStream.Length;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailSync] Attachment save failed: {ex.Message}");
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

        // Save raw MIME for archival (source of truth — never delete)
        // Body HTML is extracted on-the-fly when viewing via ExtractBodyFromMimeAsync
        var mimeFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime");
        Directory.CreateDirectory(mimeFolder);
        var mimePath = Path.Combine(mimeFolder, $"{email.Id}.eml");
        await using var mimeStream = File.Create(mimePath);
        await mimeMessage.WriteToAsync(mimeStream);

        _emailMessages.Save(email);
        return email;
    }

    public async Task MarkMessageReadAsync(string messageId, bool isRead)
    {
        var message = _emailMessages.GetById(messageId);
        if (message == null) return;

        // Local Update
        message.IsRead = isRead;
        _emailMessages.Save(message);

        // Server Update
        await UpdateServerFlagAsync(message, MessageFlags.Seen, isRead);

        _syncStateService.NotifyUnreadCountChanged(message.EmployeeId);
    }

    public async Task MarkMessageFlaggedAsync(string messageId, bool isFlagged)
    {
        var message = _emailMessages.GetById(messageId);
        if (message == null) return;

        // Local Update
        message.IsFlagged = isFlagged;
        _emailMessages.Save(message);

        // Server Update
        await UpdateServerFlagAsync(message, MessageFlags.Flagged, isFlagged);
    }

    private async Task UpdateServerFlagAsync(EmailMessage message, MessageFlags flag, bool add)
    {
        var employee = _employees.GetById(message.EmployeeId);
        var settings = _companyProfile.Get()?.EmailSettings;
        if (employee == null || settings == null) return;

        try
        {
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            var folder = await client.GetFolderAsync(message.FolderPath);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            var uid = new UniqueId(message.UniqueId);
            if (add)
                await folder.AddFlagsAsync(uid, flag, true);
            else
                await folder.RemoveFlagsAsync(uid, flag, true);

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailSync] Failed to update flags: {ex.Message}");
        }
    }

    public async Task MoveMessageAsync(string messageId, string employeeId, string destinationFolderPath)
    {
        var message = _emailMessages.GetById(messageId);
        if (message == null || message.EmployeeId != employeeId) return;

        var sourceFolderPath = message.FolderPath;
        var sourceUid = message.UniqueId;
        if (sourceFolderPath == destinationFolderPath) return;

        var employee = _employees.GetById(employeeId);
        var settings = _companyProfile.Get()?.EmailSettings;

        if (employee == null || settings == null) return;

        var folders = _emailFolders.GetByEmployee(employeeId);
        var destFolder = folders.FirstOrDefault(f => f.Path == destinationFolderPath);

        if (destFolder == null)
        {
            Console.WriteLine($"[Email] Destination folder {destinationFolderPath} not found.");
            return;
        }

        // 1. REMOVE from local DB/index BEFORE the IMAP move.
        //    This prevents the IDLE-triggered SyncFolderAsync from finding this
        //    message in the source folder and duplicating it to local Trash.
        _emailMessages.Delete(messageId, employeeId);

        // Update source folder counts immediately
        var sourceFolder = folders.FirstOrDefault(f => f.Path == sourceFolderPath);
        if (sourceFolder != null)
        {
            sourceFolder.TotalCount = Math.Max(0, sourceFolder.TotalCount - 1);
            if (!message.IsRead) sourceFolder.UnreadCount = Math.Max(0, sourceFolder.UnreadCount - 1);
            _emailFolders.Save(sourceFolder);
        }

        try
        {
            // 2. Perform the IMAP move
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            var folder = await client.GetFolderAsync(sourceFolderPath);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            var remoteDestFolder = await client.GetFolderAsync(destFolder.Path);

            var uid = new UniqueId(sourceUid);
            var result = await folder.MoveToAsync(uid, remoteDestFolder);
            await folder.ExpungeAsync();

            await client.DisconnectAsync(true);

            // 3. Re-save the message with the new folder/UID
            if (result.HasValue)
            {
                // Server supports UIDPLUS and gave us the new UID
                message.FolderPath = destFolder.Path;
                message.UniqueId = result.Value.Id;
                _emailMessages.Save(message);

                destFolder.TotalCount++;
                if (!message.IsRead) destFolder.UnreadCount++;
                _emailFolders.Save(destFolder);
            }
            // else: non-UIDPLUS — message stays deleted locally.
            // The next sync of the destination folder will re-download it with the correct UID.

            _syncStateService.NotifyUnreadCountChanged(employeeId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Email] Failed to move message: {ex.Message}");

            // Restore the message to its original location since the IMAP move failed
            message.FolderPath = sourceFolderPath;
            message.UniqueId = sourceUid;
            _emailMessages.Save(message);

            // Restore source folder counts
            if (sourceFolder != null)
            {
                sourceFolder.TotalCount++;
                if (!message.IsRead) sourceFolder.UnreadCount++;
                _emailFolders.Save(sourceFolder);
            }
        }
    }

    public async Task MoveMessageToTrashAsync(string messageId, string employeeId)
    {
        var folders = _emailFolders.GetByEmployee(employeeId);
        var trashFolder = folders.FirstOrDefault(f => f.IsTrash);

        if (trashFolder == null)
        {
            trashFolder = folders.FirstOrDefault(f => f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                                                     || f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                                                     || f.Name.Equals("Deleted", StringComparison.OrdinalIgnoreCase));
        }

        if (trashFolder == null)
        {
            Console.WriteLine($"[Email] No Trash folder found for employee {employeeId}.");
            return;
        }

        var message = _emailMessages.GetById(messageId);
        if (message != null && message.FolderPath == trashFolder.Path)
        {
            await DeleteMessageAsync(messageId, employeeId);
            return;
        }

        await MoveMessageAsync(messageId, employeeId, trashFolder.Path);
    }

    private async Task DeleteLocalMessageFilesAsync(string messageId, string employeeId)
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
            Console.WriteLine($"[EmailSync] Failed to cleanup files for {messageId}: {ex.Message}");
        }
    }

    private async Task MoveMessageToLocalTrashAsync(EmailMessage message, string employeeId)
    {
        var folders = _emailFolders.GetByEmployee(employeeId);
        var trashFolder = folders.FirstOrDefault(f => f.IsTrash)
            ?? folders.FirstOrDefault(f => f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted", StringComparison.OrdinalIgnoreCase));

        if (trashFolder == null)
        {
            Console.WriteLine($"[Email] Could not find local Trash folder to move missing message {message.Id}.");
            return;
        }

        var randomUid = (uint)(uint.MaxValue - new Random().Next(1000, 1000000));

        var oldFolder = folders.FirstOrDefault(f => f.Path == message.FolderPath);
        if (oldFolder != null)
        {
            oldFolder.TotalCount = Math.Max(0, oldFolder.TotalCount - 1);
            if (!message.IsRead) oldFolder.UnreadCount = Math.Max(0, oldFolder.UnreadCount - 1);
            _emailFolders.Save(oldFolder);
        }

        _emailMessages.Delete(message.Id, employeeId);

        message.FolderPath = trashFolder.Path;
        message.UniqueId = randomUid;

        _emailMessages.Save(message);

        trashFolder.TotalCount++;
        if (!message.IsRead) trashFolder.UnreadCount++;
        _emailFolders.Save(trashFolder);
    }

    public async Task ArchiveMessageAsync(string messageId, string employeeId)
    {
        var folders = _emailFolders.GetByEmployee(employeeId);
        var archiveFolder = folders.FirstOrDefault(f => f.IsArchive);

        if (archiveFolder == null)
        {
            archiveFolder = folders.FirstOrDefault(f => f.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase));
        }

        if (archiveFolder == null)
        {
            Console.WriteLine($"[Email] No Archive folder found for employee {employeeId}.");
            return;
        }

        await MoveMessageAsync(messageId, employeeId, archiveFolder.Path);
    }

    public async Task JunkMessageAsync(string messageId, string employeeId)
    {
        var folders = _emailFolders.GetByEmployee(employeeId);
        var junkFolder = folders.FirstOrDefault(f => f.IsJunk);

        if (junkFolder == null)
        {
            junkFolder = folders.FirstOrDefault(f => f.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase)
                                                     || f.Name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase)
                                                     || f.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase));
        }

        if (junkFolder == null)
        {
            Console.WriteLine($"[Email] No Junk folder found for employee {employeeId}.");
            return;
        }

        await MoveMessageAsync(messageId, employeeId, junkFolder.Path);
    }

    public string GetAttachmentPath(string messageId, string attachmentId, string requestingEmployeeId)
    {
        // Build path directly using requesting employee's folder (same pattern as GetEmailBodyAsync)
        return Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Attachments", messageId, attachmentId);
    }

    /// <summary>
    /// Returns the physical path to the raw MIME (.eml) file for a given message,
    /// or null if the file does not exist on disk.
    /// </summary>
    public string? GetMimeFilePath(string messageId, string employeeId)
    {
        var path = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime", $"{messageId}.eml");
        return File.Exists(path) ? path : null;
    }

    public async Task<string> GetEmailBodyAsync(string messageId, string requestingEmployeeId)
    {
        // Parse MIME on-the-fly (source of truth)
        var mimePath = Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Mime", $"{messageId}.eml");
        if (File.Exists(mimePath))
            return await ExtractBodyFromMimeAsync(mimePath);

        // Fallback for legacy emails stored directly as HTML
        var legacyHtmlPath = Path.Combine(_emailBodyPath, requestingEmployeeId, "Email", "Bodies", $"{messageId}.html");
        if (File.Exists(legacyHtmlPath))
            return await ReadHtmlWithEncodingDetectionAsync(legacyHtmlPath);

        return "";
    }

    /// <summary>
    /// Extracts the HTML body from a raw MIME .eml file, converting plain text to HTML
    /// if needed, and inlining CID-referenced images as base64 data URIs.
    /// </summary>
    public static async Task<string> ExtractBodyFromMimeAsync(string mimePath)
    {
        var mime = await MimeMessage.LoadAsync(mimePath);
        var body = mime.HtmlBody;
        if (string.IsNullOrWhiteSpace(body))
        {
            var text = mime.TextBody ?? "";
            body = $"<div style=\"white-space:pre-wrap;font-family:sans-serif;\">{System.Net.WebUtility.HtmlEncode(text)}</div>";
        }

        // Inline cid: images as base64 data URIs
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

    /// <summary>
    /// Reads an HTML email body file from disk and fixes misinterpreted Windows-1252 characters.
    /// Some emails have their Windows-1252 characters (like curly quotes 0x92) decoded as 
    /// ISO-8859-1 C1 control characters (U+0080-U+009F) instead of the correct Unicode equivalents.
    /// This method detects and remaps those characters, then opportunistically fixes the file on disk.
    /// </summary>
    private static async Task<string> ReadHtmlWithEncodingDetectionAsync(string filePath)
    {
        // Read as UTF-8 (the bytes are valid UTF-8 structurally)
        var text = await File.ReadAllTextAsync(filePath, System.Text.Encoding.UTF8);

        // Fix misinterpreted Windows-1252 characters.
        // When MailKit decodes an email whose charset is incorrectly declared (or absent),
        // Windows-1252 byte values 0x80-0x9F are mapped to Unicode C1 control characters
        // U+0080-U+009F, instead of the correct Windows-1252 visual characters (e.g.,
        // 0x92 → U+0092 instead of U+2019 right single quotation mark).
        // This table maps each C1 control character to its correct Windows-1252 equivalent.
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
            // Windows-1252 to Unicode mapping for the range 0x80-0x9F
            char[] win1252Map = new char[]
            {
                '\u20AC', // 0x80 → €
                '\u0081', // 0x81 → (undefined, keep as-is)
                '\u201A', // 0x82 → ‚
                '\u0192', // 0x83 → ƒ
                '\u201E', // 0x84 → „
                '\u2026', // 0x85 → …
                '\u2020', // 0x86 → †
                '\u2021', // 0x87 → ‡
                '\u02C6', // 0x88 → ˆ
                '\u2030', // 0x89 → ‰
                '\u0160', // 0x8A → Š
                '\u2039', // 0x8B → ‹
                '\u0152', // 0x8C → Œ
                '\u008D', // 0x8D → (undefined, keep as-is)
                '\u017D', // 0x8E → Ž
                '\u008F', // 0x8F → (undefined, keep as-is)
                '\u0090', // 0x90 → (undefined, keep as-is)
                '\u2018', // 0x91 → '
                '\u2019', // 0x92 → '  ← This is the most common fix (right single quotation mark)
                '\u201C', // 0x93 → "
                '\u201D', // 0x94 → "
                '\u2022', // 0x95 → •
                '\u2013', // 0x96 → –
                '\u2014', // 0x97 → —
                '\u02DC', // 0x98 → ˜
                '\u2122', // 0x99 → ™
                '\u0161', // 0x9A → š
                '\u203A', // 0x9B → ›
                '\u0153', // 0x9C → œ
                '\u009D', // 0x9D → (undefined, keep as-is)
                '\u017E', // 0x9E → ž
                '\u0178', // 0x9F → Ÿ
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

            // Opportunistically fix the file on disk so future reads are clean
            try
            {
                await File.WriteAllTextAsync(filePath, text, System.Text.Encoding.UTF8);
                Console.WriteLine($"[EmailEncoding] Fixed Windows-1252 character mapping in: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailSync] Failed to repair charsets during read: {ex.Message}");
            }
        }

        return text;
    }

    public async Task<string> GetSignatureAsync(string employeeId)
    {
        var path = Path.Combine(_emailBodyPath, employeeId, "Email", "Signature.html");
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8);

        // Fallback: Check if employee has legacy string
        var employee = _employees.GetById(employeeId);
        if (employee != null && !string.IsNullOrEmpty(employee.SignatureHtml))
        {
            return employee.SignatureHtml;
        }

        return "";
    }

    public async Task SaveSignatureAsync(string employeeId, string html)
    {
        var dir = Path.Combine(_emailBodyPath, employeeId, "Email");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Signature.html");
        await File.WriteAllTextAsync(path, html ?? string.Empty);

        // Clear legacy string
        var employee = _employees.GetById(employeeId);
        if (employee != null && !string.IsNullOrEmpty(employee.SignatureHtml))
        {
            employee.SignatureHtml = string.Empty;
            _employees.Save(employee);
        }
    }

    public async Task RepairEmailInstallationAsync(string employeeId)
    {
        _syncStateService.SetState(employeeId, "Preparing Repair...");

        var folders = _emailFolders.GetByEmployee(employeeId);
        var draftIds = new HashSet<string>();

        // Preserve LocalDrafts globally
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
            // 1. Wipe Mime files
            var mimeDir = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime");
            if (Directory.Exists(mimeDir))
            {
                try { Directory.Delete(mimeDir, true); } catch (Exception ex) { Console.WriteLine($"[EmailRepair] Failed to delete mime directory: {ex.Message}"); }
            }

            // 2. Wipe Attachments
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
                catch (Exception ex) { Console.WriteLine($"[EmailRepair] Failed to wipe attachments directory: {ex.Message}"); }
            }

            // 3. Wipe JSON Messages files specifically to prevent orphaned scanning
            var msgDir = Path.Combine(_emailBodyPath, employeeId, "Email", "Messages");
            if (Directory.Exists(msgDir))
            {
                foreach (var file in Directory.GetFiles(msgDir, "*.json"))
                {
                    var msgId = Path.GetFileNameWithoutExtension(file);
                    if (!draftIds.Contains(msgId))
                    {
                        try { File.Delete(file); } catch (Exception ex) { Console.WriteLine($"[EmailRepair] Failed to delete message file {file}: {ex.Message}"); }
                    }
                }
            }
        });

        // 4. Wipe everything from memory (Index) to prevent ghost IMAP files
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
                    _emailMessages.Delete(id, employeeId);
                }
            }

            folder.TotalCount = 0;
            folder.UnreadCount = 0;
            _emailFolders.Save(folder);
        }

        // 5. Run the standard pipeline so we don't have duplicated logic!
        _syncStateService.SetState(employeeId, "Starting fresh sync...");
        await SyncEmployeeAsync(employeeId);
    }

    /// <summary>
    /// Normalizes Quill.js-generated HTML for email rendering.
    /// Quill wraps every line in &lt;p&gt; tags with near-zero margins in its editor CSS,
    /// but email clients use the default margin (1em), causing doubled line spacing.
    /// This injects inline styles to match the editor appearance.
    /// </summary>
    private static string NormalizeQuillHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        // First, handle <p> tags that already have a style attribute — prepend margin/padding
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<p(\s[^>]*)style\s*=\s*""([^""]*)""",
            match =>
            {
                var before = match.Groups[1].Value;
                var existingStyle = match.Groups[2].Value;
                return $"<p{before}style=\"margin:0;padding:0;{existingStyle}\"";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Then, handle <p> tags without any style attribute
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"<p(\s(?![^>]*style\s*=)|>)",
            match =>
            {
                var afterP = match.Groups[1].Value;
                return afterP == ">"
                    ? "<p style=\"margin:0;padding:0;\">"
                    : "<p style=\"margin:0;padding:0;\" ";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Finally, ensure empty <p> tags (blank lines in Quill) contain a <br> so they
        // don't collapse to zero height now that margins are zeroed
        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            @"(<p[^>]*>)\s*</p>",
            "$1<br></p>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return html;
    }

    public async Task SendEmailAsync(string employeeId, string to, string subject, string body, List<IBrowserFile>? attachments = null, string? cc = null, string? bcc = null, List<EmailAttachmentMeta>? existingAttachments = null, string? draftId = null, List<(string FilePath, string FileName)>? fileAttachmentPaths = null)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) throw new Exception("Employee not found");

        // Check credentials
        var username = employee.Email;
        var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new Exception("Missing email credentials. Please configure them in your profile.");

        // Check Server Settings
        var profile = _companyProfile.Get();
        if (profile == null || string.IsNullOrEmpty(profile.EmailSettings.SmtpHost))
            throw new Exception("SMTP Server not configured by admin.");

        var settings = profile.EmailSettings;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(employee.FullName, username));
        message.To.AddRange(InternetAddressList.Parse(to));

        if (!string.IsNullOrWhiteSpace(cc))
            message.Cc.AddRange(InternetAddressList.Parse(cc));

        if (!string.IsNullOrWhiteSpace(bcc))
            message.Bcc.AddRange(InternetAddressList.Parse(bcc));

        message.Subject = subject;

        // Normalize Quill.js HTML so <p> margins match the editor appearance
        body = NormalizeQuillHtml(body);

        var builder = new BodyBuilder { HtmlBody = body };

        // Process inline base64 images to avoid Gmail blocking them
        var htmlBody = builder.HtmlBody;
        if (!string.IsNullOrEmpty(htmlBody))
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(htmlBody, @"src=[""']data:(image/[^;]+);base64,([^""']+)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                try
                {
                    var mimeTypeString = match.Groups[1].Value;
                    var base64Data = match.Groups[2].Value;
                    var cid = Guid.NewGuid().ToString("N");

                    var extension = mimeTypeString.Split('/').LastOrDefault() ?? "png";
                    var bytes = Convert.FromBase64String(base64Data);

                    // Add as LinkedResource (Embedded)
                    var imageResource = builder.LinkedResources.Add("image_" + cid + "." + extension, bytes, ContentType.Parse(mimeTypeString));
                    imageResource.ContentId = cid;

                    // Replace original src with cid
                    htmlBody = htmlBody.Replace(match.Value, $"src=\"cid:{cid}\"");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmailService] Failed to parse inline image: {ex.Message}");
                }
            }
            builder.HtmlBody = htmlBody;
        }

        // Generate plain text alternative
        try
        {
            builder.TextBody = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(body, "<.*?>", " "));
        }
        catch
        {
            builder.TextBody = body; // Fallback
        }

        if (attachments != null)
        {
            foreach (var file in attachments)
            {
                using var stream = file.OpenReadStream(10 * 1024 * 1024); // 10MB max
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                builder.Attachments.Add(file.Name, ms.ToArray());
            }
        }

        // Attach files already saved to disk (from auto-saved drafts)
        if (existingAttachments != null && !string.IsNullOrEmpty(draftId))
        {
            foreach (var att in existingAttachments)
            {
                var filePath = GetAttachmentPath(draftId, att.Id, employeeId);
                if (File.Exists(filePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
                    builder.Attachments.Add(att.FileName, fileBytes, ContentType.Parse(att.ContentType));
                }
            }
        }

        // Attach preloaded/server-side files (e.g. generated PDFs)
        if (fileAttachmentPaths != null)
        {
            foreach (var file in fileAttachmentPaths)
            {
                if (File.Exists(file.FilePath))
                {
                    var fileBytes = await File.ReadAllBytesAsync(file.FilePath);
                    var contentType = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream";
                    builder.Attachments.Add(file.FileName, fileBytes, ContentType.Parse(contentType));
                }
            }
        }

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        // client.ServerCertificateValidationCallback = (s,c,h,e) => true;

        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.SmtpSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(username, password);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);

        // Explicitly append to IMAP Sent folder if one exists
        try
        {
            var sentFolderEntity = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.IsSent);
            if (sentFolderEntity != null && !string.IsNullOrEmpty(settings.ImapHost))
            {
                using var imapClient = new ImapClient();
                await imapClient.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
                await imapClient.AuthenticateAsync(username, password);

                var imapSentFolder = await imapClient.GetFolderAsync(sentFolderEntity.Path);
                if (imapSentFolder != null)
                {
                    await imapSentFolder.OpenAsync(FolderAccess.ReadWrite);
                    await imapSentFolder.AppendAsync(message, MessageFlags.Seen, DateTimeOffset.Now);
                }
                await imapClient.DisconnectAsync(true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailService] Failed to explicitly append to Sent folder: {ex.Message}");
        }
    }

    public async Task<string> SaveDraftAsync(string employeeId, string to, string subject, string body, List<IBrowserFile>? attachments = null, string? cc = null, string? bcc = null, string? existingDraftId = null)
    {
        var employee = _employees.GetById(employeeId);
        if (employee == null) return "";

        // Find strictly LOCAL Drafts folder
        var folders = _emailFolders.GetByEmployee(employeeId);
        var drafts = folders.FirstOrDefault(f => f.Path == "LocalDrafts");

        if (drafts == null)
        {
            // Create "Local Drafts" folder
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
            draft = _emailMessages.GetById(existingDraftId);
            // Security check: ensure the draft belongs to the employee
            if (draft != null && draft.EmployeeId != employeeId)
            {
                draft = null; // Treat as if not found
            }
        }

        if (draft == null)
        {
            // Create New Message
            draft = new EmailMessage
            {
                EmployeeId = employeeId,
                FolderPath = drafts.Path, // Ensure this matches the Folder Path
                UniqueId = drafts.NextUid++, // Assign Unique ID and increment
                IsRead = true,
                IsFlagged = false
            };

            // Increment count for new draft
            drafts.TotalCount++;
            _emailFolders.Save(drafts);
        }
        else if (draft.UniqueId == 0)
        {
            // Fix existing drafts with 0 UID
            draft.UniqueId = drafts.NextUid++;
            _emailFolders.Save(drafts);
        }

        // Update Properties
        draft.Subject = subject;
        draft.FromAddress = employee.Email;
        draft.FromName = employee.FullName;
        draft.ToAddresses = to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        draft.CcAddresses = cc?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? new List<string>();
        draft.BccAddresses = bcc?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList() ?? new List<string>();
        draft.Date = DateTimeOffset.Now;
        // Ensure path is updated if we created a new drafts folder or decided to move it
        draft.FolderPath = drafts.Path;

        // Save Draft Body as MIME
        var mimeFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime");
        Directory.CreateDirectory(mimeFolder);
        var draftMimePath = Path.Combine(mimeFolder, $"{draft.Id}.eml");
        // Build a MimeMessage from the draft data for consistent storage
        var draftMime = new MimeMessage();
        draftMime.From.Add(new MailboxAddress(employee.FullName, employee.Email));
        if (!string.IsNullOrWhiteSpace(to))
            draftMime.To.AddRange(InternetAddressList.Parse(to));
        if (!string.IsNullOrWhiteSpace(cc))
            draftMime.Cc.AddRange(InternetAddressList.Parse(cc));
        if (!string.IsNullOrWhiteSpace(bcc))
            draftMime.Bcc.AddRange(InternetAddressList.Parse(bcc));
        draftMime.Subject = subject;
        draftMime.Body = new TextPart(TextFormat.Html) { Text = body };
        await using var draftMimeStream = File.Create(draftMimePath);
        await draftMime.WriteToAsync(draftMimeStream);

        // Attachments (TODO: Save attachments logic similar to Receive)
        // For now, if new attachments are provided, we should save them.
        // Existing attachments are preserved in draft.Attachments list unless we clear them.
        // This simple implementation *adds* new attachments. Detailed management requires UI for removing specific attachments.
        if (attachments != null)
        {
            var attachmentFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments", draft.Id);
            if (!Directory.Exists(attachmentFolder)) Directory.CreateDirectory(attachmentFolder);

            foreach (var file in attachments)
            {
                var attachmentId = Guid.NewGuid().ToString();
                var filePath = Path.Combine(attachmentFolder, attachmentId);
                using var stream = file.OpenReadStream(10 * 1024 * 1024); // 10MB limit
                using var fileStream = File.Create(filePath);
                await stream.CopyToAsync(fileStream);

                draft.Attachments.Add(new EmailAttachmentMeta
                {
                    Id = attachmentId,
                    FileName = file.Name,
                    ContentType = file.ContentType,
                    Size = file.Size
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

    public async Task RemoveDraftAttachmentAsync(string employeeId, string draftId, string attachmentId)
    {
        var draft = _emailMessages.GetById(draftId);
        if (draft == null || draft.EmployeeId != employeeId) return;

        var attachment = draft.Attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment != null)
        {
            draft.Attachments.Remove(attachment);
            draft.HasAttachments = draft.Attachments.Any();
            _emailMessages.Save(draft);

            try
            {
                var filePath = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments", draftId, attachmentId);
                if (File.Exists(filePath))
                {
                    await Task.Run(() => File.Delete(filePath));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailService] Failed to delete attachment file: {ex.Message}");
            }
        }
    }

    public Task<EmailMessage?> GetMessageAsync(string messageId)
    {
        return Task.FromResult(_emailMessages.GetById(messageId));
    }

    public async Task DeleteMessageAsync(string messageId, string employeeId)
    {
        var message = _emailMessages.GetById(messageId);
        if (message == null || message.EmployeeId != employeeId) return;

        // Server Update
        await UpdateServerDeletionAsync(message);

        // Remove from DB (using specialized index-aware Delete)
        _emailMessages.Delete(messageId, employeeId);

        // Update Folder Count
        var folder = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.Path == message.FolderPath);
        if (folder != null)
        {
            folder.TotalCount = Math.Max(0, folder.TotalCount - 1);
            if (!message.IsRead) folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
            _emailFolders.Save(folder);
        }

        // Remove Files (Body and Attachments)
        try
        {
            var mimePath = Path.Combine(_emailBodyPath, employeeId, "Email", "Mime", $"{messageId}.eml");
            if (File.Exists(mimePath)) await Task.Run(() => File.Delete(mimePath));

            var attachmentFolder = Path.Combine(_emailBodyPath, employeeId, "Email", "Attachments", messageId);
            if (Directory.Exists(attachmentFolder)) await Task.Run(() => Directory.Delete(attachmentFolder, true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailSync] Failed to cleanup files for {messageId}: {ex.Message}");
        }

        _syncStateService.NotifyUnreadCountChanged(employeeId);
    }

    private async Task UpdateServerDeletionAsync(EmailMessage message)
    {
        var employee = _employees.GetById(message.EmployeeId);
        var settings = _companyProfile.Get()?.EmailSettings;
        if (employee == null || settings == null) return;

        try
        {
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            var folder = await client.GetFolderAsync(message.FolderPath);
            await folder.OpenAsync(FolderAccess.ReadWrite);

            var uid = new UniqueId(message.UniqueId);
            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true);
            await folder.ExpungeAsync();

            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailSync] Failed to delete message from server: {ex.Message}");
        }
    }
    public async Task RepairLocalDraftsAsync(string employeeId)
    {
        var draftsFolder = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.Path == "LocalDrafts");
        if (draftsFolder == null) return;

        var msgPath = Path.Combine(_emailBodyPath, employeeId, "Email", "Messages");
        if (!Directory.Exists(msgPath)) return;

        var files = await Task.Run(() => Directory.GetFiles(msgPath, "*.json"));
        bool changed = false;

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var msg = System.Text.Json.JsonSerializer.Deserialize<EmailMessage>(json);
                if (msg != null && msg.FolderPath == "LocalDrafts" && msg.UniqueId == 0)
                {
                    msg.UniqueId = draftsFolder.NextUid++;
                    _emailMessages.Save(msg);
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmailSync] Failed to repair draft {file}: {ex.Message}");
            }
        }

        if (changed) _emailFolders.Save(draftsFolder);
    }

    private string GetParentPath(IMailFolder folder)
    {
        if (string.IsNullOrEmpty(folder.FullName)) return string.Empty;
        var sep = folder.DirectorySeparator.ToString();
        var lastIdx = folder.FullName.LastIndexOf(sep, StringComparison.Ordinal);
        return lastIdx > 0 ? folder.FullName.Substring(0, lastIdx) : string.Empty;
    }

    public async Task<EmailFolder?> CreateFolderAsync(string employeeId, string parentFolderPath, string newFolderName)
    {
        var employee = _employees.GetById(employeeId);
        var settings = _companyProfile.Get()?.EmailSettings;
        if (employee == null || settings == null) return null;

        try
        {
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            IMailFolder parentFolder;
            if (string.IsNullOrEmpty(parentFolderPath))
            {
                var personalNamespaces = client.PersonalNamespaces;
                if (personalNamespaces.Count > 0)
                    parentFolder = client.GetFolder(personalNamespaces[0]);
                else
                    return null; // Don't know root
            }
            else
            {
                parentFolder = await client.GetFolderAsync(parentFolderPath);
            }

            var newFolder = await parentFolder.CreateAsync(newFolderName, true);

            // Re-sync just this folder locally immediately
            await SyncFolderAsync(client, newFolder, employeeId);

            await client.DisconnectAsync(true);

            var localFolders = _emailFolders.GetByEmployee(employeeId);
            return localFolders.FirstOrDefault(f => f.Path == newFolder.FullName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailService] Failed to create folder: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> DeleteFolderAsync(string employeeId, string folderPath)
    {
        var employee = _employees.GetById(employeeId);
        var settings = _companyProfile.Get()?.EmailSettings;
        if (employee == null || settings == null) return false;

        try
        {
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            var folder = await client.GetFolderAsync(folderPath);
            await folder.DeleteAsync();

            await client.DisconnectAsync(true);

            // Delete locally
            var folders = _emailFolders.GetByEmployee(employeeId);
            var local = folders.FirstOrDefault(f => f.Path == folderPath);
            if (local != null) _emailFolders.Delete(local.Id);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailService] Failed to delete folder ({folderPath}): {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RenameFolderAsync(string employeeId, string folderPath, string newName)
    {
        var employee = _employees.GetById(employeeId);
        var settings = _companyProfile.Get()?.EmailSettings;
        if (employee == null || settings == null) return false;

        try
        {
            using var client = new ImapClient();
            var password = _encryptionService.Decrypt(employee.EncryptedEmailPassword);
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, settings.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.Auto);
            await client.AuthenticateAsync(employee.Email, password);

            var folder = await client.GetFolderAsync(folderPath);
            var parentPath = GetParentPath(folder);
            IMailFolder parentFolder;
            if (string.IsNullOrEmpty(parentPath))
            {
                var personalNamespaces = client.PersonalNamespaces;
                parentFolder = client.GetFolder(personalNamespaces[0]);
            }
            else
            {
                parentFolder = await client.GetFolderAsync(parentPath);
            }
            await folder.RenameAsync(parentFolder, newName);
            var renamedFolder = await parentFolder.GetSubfolderAsync(newName);

            await client.DisconnectAsync(true);

            var folders = _emailFolders.GetByEmployee(employeeId);
            var local = folders.FirstOrDefault(f => f.Path == folderPath);
            if (local != null)
            {
                var oldPath = local.Path;
                local.Name = renamedFolder.Name;
                local.Path = renamedFolder.FullName;
                local.ParentPath = GetParentPath(renamedFolder);
                local.Delimiter = renamedFolder.DirectorySeparator.ToString();

                _emailFolders.Delete(local.Id); // Need to trick it if path doesn't change file? The file uses Id.
                _emailFolders.Save(local);

                _emailMessages.RenameFolder(employeeId, oldPath, renamedFolder.FullName);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmailService] Failed to rename folder: {ex.Message}");
            return false;
        }
    }
}



