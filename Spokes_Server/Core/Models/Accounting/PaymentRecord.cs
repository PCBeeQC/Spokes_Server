namespace Spokes_Server.Core.Models.Accounting;

public class PaymentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Date { get; set; } = DateTime.Today;
    public decimal Amount { get; set; } = 0;
    public string Reference { get; set; } = string.Empty; // Check #, Wire Ref
    public string Method { get; set; } = "Check"; // Optional: Check, Credit Card, Wire, ACH, Cash
    public string Notes { get; set; } = string.Empty;
}
