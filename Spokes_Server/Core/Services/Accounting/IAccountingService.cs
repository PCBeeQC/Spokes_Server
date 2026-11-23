using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Accounting;

public interface IAccountingService
{
    decimal CalculateProjectGrossMargin(string projectId);
    void EvaluateProjectCommissions(string projectId);
    void ProcessInvoiceStatusChange(Invoice invoice, string oldStatus, string newStatus, string? user = null, Employee? actor = null);
    void RecordInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null, Employee? actor = null);
    void UpdateInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null, string? reason = null, Employee? actor = null);
    void DeleteInvoicePayment(Invoice invoice, string paymentId, string? user = null, string? reason = null, Employee? actor = null);
    void RecordInvoiceWriteOff(Invoice invoice, BalanceWriteOff writeOff, string? user = null, Employee? actor = null);
    void DeleteInvoiceWriteOff(Invoice invoice, string writeOffId, string? user = null, string? reason = null, Employee? actor = null);
    bool HasConsumedPrepaymentCredits(Invoice invoice);
    void SaveBill(Bill bill, string? user = null, Employee? actor = null);
    void DeleteBill(string billId, Employee? actor = null);
    void RecordBillPayment(Bill bill, PaymentRecord payment, string? user = null, Employee? actor = null);
    void RecordBillWriteOff(Bill bill, BalanceWriteOff writeOff, string? user = null, Employee? actor = null);
    void DeleteBillWriteOff(Bill bill, string writeOffId, string? user = null, string? reason = null, Employee? actor = null);
    void ApproveBill(Bill bill, string? user = null, Employee? actor = null);
    void VoidBill(Bill bill, string reason, string? user = null, Employee? actor = null);
    void UnvoidBill(Bill bill, string? user = null, Employee? actor = null);
    void SaveRecurringExpense(RecurringExpense profile, Employee? actor = null);
    void DeleteRecurringExpense(string profileId, Employee? actor = null);
    int MergeRecurringExpenses(string targetProfileId, IEnumerable<string> sourceProfileIds, Employee? actor = null);
    void MarkExpenseReportPaid(ExpenseReport report, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null);
    void MarkRecurringBillRecordPaid(RecurringExpense profile, RecurringBillRecord record, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null);
    void RecordRecurringBillWriteOff(RecurringExpense profile, RecurringBillRecord record, BalanceWriteOff writeOff, string? user = null, Employee? actor = null);
    void DeleteRecurringBillWriteOff(RecurringExpense profile, RecurringBillRecord record, string writeOffId, string? user = null, string? reason = null, Employee? actor = null);
    void MarkCommissionPaid(CommissionLedgerRecord commission, DateTime datePaid, string? bankGlCode = null, string? user = null, Employee? actor = null);
}
