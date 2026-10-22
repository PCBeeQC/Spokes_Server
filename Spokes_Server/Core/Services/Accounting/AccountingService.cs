using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;

namespace Spokes_Server.Core.Services.Accounting;

public class AccountingService : IAccountingService
{
    private readonly AccountingContext _accounting;
    private readonly ProjectsContext _projects;
    private readonly CoreContext _core;
    private readonly ITimelineEventService _timelineEventService;
    private readonly CommunicationContext? _communication;

    public AccountingService(
        AccountingContext accounting,
        ProjectsContext projects,
        CoreContext core,
        ITimelineEventService? timelineEventService = null,
        CommunicationContext? communication = null)
    {
        _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _timelineEventService = timelineEventService ?? new TimelineEventService(projects);
        _communication = communication;
    }

    public decimal CalculateProjectGrossMargin(string projectId)
    {
        var quotes = _projects.Quotes.GetByProject(projectId);
        var quoteRevenue = quotes.Where(q => q.Status == QuoteStatus.Accepted).Sum(q => q.GrandTotal);
        var invoices = _accounting.Invoices.GetByProject(projectId).Where(i => i.Status != InvoiceStatus.Void).ToList();
        var totalIncome = invoices.Any() ? invoices.Sum(i => i.SubTotal) : quoteRevenue;

        var allPos = _accounting.PurchaseOrders.GetAll().Where(p => p.Status != PurchaseOrderStatus.Void && p.LineItems.Any(i => i.ProjectId == projectId)).ToList();
        var totalPurchases = allPos.SelectMany(p => p.LineItems.Where(i => i.ProjectId == projectId)).Sum(i => i.Total);

        var allExpenses = _accounting.ExpenseReports.GetAll().SelectMany(r => r.LineItems).Where(i => i.ProjectId == projectId).ToList();
        var totalExpenses = allExpenses.Sum(e => e.Total);

        return totalIncome - (totalPurchases + totalExpenses);
    }

