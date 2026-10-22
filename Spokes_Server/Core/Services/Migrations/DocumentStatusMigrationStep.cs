using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;

namespace Spokes_Server.Core.Services.Migrations;

/// <summary>
/// Migrates legacy 'Sent' status documents (Quotes, Invoices, ProjectDocuments) to 'Final'.
/// </summary>
public class DocumentStatusMigrationStep : IMigrationStep
{
    private readonly AccountingContext _accounting;
    private readonly ProjectsContext _projects;
    private readonly ILogger<DocumentStatusMigrationStep>? _logger;

    public string MigrationKey => "20260104_DocumentStatusSentToFinal";
    public string Description => "Migrates legacy 'Sent' status documents (Quotes, Invoices, ProjectDocuments) to 'Final'";
    public int Order => 40;

    [ActivatorUtilitiesConstructor]
    public DocumentStatusMigrationStep(
        AccountingContext accounting,
        ProjectsContext projects,
        ILogger<DocumentStatusMigrationStep>? logger = null)
    {
        _accounting = accounting;
        _projects = projects;
        _logger = logger;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        int migrated = 0;

        foreach (var q in _projects.Quotes.GetAll().Where(q => q.Status == "Sent"))
        {
            if (cancellationToken.IsCancellationRequested) break;
            q.Status = "Final";
            _projects.Quotes.Save(q);
            migrated++;
        }

        foreach (var inv in _accounting.Invoices.GetAll().Where(i => i.Status == "Sent"))
        {
            if (cancellationToken.IsCancellationRequested) break;
            inv.Status = "Final";
            _accounting.Invoices.Save(inv);
            migrated++;
        }

        foreach (var doc in _projects.ProjectDocuments.GetAll().Where(d => d.Status == "Sent"))
        {
            if (cancellationToken.IsCancellationRequested) break;
            doc.Status = "Final";
            _projects.ProjectDocuments.Save(doc);
            migrated++;
        }

        if (migrated > 0)
        {
            _logger?.LogInformation("[DocumentStatusMigrationStep] Updated {Count} document(s) from 'Sent' to 'Final'.", migrated);
        }

        return Task.CompletedTask;
    }
}
