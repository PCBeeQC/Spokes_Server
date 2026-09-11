using Microsoft.Extensions.Configuration;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using System.IO;

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

    public async Task DeleteAccountAsync(string employeeId, string? excludeSessionId = null)
    {
        var systemDeletedUserId = "system-deleted-user";

        // 1. Ensure System Deleted User exists
        var sysUser = _db.Employees.GetById(systemDeletedUserId);
        if (sysUser == null)
        {
            sysUser = new Employee
            {
                Id = systemDeletedUserId,
                FirstName = "Deleted",
                LastName = "User",
                Email = "deleted@system.local",
                IsActive = false
            };
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
                session.EmployeeId = systemDeletedUserId;
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
            msg.SenderId = systemDeletedUserId;

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

        // 4. Re-parent Other Shared Data
        var notes = _db.ProjectNotes.GetAll().Where(n => n.CreatedBy == employeeId).ToList();
        foreach (var note in notes)
        {
            note.CreatedBy = systemDeletedUserId;
            _db.ProjectNotes.Save(note);
        }

        var reports = _db.ReportedMessages.GetAll().Where(r => r.ReporterId == employeeId).ToList();
        foreach (var report in reports)
        {
            report.ReporterId = systemDeletedUserId;
            _db.ReportedMessages.Save(report);
        }

        // 5. Hard-delete Private Data
        var folders = _db.EmailFolders.GetAll().Where(f => f.EmployeeId == employeeId).ToList();
        foreach (var folder in folders) _db.EmailFolders.Delete(folder.Id);

        var emails = _db.EmailMessages.GetAll().Where(e => e.EmployeeId == employeeId).ToList();
        foreach (var email in emails) _db.EmailMessages.Delete(email.Id, email.EmployeeId);

        var contacts = _db.PrivateContacts.GetAllForEmployee(employeeId).ToList();
        foreach (var contact in contacts) _db.PrivateContacts.Delete(employeeId, contact.Id);

        // 6. Delete User Records
        var employee = _db.Employees.GetById(employeeId);
        var linkedAccounts = _db.OpenIdAccounts.GetByEmployeeId(employeeId);
        foreach (var account in linkedAccounts)
        {
            _db.OpenIdAccounts.Delete(account.Id);
        }


        _db.Employees.Delete(employeeId);

        // 7. Physical Directory Wipe
        var employeeDir = Path.Combine(_dataPath, "Employees", employeeId);
        if (Directory.Exists(employeeDir))
        {
            Directory.Delete(employeeDir, true);
        }

        await Task.CompletedTask;
    }
}
