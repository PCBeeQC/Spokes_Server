using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Email;

public class EmailMessageLifecycleService : IEmailMessageLifecycleService
{
    private readonly EmployeeRepository _employees;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EncryptionService _encryptionService;
    private readonly EmailSyncStateService _syncStateService;
    private readonly IEmailCacheMaintenanceService _cacheMaintenance;
    private readonly ILogger<EmailMessageLifecycleService>? _logger;
    private readonly IImapConnectionFactory _imapFactory;

    public EmailMessageLifecycleService(
        EmployeeRepository employees,
        CompanyProfileRepository companyProfile,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        EncryptionService encryptionService,
        EmailSyncStateService syncStateService,
        IEmailCacheMaintenanceService cacheMaintenance,
        ILogger<EmailMessageLifecycleService>? logger = null,
        IImapConnectionFactory? imapFactory = null)
    {
        _employees = employees;
        _companyProfile = companyProfile;
        _emailFolders = emailFolders;
        _emailMessages = emailMessages;
        _encryptionService = encryptionService;
        _syncStateService = syncStateService;
        _cacheMaintenance = cacheMaintenance;
        _logger = logger;
        _imapFactory = imapFactory ?? new ImapConnectionFactory(companyProfile, employees, encryptionService);
    }

    public Task<EmailMessage?> GetMessageAsync(string employeeId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(employeeId) || string.IsNullOrWhiteSpace(messageId))
            return Task.FromResult<EmailMessage?>(null);

        var msg = _emailMessages.GetById(employeeId, messageId);
        return Task.FromResult(msg);
    }

    public async Task<SpokesResult> MarkMessageReadAsync(string employeeId, string messageId, bool isRead, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID cannot be empty.", "INVALID_INPUT");

        var message = _emailMessages.GetById(employeeId, messageId);
        if (message == null)
            return SpokesResult.Failure($"Message '{messageId}' not found.", "MESSAGE_NOT_FOUND");

        message.IsRead = isRead;
        _emailMessages.Save(message);

        await UpdateServerFlagAsync(message, MessageFlags.Seen, isRead, cancellationToken);
        _syncStateService.NotifyUnreadCountChanged(message.EmployeeId);
        return SpokesResult.Success();
    }


    public async Task<SpokesResult> MarkMessageFlaggedAsync(string employeeId, string messageId, bool isFlagged, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID cannot be empty.", "INVALID_INPUT");

        var message = _emailMessages.GetById(employeeId, messageId);
        if (message == null)
            return SpokesResult.Failure($"Message '{messageId}' not found.", "MESSAGE_NOT_FOUND");

        message.IsFlagged = isFlagged;
        _emailMessages.Save(message);

        await UpdateServerFlagAsync(message, MessageFlags.Flagged, isFlagged, cancellationToken);
        return SpokesResult.Success();
    }


    private async Task UpdateServerFlagAsync(EmailMessage message, MessageFlags flag, bool add, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(message.EmployeeId, cancellationToken);
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

    public async Task<SpokesResult> MoveMessageAsync(string employeeId, string messageId, string destinationFolderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(destinationFolderPath))
            return SpokesResult.Failure("Destination folder path is required.", "INVALID_INPUT");

        var message = _emailMessages.GetById(employeeId, messageId);
        if (message == null || message.EmployeeId != employeeId)
            return SpokesResult.Failure($"Message '{messageId}' not found for employee '{employeeId}'.", "MESSAGE_NOT_FOUND");

        var sourceFolderPath = message.FolderPath;
        var sourceUid = message.UniqueId;
        if (sourceFolderPath == destinationFolderPath)
            return SpokesResult.Success();

        var employee = _employees.GetById(employeeId);
        var settings = _companyProfile.Get()?.EmailSettings;

        if (employee == null || settings == null)
            return SpokesResult.Failure("Employee or email settings not found.", "CONFIGURATION_ERROR");

        var folders = _emailFolders.GetByEmployee(employeeId);
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
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(employeeId, cancellationToken);
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

        // Remote move confirmed: update local folder references and message persistence
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

        _syncStateService.NotifyUnreadCountChanged(employeeId);
        return SpokesResult.Success();
    }

    public async Task<SpokesResult> MoveMessageToTrashAsync(string employeeId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByEmployee(employeeId);
        var trashFolder = folders.FirstOrDefault(f => f.IsTrash)
            ?? folders.FirstOrDefault(f => f.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Deleted", StringComparison.OrdinalIgnoreCase));

        if (trashFolder == null)
        {
            _logger?.LogWarning("No Trash folder found for employee {EmployeeId}", employeeId);
            return SpokesResult.Failure($"No Trash folder found for employee '{employeeId}'.", "TRASH_FOLDER_NOT_FOUND");
        }

        var message = _emailMessages.GetById(employeeId, messageId);
        if (message != null && message.FolderPath == trashFolder.Path)
        {
            return await DeleteMessageAsync(employeeId, messageId, cancellationToken);
        }

        return await MoveMessageAsync(employeeId, messageId, trashFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> ArchiveMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByEmployee(employeeId);
        var archiveFolder = folders.FirstOrDefault(f => f.IsArchive)
            ?? folders.FirstOrDefault(f => f.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase));

        if (archiveFolder == null)
        {
            _logger?.LogWarning("No Archive folder found for employee {EmployeeId}", employeeId);
            return SpokesResult.Failure($"No Archive folder found for employee '{employeeId}'.", "ARCHIVE_FOLDER_NOT_FOUND");
        }

        return await MoveMessageAsync(employeeId, messageId, archiveFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> JunkMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");

        var folders = _emailFolders.GetByEmployee(employeeId);
        var junkFolder = folders.FirstOrDefault(f => f.IsJunk)
            ?? folders.FirstOrDefault(f => f.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Junk Email", StringComparison.OrdinalIgnoreCase)
                                        || f.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase));

        if (junkFolder == null)
        {
            _logger?.LogWarning("No Junk folder found for employee {EmployeeId}", employeeId);
            return SpokesResult.Failure($"No Junk folder found for employee '{employeeId}'.", "JUNK_FOLDER_NOT_FOUND");
        }

        return await MoveMessageAsync(employeeId, messageId, junkFolder.Path, cancellationToken);
    }

    public async Task<SpokesResult> DeleteMessageAsync(string employeeId, string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return SpokesResult.Failure("Employee ID is required.", "INVALID_INPUT");
        if (string.IsNullOrWhiteSpace(messageId))
            return SpokesResult.Failure("Message ID is required.", "INVALID_INPUT");

        var message = _emailMessages.GetById(employeeId, messageId);
        if (message == null || message.EmployeeId != employeeId)
            return SpokesResult.Failure($"Message '{messageId}' not found for employee '{employeeId}'.", "MESSAGE_NOT_FOUND");

        await UpdateServerDeletionAsync(message, cancellationToken);

        _emailMessages.Delete(employeeId, messageId);

        var folder = _emailFolders.GetByEmployee(employeeId).FirstOrDefault(f => f.Path == message.FolderPath);
        if (folder != null)
        {
            folder.TotalCount = Math.Max(0, folder.TotalCount - 1);
            if (!message.IsRead) folder.UnreadCount = Math.Max(0, folder.UnreadCount - 1);
            _emailFolders.Save(folder);
        }

        await _cacheMaintenance.DeleteLocalMessageFilesAsync(employeeId, messageId);

        _syncStateService.NotifyUnreadCountChanged(employeeId);
        return SpokesResult.Success();
    }

    private async Task UpdateServerDeletionAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = await _imapFactory.CreateAuthenticatedClientAsync(message.EmployeeId, cancellationToken);
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
}
