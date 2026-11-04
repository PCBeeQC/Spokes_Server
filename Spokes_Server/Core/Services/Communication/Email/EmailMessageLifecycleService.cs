using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailMessageLifecycleService : IEmailMessageLifecycleService
{
    private readonly EmailAccountRepository? _accounts;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EncryptionService _encryptionService;
    private readonly EmailSyncStateService _syncStateService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailMessageLifecycleService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;

    public EmailMessageLifecycleService(
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        EmailSyncStateService syncStateService,
        IEmailCacheMaintenanceService cacheMaintenance,
        EmailAccountRepository? accounts = null,
        ILogger<EmailMessageLifecycleService>? logger = null,
        IImapConnectionFactory? imapFactory = null)
    {
        _accounts = accounts;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _encryptionService = encryptionService;
        _syncStateService = syncStateService;
        _cacheMaintenance = cacheMaintenance;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts);
    }

    public Task<EmailMessage?> GetMessageAsync(string accountId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return Task.FromResult<EmailMessage?>(null);

        var msg = !string.IsNullOrWhiteSpace(accountId)
            ? _emailMessages.GetById(accountId, messageId)
            : _emailMessages.GetById(messageId);
        return Task.FromResult(msg);
    }

    public async Task<SpokesResult> MarkMessageReadAsync(string accountId, string messageId, bool isRead, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID cannot be empty.", "INVALID_INPUT");

        var message = _emailMessages.GetById(accountId, messageId);
        if (message == null)
            return SpokesResult.Failure($"Message '{messageId}' not found.", "MESSAGE_NOT_FOUND");

        message.IsRead = isRead;
        _emailMessages.Save(message);

        var folder = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == message.FolderPath);
        if (folder != null)
        {
            folder.UnreadCount = _emailMessages.GetUnreadCount(accountId, folder.Path);
            _emailFolders.Save(folder);
        }

        _syncStateService.NotifyUnreadCountChanged(message.AccountId);

        await UpdateServerFlagAsync(message, MessageFlags.Seen, isRead, cancellationToken);
        return SpokesResult.Success();
    }

    public async Task<SpokesResult> MarkMessageFlaggedAsync(string accountId, string messageId, bool isFlagged, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID cannot be empty.", "INVALID_INPUT");

        var message = _emailMessages.GetById(accountId, messageId);
        if (message == null)
            return SpokesResult.Failure($"Message '{messageId}' not found.", "MESSAGE_NOT_FOUND");

        message.IsFlagged = isFlagged;
        _emailMessages.Save(message);

        await UpdateServerFlagAsync(message, MessageFlags.Flagged, isFlagged, cancellationToken);
        return SpokesResult.Success();
    }

    private async Task UpdateServerFlagAsync(EmailMessage message, MessageFlags flag, bool add, CancellationToken cancellationToken = default)
    {
        if (message.IsLocalOnly) return;

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(message.AccountId, cancellationToken);
            if (client == null) return;

            var folder = await client.GetFolderAsync(message.FolderPath, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var uid = new UniqueId(message.UniqueId);
            if (add)
                await folder.AddFlagsAsync(uid, flag, true, cancellationToken);
            else
                await folder.RemoveFlagsAsync(uid, flag, true, cancellationToken);

            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update flags");
        }
    }

    public async Task<SpokesResult> MoveMessageAsync(string accountId, string messageId, string destinationFolderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
            return SpokesResult.Failure("Destination folder path is required.", "INVALID_INPUT");

        var message = _emailMessages.GetById(accountId, messageId);
        if (message == null || message.AccountId != accountId)
            return SpokesResult.Failure($"Message '{messageId}' not found for account '{accountId}'.", "MESSAGE_NOT_FOUND");

        var sourceFolderPath = message.FolderPath;
        var sourceUid = message.UniqueId;
        if (sourceFolderPath == destinationFolderPath)
            return SpokesResult.Success();

        var folders = _emailFolders.GetByAccount(accountId);
        var destFolder = folders.FirstOrDefault(f => f.Path == destinationFolderPath);

        if (destFolder == null)
        {
            _logger?.LogWarning("Destination folder {DestinationFolderPath} not found", destinationFolderPath);
            return SpokesResult.Failure($"Destination folder '{destinationFolderPath}' not found.", "FOLDER_NOT_FOUND");
        }

        var sourceFolder = folders.FirstOrDefault(f => f.Path == sourceFolderPath);

        UniqueId? movedUid = null;
        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(accountId, cancellationToken);
            if (client == null)
                return SpokesResult.Failure("Unable to connect to IMAP server.", "IMAP_ERROR");

            var folder = await client.GetFolderAsync(sourceFolderPath, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var remoteDestFolder = await client.GetFolderAsync(destFolder.Path, cancellationToken);

            var uid = new UniqueId(sourceUid);
            var result = await folder.MoveToAsync(uid, remoteDestFolder, cancellationToken);
            await folder.ExpungeAsync(cancellationToken);

            await client.DisconnectAsync(true, cancellationToken);

            movedUid = result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to move message {MessageId}", messageId);
            return SpokesResult.Failure($"Failed to move message: {ex.Message}", "IMAP_ERROR");
        }

        if (!movedUid.HasValue)
        {
            _logger?.LogWarning("IMAP MoveToAsync did not return a destination UID for message {MessageId}", messageId);
            return SpokesResult.Failure("IMAP server did not confirm move operation.", "IMAP_ERROR");
        }

        _emailMessages.Delete(accountId, messageId);

        message.FolderPath = destFolder.Path;
        message.UniqueId = movedUid.Value.Id;
        _emailMessages.Save(message);

        if (sourceFolder != null)
        {
            sourceFolder.TotalCount = Math.Max(0, sourceFolder.TotalCount - 1);
            if (!message.IsRead) sourceFolder.UnreadCount = Math.Max(0, sourceFolder.UnreadCount - 1);
            _emailFolders.Save(sourceFolder);
        }

        destFolder.TotalCount++;
        if (!message.IsRead) destFolder.UnreadCount++;
        _emailFolders.Save(destFolder);

        _syncStateService.NotifyUnreadCountChanged(accountId);
        return SpokesResult.Success();
    }

    public async Task<SpokesResult> MoveMessageToTrashAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByAccount(accountId);
        var trashFolder = folders.FirstOrDefault(f => f.IsTrash)
            ?? folders.FirstOrDefault(f => f.Path.EndsWith("/Trash", StringComparison.OrdinalIgnoreCase)
                                        || f.Path.EndsWith("/Bin", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted", StringComparison.OrdinalIgnoreCase));

        if (trashFolder == null)
        {
            trashFolder = await TryResolveSpecialFolderAsync(accountId, SpecialFolder.Trash, cancellationToken);
        }

        if (trashFolder == null)
        {
            _logger?.LogWarning("No Trash folder found for account {AccountId}", accountId);
            return SpokesResult.Failure($"No Trash folder found for account '{accountId}'.", "TRASH_FOLDER_NOT_FOUND");
        }

        var message = _emailMessages.GetById(accountId, messageId);
        if (message != null && message.FolderPath == trashFolder.Path)
        {
            return await DeleteMessageAsync(accountId, messageId, cancellationToken);
        }

        return await MoveMessageAsync(accountId, messageId, trashFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> ArchiveMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByAccount(accountId);
        var archiveFolder = folders.FirstOrDefault(f => f.IsArchive)
            ?? folders.FirstOrDefault(f => f.Path.EndsWith("/All Mail", StringComparison.OrdinalIgnoreCase)
                                        || f.Path.EndsWith("/Archive", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("All Mail", StringComparison.OrdinalIgnoreCase));

        if (archiveFolder == null)
        {
            archiveFolder = await TryResolveSpecialFolderAsync(accountId, SpecialFolder.Archive, cancellationToken);
        }

        if (archiveFolder == null)
        {
            _logger?.LogWarning("No Archive folder found for account {AccountId}", accountId);
            return SpokesResult.Failure($"No Archive folder found for account '{accountId}'.", "ARCHIVE_FOLDER_NOT_FOUND");
        }

        return await MoveMessageAsync(accountId, messageId, archiveFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> JunkMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByAccount(accountId);
        var junkFolder = folders.FirstOrDefault(f => f.IsJunk)
            ?? folders.FirstOrDefault(f => f.Path.EndsWith("/Spam", StringComparison.OrdinalIgnoreCase)
                                        || f.Path.EndsWith("/Junk", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase));

        if (junkFolder == null)
        {
            junkFolder = await TryResolveSpecialFolderAsync(accountId, SpecialFolder.Junk, cancellationToken);
        }

        if (junkFolder == null)
        {
            _logger?.LogWarning("No Junk folder found for account {AccountId}", accountId);
            return SpokesResult.Failure($"No Junk folder found for account '{accountId}'.", "JUNK_FOLDER_NOT_FOUND");
        }

        return await MoveMessageAsync(accountId, messageId, junkFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> DeleteMessageAsync(string accountId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");

        var message = _emailMessages.GetById(accountId, messageId);
        if (message == null || message.AccountId != accountId)
            return SpokesResult.Failure($"Message '{messageId}' not found for account '{accountId}'.", "MESSAGE_NOT_FOUND");

        await UpdateServerDeletionAsync(message, cancellationToken);

        _emailMessages.Delete(accountId, messageId);

        var folder = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == message.FolderPath);
        if (folder != null)
        {
            folder.TotalCount = Math.Max(0, folder.TotalCount - 1);
            if (!message.IsRead) folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
            _emailFolders.Save(folder);
        }

        await _cacheMaintenance.DeleteLocalMessageFilesAsync(accountId, messageId);

        _syncStateService.NotifyUnreadCountChanged(accountId);
        return SpokesResult.Success();
    }

    private async Task UpdateServerDeletionAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (message.IsLocalOnly) return;

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(message.AccountId, cancellationToken);
            if (client == null) return;

            var folder = await client.GetFolderAsync(message.FolderPath, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var uid = new UniqueId(message.UniqueId);
            await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
            await folder.ExpungeAsync(cancellationToken);

            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete message from server: {MessageId}", message.Id);
        }
    }

    private async Task<EmailFolder?> TryResolveSpecialFolderAsync(string accountId, SpecialFolder specialFolder, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(accountId, cancellationToken);
            if (client == null) return null;

            IMailFolder? imapFolder = null;
            try
            {
                if (client.Capabilities.HasFlag(ImapCapabilities.SpecialUse) || client.Capabilities.HasFlag(ImapCapabilities.XList))
                {
                    imapFolder = client.GetFolder(specialFolder);
                }
            }
            catch
            {
                imapFolder = null;
            }

            if (imapFolder == null && client.PersonalNamespaces.Count > 0)
            {
                try
                {
                    var folders = await client.GetFoldersAsync(client.PersonalNamespaces[0], false, cancellationToken);
                    imapFolder = specialFolder switch
                    {
                        SpecialFolder.Trash => folders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Trash) ||
                            f.FullName.EndsWith("/Trash", StringComparison.OrdinalIgnoreCase) ||
                            f.FullName.EndsWith("/Bin", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase)),
                        SpecialFolder.Archive => folders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Archive) ||
                            f.FullName.EndsWith("/All Mail", StringComparison.OrdinalIgnoreCase) ||
                            f.FullName.EndsWith("/Archive", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("All Mail", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase)),
                        SpecialFolder.Junk => folders.FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.Junk) ||
                            f.FullName.EndsWith("/Spam", StringComparison.OrdinalIgnoreCase) ||
                            f.FullName.EndsWith("/Junk", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase) ||
                            f.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase)),
                        _ => null
                    };
                }
                catch
                {
                    imapFolder = null;
                }
            }

            if (imapFolder == null) return null;

            var existing = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == imapFolder.FullName);
            if (existing != null) return existing;

            var localFolder = new EmailFolder
            {
                AccountId = accountId,
                Name = imapFolder.Name,
                Path = imapFolder.FullName,
                Delimiter = imapFolder.DirectorySeparator.ToString(),
                IsSelectable = !imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect),
                IsInbox = imapFolder.Attributes.HasFlag(FolderAttributes.Inbox),
                IsSent = imapFolder.Attributes.HasFlag(FolderAttributes.Sent),
                IsTrash = specialFolder == SpecialFolder.Trash || imapFolder.Attributes.HasFlag(FolderAttributes.Trash),
                IsDrafts = imapFolder.Attributes.HasFlag(FolderAttributes.Drafts),
                IsArchive = specialFolder == SpecialFolder.Archive || imapFolder.Attributes.HasFlag(FolderAttributes.Archive),
                IsJunk = specialFolder == SpecialFolder.Junk || imapFolder.Attributes.HasFlag(FolderAttributes.Junk)
            };

            _emailFolders.Save(localFolder);
            return localFolder;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not resolve special folder {SpecialFolder} for account {AccountId}", specialFolder, accountId);
            return null;
        }
    }
}