    public void EvaluateProjectCommissions(string projectId)
    {
        var project = _projects.Projects.GetById(projectId);
        if (project == null || project.Commissions.Count == 0) return;

        var profile = _core.CompanyProfile.Get();

        var currentStatusConfig = profile.ProjectStatuses
            .FirstOrDefault(s => string.Equals(s.Name, project.Status, StringComparison.OrdinalIgnoreCase));

        bool isCompleted = currentStatusConfig != null
            ? (currentStatusConfig.SystemMapping == SystemStatusMappings.Completed || currentStatusConfig.SystemMapping == SystemStatusMappings.Archived)
            : (string.Equals(project.Status, "Completed", StringComparison.OrdinalIgnoreCase) || string.Equals(project.Status, "Archived", StringComparison.OrdinalIgnoreCase));

        var projectInvoices = _accounting.Invoices.GetByProject(projectId).Where(i => i.Status != InvoiceStatus.Void).ToList();
        bool allPaid = projectInvoices.All(i => i.Status == InvoiceStatus.Paid);

        var existingLedgers = _accounting.CommissionLedgerRecords.GetByProject(projectId);

        if (isCompleted && allPaid)
        {
            // Only generate if we haven't already generated them for this project!
            if (existingLedgers.Any(l => l.Status != CommissionStatus.Clawback)) return;

            var quotes = _projects.Quotes.GetByProject(projectId);
            var quoteRevenue = quotes.Where(q => q.Status == QuoteStatus.Accepted).Sum(q => q.GrandTotal);
            var totalIncome = projectInvoices.Count > 0 ? projectInvoices.Sum(i => i.SubTotal) : quoteRevenue;

            var projectGrossMargin = CalculateProjectGrossMargin(projectId);

            foreach (var comm in project.Commissions)
            {
                var basis = string.IsNullOrEmpty(comm.CommissionBasis) ? CommissionBasisTypes.Revenue : comm.CommissionBasis;
                decimal baseAmount = basis.Contains("Margin") ? projectGrossMargin : totalIncome;

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

                _accounting.CommissionLedgerRecords.Save(record);
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
                        _accounting.CommissionLedgerRecords.Save(clawback);
                    }
                }
                else if (record.Status == CommissionStatus.Payable || record.Status == CommissionStatus.Pending)
                {
                    _accounting.CommissionLedgerRecords.Delete(record.Id);
                }
            }
        }
    }

    public void ProcessInvoiceStatusChange(Invoice invoice, string oldStatus, string newStatus, string? user = null)
    {
        if (invoice == null || string.IsNullOrEmpty(invoice.ProjectId) || oldStatus == newStatus) return;

        // Linearized handoff:
        // 1. Accounting domain evaluates commissions first
        EvaluateProjectCommissions(invoice.ProjectId);

        // 2. Timeline event service records the timeline note
        _timelineEventService.SyncInvoiceStatusTimelineEvent(invoice, oldStatus, newStatus, user);
    }

    public void RecordInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(payment);

        var oldStatus = invoice.Status;
        invoice.Payments.Add(payment);
        if (invoice.BalanceDue <= 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.DatePaid = payment.Date;
        }
        else
        {
            invoice.Status = InvoiceStatus.Partial;
        }

        _accounting.Invoices.Save(invoice);
        ProcessInvoiceStatusChange(invoice, oldStatus, invoice.Status, user);
    }

    public void RecordBillPayment(Bill bill, PaymentRecord payment)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(payment);

        bill.Payments.Add(payment);
        if (bill.BalanceDue <= 0)
        {
            bill.Status = BillStatus.Paid;
            bill.DatePaid = payment.Date;
        }

        _accounting.Bills.Save(bill);
    }

    public void ApproveBill(Bill bill, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Status == BillStatus.Void)
        {
            throw new InvalidOperationException("Cannot approve a voided bill.");
        }

        if (bill.BalanceDue <= 0 && bill.Amount > 0)
        {
            bill.Status = BillStatus.Paid;
            bill.DatePaid ??= bill.Payments.LastOrDefault()?.Date ?? DateTime.Today;
        }
        else
        {
            bill.Status = BillStatus.Unpaid;
        }

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);

        if (_communication != null && !string.IsNullOrEmpty(bill.Id))
        {
            var linkedEmails = _communication.EmailMessages.GetAll()
                .Where(e => e.LinkedBillId == bill.Id || e.LinkedBillIds.Contains(bill.Id))
                .ToList();

            foreach (var email in linkedEmails)
            {
                if (email.BillingStatus == CompanyBillInboxConstants.BillingStatus.AutoDrafted)
                {
                    email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved;
                    email.ProcessedByUserId = user;
                    email.ProcessedAt = DateTime.UtcNow;
                    _communication.EmailMessages.Save(email);
                }
            }
        }
    }

    public void VoidBill(Bill bill, string reason, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }

        if (bill.Payments.Count > 0 || bill.AmountPaid > 0)
        {
            throw new InvalidOperationException("Cannot void a bill that has recorded payments. Remove recorded payments first.");
        }

        bill.Status = BillStatus.Void;
        bill.VoidReason = reason.Trim();
        bill.VoidedAt = DateTime.UtcNow;
        bill.VoidedBy = user ?? "System";

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
    }

    public void UnvoidBill(Bill bill, string? user = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (bill.Status != BillStatus.Void) return;

        bill.Status = BillStatus.Unpaid;
        bill.VoidReason = null;
        bill.VoidedAt = null;
        bill.VoidedBy = null;

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
    }

    private void SyncPurchaseOrderOnBillChange(Bill bill)
    {
        if (string.IsNullOrEmpty(bill.PurchaseOrderId)) return;

        var po = _accounting.PurchaseOrders.GetById(bill.PurchaseOrderId);
        if (po == null) return;

        var linkedBills = _accounting.Bills.GetByPo(po.Id);
        var otherBills = linkedBills.Where(b => b.Id != bill.Id && b.Status != BillStatus.Void);
        var totalBilled = otherBills.Sum(b => b.Amount);
        if (bill.Status != BillStatus.Void)
        {
            totalBilled += bill.Amount;
        }

        po.TotalBilled = totalBilled;

        if (po.Status != PurchaseOrderStatus.Void && po.Status != PurchaseOrderStatus.Draft)
        {
            if (po.TotalBilled >= po.GrandTotal && po.GrandTotal > 0)
            {
                po.Status = PurchaseOrderStatus.FullyBilled;
            }
            else if (po.TotalBilled > 0)
            {
                po.Status = PurchaseOrderStatus.PartiallyBilled;
            }
            else if (po.Status == PurchaseOrderStatus.FullyBilled || po.Status == PurchaseOrderStatus.PartiallyBilled)
            {
                po.Status = PurchaseOrderStatus.Final;
            }
        }

        _accounting.PurchaseOrders.Save(po);
    }
}
