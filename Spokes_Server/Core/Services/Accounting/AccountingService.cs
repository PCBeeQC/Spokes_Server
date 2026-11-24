using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Accounting;

public class AccountingService : IAccountingService
{
    private readonly AccountingContext _accounting;
    private readonly ProjectsContext _projects;
    private readonly CoreContext _core;
    private readonly ITimelineEventService _timelineEventService;
    private readonly CommunicationContext? _communication;
    private readonly ILedgerService? _ledgerService;
    private readonly HrContext? _hr;

    [ActivatorUtilitiesConstructor]
    public AccountingService(
        AccountingContext accounting,
        ProjectsContext projects,
        CoreContext core,
        ITimelineEventService? timelineEventService = null,
        CommunicationContext? communication = null,
        ILedgerService? ledgerService = null,
        HrContext? hr = null)
    {
        _accounting = accounting ?? throw new ArgumentNullException(nameof(accounting));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _timelineEventService = timelineEventService ?? new TimelineEventService(projects);
        _communication = communication;
        _ledgerService = ledgerService ?? (accounting.JournalEntries != null ? new LedgerService(accounting, projects) : null);
        _hr = hr;
    }

    public decimal CalculateProjectGrossMargin(string projectId)
    {
        var quotes = _projects.Quotes.GetByProject(projectId);
        var quoteRevenue = quotes.Where(q => q.Status == QuoteStatus.Accepted).Sum(q => q.GrandTotal);
        var invoices = _accounting.Invoices.GetByProject(projectId).Where(i => InvoiceStatus.IsPosted(i.Status) && !i.IsOpeningBalance).ToList();
        var directSummary = _accounting.DirectTransactions?.SummarizeForProject(projectId);
        var directIncome = directSummary?.DirectIncomeSubTotal ?? 0m;
        var directExpense = directSummary?.DirectExpenseTotal ?? 0m;

        var totalIncome = (invoices.Any() ? invoices.Sum(i => i.SubTotal) : quoteRevenue) + directIncome;

        var allPos = _accounting.PurchaseOrders.GetAll().Where(p => p.Status != PurchaseOrderStatus.Void && p.LineItems.Any(i => i.ProjectId == projectId)).ToList();
        var totalPurchases = allPos.SelectMany(p => p.LineItems.Where(i => i.ProjectId == projectId)).Sum(i => i.Total);

        var allExpenses = _accounting.ExpenseReports.GetAll()
            .Where(r => (r.DateSubmitted.HasValue || r.DatePaid.HasValue)
                        && !string.Equals(r.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.LineItems)
            .Where(i => i.ProjectId == projectId)
            .ToList();
        var totalExpenses = allExpenses.Sum(e => e.Total);

        return totalIncome - (totalPurchases + totalExpenses + directExpense);
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

        var projectInvoices = _accounting.Invoices.GetByProject(projectId).Where(i => InvoiceStatus.IsPosted(i.Status) && !i.IsOpeningBalance).ToList();
        if (projectInvoices.Count == 0 && !project.Commissions.Any()) return;
        bool allPaid = projectInvoices.All(i => InvoiceStatus.IsSettled(i.Status));

        var existingLedgers = _accounting.CommissionLedgerRecords.GetByProject(projectId);

        if (isCompleted && allPaid)
        {
            if (existingLedgers.Any(l => l.Status != CommissionStatus.Clawback)) return;

            var quotes = _projects.Quotes.GetByProject(projectId);
            var quoteRevenue = quotes.Where(q => q.Status == QuoteStatus.Accepted).Sum(q => q.GrandTotal);
            var directIncome = _accounting.DirectTransactions?.SummarizeForProject(projectId).DirectIncomeSubTotal ?? 0m;
            var totalIncome = (projectInvoices.Count > 0 ? projectInvoices.Sum(i => i.SubTotal) : quoteRevenue) + directIncome;

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
                    InvoiceId = null,
                    BeneficiaryId = comm.EmployeeId ?? comm.Name,
                    BeneficiaryName = comm.Name,
                    Type = comm.Type,
                    BaseAmount = baseAmount,
                    CommissionAmount = commissionAmount,
                    Status = CommissionStatus.Payable,
                    DateGenerated = DateTime.Today
                };

                _accounting.CommissionLedgerRecords.Save(record);
                _ledgerService?.SyncCommission(record);
            }
        }
        else
        {
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
                        _ledgerService?.SyncCommission(clawback);
                    }
                }
                else if (record.Status == CommissionStatus.Payable || record.Status == CommissionStatus.Pending)
                {
                    _accounting.CommissionLedgerRecords.Delete(record.Id);
                    _ledgerService?.RemoveEntriesForSource(JournalSourceType.Commission, record.Id);
                }
            }
        }
    }

    public void ProcessInvoiceStatusChange(Invoice invoice, string oldStatus, string newStatus, string? user = null, Employee? actor = null)
    {
        if (invoice == null) return;
        if (!string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(oldStatus, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(newStatus, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase))
            {
                DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.ApproveInvoices, "approve or revert invoice draft status");
            }
            else
            {
                DomainActorGuard.RequireInteractiveAnyPermission(
                    actor,
                    "update invoice payment status",
                    AppPermissions.Finance.AccessAccounting,
                    AppPermissions.Finance.ApproveInvoices);
            }
        }
        else
        {
            DomainActorGuard.RequireInteractiveAnyPermission(
                actor,
                "sync invoice status",
                AppPermissions.Finance.AccessAccounting,
                AppPermissions.Finance.ApproveInvoices);
        }

        ProcessInvoiceStatusChangeCore(invoice, oldStatus, newStatus, user);
    }

    private void ProcessInvoiceStatusChangeCore(Invoice invoice, string oldStatus, string newStatus, string? user)
    {
        _ledgerService?.SyncInvoice(invoice);

        if (string.IsNullOrEmpty(invoice.ProjectId) || oldStatus == newStatus) return;

        EvaluateProjectCommissions(invoice.ProjectId);
        _timelineEventService.SyncInvoiceStatusTimelineEvent(invoice, oldStatus, newStatus, user);
    }

    public bool HasConsumedPrepaymentCredits(Invoice invoice)
    {
        if (invoice == null) return false;

        var prepayLineIds = invoice.LineItems
            .Where(l => l.IsPrepayment && !string.IsNullOrEmpty(l.Id))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (prepayLineIds.Count == 0) return false;

        return _accounting.Invoices.GetAll()
            .Where(i => i.Id != invoice.Id && InvoiceStatus.IsPosted(i.Status))
            .SelectMany(i => i.LineItems)
            .Any(l => l.IsCreditUsed && !string.IsNullOrEmpty(l.SourceId) && prepayLineIds.Contains(l.SourceId));
    }

    private static void InvalidateCachedPdf(Invoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.PdfPath) && File.Exists(invoice.PdfPath))
        {
            try { File.Delete(invoice.PdfPath); } catch { }
        }
        invoice.PdfPath = string.Empty;
    }

    public void RecordInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(payment);
        DomainActorGuard.RequireInteractiveAnyPermission(
            actor,
            "record invoice payments",
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Finance.ApproveInvoices);

        var oldStatus = invoice.Status;
        invoice.Payments.Add(payment);
        ApplyInvoicePaymentMutation(
            invoice,
            oldStatus,
            DocumentHistoryActions.PaymentRecorded,
            $"Recorded payment of {payment.Amount:C2} ({payment.Method})",
            user,
            reason: null,
            paidDateFallback: payment.Date,
            forcePartialWhenPositiveBalance: true);
    }

    public void UpdateInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null, string? reason = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(payment);
        DomainActorGuard.RequireInteractiveAnyPermission(
            actor,
            "update invoice payments",
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Finance.ApproveInvoices);

        var existing = invoice.Payments.FirstOrDefault(p => p.Id == payment.Id);
        if (existing == null)
        {
            throw new InvalidOperationException($"Payment with ID '{payment.Id}' not found on invoice '{invoice.InvoiceNumber}'.");
        }

        var amountDifference = payment.Amount - existing.Amount;
        var hypotheticalBalanceDue = invoice.BalanceDue - amountDifference;

        if (hypotheticalBalanceDue > 0 && HasConsumedPrepaymentCredits(invoice))
        {
            throw new InvalidOperationException("Cannot reduce payments below the full amount because prepayment credits from this invoice have been applied to other invoices.");
        }

        var oldStatus = invoice.Status;
        var previousAmount = existing.Amount;

        existing.Amount = payment.Amount;
        existing.Date = payment.Date;
        existing.Method = payment.Method;
        existing.Reference = payment.Reference;
        existing.Notes = payment.Notes;
        existing.BankGlAccountCode = payment.BankGlAccountCode;
        existing.SettledFunctionalAmount = payment.SettledFunctionalAmount;

        ApplyInvoicePaymentMutation(
            invoice,
            oldStatus,
            DocumentHistoryActions.PaymentUpdated,
            $"Updated payment from {previousAmount:C2} to {payment.Amount:C2} ({payment.Method})",
            user,
            reason,
            paidDateFallback: existing.Date,
            forcePartialWhenPositiveBalance: false);
    }

    public void DeleteInvoicePayment(Invoice invoice, string paymentId, string? user = null, string? reason = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (string.IsNullOrWhiteSpace(paymentId))
        {
            throw new ArgumentException("Payment ID cannot be null or empty.", nameof(paymentId));
        }
        DomainActorGuard.RequireInteractiveAnyPermission(
            actor,
            "delete invoice payments",
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Finance.ApproveInvoices);

        var existing = invoice.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (existing == null)
        {
            throw new InvalidOperationException($"Payment with ID '{paymentId}' not found on invoice '{invoice.InvoiceNumber}'.");
        }

        var hypotheticalBalanceDue = invoice.BalanceDue + existing.Amount;
        if (hypotheticalBalanceDue > 0 && HasConsumedPrepaymentCredits(invoice))
        {
            throw new InvalidOperationException("Cannot delete payment because prepayment credits from this invoice have been applied to other invoices.");
        }

        var oldStatus = invoice.Status;
        var deletedAmount = existing.Amount;
        var deletedMethod = existing.Method;
        invoice.Payments.Remove(existing);

        ApplyInvoicePaymentMutation(
            invoice,
            oldStatus,
            DocumentHistoryActions.PaymentDeleted,
            $"Deleted payment of {deletedAmount:C2} ({deletedMethod})",
            user,
            reason,
            paidDateFallback: null,
            forcePartialWhenPositiveBalance: false);
    }

    public void RecordInvoiceWriteOff(Invoice invoice, BalanceWriteOff writeOff, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(writeOff);
        DomainActorGuard.RequireInteractiveAnyPermission(
            actor,
            "record invoice balance write-offs",
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Finance.ApproveInvoices);

        if (!InvoiceStatus.IsPosted(invoice.Status))
        {
            throw new InvalidOperationException($"Cannot write off balance on a {invoice.Status} invoice.");
        }

        var normalized = PrepareAndValidateWriteOff(writeOff, invoice.BalanceDue, user);
        var oldStatus = invoice.Status;
        invoice.WriteOffs.Add(normalized);

        ApplyInvoicePaymentMutation(
            invoice,
            oldStatus,
            DocumentHistoryActions.BalanceWrittenOff,
            $"Closed/wrote off balance of {normalized.Amount:C2} to GL {normalized.GlAccountCode}",
            user,
            normalized.Reason,
            paidDateFallback: null,
            forcePartialWhenPositiveBalance: false);
    }

    public void DeleteInvoiceWriteOff(Invoice invoice, string writeOffId, string? user = null, string? reason = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (string.IsNullOrWhiteSpace(writeOffId))
        {
            throw new ArgumentException("Write-off ID cannot be null or empty.", nameof(writeOffId));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse a balance write-off.", nameof(reason));
        }

        DomainActorGuard.RequireInteractiveAnyPermission(
            actor,
            "reverse invoice balance write-offs",
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Finance.ApproveInvoices);

        var existing = invoice.WriteOffs.FirstOrDefault(w => w.Id == writeOffId);
        if (existing == null)
        {
            throw new InvalidOperationException($"Write-off with ID '{writeOffId}' not found on invoice '{invoice.InvoiceNumber}'.");
        }

        EnsureWriteOffJournalEntryUnlocked(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:{existing.Id}");

        var hypotheticalBalanceDue = Math.Round(invoice.BalanceDue + existing.Amount, 2, MidpointRounding.AwayFromZero);
        if (hypotheticalBalanceDue > 0m && HasConsumedPrepaymentCredits(invoice))
        {
            throw new InvalidOperationException("Cannot reverse write-off because prepayment credits from this invoice have been applied to other invoices.");
        }

        var oldStatus = invoice.Status;
        var deletedAmount = existing.Amount;
        invoice.WriteOffs.Remove(existing);

        ApplyInvoicePaymentMutation(
            invoice,
            oldStatus,
            DocumentHistoryActions.WriteOffReversed,
            $"Reversed balance write-off of {deletedAmount:C2}",
            user,
            reason,
            paidDateFallback: null,
            forcePartialWhenPositiveBalance: false);
    }

    private void ApplyInvoicePaymentMutation(
        Invoice invoice,
        string oldStatus,
        string action,
        string summary,
        string? user,
        string? reason,
        DateTime? paidDateFallback,
        bool forcePartialWhenPositiveBalance)
    {
        if (Math.Round(invoice.BalanceDue, 2, MidpointRounding.AwayFromZero) <= 0m &&
            (invoice.Payments.Count > 0 || invoice.WriteOffs.Count > 0 || forcePartialWhenPositiveBalance))
        {
            if (invoice.AmountPaid > 0m)
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.DatePaid = invoice.Payments.MaxBy(p => p.Date)?.Date ?? paidDateFallback;
            }
            else if (invoice.AmountWrittenOff > 0m)
            {
                invoice.Status = InvoiceStatus.WrittenOff;
                invoice.DatePaid = null;
            }
            else
            {
                invoice.Status = InvoiceStatus.Paid;
                invoice.DatePaid = paidDateFallback;
            }
        }
        else if (forcePartialWhenPositiveBalance || invoice.AmountPaid > 0m || invoice.AmountWrittenOff > 0m)
        {
            invoice.Status = InvoiceStatus.Partial;
            if (!forcePartialWhenPositiveBalance || invoice.AmountPaid <= 0m)
            {
                invoice.DatePaid = null;
            }
        }
        else
        {
            invoice.DatePaid = null;
            if (invoice.Status is InvoiceStatus.Paid or InvoiceStatus.WrittenOff or InvoiceStatus.Partial)
            {
                invoice.Status = InvoiceStatus.Final;
            }
        }

        invoice.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = action,
            OldStatus = oldStatus,
            NewStatus = invoice.Status,
            ChangedBy = string.IsNullOrWhiteSpace(user) ? "System" : user,
            Summary = summary,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });

        InvalidateCachedPdf(invoice);
        _accounting.Invoices.Save(invoice);
        ProcessInvoiceStatusChangeCore(invoice, oldStatus, invoice.Status, user);
    }

    public void SaveBill(Bill bill, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "save bills");

        var existing = _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existing?.PurchaseOrderId, actor, "save bills");
        var baselineStatus = existing == null
            ? null
            : (ReferenceEquals(existing, bill) ? (bill.LastSavedSnapshot?.Status ?? existing.Status) : existing.Status);

        if ((existing == null && bill.Status != BillStatus.AwaitingApproval && bill.Status != BillStatus.Void) ||
            (baselineStatus == BillStatus.AwaitingApproval && bill.Status != BillStatus.AwaitingApproval && bill.Status != BillStatus.Void))
        {
            DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ApproveBills, "approve bills");
        }
        if (existing == null)
        {
            if (bill.History.Count == 0)
            {
                DocumentHistoryHelper.RecordCreated(
                    bill,
                    user,
                    $"Bill created for {bill.SupplierName} ({bill.Amount:C2})");
            }
        }
        else
        {
            var baseline = ReferenceEquals(existing, bill) ? (bill.LastSavedSnapshot ?? existing) : existing;
            var diffs = new System.Collections.Generic.List<string>();
            DocumentHistoryHelper.AddDiffIfChanged(diffs, "Supplier", baseline.SupplierName, bill.SupplierName);
            DocumentHistoryHelper.AddDiffIfChanged(diffs, "Invoice #", baseline.InvoiceNumber, bill.InvoiceNumber);
            DocumentHistoryHelper.AddDateDiffIfChanged(diffs, "Date", baseline.Date, bill.Date);
            DocumentHistoryHelper.AddDateDiffIfChanged(diffs, "Due Date", baseline.DueDate, bill.DueDate);
            DocumentHistoryHelper.AddCurrencyDiffIfChanged(diffs, "Amount", baseline.Amount, bill.Amount);
            DocumentHistoryHelper.AddCurrencyDiffIfChanged(diffs, "Subtotal", baseline.SubTotal, bill.SubTotal);
            DocumentHistoryHelper.AddCurrencyDiffIfChanged(diffs, "Tax", baseline.TaxAmount, bill.TaxAmount);
            DocumentHistoryHelper.AddDiffIfChanged(diffs, "GL Account", baseline.GlAccountCode, bill.GlAccountCode);

            if (diffs.Count > 0)
            {
                DocumentHistoryHelper.RecordFieldEdits(bill, diffs, user, oldStatus: baseline.Status, newStatus: bill.Status);
            }
            if (baseline.Status != bill.Status)
            {
                DocumentHistoryHelper.RecordStatusChange(bill, baseline.Status, bill.Status, user);
            }
        }

        bill.CaptureAuditSnapshot();
        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);

        if (!string.IsNullOrWhiteSpace(bill.LinkedRecurringExpenseId))
        {
            var profile = _accounting.RecurringExpenses?.GetById(bill.LinkedRecurringExpenseId);
            if (profile != null)
            {
                _ledgerService?.SyncRecurringExpense(profile);
            }
        }
    }

    public void DeleteBill(string billId, Employee? actor = null)
    {
        if (string.IsNullOrWhiteSpace(billId)) return;
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "delete bills");
        var existing = _accounting.Bills.GetById(billId);
        EnsureBillPurchaseOrderAccess(existing?.PurchaseOrderId, existing?.PurchaseOrderId, actor, "delete bills");
        var linkedRecurringId = existing?.LinkedRecurringExpenseId;

        if (_accounting.JournalEntries != null)
        {
            var billJe = _accounting.JournalEntries.GetBySource(JournalSourceType.Bill, billId);
            if (billJe != null && billJe.IsLocked)
            {
                throw new InvalidOperationException("Cannot delete this bill because its journal entry is locked.");
            }

            var hasLockedPaymentJe = _accounting.JournalEntries.GetAll().Any(j =>
                j.IsLocked &&
                (string.Equals(j.SourceType, JournalSourceType.BillPayment, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(j.SourceType, JournalSourceType.BillWriteOff, StringComparison.OrdinalIgnoreCase)) &&
                (j.SourceId?.StartsWith($"{billId}:", StringComparison.OrdinalIgnoreCase) ?? false));
            if (hasLockedPaymentJe)
            {
                throw new InvalidOperationException("Cannot delete this bill because one of its payment or write-off journal entries is locked.");
            }
        }

        if (existing != null)
        {
            // Mark void transiently in memory so SyncBill cleans up both Bill and BillPayment ledger entries.
            existing.Status = BillStatus.Void;
            _ledgerService?.SyncBill(existing);
            SyncPurchaseOrderOnBillChange(existing);

            if (!string.IsNullOrWhiteSpace(existing.SourceEmailId) && _communication?.EmailMessages != null)
            {
                var email = _communication.EmailMessages.GetById(existing.SourceEmailId);
                if (email != null)
                {
                    email.LinkedBillIds.RemoveAll(id => string.Equals(id, billId, StringComparison.OrdinalIgnoreCase));
                    if (string.Equals(email.LinkedBillId, billId, StringComparison.OrdinalIgnoreCase))
                    {
                        email.LinkedBillId = email.LinkedBillIds.FirstOrDefault();
                    }
                    if (email.LinkedBillIds.Count == 0 &&
                        email.BillingStatus is CompanyBillInboxConstants.BillingStatus.AutoDrafted
                            or CompanyBillInboxConstants.BillingStatus.Approved
                            or CompanyBillInboxConstants.BillingStatus.DuplicateDetected)
                    {
                        email.BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed;
                    }
                    _communication.EmailMessages.Save(email);
                }
            }
        }
        else
        {
            _ledgerService?.RemoveEntriesForSource(JournalSourceType.Bill, billId);
        }

        _accounting.Bills.Delete(billId);

        if (!string.IsNullOrWhiteSpace(linkedRecurringId))
        {
            var profile = _accounting.RecurringExpenses?.GetById(linkedRecurringId);
            if (profile != null)
            {
                _ledgerService?.SyncRecurringExpense(profile);
            }
        }
    }

    public void RecordBillPayment(Bill bill, PaymentRecord payment, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(payment);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "record bill payments");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "record bill payments");
        if (string.Equals(bill.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase))
        {
            DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ApproveBills, "pay unapproved bills");
        }

        var oldStatus = bill.Status;
        bill.Payments.Add(payment);
        if (bill.BalanceDue <= 0)
        {
            bill.Status = BillStatus.Paid;
            bill.DatePaid = payment.Date;
        }

        DocumentHistoryHelper.RecordPaymentAction(
            bill,
            DocumentHistoryActions.PaymentRecorded,
            oldStatus,
            bill.Status,
            $"Recorded payment of {payment.Amount:C2} ({payment.Method})",
            user);

        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);
    }

    public void RecordBillWriteOff(Bill bill, BalanceWriteOff writeOff, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        ArgumentNullException.ThrowIfNull(writeOff);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "record bill balance write-offs");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "record bill balance write-offs");
        if (string.Equals(bill.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot write off balance on a voided bill.");
        }
        if (string.Equals(bill.Status, BillStatus.AwaitingApproval, StringComparison.OrdinalIgnoreCase))
        {
            DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ApproveBills, "write off unapproved bills");
        }

        var normalized = PrepareAndValidateWriteOff(writeOff, bill.BalanceDue, user);
        var oldStatus = bill.Status;
        bill.WriteOffs.Add(normalized);
        ApplyBillSettlementStatus(bill);

        DocumentHistoryHelper.RecordPaymentAction(
            bill,
            DocumentHistoryActions.BalanceWrittenOff,
            oldStatus,
            bill.Status,
            $"Closed/wrote off balance of {normalized.Amount:C2} to GL {normalized.GlAccountCode}",
            user,
            normalized.Reason);

        bill.CaptureAuditSnapshot();
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);
    }

    public void DeleteBillWriteOff(Bill bill, string writeOffId, string? user = null, string? reason = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (string.IsNullOrWhiteSpace(writeOffId))
        {
            throw new ArgumentException("Write-off ID cannot be null or empty.", nameof(writeOffId));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse a balance write-off.", nameof(reason));
        }

        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "reverse bill balance write-offs");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "reverse bill balance write-offs");

        var existing = bill.WriteOffs.FirstOrDefault(w => w.Id == writeOffId);
        if (existing == null)
        {
            throw new InvalidOperationException($"Write-off with ID '{writeOffId}' not found on bill '{bill.InvoiceNumber ?? bill.Description}'.");
        }

        EnsureWriteOffJournalEntryUnlocked(JournalSourceType.BillWriteOff, $"{bill.Id}:{existing.Id}");

        var oldStatus = bill.Status;
        var deletedAmount = existing.Amount;
        bill.WriteOffs.Remove(existing);
        ApplyBillSettlementStatus(bill);

        DocumentHistoryHelper.RecordPaymentAction(
            bill,
            DocumentHistoryActions.WriteOffReversed,
            oldStatus,
            bill.Status,
            $"Reversed balance write-off of {deletedAmount:C2}",
            user,
            reason);

        bill.CaptureAuditSnapshot();
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);
    }

    private static void ApplyBillSettlementStatus(Bill bill)
    {
        if (bill.Status == BillStatus.Void) return;

        if (bill.BalanceDue <= 0m && bill.Amount > 0m)
        {
            if (bill.AmountPaid > 0m)
            {
                bill.Status = BillStatus.Paid;
                bill.DatePaid ??= bill.Payments.MaxBy(p => p.Date)?.Date ?? DateTime.Today;
            }
            else if (bill.AmountWrittenOff > 0m)
            {
                bill.Status = BillStatus.WrittenOff;
                bill.DatePaid = null;
            }
        }
        else
        {
            if (BillStatus.IsSettled(bill.Status) || bill.Status == BillStatus.AwaitingApproval)
            {
                bill.Status = BillStatus.Unpaid;
            }
            if (!bill.Payments.Any())
            {
                bill.DatePaid = null;
            }
        }
    }

    public void ApproveBill(Bill bill, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ApproveBills, "approve bills");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "approve bills");
        if (bill.Status == BillStatus.Void)
        {
            throw new InvalidOperationException("Cannot approve a voided bill.");
        }

        var oldStatus = bill.Status;
        if (bill.BalanceDue <= 0 && bill.Amount > 0)
        {
            if (bill.AmountPaid <= 0m && bill.HasWriteOffs)
            {
                bill.Status = BillStatus.WrittenOff;
                bill.DatePaid = null;
            }
            else
            {
                bill.Status = BillStatus.Paid;
                bill.DatePaid ??= bill.Payments.LastOrDefault()?.Date ?? DateTime.Today;
            }
        }
        else
        {
            bill.Status = BillStatus.Unpaid;
        }

        DocumentHistoryHelper.RecordStatusChange(bill, oldStatus, bill.Status, user, "Bill approved for payment");

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);

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

    public void VoidBill(Bill bill, string reason, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A void reason is required.", nameof(reason));
        }
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "void bills");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "void bills");

        if (bill.Payments.Count > 0 || bill.AmountPaid > 0 || bill.WriteOffs.Count > 0)
        {
            throw new InvalidOperationException("Cannot void a bill that has recorded payments or write-offs. Remove them first.");
        }

        var oldStatus = bill.Status;
        bill.Status = BillStatus.Void;
        bill.VoidReason = reason.Trim();
        bill.VoidedAt = DateTime.UtcNow;
        bill.VoidedBy = user ?? "System";

        DocumentHistoryHelper.RecordStatusChange(bill, oldStatus, bill.Status, user, "Bill voided", reason);

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);
    }

    public void UnvoidBill(Bill bill, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(bill);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "unvoid bills");
        if (bill.Status != BillStatus.Void) return;
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ApproveBills, "restore voided bills to payable status");
        var existingBill = string.IsNullOrWhiteSpace(bill.Id) ? null : _accounting.Bills.GetById(bill.Id);
        EnsureBillPurchaseOrderAccess(bill.PurchaseOrderId, existingBill?.PurchaseOrderId, actor, "unvoid bills");

        var oldStatus = bill.Status;
        bill.Status = BillStatus.Unpaid;
        bill.VoidReason = null;
        bill.VoidedAt = null;
        bill.VoidedBy = null;

        DocumentHistoryHelper.RecordStatusChange(bill, oldStatus, bill.Status, user, "Bill restored from Void", isReverted: true);

        SyncPurchaseOrderOnBillChange(bill);
        _accounting.Bills.Save(bill);
        _ledgerService?.SyncBill(bill);
    }

    public void SaveRecurringExpense(RecurringExpense profile, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        DomainActorGuard.RequireInteractivePermission(
            actor,
            profile.IsEstimate ? AppPermissions.Finance.AccessAccounting : AppPermissions.Purchases.ManageBills,
            profile.IsEstimate ? "save business overhead estimates" : "save recurring bills");
        _accounting.RecurringExpenses.Save(profile);
        _ledgerService?.SyncRecurringExpense(profile);
    }

    public void DeleteRecurringExpense(string profileId, Employee? actor = null)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        var profile = _accounting.RecurringExpenses.GetById(profileId);
        var isEstimate = profile?.IsEstimate == true;
        DomainActorGuard.RequireInteractivePermission(
            actor,
            isEstimate ? AppPermissions.Finance.AccessAccounting : AppPermissions.Purchases.ManageBills,
            isEstimate ? "delete business overhead estimates" : "delete recurring bills");
        if (profile != null)
        {
            profile.History.Clear();
            _ledgerService?.SyncRecurringExpense(profile);
        }
        _accounting.RecurringExpenses.Delete(profileId);
    }

    public int MergeRecurringExpenses(string targetProfileId, IEnumerable<string> sourceProfileIds, Employee? actor = null)
    {
        if (string.IsNullOrWhiteSpace(targetProfileId))
        {
            throw new ArgumentException("Target recurring profile ID is required.", nameof(targetProfileId));
        }
        ArgumentNullException.ThrowIfNull(sourceProfileIds);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Purchases.ManageBills, "merge recurring bills");

        var target = _accounting.RecurringExpenses.GetById(targetProfileId)
            ?? throw new KeyNotFoundException($"Target recurring expense profile '{targetProfileId}' was not found.");

        var distinctSourceIds = sourceProfileIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !string.Equals(id, targetProfileId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (target.IsEstimate || distinctSourceIds.Any(id => _accounting.RecurringExpenses.GetById(id)?.IsEstimate == true))
        {
            DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, "merge business overhead estimate profiles");
        }

        if (distinctSourceIds.Count == 0)
        {
            return 0;
        }

        target.History ??= [];
        var allBills = _accounting.Bills.GetAll();
        var allEmails = _communication?.EmailMessages?.GetAll();
        var mergedCount = 0;

        foreach (var srcId in distinctSourceIds)
        {
            var src = _accounting.RecurringExpenses.GetById(srcId);
            if (src == null) continue;

            if (string.IsNullOrWhiteSpace(target.Supplier) && !string.IsNullOrWhiteSpace(src.Supplier))
                target.Supplier = src.Supplier;
            if (string.IsNullOrWhiteSpace(target.SupplierId) && !string.IsNullOrWhiteSpace(src.SupplierId))
                target.SupplierId = src.SupplierId;
            if (string.IsNullOrWhiteSpace(target.GlAccountCode) && !string.IsNullOrWhiteSpace(src.GlAccountCode))
                target.GlAccountCode = src.GlAccountCode;
            if (target.Amount <= 0m && src.Amount > 0m)
                target.Amount = src.Amount;

            if (src.History != null)
            {
                foreach (var rec in src.History)
                {
                    if (rec == null) continue;
                    var existingRec = target.History.FirstOrDefault(h =>
                        string.Equals(h.Id, rec.Id, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(rec.SourceEmailId) && string.Equals(h.SourceEmailId, rec.SourceEmailId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(rec.InvoiceNumber) &&
                         string.Equals(h.InvoiceNumber, rec.InvoiceNumber, StringComparison.OrdinalIgnoreCase) &&
                         h.Amount == rec.Amount &&
                         h.Date.Date == rec.Date.Date));

                    if (existingRec != null)
                    {
                        if (string.IsNullOrWhiteSpace(existingRec.AttachmentPath) && !string.IsNullOrWhiteSpace(rec.AttachmentPath))
                        {
                            existingRec.AttachmentPath = rec.AttachmentPath;
                            existingRec.AttachmentName = rec.AttachmentName;
                        }
                        if (!existingRec.IsPaid && rec.IsPaid)
                        {
                            existingRec.IsPaid = true;
                            existingRec.DatePaid ??= rec.DatePaid;
                        }
                        if (existingRec.Payments.Count == 0 && rec.Payments.Count > 0)
                        {
                            existingRec.Payments.AddRange(rec.Payments);
                        }
                        if (existingRec.WriteOffs.Count == 0 && rec.WriteOffs.Count > 0)
                        {
                            existingRec.WriteOffs.AddRange(rec.WriteOffs);
                        }
                        continue;
                    }

                    target.History.Add(rec);
                }
            }

            foreach (var bill in allBills.Where(b => string.Equals(b.LinkedRecurringExpenseId, src.Id, StringComparison.OrdinalIgnoreCase)))
            {
                bill.LinkedRecurringExpenseId = target.Id;
                _accounting.Bills.Save(bill);
            }

            if (allEmails != null && _communication?.EmailMessages != null)
            {
                foreach (var email in allEmails.Where(m => string.Equals(m.LinkedRecurringExpenseId, src.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    email.LinkedRecurringExpenseId = target.Id;
                    _communication.EmailMessages.Save(email);
                }
            }

            src.History?.Clear();
            _ledgerService?.SyncRecurringExpense(src);
            _accounting.RecurringExpenses.Delete(src.Id);
            mergedCount++;
        }

        target.History = [.. target.History.OrderBy(h => h.Date)];
        _accounting.RecurringExpenses.Save(target);
        _ledgerService?.SyncRecurringExpense(target);
        return mergedCount;
    }

    public void MarkExpenseReportPaid(ExpenseReport report, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, "mark expense reports paid");
        if ((!report.DateSubmitted.HasValue && !report.DatePaid.HasValue) ||
            string.Equals(report.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot mark an unsubmitted draft expense report as paid.");
        }
        var oldStatus = report.Status;
        report.DatePaid = datePaid;
        if (!string.IsNullOrWhiteSpace(bankGlCode))
        {
            report.PaymentSourceGlCode = bankGlCode;
        }

        DocumentHistoryHelper.RecordPaymentAction(
            report,
            DocumentHistoryActions.PaymentRecorded,
            oldStatus,
            report.Status,
            $"Reimbursed {report.TotalAmount:C2} on {datePaid:yyyy-MM-dd}",
            user);

        _accounting.ExpenseReports.Save(report);
        _ledgerService?.SyncExpenseReport(report);
    }

    public void MarkRecurringBillRecordPaid(RecurringExpense profile, RecurringBillRecord record, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(record);
        DomainActorGuard.RequireInteractivePermission(
            actor,
            profile.IsEstimate ? AppPermissions.Finance.AccessAccounting : AppPermissions.Purchases.ManageBills,
            profile.IsEstimate ? "mark overhead estimate records paid" : "mark recurring bill records paid");

        var oldStatus = record.Status;
        record.IsPaid = true;
        record.DatePaid = datePaid;
        if (!string.IsNullOrWhiteSpace(bankGlCode))
        {
            record.PaymentSourceGlCode = bankGlCode;
        }

        if (record.Payments.Count == 0 && record.Amount > 0)
        {
            record.Payments.Add(new PaymentRecord
            {
                Amount = record.Amount,
                Date = datePaid,
                Method = "Direct Payment",
                BankGlAccountCode = bankGlCode ?? "1020"
            });
        }

        DocumentHistoryHelper.RecordPaymentAction(
            record,
            DocumentHistoryActions.PaymentRecorded,
            oldStatus,
            record.Status,
            $"Paid recurring bill {profile.Name} ({record.Amount:C2}) on {datePaid:yyyy-MM-dd}",
            user);

        _accounting.RecurringExpenses.Save(profile);
        _ledgerService?.SyncRecurringExpense(profile);
    }

    public void RecordRecurringBillWriteOff(RecurringExpense profile, RecurringBillRecord record, BalanceWriteOff writeOff, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(writeOff);
        DomainActorGuard.RequireInteractivePermission(
            actor,
            profile.IsEstimate ? AppPermissions.Finance.AccessAccounting : AppPermissions.Purchases.ManageBills,
            profile.IsEstimate ? "record overhead estimate write-offs" : "record recurring bill balance write-offs");

        var normalized = PrepareAndValidateWriteOff(writeOff, record.BalanceDue, user);
        var oldStatus = record.Status;
        record.WriteOffs.Add(normalized);

        if (record.BalanceDue <= 0m)
        {
            if (record.AmountPaid > 0m)
            {
                record.IsPaid = true;
                record.DatePaid ??= record.Payments.MaxBy(p => p.Date)?.Date ?? normalized.Date;
            }
            else
            {
                record.IsPaid = false;
                record.DatePaid = null;
            }
        }

        DocumentHistoryHelper.RecordPaymentAction(
            record,
            DocumentHistoryActions.BalanceWrittenOff,
            oldStatus,
            record.Status,
            $"Closed/wrote off balance of {normalized.Amount:C2} to GL {normalized.GlAccountCode}",
            user,
            normalized.Reason);

        _accounting.RecurringExpenses.Save(profile);
        _ledgerService?.SyncRecurringExpense(profile);
    }

    public void DeleteRecurringBillWriteOff(RecurringExpense profile, RecurringBillRecord record, string writeOffId, string? user = null, string? reason = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(writeOffId))
        {
            throw new ArgumentException("Write-off ID cannot be null or empty.", nameof(writeOffId));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse a balance write-off.", nameof(reason));
        }

        DomainActorGuard.RequireInteractivePermission(
            actor,
            profile.IsEstimate ? AppPermissions.Finance.AccessAccounting : AppPermissions.Purchases.ManageBills,
            profile.IsEstimate ? "reverse overhead estimate write-offs" : "reverse recurring bill balance write-offs");

        var existing = record.WriteOffs.FirstOrDefault(w => w.Id == writeOffId);
        if (existing == null)
        {
            throw new InvalidOperationException($"Write-off with ID '{writeOffId}' not found on recurring bill '{profile.Name}'.");
        }

        EnsureWriteOffJournalEntryUnlocked(JournalSourceType.RecurringWriteOff, $"{profile.Id}:{record.Id}:{existing.Id}");

        var oldStatus = record.Status;
        var deletedAmount = existing.Amount;
        record.WriteOffs.Remove(existing);

        if (record.BalanceDue > 0m)
        {
            record.IsPaid = false;
            if (record.Payments.Count == 0)
            {
                record.DatePaid = null;
            }
        }

        DocumentHistoryHelper.RecordPaymentAction(
            record,
            DocumentHistoryActions.WriteOffReversed,
            oldStatus,
            record.Status,
            $"Reversed balance write-off of {deletedAmount:C2}",
            user,
            reason);

        _accounting.RecurringExpenses.Save(profile);
        _ledgerService?.SyncRecurringExpense(profile);
    }

    private BalanceWriteOff PrepareAndValidateWriteOff(BalanceWriteOff input, decimal currentBalanceDue, string? user)
    {
        var remaining = Math.Round(currentBalanceDue, 2, MidpointRounding.AwayFromZero);
        if (remaining <= 0m)
        {
            throw new InvalidOperationException("There is no remaining open balance to write off.");
        }

        var amount = Math.Round(input.Amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
        {
            throw new ArgumentException("Write-off amount must be greater than zero.", nameof(input));
        }
        if (amount > remaining)
        {
            throw new InvalidOperationException($"Write-off amount ({amount:C2}) cannot exceed the remaining balance ({remaining:C2}).");
        }
        if (string.IsNullOrWhiteSpace(input.Reason))
        {
            throw new ArgumentException("A reason is required when writing off a balance.", nameof(input));
        }

        var defaultWriteOffCode = _ledgerService?.ResolveAccountCodeByRole(GlSystemRole.BadDebtsAndWriteOffs, "5860")
            ?? _accounting.GlAccounts?.GetBySystemRole(GlSystemRole.BadDebtsAndWriteOffs)?.Code
            ?? "5860";
        var resolvedCode = string.IsNullOrWhiteSpace(input.GlAccountCode)
            ? defaultWriteOffCode
            : input.GlAccountCode.Trim();

        var glAccounts = _accounting.GlAccounts;
        if (glAccounts != null)
        {
            var acct = glAccounts.GetByCode(resolvedCode);
            if (acct == null && !string.IsNullOrWhiteSpace(input.GlAccountCode) && glAccounts.GetAll().Count > 0)
            {
                throw new InvalidOperationException($"GL account '{resolvedCode}' was not found.");
            }
            if (acct != null && GlAccountSubType.IsCashOrCardPaymentAccount(acct.SubType))
            {
                throw new InvalidOperationException("Balance write-offs are non-cash adjustments and cannot post to a Bank or Credit Card GL account.");
            }
        }

        return new BalanceWriteOff
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id.Trim(),
            Date = input.Date == default ? DateTime.Today : input.Date.Date,
            Amount = amount,
            Reason = input.Reason.Trim(),
            GlAccountCode = resolvedCode,
            CreatedBy = DocumentHistoryHelper.NormalizeActor(string.IsNullOrWhiteSpace(input.CreatedBy) ? user : input.CreatedBy),
            CreatedAt = input.CreatedAt == default ? DateTime.UtcNow : input.CreatedAt
        };
    }

    private void EnsureWriteOffJournalEntryUnlocked(string sourceType, string sourceId)
    {
        var je = _accounting.JournalEntries?.GetBySource(sourceType, sourceId);
        if (je != null && je.IsLocked)
        {
            throw new InvalidOperationException("Cannot reverse this balance write-off because its journal entry is locked.");
        }
    }

    public void MarkCommissionPaid(CommissionLedgerRecord commission, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(commission);
        DomainActorGuard.RequireInteractivePermission(actor, AppPermissions.Finance.AccessAccounting, "mark commissions paid");
        commission.Status = CommissionStatus.Paid;
        commission.DatePaid = datePaid;
        if (!string.IsNullOrWhiteSpace(bankGlCode))
        {
            commission.PaymentSourceGlCode = bankGlCode;
        }

        _accounting.CommissionLedgerRecords.Save(commission);
        _ledgerService?.SyncCommission(commission);
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

    private void EnsureBillPurchaseOrderAccess(string? newPoId, string? existingPoId, Employee? actor, string action)
    {
        if (actor == null || actor.IsEffectiveAdmin) return;

        void ValidatePoProjectsAccessible(string poId)
        {
            var po = _accounting.PurchaseOrders.GetById(poId);
            if (po == null) return;

            var projectIds = po.LineItems?
                .Where(l => !string.IsNullOrWhiteSpace(l.ProjectId))
                .Select(l => l.ProjectId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            foreach (var projectId in projectIds)
            {
                var project = _projects.Projects.GetById(projectId);
                if (project == null || !ProjectAccessHelper.CanAccess(actor, project, _hr?.Teams, allowAccountingFinancialRead: false))
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: you do not have access to restricted project '{projectId}' referenced by purchase order '{poId}'.");
                }
            }
        }

        var normalizedExisting = string.IsNullOrWhiteSpace(existingPoId) ? string.Empty : existingPoId.Trim();
        var normalizedNew = string.IsNullOrWhiteSpace(newPoId) ? string.Empty : newPoId.Trim();

        if (!string.Equals(normalizedExisting, normalizedNew, StringComparison.OrdinalIgnoreCase))
        {
            DomainActorGuard.RequireAnyPermission(
                actor,
                $"link or unlink purchase orders when attempting to {action}",
                allowSuspended: false,
                AppPermissions.Purchases.ViewPOs,
                AppPermissions.Purchases.ManagePOs);

            if (!string.IsNullOrWhiteSpace(normalizedNew))
            {
                var targetPo = _accounting.PurchaseOrders.GetById(normalizedNew)
                    ?? throw new InvalidOperationException($"Purchase order '{normalizedNew}' was not found.");
                if (string.Equals(targetPo.Status, PurchaseOrderStatus.Draft, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(targetPo.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Cannot link a bill to a {targetPo.Status} purchase order.");
                }

                ValidatePoProjectsAccessible(normalizedNew);
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedExisting))
        {
            ValidatePoProjectsAccessible(normalizedExisting);
        }

        if (!string.IsNullOrWhiteSpace(normalizedNew))
        {
            ValidatePoProjectsAccessible(normalizedNew);
        }
    }

    public int BackfillTaxSplitOnBills(IEnumerable<string> billIds, string? user = null, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(billIds);
        if (actor != null)
        {
            DomainActorGuard.RequireAnyPermission(actor, "backfill tax splits on bills", allowSuspended: false,
                AppPermissions.Purchases.ManageBills, AppPermissions.Purchases.ApproveBills, AppPermissions.Finance.AccessAccounting);
        }

        if (FinancialCalculationEngine.ActiveCombinedTaxRate <= 0m) return 0;

        var periodLock = _accounting.GetAccountingConfig().PeriodLockDate;
        int updated = 0;

        foreach (var id in billIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bill = _accounting.Bills.GetById(id);
            if (bill == null) continue;
            if (bill.IsVoid || string.Equals(bill.Status, BillStatus.Void, StringComparison.OrdinalIgnoreCase)) continue;
            if (bill.IsTaxExempt || bill.Amount <= 0m || bill.TaxAmount > 0m || bill.TpsAmount > 0m || bill.TvqAmount > 0m) continue;
            if (periodLock.HasValue && bill.Date.Date <= periodLock.Value.Date) continue;

            var breakdown = FinancialCalculationEngine.CalculateFromGrandTotal(bill.Amount, isTaxExempt: false);
            bill.SubTotal = breakdown.SubTotal;
            bill.TpsAmount = breakdown.TpsAmount;
            bill.TvqAmount = breakdown.TvqAmount;
            bill.TaxAmount = breakdown.TaxAmount;
            bill.TaxComponents = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(
                breakdown.SubTotal,
                FinancialCalculationEngine.ActiveTemplate?.SalesTaxes,
                isTaxExempt: false,
                forPurchases: true);

            bill.History ??= [];
            bill.History.Add(new DocumentHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                ChangedBy = string.IsNullOrWhiteSpace(user) ? (actor?.FullName ?? "System") : user,
                Action = DocumentHistoryActions.Edited,
                Summary = $"Tax split backfilled: {bill.Amount:C2} -> SubTotal {bill.SubTotal:C2} + Tax {bill.TaxAmount:C2}."
            });

            _accounting.Bills.Save(bill);
            _ledgerService?.SyncBill(bill);
            updated++;
        }

        return updated;
    }

    public void ApplySuggestedInvoicePayment(string suggestionId, string? user = null, Employee? actor = null)
    {
        if (string.IsNullOrWhiteSpace(suggestionId) || _accounting.SuggestedInvoicePayments == null) return;
        if (actor != null)
        {
            DomainActorGuard.RequireAnyPermission(actor, "apply suggested invoice payments", allowSuspended: false,
                AppPermissions.Finance.ApproveInvoices, AppPermissions.Finance.AccessAccounting);
        }

        var suggestion = _accounting.SuggestedInvoicePayments.GetById(suggestionId)
            ?? throw new InvalidOperationException("Suggested invoice payment not found.");
        if (!string.Equals(suggestion.Status, SuggestedInvoicePaymentStatus.Pending, StringComparison.OrdinalIgnoreCase))
            return;

        var invoice = _accounting.Invoices.GetById(suggestion.InvoiceId)
            ?? throw new InvalidOperationException($"Invoice '{suggestion.InvoiceNumber}' was not found.");

        var actorName = string.IsNullOrWhiteSpace(user) ? (actor?.FullName ?? "System") : user;
        if (!InvoiceStatus.IsPosted(invoice.Status))
        {
            throw new InvalidOperationException($"Invoice {invoice.InvoiceNumber} is not finalized ({invoice.Status}). Finalize it in the project before recording payment.");
        }

        var payment = new PaymentRecord
        {
            Id = Guid.NewGuid().ToString(),
            Date = suggestion.PaymentDate,
            Amount = suggestion.SuggestedAmount,
            Method = "EFT / Wire",
            Reference = !string.IsNullOrWhiteSpace(suggestion.SourceEmailSubject) ? suggestion.SourceEmailSubject : $"Payment advice ({suggestion.InvoiceNumber})",
            Notes = $"Applied from Bill Inbox payment advice ({suggestion.SourceSenderEmail})"
        };

        RecordInvoicePayment(invoice, payment, actorName, actor);

        suggestion.Status = SuggestedInvoicePaymentStatus.Applied;
        suggestion.ResolvedAtUtc = DateTime.UtcNow;
        suggestion.ResolvedBy = actorName;
        _accounting.SuggestedInvoicePayments.Save(suggestion);
    }

    public void DismissSuggestedInvoicePayment(string suggestionId, string? user = null, Employee? actor = null)
    {
        if (string.IsNullOrWhiteSpace(suggestionId) || _accounting.SuggestedInvoicePayments == null) return;
        var suggestion = _accounting.SuggestedInvoicePayments.GetById(suggestionId);
        if (suggestion == null) return;

        suggestion.Status = SuggestedInvoicePaymentStatus.Dismissed;
        suggestion.ResolvedAtUtc = DateTime.UtcNow;
        suggestion.ResolvedBy = string.IsNullOrWhiteSpace(user) ? (actor?.FullName ?? "System") : user;
        _accounting.SuggestedInvoicePayments.Save(suggestion);
    }
}
