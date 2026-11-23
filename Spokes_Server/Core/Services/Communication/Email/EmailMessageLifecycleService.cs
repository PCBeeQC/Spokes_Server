using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailMessageLifecycleService : IEmailMessageLifecycleService
{
    private readonly EmailAccountRepository? _accounts;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EmailSyncStateService _syncStateService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailMessageLifecycleService>? _logger;
    private readonly EmployeeRepository? _employees;
    private readonly IEmailInboxService? _inboxService;
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
        IImapConnectionFactory? imapFactory = null,
        EmployeeRepository? employees = null,
        IEmailInboxService? inboxService = null)
    {
        _accounts = accounts;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _syncStateService = syncStateService;
        _cacheMaintenance = cacheMaintenance;
        _logger = logger;
        _employees = employees;
        _inboxService = inboxService;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, encryptionService, accounts, null, employees);
    }

    private SpokesResult? ValidateLifecycleCaller(string accountId, string messageId, string? callerEmployeeId)
    {
        if (string.Equals(_companyProfile?.Get()?.Edition, "Family", StringComparison.OrdinalIgnoreCase))
        {
            return SpokesResult.Failure("Email is not available in Family edition.", "ACCESS_DENIED");
        }

        if (_employees == null && string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            return null;
        }

        var caller = !string.IsNullOrWhiteSpace(callerEmployeeId) ? _employees?.GetById(callerEmployeeId) : null;
        if (caller == null || !caller.IsActive || caller.IsBanned || caller.IsSuspended ||
            !caller.HasPermission(AppPermissions.Email.Use))
        {
            return SpokesResult.Failure("Suspended or unauthorized users cannot modify email messages.", "ACCESS_DENIED");
        }

        var targetAccount = _accounts?.GetById(accountId);
        if (targetAccount is { IsArchived: true })
        {
            return SpokesResult.Failure("Archived email accounts are read-only.", "ACCESS_DENIED");
        }

        if (_inboxService != null)
        {
            if (!string.IsNullOrWhiteSpace(messageId) && _emailMessages.GetById(accountId, messageId) != null)
            {
                if (!_inboxService.CanUserAccessEmailMessage(caller, accountId, messageId))
                {
                    return SpokesResult.Failure("You are not authorized to modify this email message.", "ACCESS_DENIED");
                }
            }
            else if (targetAccount is { } acc)
            {
                bool authorized = (acc.IsServerManaged && (caller.IsAdmin || caller.HasPermission(AppPermissions.Admin.ManageSettings))) ||
                                  acc.CanUserAccess(caller.Id, _inboxService.GetUserTeamIds(caller.Id)) ||
                                  _inboxService.GetAccessibleInboxes(caller.Id).Any(i => string.Equals(i.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
                if (!authorized)
                {
                    return SpokesResult.Failure("You are not authorized to access this mailbox.", "ACCESS_DENIED");
                }
            }
            else
            {
                return SpokesResult.Failure("You are not authorized to access this mailbox.", "ACCESS_DENIED");
            }
        }

        return null;
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

    public Task<SpokesResult> MarkMessageReadAsync(string accountId, string messageId, bool isRead, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        UpdateMessageFlagCoreAsync(
            accountId,
            messageId,
            callerEmployeeId,
            MessageFlags.Seen,
            isRead,
            message =>
            {
                message.IsRead = isRead;
                _emailMessages.Save(message);

                var folder = _emailFolders.GetByAccount(accountId).FirstOrDefault(f => f.Path == message.FolderPath);
                if (folder != null)
                {
                    folder.UnreadCount = _emailMessages.GetUnreadCount(accountId, folder.Path);
                    _emailFolders.Save(folder);
                }

                _syncStateService.NotifyUnreadCountChanged(message.AccountId);
            },
            cancellationToken);

    public Task<SpokesResult> MarkMessageFlaggedAsync(string accountId, string messageId, bool isFlagged, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        UpdateMessageFlagCoreAsync(
            accountId,
            messageId,
            callerEmployeeId,
            MessageFlags.Flagged,
            isFlagged,
            message =>
            {
                message.IsFlagged = isFlagged;
                _emailMessages.Save(message);
            },
            cancellationToken);

    private async Task<SpokesResult> UpdateMessageFlagCoreAsync(
        string accountId,
        string messageId,
        string? callerEmployeeId,
        MessageFlags flag,
        bool add,
        Action<EmailMessage> applyLocalUpdate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID cannot be empty.", "INVALID_INPUT");

        if (ValidateLifecycleCaller(accountId, messageId, callerEmployeeId) is { } authFailure)
            return authFailure;

        var message = _emailMessages.GetById(accountId, messageId);
        if (message == null)
            return SpokesResult.Failure($"Message '{messageId}' not found.", "MESSAGE_NOT_FOUND");

        applyLocalUpdate(message);
        await UpdateServerFlagAsync(message, flag, add, cancellationToken);
        return SpokesResult.Success();
    }

    private Task UpdateServerFlagAsync(EmailMessage message, MessageFlags flag, bool add, CancellationToken cancellationToken = default) =>
        ExecuteFolderFlagUpdateAsync(
            message,
            async (folder, uid) =>
            {
                if (add)
                    await folder.AddFlagsAsync(uid, flag, true, cancellationToken);
                else
                    await folder.RemoveFlagsAsync(uid, flag, true, cancellationToken);
            },
            ex => _logger?.LogWarning(ex, "Failed to update flags"),
            cancellationToken);

    private async Task ExecuteFolderFlagUpdateAsync(
        EmailMessage message,
        Func<IMailFolder, UniqueId, Task> operation,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        if (message.IsLocalOnly) return;

        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(message.AccountId, cancellationToken);
            if (client == null) return;

            var folder = await client.GetFolderAsync(message.FolderPath, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var uid = new UniqueId(message.UniqueId);
            await operation(folder, uid);

            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            onError(ex);
        }
    }

    public Task<SpokesResult> MoveMessageAsync(string accountId, string messageId, string destinationFolderPath, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        MoveMessageCoreAsync(accountId, messageId, destinationFolderPath, callerEmployeeId, allowVirtualInboxSpecialFolderMove: false, cancellationToken);

    private async Task<SpokesResult> MoveMessageCoreAsync(string accountId, string messageId, string destinationFolderPath, string? callerEmployeeId, bool allowVirtualInboxSpecialFolderMove, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
            return SpokesResult.Failure("Destination folder path is required.", "INVALID_INPUT");

        if (ValidateLifecycleCaller(accountId, messageId, callerEmployeeId) is { } authFailure)
            return authFailure;

        if (!allowVirtualInboxSpecialFolderMove && _inboxService != null && !string.IsNullOrWhiteSpace(callerEmployeeId))
        {
            var caller = _employees?.GetById(callerEmployeeId);
            var acc = _accounts?.GetById(accountId);
            bool hasFullRealInboxAccess = caller != null && acc != null && !acc.IsArchived &&
                ((acc.IsServerManaged && (caller.IsAdmin || caller.HasPermission(AppPermissions.Admin.ManageSettings))) ||
                 acc.CanUserAccess(caller.Id, _inboxService.GetUserTeamIds(caller.Id)));
            if (!hasFullRealInboxAccess)
            {
                return SpokesResult.Failure("Virtual Inbox users cannot move messages to arbitrary folders.", "ACCESS_DENIED");
            }
        }

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

    public Task<SpokesResult> MoveMessageToTrashAsync(string accountId, string messageId, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        ResolveAndMoveToSpecialFolderAsync(
            accountId,
            messageId,
            callerEmployeeId,
            SpecialFolder.Trash,
            f => f.IsTrash,
            "Trash",
            "TRASH_FOLDER_NOT_FOUND",
            deleteIfAlreadyInFolder: true,
            cancellationToken);

    public Task<SpokesResult> ArchiveMessageAsync(string accountId, string messageId, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        ResolveAndMoveToSpecialFolderAsync(
            accountId,
            messageId,
            callerEmployeeId,
            SpecialFolder.Archive,
            f => f.IsArchive,
            "Archive",
            "ARCHIVE_FOLDER_NOT_FOUND",
            deleteIfAlreadyInFolder: false,
            cancellationToken);

    public Task<SpokesResult> JunkMessageAsync(string accountId, string messageId, string? callerEmployeeId = null, CancellationToken cancellationToken = default) =>
        ResolveAndMoveToSpecialFolderAsync(
            accountId,
            messageId,
            callerEmployeeId,
            SpecialFolder.Junk,
            f => f.IsJunk,
            "Junk",
            "JUNK_FOLDER_NOT_FOUND",
            deleteIfAlreadyInFolder: false,
            cancellationToken);

    private async Task<SpokesResult> ResolveAndMoveToSpecialFolderAsync(
        string accountId,
        string messageId,
        string? callerEmployeeId,
        SpecialFolder specialFolder,
        Func<EmailFolder, bool> flagPredicate,
        string label,
        string notFoundCode,
        bool deleteIfAlreadyInFolder,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");

        if (ValidateLifecycleCaller(accountId, messageId, callerEmployeeId) is { } authFailure)
            return authFailure;

        var folders = _emailFolders.GetByAccount(accountId);
        var targetFolder = folders.FirstOrDefault(flagPredicate)
            ?? folders.FirstOrDefault(f => MatchesSpecialFolderName(specialFolder, f.Path, f.Name))
            ?? await TryResolveSpecialFolderAsync(accountId, specialFolder, cancellationToken);

        if (targetFolder == null)
        {
            _logger?.LogWarning("No {FolderLabel} folder found for account {AccountId}", label, accountId);
            return SpokesResult.Failure($"No {label} folder found for account '{accountId}'.", notFoundCode);
        }

        if (deleteIfAlreadyInFolder)
        {
            var message = _emailMessages.GetById(accountId, messageId);
            if (message != null && message.FolderPath == targetFolder.Path)
            {
                return await DeleteMessageAsync(accountId, messageId, callerEmployeeId, cancellationToken);
            }
        }

        return await MoveMessageCoreAsync(accountId, messageId, targetFolder.Path, callerEmployeeId, allowVirtualInboxSpecialFolderMove: true, cancellationToken);
    }

    private static bool MatchesSpecialFolderName(SpecialFolder specialFolder, string fullPath, string name) =>
        specialFolder switch
        {
            SpecialFolder.Trash => fullPath.EndsWith("/Trash", StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith("/Bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Deleted", StringComparison.OrdinalIgnoreCase),
            SpecialFolder.Archive => fullPath.EndsWith("/All Mail", StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith("/Archive", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Archive", StringComparison.OrdinalIgnoreCase)
                || name.Equals("All Mail", StringComparison.OrdinalIgnoreCase),
            SpecialFolder.Junk => fullPath.EndsWith("/Spam", StringComparison.OrdinalIgnoreCase)
                || fullPath.EndsWith("/Junk", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Junk", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Spam", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    public async Task<SpokesResult> DeleteMessageAsync(string accountId, string messageId, string? callerEmployeeId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return SpokesResult.Failure("Account ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");

        if (ValidateLifecycleCaller(accountId, messageId, callerEmployeeId) is { } authFailure)
            return authFailure;

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

    private Task UpdateServerDeletionAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        ExecuteFolderFlagUpdateAsync(
            message,
            async (folder, uid) =>
            {
                await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
                await folder.ExpungeAsync(cancellationToken);
            },
            ex => _logger?.LogError(ex, "Failed to delete message from server: {MessageId}", message.Id),
            cancellationToken);

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
