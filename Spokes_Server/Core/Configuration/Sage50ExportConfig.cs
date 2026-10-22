namespace Spokes_Server.Core.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Represents a Customer or Vendor record imported from a Sage 50 Canadian Edition export file.
/// </summary>
public class SagePartyRecord
{
    public string Name { get; set; } = string.Empty;
    public string Contact { get; set; } = string.Empty;
    public string Street1 { get; set; } = string.Empty;
    public string Street2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Province { get; set; } = "QC";
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = "Canada";
    public string Phone1 { get; set; } = string.Empty;
    public string Phone2 { get; set; } = string.Empty;
    public string Fax { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
}

/// <summary>
/// Represents a General Ledger account imported from Sage 50's Chart of Accounts export.
/// </summary>
public class SageGlAccountRecord
{
    public string AccountNumber { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string AccountType { get; set; } = string.Empty;
    public string AccountClass { get; set; } = string.Empty;
    public string AccountNameFr { get; set; } = string.Empty;
    public string DisplayLabel => string.IsNullOrWhiteSpace(AccountName) ? AccountNumber : $"{AccountNumber} - {AccountName}";
}

/// <summary>
/// Represents an Inventory or Service item imported from Sage 50's Inventory &amp; Services export.
/// </summary>
public class SageServiceItemRecord
{
    public string ItemId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ItemType { get; set; } = "Service";
    public string Unit { get; set; } = string.Empty;
    public string LinkedRevenueGl { get; set; } = string.Empty;
    public string LinkedExpenseGl { get; set; } = string.Empty;
    public string DescriptionFr { get; set; } = string.Empty;
    public string DisplayLabel => string.IsNullOrWhiteSpace(Description) ? ItemId : $"{ItemId} - {Description}";
}

/// <summary>
/// Configuration for exporting financial transactions to Sage 50 Canadian Edition.
/// </summary>
public class Sage50ExportConfig
{
    /// <summary>
    /// Sage 50 version header in .imp files. Defaults to the universal "12001".
    /// </summary>
    public string VersionHeader { get; set; } = "12001";

    /// <summary>
    /// Country code for Sage 50. 1 = Canada (English), 3 = Canada (French).
    /// </summary>
    public string CountryCode { get; set; } = "1";

    /// <summary>
    /// General Ledger account for Accounts Receivable (e.g. 1200).
    /// </summary>
    public string AccountsReceivableGl { get; set; } = "1200";

    /// <summary>
    /// General Ledger account for Accounts Payable (e.g. 2100).
    /// </summary>
    public string AccountsPayableGl { get; set; } = "2100";

    /// <summary>
    /// Default General Ledger account for sales/service revenue (e.g. 4100).
    /// </summary>
    public string DefaultSalesRevenueGl { get; set; } = "4100";

    /// <summary>
    /// Optional Sage 50 Service/Inventory Item ID for sales lines (falls back to DefaultSalesRevenueGl if empty).
    /// </summary>
    public string DefaultSalesItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Default General Ledger account for purchase expenses / COGS (e.g. 5000).
    /// </summary>
    public string DefaultExpenseGl { get; set; } = "5000";

    /// <summary>
    /// Optional Sage 50 Service/Inventory Item ID for purchase lines (falls back to DefaultExpenseGl if empty).
    /// </summary>
    public string DefaultPurchaseItemCode { get; set; } = string.Empty;

    /// <summary>
    /// Default General Ledger account for recurring overhead/utility bills (e.g. 5200).
    /// </summary>
    public string DefaultRecurringExpenseGl { get; set; } = "5200";

    /// <summary>
    /// Default General Ledger account for employee expense report reimbursements (e.g. 5300).
    /// </summary>
    public string DefaultEmployeeExpenseGl { get; set; } = "5300";

    /// <summary>
    /// Default General Ledger account for employee kilometrage / travel reimbursements (e.g. 5310).
    /// </summary>
    public string DefaultKilometrageGl { get; set; } = "5310";

    /// <summary>
    /// Default General Ledger account for sales commissions (e.g. 5400).
    /// </summary>
    public string DefaultCommissionGl { get; set; } = "5400";

    /// <summary>
    /// Tax code in Sage 50 for TPS / GST (e.g. TPS or GST).
    /// </summary>
    public string TpsTaxCode { get; set; } = "TPS";

    /// <summary>
    /// General Ledger account for TPS/GST Payable/Receivable (e.g. 2210).
    /// </summary>
    public string TpsTaxAccount { get; set; } = "2210";

    /// <summary>
    /// Tax code in Sage 50 for TVQ / QST (e.g. TVQ or QST).
    /// </summary>
    public string TvqTaxCode { get; set; } = "TVQ";

    /// <summary>
    /// General Ledger account for TVQ/QST Payable/Receivable (e.g. 2220).
    /// </summary>
    public string TvqTaxAccount { get; set; } = "2220";

