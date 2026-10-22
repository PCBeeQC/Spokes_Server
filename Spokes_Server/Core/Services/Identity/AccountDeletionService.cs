using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Identity;

public class AccountDeletionService
{
    private readonly HrContext _hr;
    private readonly CoreContext _core;
    private readonly CommunicationContext _communication;
    private readonly ProjectsContext _projects;
    private readonly AccountingContext? _accounting;
    private readonly PresenceStateService _presenceState;
    private readonly SessionService? _sessionService;
    private readonly string _dataPath;
    private readonly ILogger<AccountDeletionService>? _logger;

    public AccountDeletionService(
        HrContext hr,
        CoreContext core,
        CommunicationContext communication,
        ProjectsContext projects,
        IConfiguration config,
        PresenceStateService presenceState,
        SessionService? sessionService = null,
        ILogger<AccountDeletionService>? logger = null,
        AccountingContext? accounting = null)
    {
        _hr = hr;
        _core = core;
        _communication = communication;
        _projects = projects;
        _accounting = accounting;
        _presenceState = presenceState;
        _sessionService = sessionService;
        _dataPath = config["DataPath"] ?? "Data";
        _logger = logger;
    }

    public const string SystemDeletedUserId = "system-deleted-user";

    public void DeleteAccount(string employeeId, string? excludeSessionId = null)
    {
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

        // 2. Revoke Access & Devices
        try
        {
            var sessions = _core.DeviceSessions.GetAll().Where(s => s.EmployeeId == employeeId).ToList();
            foreach (var session in sessions)
            {
                if (excludeSessionId != null && session.Id == excludeSessionId)
                {
                    // Keep the current session briefly for the final logout redirect, 
                    // but anonymize it and mark it revoked.
                    session.EmployeeId = SystemDeletedUserId;
                    session.RevokedAt = DateTime.UtcNow;
                    _core.DeviceSessions.Save(session);
                }
                else
                {
                    try
                    {
                        if (_sessionService != null)
                            _sessionService.TriggerRemoteLogout(session.Id);
                        else
                            _presenceState.TriggerRemoteLogout(session.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error triggering remote logout for session {SessionId}", session.Id);
                    }

                    _core.DeviceSessions.Delete(session.Id);
                }
            }

            var sessionsWithPush = _core.DeviceSessions.GetActiveByEmployeeId(employeeId).Where(s => s.HasPush).ToList();
            foreach (var session in sessionsWithPush)
            {
                _core.DeviceSessions.ClearPushFields(session);
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
                        try
                        {
                            var fullPath = Path.Combine(_dataPath, attachment.FilePath);
                            if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to physically delete attachment file {FilePath} for message {MessageId} during account deletion", attachment.FilePath, msg.Id);
                        }
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
            var reports = _communication.ReportedMessages.GetAll().Where(r => r.ReporterId == employeeId).ToList();
            foreach (var report in reports)
            {
                report.ReporterId = SystemDeletedUserId;
                _communication.ReportedMessages.Save(report);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error reparenting reported messages for employee {EmployeeId}", employeeId);
        }

        // 6. Hard-delete Private Data
        try
        {
            var folders = _communication.EmailFolders.GetAll().Where(f => f.EmployeeId == employeeId).ToList();
            foreach (var folder in folders) _communication.EmailFolders.Delete(folder.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting email folders for employee {EmployeeId}", employeeId);
        }

        try
        {
            var emails = _communication.EmailMessages.GetAllForEmployee(employeeId);
            foreach (var email in emails) _communication.EmailMessages.Delete(email.EmployeeId, email.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting email messages for employee {EmployeeId}", employeeId);
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
}
