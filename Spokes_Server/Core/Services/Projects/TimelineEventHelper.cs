namespace Spokes_Server.Core.Services.Projects;

using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Aggregate;
using System;
using System.Linq;

public static class TimelineEventHelper
{
    private static void RemoveSystemEvent(Database db, string projectId, string refId)
    {
        if (string.IsNullOrEmpty(projectId)) return;
        var existing = db.ProjectNotes.GetByProject(projectId).FirstOrDefault(n => n.SystemReferenceId == refId);
        if (existing != null)
        {
            db.ProjectNotes.Delete(existing.Id);
        }
    }

    private static void AddSystemEvent(Database db, string projectId, string title, string content, string refId, string user)
    {
        if (string.IsNullOrEmpty(projectId)) return;

        // Remove if it already exists to avoid duplicates
        RemoveSystemEvent(db, projectId, refId);

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
        db.ProjectNotes.Save(note);
    }

    public static void ProcessQuoteStatusChange(Database db, Quote quote, string oldStatus, string newStatus, string user)
    {
        if (quote == null || string.IsNullOrEmpty(quote.ProjectId) || oldStatus == newStatus) return;

        // Draft <-> Final
        if (oldStatus == "Draft" && newStatus == "Final")
        {
            AddSystemEvent(db, quote.ProjectId, $"Quote #{quote.QuoteNumber} Finalized", $"Quote #{quote.QuoteNumber} was marked as Final.", $"Quote_Final_{quote.Id}", user);
        }
        else if (oldStatus == "Final" && newStatus == "Draft")
        {
            RemoveSystemEvent(db, quote.ProjectId, $"Quote_Final_{quote.Id}");
        }

        // Accepted / Rejected
        if (newStatus == "Accepted")
        {
            AddSystemEvent(db, quote.ProjectId, $"Quote #{quote.QuoteNumber} Accepted", $"Quote #{quote.QuoteNumber} was accepted.", $"Quote_Resolution_{quote.Id}", user);
        }
        else if (newStatus == "Rejected")
        {
            AddSystemEvent(db, quote.ProjectId, $"Quote #{quote.QuoteNumber} Rejected", $"Quote #{quote.QuoteNumber} was rejected.", $"Quote_Resolution_{quote.Id}", user);
        }
        else if (oldStatus == "Accepted" || oldStatus == "Rejected")
        {
            // If reverted from Accepted/Rejected to something else
            if (newStatus != "Accepted" && newStatus != "Rejected")
            {
                RemoveSystemEvent(db, quote.ProjectId, $"Quote_Resolution_{quote.Id}");
            }
        }
    }

    public static void ProcessInvoiceStatusChange(Database db, Invoice invoice, string oldStatus, string newStatus, string user)
    {
        if (invoice == null || string.IsNullOrEmpty(invoice.ProjectId) || oldStatus == newStatus) return;

        // Draft <-> Final
        if (oldStatus == "Draft" && newStatus == "Final")
        {
            AddSystemEvent(db, invoice.ProjectId, $"Invoice #{invoice.InvoiceNumber} Finalized", $"Invoice #{invoice.InvoiceNumber} was marked as Final.", $"Invoice_Final_{invoice.Id}", user);
        }
        else if (oldStatus == "Final" && newStatus == "Draft")
        {
            RemoveSystemEvent(db, invoice.ProjectId, $"Invoice_Final_{invoice.Id}");
        }

        // Paid
        if (newStatus == "Paid")
        {
            AddSystemEvent(db, invoice.ProjectId, $"Invoice #{invoice.InvoiceNumber} Paid", $"Invoice #{invoice.InvoiceNumber} has been paid in full.", $"Invoice_Paid_{invoice.Id}", user);
        }
        else if (oldStatus == "Paid" && newStatus != "Paid")
        {
            RemoveSystemEvent(db, invoice.ProjectId, $"Invoice_Paid_{invoice.Id}");
            // Edge case: Unpaying might affect project commissions
        }

        EvaluateProjectCommissions(db, invoice.ProjectId);
    }