    /// <summary>
    /// Combined tax code in Sage 50 for Quebec sales taxes (e.g. Q).
    /// </summary>
    public string CombinedTaxCode { get; set; } = "Q";

    /// <summary>
    /// When true, emits &lt;customers&gt; and &lt;vendors&gt; blocks at the top of the .imp file
    /// for any Client or Supplier not already present in the imported Sage 50 baseline.
    /// </summary>
    public bool IncludeNewCustomersAndVendorsInImp { get; set; } = true;

    /// <summary>
    /// Timestamp of the last Sage 50 baseline import.
    /// </summary>
    public DateTime? LastBaselineImportAt { get; set; }

    /// <summary>
    /// Customers imported from Sage 50 Export Records.
    /// </summary>
    public List<SagePartyRecord> ImportedCustomers { get; set; } = [];

    /// <summary>
    /// Vendors imported from Sage 50 Export Records.
    /// </summary>
    public List<SagePartyRecord> ImportedVendors { get; set; } = [];

    /// <summary>
    /// Chart of Accounts imported from Sage 50 Export Records.
    /// </summary>
    public List<SageGlAccountRecord> ImportedAccounts { get; set; } = [];

    /// <summary>
    /// Inventory &amp; Service items imported from Sage 50 Export Records.
    /// </summary>
    public List<SageServiceItemRecord> ImportedServiceItems { get; set; } = [];

    /// <summary>
    /// Explicit mapping from Spokes Client Name to Sage 50 Customer Name (max 35 chars).
    /// </summary>
    public Dictionary<string, string> CustomerNameMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit mapping from Spokes Supplier / Employee / Beneficiary Name to Sage 50 Vendor Name (max 35 chars).
    /// </summary>
    public Dictionary<string, string> VendorNameMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Sage50ExportConfig Clone() => new()
    {
        VersionHeader = VersionHeader,
        CountryCode = CountryCode,
        AccountsReceivableGl = AccountsReceivableGl,
        AccountsPayableGl = AccountsPayableGl,
        DefaultSalesRevenueGl = DefaultSalesRevenueGl,
        DefaultSalesItemCode = DefaultSalesItemCode,
        DefaultExpenseGl = DefaultExpenseGl,
        DefaultPurchaseItemCode = DefaultPurchaseItemCode,
        DefaultRecurringExpenseGl = DefaultRecurringExpenseGl,
        DefaultEmployeeExpenseGl = DefaultEmployeeExpenseGl,
        DefaultKilometrageGl = DefaultKilometrageGl,
        DefaultCommissionGl = DefaultCommissionGl,
        TpsTaxCode = TpsTaxCode,
        TpsTaxAccount = TpsTaxAccount,
        TvqTaxCode = TvqTaxCode,
        TvqTaxAccount = TvqTaxAccount,
        CombinedTaxCode = CombinedTaxCode,
        IncludeNewCustomersAndVendorsInImp = IncludeNewCustomersAndVendorsInImp,
        LastBaselineImportAt = LastBaselineImportAt,
        ImportedCustomers = ImportedCustomers.Select(c => new SagePartyRecord
        {
            Name = c.Name, Contact = c.Contact, Street1 = c.Street1, Street2 = c.Street2,
            City = c.City, Province = c.Province, PostalCode = c.PostalCode, Country = c.Country,
            Phone1 = c.Phone1, Phone2 = c.Phone2, Fax = c.Fax, Email = c.Email, Website = c.Website
        }).ToList(),
        ImportedVendors = ImportedVendors.Select(v => new SagePartyRecord
        {
            Name = v.Name, Contact = v.Contact, Street1 = v.Street1, Street2 = v.Street2,
            City = v.City, Province = v.Province, PostalCode = v.PostalCode, Country = v.Country,
            Phone1 = v.Phone1, Phone2 = v.Phone2, Fax = v.Fax, Email = v.Email, Website = v.Website
        }).ToList(),
        ImportedAccounts = ImportedAccounts.Select(a => new SageGlAccountRecord
        {
            AccountNumber = a.AccountNumber, AccountName = a.AccountName,
            AccountType = a.AccountType, AccountClass = a.AccountClass, AccountNameFr = a.AccountNameFr
        }).ToList(),
        ImportedServiceItems = ImportedServiceItems.Select(s => new SageServiceItemRecord
        {
            ItemId = s.ItemId, Description = s.Description, ItemType = s.ItemType,
            Unit = s.Unit, LinkedRevenueGl = s.LinkedRevenueGl, LinkedExpenseGl = s.LinkedExpenseGl,
            DescriptionFr = s.DescriptionFr
        }).ToList(),
        CustomerNameMappings = new Dictionary<string, string>(CustomerNameMappings, StringComparer.OrdinalIgnoreCase),
        VendorNameMappings = new Dictionary<string, string>(VendorNameMappings, StringComparer.OrdinalIgnoreCase)
    };
}
