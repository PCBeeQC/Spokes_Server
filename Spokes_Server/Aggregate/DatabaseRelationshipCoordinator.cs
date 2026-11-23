using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Aggregate;

/// <summary>
/// Coordinates cross-entity relationship maintenance and cascading metadata updates.
/// Decouples side-effects and repository event subscriptions from Database initialization.
/// </summary>
public class DatabaseRelationshipCoordinator : IHostedService
{
    private readonly Database _db;
    private readonly EmailIdleService? _idleService;
    private readonly EmailSyncStateService? _syncStateService;
    private bool _isRegistered;

    public DatabaseRelationshipCoordinator(Database db)
        : this(db, null, null)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DatabaseRelationshipCoordinator(
        Database db,
        EmailIdleService? idleService = null,
        EmailSyncStateService? syncStateService = null)
    {
        _db = db;
        _idleService = idleService;
        _syncStateService = syncStateService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterSubscriptions();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Binds cross-repository event handlers to keep denormalized references and project metadata in sync.
    /// This method is idempotent.
    /// </summary>
    public void RegisterSubscriptions()
    {
        if (_isRegistered) return;
        _isRegistered = true;

        _db.OnInitialized += LinkEmployeePermissionGroups;
        LinkEmployeePermissionGroups();

        // Auto-update Project LastEdited
        void TriggerProjectUpdate(string? projectId)
        {
            if (!string.IsNullOrEmpty(projectId))
            {
                var project = _db.Projects.GetById(projectId);
                if (project != null) _db.Projects.Save(project);
            }
        }

        _db.Quotes.OnSaved += q => TriggerProjectUpdate(q.ProjectId);
        _db.Quotes.OnDeleted += q => TriggerProjectUpdate(q.ProjectId);

        _db.Invoices.OnSaved += i => TriggerProjectUpdate(i.ProjectId);
        _db.Invoices.OnDeleted += i => TriggerProjectUpdate(i.ProjectId);

        _db.ProjectNotes.OnSaved += n => TriggerProjectUpdate(n.ProjectId);
        _db.ProjectNotes.OnDeleted += n => TriggerProjectUpdate(n.ProjectId);

        _db.ProjectDocuments.OnSaved += d => TriggerProjectUpdate(d.ProjectId);
        _db.ProjectDocuments.OnDeleted += d => TriggerProjectUpdate(d.ProjectId);

        if (_db.ProjectsContext?.ProjectReports != null)
        {
            _db.ProjectReports.OnSaved += r => TriggerProjectUpdate(r.ProjectId);
            _db.ProjectReports.OnDeleted += r => TriggerProjectUpdate(r.ProjectId);
        }

        // Maintain Dynamic Permission Group References
        _db.CompanyProfile.OnSaved += profile => 
        {
            foreach (var employee in _db.Employees.GetAll())
            {
                HydrateEmployeePermissionGroups(employee, profile);
            }
        };

        _db.Employees.OnSaved += employee => 
        {
            var profile = _db.CompanyProfile.Get();
            HydrateEmployeePermissionGroups(employee, profile);
        };

        // Email Real Inbox / Virtual Inbox / Sending Identity Cascading Cleanup & Unread Count Sync
        if (_db.CommunicationContext?.EmailAccounts != null)
        {
            _db.EmailAccounts.OnSaved += HandleEmailAccountSaved;
            _db.EmailAccounts.OnDeleted += HandleEmailAccountDeleted;
        }

        if (_db.CommunicationContext?.EmailInboxes != null)
        {
            _db.EmailInboxes.OnSaved += HandleEmailInboxSaved;
            _db.EmailInboxes.OnDeleted += HandleEmailInboxDeleted;
        }

        if (_db.CommunicationContext?.EmailFolders != null)
        {
            _db.EmailFolders.OnDeleted += HandleEmailFolderDeleted;
        }

        if (_db.CommunicationContext?.EmailSendingIdentities != null)
        {
            _db.EmailSendingIdentities.OnDeleted += HandleEmailSendingIdentityDeleted;
        }
    }

    private void HandleEmailAccountSaved(EmailAccount account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Id)) return;

        if (account.IsArchived || !account.Enabled)
        {
            _idleService?.StopWatchingAccount(account.Id);
        }

