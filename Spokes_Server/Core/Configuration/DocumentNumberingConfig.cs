namespace Spokes_Server.Core.Configuration;

public class DocumentNumberingConfig
{
    public string QuotePrefix { get; set; } = "Q";
    public string InvoicePrefix { get; set; } = "INV";
    public string PurchaseOrderPrefix { get; set; } = "PO";
    public string BillPrefix { get; set; } = "BILL";
    public string PayRunPrefix { get; set; } = "PAY";
    public string ReportPrefix { get; set; } = "RPT";
}
