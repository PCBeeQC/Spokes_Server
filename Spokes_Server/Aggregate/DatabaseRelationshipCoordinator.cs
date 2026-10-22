using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Spokes_Server.Aggregate;

/// <summary>
/// Coordinates cross-entity relationship maintenance and cascading metadata updates.
/// Decouples side-effects and repository event subscriptions from Database initialization.
/// </summary>
public class DatabaseRelationshipCoordinator : IHostedService
{
    private readonly Database _db;
    private bool _isRegistered;

    public DatabaseRelationshipCoordinator(Database db)
    {
        _db = db;
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
