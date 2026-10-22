namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Projects;

public static class ProjectAccountingExtensions
{
    public static bool IsCommissionPayable(this Project project, List<Invoice> projectInvoices, List<ProjectStatusConfig> availableStatuses)
    {
        var config = availableStatuses.FirstOrDefault(s => string.Equals(s.Name, project.Status, StringComparison.OrdinalIgnoreCase));
        bool isProjectDone = config != null && (config.SystemMapping == SystemStatusMappings.Completed || config.SystemMapping == SystemStatusMappings.Archived);
        if (!isProjectDone) return false;

        // All Invoices must be fully Paid or Void
        bool allInvoicesPaid = projectInvoices.All(i => i.Status == InvoiceStatus.Paid || i.Status == InvoiceStatus.Void);

        return allInvoicesPaid;
    }
}
