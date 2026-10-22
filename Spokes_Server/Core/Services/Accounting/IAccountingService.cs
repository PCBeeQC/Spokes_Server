using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Core.Services.Accounting;

public interface IAccountingService
{
    decimal CalculateProjectGrossMargin(string projectId);
    void EvaluateProjectCommissions(string projectId);
    void ProcessInvoiceStatusChange(Invoice invoice, string oldStatus, string newStatus, string? user = null);
    void RecordInvoicePayment(Invoice invoice, PaymentRecord payment, string? user = null);
    void RecordBillPayment(Bill bill, PaymentRecord payment);
    void ApproveBill(Bill bill, string? user = null);
    void VoidBill(Bill bill, string reason, string? user = null);
    void UnvoidBill(Bill bill, string? user = null);
}
