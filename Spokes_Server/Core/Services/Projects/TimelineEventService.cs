using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Projects;

public class TimelineEventService : ITimelineEventService
{
    private readonly ProjectsContext _projects;

    [ActivatorUtilitiesConstructor]
    public TimelineEventService(ProjectsContext projects)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    }

    private void RemoveSystemEvent(string projectId, string refId)
    {
        if (string.IsNullOrEmpty(projectId)) return;
        var existing = _projects.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == refId);
        if (existing != null)
        {
            _projects.ProjectNotes.Delete(existing.Id);
        }
    }

    private void AddSystemEvent(string projectId, string title, string content, string refId, string? user = null)
    {
        if (string.IsNullOrEmpty(projectId)) return;

        // Remove if it already exists to avoid duplicates
        RemoveSystemEvent(projectId, refId);

        var note = new ProjectNote
        {
            ProjectId = projectId,
            Date = DateTime.Today,
            Title = title,
            Content = content,
            Category = "Status Update",
            CreatedBy = string.IsNullOrEmpty(user) ? "System" : user,
            SystemReferenceId = refId,
            IsSystemEvent = true
        };
        _projects.ProjectNotes.Save(note);
    }

    public void SyncQuoteStatusTimelineEvent(Quote quote, string oldStatus, string newStatus, string? user = null)
    {
        if (quote == null || string.IsNullOrEmpty(quote.ProjectId) || oldStatus == newStatus) return;

        // Draft <-> Final
        if (oldStatus == QuoteStatus.Draft && newStatus == QuoteStatus.Final)
        {
            AddSystemEvent(quote.ProjectId, $"Quote #{quote.QuoteNumber} Finalized", $"Quote #{quote.QuoteNumber} was marked as Final.", $"Quote_Final_{quote.Id}", user);
        }
        else if (oldStatus == QuoteStatus.Final && newStatus == QuoteStatus.Draft)
        {
            RemoveSystemEvent(quote.ProjectId, $"Quote_Final_{quote.Id}");
        }

        // Accepted / Rejected
        if (newStatus == QuoteStatus.Accepted)
        {
            AddSystemEvent(quote.ProjectId, $"Quote #{quote.QuoteNumber} Accepted", $"Quote #{quote.QuoteNumber} was accepted.", $"Quote_Resolution_{quote.Id}", user);
        }
        else if (newStatus == QuoteStatus.Rejected)
        {
            AddSystemEvent(quote.ProjectId, $"Quote #{quote.QuoteNumber} Rejected", $"Quote #{quote.QuoteNumber} was rejected.", $"Quote_Resolution_{quote.Id}", user);
        }
        else if (oldStatus == QuoteStatus.Accepted || oldStatus == QuoteStatus.Rejected)
        {
            // If reverted from Accepted/Rejected to something else
            if (newStatus != QuoteStatus.Accepted && newStatus != QuoteStatus.Rejected)
            {
                RemoveSystemEvent(quote.ProjectId, $"Quote_Resolution_{quote.Id}");
            }
        }
    }

    public void SyncInvoiceStatusTimelineEvent(Invoice invoice, string oldStatus, string newStatus, string? user = null)
    {
        if (invoice == null || string.IsNullOrEmpty(invoice.ProjectId) || oldStatus == newStatus) return;

        // Draft <-> Final
        if (oldStatus == InvoiceStatus.Draft && newStatus == InvoiceStatus.Final)
        {
            AddSystemEvent(invoice.ProjectId, $"Invoice #{invoice.InvoiceNumber} Finalized", $"Invoice #{invoice.InvoiceNumber} was marked as Final.", $"Invoice_Final_{invoice.Id}", user);
        }
        else if (oldStatus == InvoiceStatus.Final && newStatus == InvoiceStatus.Draft)
        {
            RemoveSystemEvent(invoice.ProjectId, $"Invoice_Final_{invoice.Id}");
        }

        // Paid
        if (newStatus == InvoiceStatus.Paid)
        {
            AddSystemEvent(invoice.ProjectId, $"Invoice #{invoice.InvoiceNumber} Paid", $"Invoice #{invoice.InvoiceNumber} has been paid in full.", $"Invoice_Paid_{invoice.Id}", user);
        }
        else if (oldStatus == InvoiceStatus.Paid && newStatus != InvoiceStatus.Paid)
        {
            RemoveSystemEvent(invoice.ProjectId, $"Invoice_Paid_{invoice.Id}");
        }
    }

    public void SyncDocumentStatusTimelineEvent(ProjectDocument doc, string oldStatus, string newStatus, string? user = null)
    {
        if (doc == null || string.IsNullOrEmpty(doc.ProjectId) || oldStatus == newStatus) return;

        if (oldStatus == ProjectDocumentStatus.Draft && newStatus == ProjectDocumentStatus.Final)
        {
            AddSystemEvent(doc.ProjectId, $"'{doc.Name}' Finalized", $"Document '{doc.Name}' was finalized.", $"Doc_Final_{doc.Id}", user);
        }
        else if (oldStatus == ProjectDocumentStatus.Final && newStatus == ProjectDocumentStatus.Draft)
        {
            RemoveSystemEvent(doc.ProjectId, $"Doc_Final_{doc.Id}");
        }
    }
}
