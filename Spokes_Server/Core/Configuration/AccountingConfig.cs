namespace Spokes_Server.Core.Configuration;

using System.Collections.Generic;

public class AccountingConfig
{
    public decimal DefaultInternalCommission { get; set; } = 5.0m;
    public decimal DefaultExternalCommission { get; set; } = 10.0m;
    public decimal KilometrageRate { get; set; } = 0.55m; // $/km
    public decimal DefaultTaxRate { get; set; } = 0.14975m;
    public string CurrencySymbol { get; set; } = "$";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public List<string> SupportedCurrencies { get; set; } = ["CAD", "USD", "EUR"];

    // Company Bill Mailbox & Sage 50 Integration
    public CompanyBillInboxSettings BillInbox { get; set; } = new();
    public Sage50ExportConfig SageSettings { get; set; } = new();
}