    public static void EvaluateProjectCommissions(Database db, string projectId)
    {
        var project = db.Projects.GetById(projectId);
        if (project == null || !project.Commissions.Any()) return;

        var profile = db.CompanyProfile.Get();

        var currentStatusConfig = profile.ProjectStatuses
            .FirstOrDefault(s => string.Equals(s.Name, project.Status, StringComparison.OrdinalIgnoreCase));

        bool isCompleted = currentStatusConfig != null
            ? (currentStatusConfig.SystemMapping == SystemStatusMappings.Completed || currentStatusConfig.SystemMapping == SystemStatusMappings.Archived)
            : (string.Equals(project.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.Equals(project.Status, "Archived", StringComparison.OrdinalIgnoreCase));

        var projectInvoices = db.Invoices.GetByProject(projectId).Where(i => i.Status != "Void").ToList();
        bool allPaid = projectInvoices.All(i => i.Status == "Paid");

        var existingLedgers = db.CommissionLedgerRecords.GetByProject(projectId);

        if (isCompleted && allPaid)
        {
            // Only generate if we haven't already generated them for this project!
            if (existingLedgers.Any(l => l.Status != CommissionStatus.Clawback)) return;

            var quotes = db.Quotes.GetByProject(projectId);
            var calcEngine = new Spokes_Server.Core.Services.Accounting.FinancialCalculationEngine(db.Invoices, db.Quotes, db.Projects);
            var quoteRevenue = quotes.Where(q => q.Status == "Accepted").Sum(q => q.GrandTotal);
            var totalIncome = projectInvoices.Any() ? projectInvoices.Sum(i => i.SubTotal) : quoteRevenue;

            var allPos = db.Purchases.GetAll().Where(p => p.Items.Any(i => i.ProjectId == projectId)).ToList();
            var totalPurchases = allPos.SelectMany(p => p.Items.Where(i => i.ProjectId == projectId)).Sum(i => i.Total);

            var allExpenses = db.ExpenseReports.GetAll().SelectMany(r => r.Items).Where(i => i.ProjectId == projectId).ToList();
            var totalExpenses = allExpenses.Sum(e => e.Total);

            var projectGrossMargin = totalIncome - (totalPurchases + totalExpenses);

            foreach (var comm in project.Commissions)
            {
                var basis = string.IsNullOrEmpty(comm.CommissionBasis) ? CommissionBasisTypes.Revenue : comm.CommissionBasis;

                decimal baseAmount = 0;
                if (basis.Contains("Margin")) baseAmount = projectGrossMargin;
                else baseAmount = totalIncome;

                if (baseAmount <= 0) continue;

                var commissionAmount = baseAmount * (comm.Percentage / 100m);

                var record = new CommissionLedgerRecord
                {
                    ProjectId = project.Id,
                    InvoiceId = null, // Single aggregated record for the project
                    BeneficiaryId = comm.EmployeeId ?? comm.Name,
                    BeneficiaryName = comm.Name,
                    Type = comm.Type,
                    BaseAmount = baseAmount,
                    CommissionAmount = commissionAmount,
                    Status = CommissionStatus.Payable,
                    DateGenerated = DateTime.Today
                };

                db.CommissionLedgerRecords.Save(record);
            }
        }
        else
        {
            // Project is no longer completed or invoices became unpaid. Handle clawbacks.
            foreach (var record in existingLedgers)
            {
                if (record.Status == CommissionStatus.Paid)
                {
                    if (!existingLedgers.Any(l => l.Status == CommissionStatus.Clawback && l.BeneficiaryName == record.BeneficiaryName))
                    {
                        var clawback = new CommissionLedgerRecord
                        {
                            ProjectId = record.ProjectId,
                            BeneficiaryId = record.BeneficiaryId,
                            BeneficiaryName = record.BeneficiaryName,
                            Type = record.Type,
                            BaseAmount = -record.BaseAmount,
                            CommissionAmount = -record.CommissionAmount,
                            Status = CommissionStatus.Payable,
                            DateGenerated = DateTime.Today
                        };
                        db.CommissionLedgerRecords.Save(clawback);
                    }
                }
                else if (record.Status == CommissionStatus.Payable || record.Status == CommissionStatus.Pending)
                {
                    db.CommissionLedgerRecords.Delete(record.Id);
                }
            }
        }
    }

    public static void ProcessDocumentStatusChange(Database db, ProjectDocument doc, string oldStatus, string newStatus, string user)
    {
        if (doc == null || string.IsNullOrEmpty(doc.ProjectId) || oldStatus == newStatus) return;

        if (oldStatus == "Draft" && newStatus == "Final")
        {
            AddSystemEvent(db, doc.ProjectId, $"'{doc.Name}' Finalized", $"Document '{doc.Name}' was finalized.", $"Doc_Final_{doc.Id}", user);
        }
        else if (oldStatus == "Final" && newStatus == "Draft")
        {
            RemoveSystemEvent(db, doc.ProjectId, $"Doc_Final_{doc.Id}");
        }
    }
}
