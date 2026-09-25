using Microsoft.Extensions.Configuration;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;

namespace Spokes_Server.Core.Services.Core;

public class AccountDeletionService
{
    private readonly Database _db;
    private readonly PresenceStateService _presenceState;
    private readonly string _dataPath;

    public AccountDeletionService(Database db, IConfiguration config, PresenceStateService presenceState)
    {
        _db = db;
        _presenceState = presenceState;
        _dataPath = config["DataPath"] ?? "Data";
    }

    public const string SystemDeletedUserId = "system-deleted-user";

    public async Task DeleteAccountAsync(string employeeId, string? excludeSessionId = null)
    {
        // 1. Ensure System Deleted User exists
        var sysUser = _db.Employees.GetById(SystemDeletedUserId);
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
            _db.Employees.Save(sysUser);
        }
        else if (!sysUser.IsSystem || sysUser.IsActive)
        {
            sysUser.IsSystem = true;
            sysUser.IsActive = false;
            _db.Employees.Save(sysUser);
        }

        // 2. Revoke Access & Devices
        var sessions = _db.DeviceSessions.GetAll().Where(s => s.EmployeeId == employeeId).ToList();
        foreach (var session in sessions)
        {
            if (excludeSessionId != null && session.Id == excludeSessionId)
            {
                // Keep the current session briefly for the final logout redirect, 
                // but anonymize it and mark it revoked.
                session.EmployeeId = SystemDeletedUserId;
                session.RevokedAt = DateTime.UtcNow;
                _db.DeviceSessions.Save(session);
            }
            else
            {
                _presenceState.TriggerRemoteLogout(session.Id);
                _db.DeviceSessions.Delete(session.Id);
            }
        }

        var sessionsWithPush = _db.DeviceSessions.GetActiveByEmployeeId(employeeId).Where(s => s.HasPush).ToList();
        foreach (var session in sessionsWithPush)
        {
            _db.DeviceSessions.ClearPushFields(session);
        }

        // 3. Chat Anonymization & Attachment Wipe
        var messages = _db.ChatMessages.GetAll().Where(m => m.SenderId == employeeId).ToList();
        foreach (var msg in messages)
        {
            msg.SenderId = SystemDeletedUserId;

            if (msg.Attachments != null && msg.Attachments.Any())
            {
                foreach (var attachment in msg.Attachments)
                {
                    var fullPath = Path.Combine(_dataPath, attachment.FilePath);
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
                msg.Attachments.Clear();

                if (string.IsNullOrWhiteSpace(msg.Content))
                {
                    msg.Content = "[Deleted Image]";
                }
            }

            _db.ChatMessages.Save(msg);
        }

        // 4. Clean Channel Permissions & Memberships
        var channels = _db.ChatChannels.GetAll();
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
                _db.ChatChannels.Save(ch);
            }
        }

        // 5. Re-parent Other Shared Data
        var notes = _db.ProjectNotes.GetAll().Where(n => n.CreatedBy == employeeId).ToList();
        foreach (var note in notes)
        {
            note.CreatedBy = SystemDeletedUserId;
            _db.ProjectNotes.Save(note);
        }

        var reports = _db.ReportedMessages.GetAll().Where(r => r.ReporterId == employeeId).ToList();
        foreach (var report in reports)
        {
            report.ReporterId = SystemDeletedUserId;
            _db.ReportedMessages.Save(report);
        }

        // 6. Hard-delete Private Data
        var folders = _db.EmailFolders.GetAll().Where(f => f.EmployeeId == employeeId).ToList();
        foreach (var folder in folders) _db.EmailFolders.Delete(folder.Id);

        var emails = _db.EmailMessages.GetAll().Where(e => e.EmployeeId == employeeId).ToList();
        foreach (var email in emails) _db.EmailMessages.Delete(email.Id, email.EmployeeId);

        var contacts = _db.PrivateContacts.GetAllForEmployee(employeeId).ToList();
        foreach (var contact in contacts) _db.PrivateContacts.Delete(employeeId, contact.Id);

        // 7. Delete User Records
        var linkedAccounts = _db.OpenIdAccounts.GetByEmployeeId(employeeId);
        foreach (var account in linkedAccounts)
        {
            _db.OpenIdAccounts.Delete(account.Id);
        }

        _db.Employees.Delete(employeeId);

        // 8. Physical Directory Wipe
        var employeeDir = Path.Combine(_dataPath, "Employees", employeeId);
        if (Directory.Exists(employeeDir))
        {
            Directory.Delete(employeeDir, true);
        }

        await Task.CompletedTask;
    }
}
