namespace Spokes_Server.Core.Configuration;

using System.Collections.Generic;

public class AccountingConfig
{
    public decimal DefaultInternalCommission { get; set; } = 5.0m;
    public decimal DefaultExternalCommission { get; set; } = 10.0m;
    public decimal KilometrageRate { get; set; } = 0.70m; // $/km (primary rate)
    public decimal SecondaryKilometrageRate { get; set; } = 0.64m; // $/km above tier threshold
    public decimal? KilometrageTierThresholdKm { get; set; } = 5000m;
    public decimal DefaultTaxRate { get; set; } = 0.14975m;
    public string CurrencySymbol { get; set; } = "$";
    public string DefaultCurrency { get; set; } = "CAD";
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public List<string> SupportedCurrencies { get; set; } = ["CAD", "USD", "EUR"];

    // Active Jurisdiction Template & Fiscal Settings
    public string ActiveJurisdictionId { get; set; } = "CA-QC";
    public int ActiveTaxYear { get; set; } = 2026;
    public int FiscalYearStartMonth { get; set; } = 1; // 1 = January
    public string DefaultSalesTaxFilingFrequency { get; set; } = "Quarterly"; // Monthly, Quarterly, Annual
    public System.DateTime? PeriodLockDate { get; set; }

    /// <summary>
    /// Company tax registration numbers keyed by <see cref="Models.Accounting.SalesTaxRule.Code"/>
    /// (e.g. "TPS" -> "123456789 RT0001", "TVQ" -> "1234567890 TQ0001", "VAT" -> "FR123456789").
    /// </summary>
    public Dictionary<string, string> TaxRegistrationNumbersByCode { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

    // Company Bill Mailbox
    public CompanyBillInboxSettings BillInbox { get; set; } = new();
}
