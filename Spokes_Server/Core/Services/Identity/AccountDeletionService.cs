using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Identity;

public class AccountDeletionService
{
    private readonly HrContext _hr;
    private readonly CoreContext _core;
    private readonly CommunicationContext _communication;
    private readonly ProjectsContext _projects;
    private readonly SessionService? _sessionService;
    private readonly GlobalKeystoreService? _globalKeystore;
    private readonly FileTokenService? _fileTokenService;
    private readonly IFileService? _fileService;
    private readonly string _dataPath;
    private readonly ILogger<AccountDeletionService>? _logger;

    public AccountDeletionService(
        HrContext hr,
        CoreContext core,
        CommunicationContext communication,
        ProjectsContext projects,
        IConfiguration config,
        SessionService? sessionService = null,
        ILogger<AccountDeletionService>? logger = null,
        GlobalKeystoreService? globalKeystore = null,
        FileTokenService? fileTokenService = null,
        IFileService? fileService = null)
    {
        _hr = hr;
        _core = core;
        _communication = communication;
        _projects = projects;
        _sessionService = sessionService;
        _globalKeystore = globalKeystore;
        _fileTokenService = fileTokenService;
        _fileService = fileService;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public const string SystemDeletedUserId = "system-deleted-user";

    public void DeleteAccount(string employeeId, string? excludeSessionId = null)
    {
        if (UserService.IsSoleActiveAdmin(_hr.Employees, employeeId))
        {
            throw new InvalidOperationException("Cannot delete the sole active administrator account. Promote another active administrator first.");
        }

        _logger?.LogInformation("Starting account deletion workflow for employee {EmployeeId}", employeeId);

        // 1. Ensure System Deleted User exists
        try
        {
            var sysUser = _hr.Employees.GetById(SystemDeletedUserId);
            if (sysUser == null)
            {
                sysUser = new Employee
                {
                    Id = SystemDeletedUserId,
                    FirstName = "Deleted",
                    LastName = "User",
                    Email = "deleted@system.local",
                    IsActive = false,
                    IsSystem = true
                };
                _hr.Employees.Save(sysUser);
            }
            else if (!sysUser.IsSystem || sysUser.IsActive)
            {
                sysUser.IsSystem = true;
                sysUser.IsActive = false;
                _hr.Employees.Save(sysUser);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to ensure system-deleted user exists during deletion of employee {EmployeeId}", employeeId);
            throw;
        }

        // 2. Revoke Access, Vault Keys & Devices
        try
        {
            _globalKeystore?.LockVault(employeeId);
            _fileTokenService?.InvalidateUserEncryptedTokens(employeeId);

            var sessions = _core.DeviceSessions.GetAll().Where(s => s.EmployeeId == employeeId).ToList();
            foreach (var session in sessions)
            {
                if (excludeSessionId != null && session.Id == excludeSessionId)
                {
                    // Keep the current session briefly for the final logout redirect, 
                    // but anonymize it, clear push credentials, and mark it revoked.
                    session.EmployeeId = SystemDeletedUserId;
                    session.HasVaultCookie = false;
                    session.VaultNonce = Guid.NewGuid().ToString("N");
                    session.RevokedAt = DateTime.UtcNow;
                    if (session.HasPush)
                    {
                        _core.DeviceSessions.ClearPushFields(session);
                    }
                    else
                    {
                        _core.DeviceSessions.Save(session);
                    }
                }
                else
                {
                    try
                    {
                        _sessionService?.TriggerRemoteLogout(session.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error triggering remote logout for session {SessionId}", session.Id);
                    }

                    _core.DeviceSessions.Delete(session.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error revoking sessions for employee {EmployeeId}", employeeId);
        }

        // 3. Chat Anonymization & Attachment Wipe
        try
        {
            var messages = _communication.ChatMessages.GetAllForSender(employeeId);
            foreach (var msg in messages)
            {
                msg.SenderId = SystemDeletedUserId;

                if (msg.Attachments != null && msg.Attachments.Any())
                {
                    foreach (var attachment in msg.Attachments)
                    {
                        StoredFileCleanupHelper.DeleteFileWithThumbnail(_dataPath, _fileService, attachment.FilePath, _logger);
                    }
                    msg.Attachments.Clear();

                    if (string.IsNullOrWhiteSpace(msg.Content))
                    {
                        msg.Content = "[Deleted Image]";
                    }
                }

                try
                {
                    _communication.ChatMessages.Save(msg);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to save anonymized chat message {MessageId}", msg.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error anonymizing chat messages for employee {EmployeeId}", employeeId);
        }

        // 4. Clean Channel Permissions & Memberships
        try
        {
            var channels = _communication.ChatChannels.GetAll();
            foreach (var ch in channels)
            {
                var modified = false;
                if (ch.ParticipantIds != null && ch.ParticipantIds.Remove(employeeId))
                {
                    modified = true;
                }
                if (ch.AllowedPostUserIds != null && ch.AllowedPostUserIds.Remove(employeeId))
                {
                    modified = true;
                }
                if (modified)
                {
                    _communication.ChatChannels.Save(ch);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error updating channel memberships for employee {EmployeeId}", employeeId);
        }

        // 5. Re-parent Other Shared Data
        try
        {
            var notes = _projects.ProjectNotes.GetAll().Where(n => n.CreatedBy == employeeId).ToList();
            foreach (var note in notes)
            {
                note.CreatedBy = SystemDeletedUserId;
                _projects.ProjectNotes.Save(note);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reparenting project notes for employee {EmployeeId}", employeeId);
        }

        try
        {
            var reports = _communication.ReportedMessages.GetAll()
                .Where(r => string.Equals(r.ReporterId, employeeId, StringComparison.Ordinal) ||
                            string.Equals(r.ReportedUserId, employeeId, StringComparison.Ordinal))
                .ToList();
            foreach (var report in reports)
            {
                if (string.Equals(report.ReporterId, employeeId, StringComparison.Ordinal))
                {
                    report.ReporterId = SystemDeletedUserId;
                }

                if (string.Equals(report.ReportedUserId, employeeId, StringComparison.Ordinal))
                {
                    report.ReportedUserId = SystemDeletedUserId;
                    if (report.Attachments != null)
                    {
                        foreach (var att in report.Attachments)
                        {
                            StoredFileCleanupHelper.DeleteFileWithThumbnail(_dataPath, _fileService, att.FilePath, _logger);
                        }
                        report.Attachments.Clear();
                    }
                    report.SnapshotContent = ReportedMessageResolution.DeletedContentPlaceholder;

                    if (string.IsNullOrWhiteSpace(report.Status) ||
                        string.Equals(report.Status, ReportedMessageStatus.Open, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Status = ReportedMessageStatus.Resolved;
                        report.ResolutionAction = ReportedMessageResolution.DeletedMessage;
                        report.ResolvedBy = "System";
                        report.ResolvedAt = DateTime.UtcNow;
                    }
                }

                _communication.ReportedMessages.Save(report);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reparenting reported messages for employee {EmployeeId}", employeeId);
        }

        try
        {
            if (_communication.ChatPolls != null)
            {
                var polls = _communication.ChatPolls.GetAll().ToList();
                foreach (var poll in polls)
                {
                    var modified = false;
                    if (poll.CreatorId == employeeId)
                    {
                        poll.CreatorId = SystemDeletedUserId;
                        modified = true;
                    }
                    foreach (var opt in poll.Options)
                    {
                        if (opt.AddedByUserId == employeeId)
                        {
                            opt.AddedByUserId = SystemDeletedUserId;
                            modified = true;
                        }
                        if (opt.VoterIds.Remove(employeeId))
                        {
                            modified = true;
                        }
                    }
                    if (modified)
                    {
                        _communication.ChatPolls.Save(poll);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error anonymizing chat polls for employee {EmployeeId}", employeeId);
        }

        // 6. Hard-delete Private Data
        try
        {
            CleanupOwnedOrSharedResources(
                _communication.EmailAccounts.GetAll().ToList(),
                employeeId,
                a => a.OwnerEmployeeId,
                a => a.AllowedUserIds,
                a => a.AllowedTeamIds,
                a =>
                {
                    _communication.EmailFolders.DeleteByAccount(a.Id);
                    _communication.EmailMessages.DeleteByAccount(a.Id);
                    _communication.EmailAccounts.Delete(a.Id);
                },
                a => _communication.EmailAccounts.Save(a));

            _communication.EmailFolders.DeleteByAccount(employeeId);
            _communication.EmailMessages.DeleteByAccount(employeeId);

            CleanupOwnedOrSharedResources(
                _communication.EmailInboxes.GetAll().ToList(),
                employeeId,
                i => i.OwnerEmployeeId,
                i => i.AllowedUserIds,
                i => i.AllowedTeamIds,
                i => _communication.EmailInboxes.Delete(i.Id),
                i => _communication.EmailInboxes.Save(i));

            CleanupOwnedOrSharedResources(
                _communication.EmailSendingIdentities.GetAll().ToList(),
                employeeId,
                id => id.OwnerEmployeeId,
                id => id.AllowedUserIds,
                id => id.AllowedTeamIds,
                id => _communication.EmailSendingIdentities.Delete(id.Id),
                id => _communication.EmailSendingIdentities.Save(id));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting email accounts/folders/messages for employee {EmployeeId}", employeeId);
        }

        try
        {
            var contacts = _communication.PrivateContacts.GetAllForEmployee(employeeId).ToList();
            foreach (var contact in contacts) _communication.PrivateContacts.Delete(employeeId, contact.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting private contacts for employee {EmployeeId}", employeeId);
        }

        try
        {
            if (_communication.Albums != null)
            {
                foreach (var album in _communication.Albums.GetAll().ToList())
                {
                    if (string.Equals(album.OwnerId, employeeId, StringComparison.Ordinal))
                    {
                        _fileTokenService?.InvalidateContextTokens("albums", album.Id);
                        if (album.Media != null)
                        {
                            foreach (var media in album.Media.ToList())
                            {
                                StoredFileCleanupHelper.DeleteFileWithThumbnail(_dataPath, _fileService, media.FilePath, _logger);
                            }
                        }

                        _communication.Albums.Delete(album.Id);

                        var albumUploadsDir = Path.Combine(_dataPath, "Uploads", "albums", album.Id);
                        if (Directory.Exists(albumUploadsDir) &&
                            Directory.GetFiles(albumUploadsDir).Length == 0 &&
                            Directory.GetDirectories(albumUploadsDir).Length == 0)
                        {
                            Directory.Delete(albumUploadsDir);
                        }
                    }
                    else
                    {
                        bool modified = album.ContributorUserIds != null && album.ContributorUserIds.Remove(employeeId);
                        if (album.EncryptedAlbumKeys != null && album.EncryptedAlbumKeys.Remove(employeeId))
                        {
                            modified = true;
                        }

                        if (modified)
                        {
                            _communication.Albums.Save(album);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error cleaning up albums for employee {EmployeeId}", employeeId);
        }

        // 7. Delete User Records
        try
        {
            var linkedAccounts = _core.OpenIdAccounts.GetByEmployeeId(employeeId);
            foreach (var account in linkedAccounts)
            {
                _core.OpenIdAccounts.Delete(account.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting OpenID accounts for employee {EmployeeId}", employeeId);
        }

        try
        {
            _hr.Employees.Delete(employeeId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting employee record for employee {EmployeeId}", employeeId);
            throw;
        }

        // 8. Physical Directory Wipe
        try
        {
            var employeeDir = Path.Combine(_dataPath, "Employees", employeeId);
            if (Directory.Exists(employeeDir))
            {
                Directory.Delete(employeeDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to physically delete directory for employee {EmployeeId}", employeeId);
        }

        _logger?.LogInformation("Completed account deletion workflow for employee {EmployeeId}", employeeId);
    }

    private static void CleanupOwnedOrSharedResources<T>(
        IEnumerable<T> items,
        string employeeId,
        Func<T, string?> getOwnerId,
        Func<T, List<string>> getAllowedUsers,
        Func<T, List<string>> getAllowedTeams,
        Action<T> deleteItem,
        Action<T> saveItem)
    {
        foreach (var item in items)
        {
            var allowedUsers = getAllowedUsers(item);
            if (getOwnerId(item) == employeeId && allowedUsers.Count == 0 && getAllowedTeams(item).Count == 0)
            {
                deleteItem(item);
            }
            else if (allowedUsers.Remove(employeeId))
            {
                saveItem(item);
            }
        }
    }
}