        _syncStateService?.NotifyUnreadCountChanged(account.Id);
    }

    private void HandleEmailInboxSaved(EmailInbox inbox)
    {
        if (inbox == null || string.IsNullOrWhiteSpace(inbox.AccountId)) return;
        _syncStateService?.NotifyUnreadCountChanged(inbox.AccountId);
    }

    private void HandleEmailFolderDeleted(EmailFolder folder)
    {
        if (folder == null || string.IsNullOrWhiteSpace(folder.AccountId)) return;
        _syncStateService?.NotifyUnreadCountChanged(folder.AccountId);
    }

    private void HandleEmailAccountDeleted(EmailAccount account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Id)) return;

        _idleService?.StopWatchingAccount(account.Id);

        if (_db.CommunicationContext?.EmailInboxes != null)
        {
            foreach (var inbox in _db.EmailInboxes.GetByAccount(account.Id).ToList())
            {
                _db.EmailInboxes.Delete(inbox.Id);
            }
        }

        if (_db.CommunicationContext?.EmailSendingIdentities != null)
        {
            var identitiesToDelete = _db.EmailSendingIdentities.GetAll()
                .Where(i =>
                    (!string.IsNullOrWhiteSpace(account.AutoCreatedIdentityId) && string.Equals(i.Id, account.AutoCreatedIdentityId, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(i.SourceAccountId, account.Id, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id)
                .ToList();

            foreach (var identityId in identitiesToDelete)
            {
                _db.EmailSendingIdentities.Delete(identityId);
            }
        }

        _db.CommunicationContext?.EmailFolders?.DeleteByAccount(account.Id);
        _db.CommunicationContext?.EmailMessages?.DeleteByAccount(account.Id);
        _syncStateService?.RemoveAccount(account.Id);
    }

    private void HandleEmailInboxDeleted(EmailInbox inbox)
    {
        if (inbox == null || string.IsNullOrWhiteSpace(inbox.Id)) return;

        if (_db.CommunicationContext?.EmailSendingIdentities != null)
        {
            var sourcedIdentities = _db.EmailSendingIdentities.GetAll()
                .Where(i => i.SourceType == EmailIdentitySourceType.VirtualInbox &&
                            string.Equals(i.SourceInboxId, inbox.Id, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Id)
                .ToList();

            foreach (var identityId in sourcedIdentities)
            {
                _db.EmailSendingIdentities.Delete(identityId);
            }
        }

        if (!string.IsNullOrWhiteSpace(inbox.AccountId))
        {
            _syncStateService?.NotifyUnreadCountChanged(inbox.AccountId);
        }
    }

    private void HandleEmailSendingIdentityDeleted(EmailSendingIdentity identity)
    {
        if (identity == null || string.IsNullOrWhiteSpace(identity.Id)) return;

        if (_db.CommunicationContext?.EmailAccounts != null)
        {
            foreach (var acc in _db.EmailAccounts.GetAll().ToList())
            {
                bool modified = false;
                if (acc.LinkedIdentityIds != null && acc.LinkedIdentityIds.RemoveAll(id => string.Equals(id, identity.Id, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    modified = true;
                }
                if (string.Equals(acc.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    acc.DefaultIdentityId = acc.LinkedIdentityIds?.FirstOrDefault();
                    modified = true;
                }
                if (string.Equals(acc.AutoCreatedIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    acc.AutoCreatedIdentityId = null;
                    modified = true;
                }
                if (modified)
                {
                    _db.EmailAccounts.Save(acc);
                }
            }
        }

        if (_db.CommunicationContext?.EmailInboxes != null)
        {
            foreach (var inbox in _db.EmailInboxes.GetAll().ToList())
            {
                bool modified = false;
                if (inbox.LinkedIdentityIds != null && inbox.LinkedIdentityIds.RemoveAll(id => string.Equals(id, identity.Id, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    modified = true;
                }
                if (string.Equals(inbox.DefaultIdentityId, identity.Id, StringComparison.OrdinalIgnoreCase))
                {
                    inbox.DefaultIdentityId = inbox.LinkedIdentityIds?.FirstOrDefault();
                    modified = true;
                }
                if (modified)
                {
                    _db.EmailInboxes.Save(inbox);
                }
            }
        }
    }

    private static void HydrateEmployeePermissionGroups(Spokes_Server.Core.Models.HR.Employee employee, Spokes_Server.Core.Models.Core.CompanyProfile? profile)
    {
        if (profile != null && employee.PermissionGroupIds.Count > 0)
        {
            employee.PermissionGroups = profile.PermissionGroups
                .Where(g => employee.PermissionGroupIds.Contains(g.Id))
                .ToList();
        }
        else
        {
            employee.PermissionGroups = [];
        }
    }

    /// <summary>
    /// Explicitly links in-memory PermissionGroup references for all employees based on CompanyProfile.
    /// </summary>
    public void LinkEmployeePermissionGroups()
    {
        var profile = _db.CompanyProfile?.Get();
        if (profile != null && _db.Employees != null)
        {
            foreach (var employee in _db.Employees.GetAll())
            {
                HydrateEmployeePermissionGroups(employee, profile);
            }
        }
    }
}
