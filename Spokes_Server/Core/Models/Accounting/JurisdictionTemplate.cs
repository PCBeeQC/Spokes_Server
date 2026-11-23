namespace Spokes_Server.Core.Models.Accounting;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Declarative accounting, sales tax, mileage, Chart of Accounts, and payroll template for a jurisdiction.
/// Stored as `.spokes-jurisdiction.json` in `Spokes_Marketplace/accounting-templates/`.
/// </summary>
public class JurisdictionTemplate
{
    public string SchemaVersion { get; set; } = "1.0";
    public string JurisdictionId { get; set; } = "CA-QC";
    public string Name { get; set; } = "Canada — Québec";
    public string NameFr { get; set; } = "Canada — Québec";
    public string CountryCode { get; set; } = "CA";
    public string RegionCode { get; set; } = "QC";
    public int TaxYear { get; set; } = 2026;
    public string DefaultCurrency { get; set; } = "CAD";

    /// <summary>
    /// Standard tax/statutory account mapping standard used by CPAs in this jurisdiction ("GIFI", "PCG", "IRS", "NONE").
    /// </summary>
    public string TaxMappingStandard { get; set; } = "GIFI";

    public List<GlAccountTemplateItem> ChartOfAccounts { get; set; } = [];
    public List<SalesTaxRule> SalesTaxes { get; set; } = [];
    public MileageAllowanceRule MileageRules { get; set; } = new();
    public JurisdictionPayrollRules PayrollRules { get; set; } = new();

    public decimal CombinedSalesTaxRate => SalesTaxes.Sum(t => t.Rate);
}

public class GlAccountTemplateItem
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public string Type { get; set; } = GlAccountType.Expense;
    public string SubType { get; set; } = GlAccountSubType.Overhead;
    public string StandardTaxCode { get; set; } = string.Empty;
    public string? SystemRole { get; set; }
}

public class SalesTaxRule
{
    public string Code { get; set; } = string.Empty; // e.g. "TPS", "TVQ", "HST", "VAT"
    public string Name { get; set; } = string.Empty;
    public string NameFr { get; set; } = string.Empty;
    public decimal Rate { get; set; } // e.g. 0.05m, 0.09975m
    public bool IsRecoverable { get; set; } = true; // True for VAT/GST/QST (ITCs/ITRs), False for US State Sales Tax
    public bool IsCompound { get; set; } // True if calculated on SubTotal + prior taxes
    public string AuthorityName { get; set; } = string.Empty; // e.g. "CRA", "Revenu Québec"
    public string RegistrationNumberLabel { get; set; } = string.Empty; // e.g. "GST / TPS Registration #", "QST / TVQ Registration #"
    public string CollectedLiabilityGlCode { get; set; } = "2160";
    public string RecoverableAssetGlCode { get; set; } = "2165";
}

public class MileageAllowanceRule
{
    /// <summary>
    /// Primary tax-free mileage allowance rate per kilometer (e.g. $0.70/km for first 5,000 km in Canada/Quebec).
    /// </summary>
    public decimal PrimaryRatePerKm { get; set; } = 0.70m;

    /// <summary>
    /// Optional annual kilometer threshold after which <see cref="SecondaryRatePerKm"/> applies (e.g. 5000m).
    /// </summary>
    public decimal? TierThresholdKm { get; set; } = 5000m;

    /// <summary>
    /// Secondary mileage rate per kilometer above the tier threshold (e.g. $0.64/km).
    /// </summary>
    public decimal SecondaryRatePerKm { get; set; } = 0.64m;
}
