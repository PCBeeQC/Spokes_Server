using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Projects;

public interface ITimelineEventService
{
    void SyncQuoteStatusTimelineEvent(Quote quote, string oldStatus, string newStatus, string? user = null);
    void SyncInvoiceStatusTimelineEvent(Invoice invoice, string oldStatus, string newStatus, string? user = null);
    void SyncDocumentStatusTimelineEvent(ProjectDocument doc, string oldStatus, string newStatus, string? user = null);
}
