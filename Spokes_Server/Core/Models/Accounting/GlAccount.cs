namespace Spokes_Server.Core.Models.Accounting;

using System;
using Spokes_Server.Core.Data;

public static class GlAccountType
{
    public const string Asset = "Asset";
    public const string Liability = "Liability";
    public const string Equity = "Equity";
    public const string Revenue = "Revenue";
    public const string Expense = "Expense";
    public static readonly string[] All = [Asset, Liability, Equity, Revenue, Expense];
}

public static class GlAccountSubType
{
    public const string Bank = "Bank";
    public const string CreditCard = "CreditCard";
    public const string LineOfCredit = "LineOfCredit";
    public const string Loan = "Loan";
    public const string AccountsReceivable = "AccountsReceivable";
    public const string PrepaidOrOtherAsset = "PrepaidOrOtherAsset";
    public const string FixedAsset = "FixedAsset";
    public const string AccountsPayable = "AccountsPayable";
    public const string SalesTax = "SalesTax";
    public const string PayrollLiability = "PayrollLiability";
    public const string OtherLiability = "OtherLiability";
    public const string Equity = "Equity";
    public const string OperatingRevenue = "OperatingRevenue";
    public const string OtherIncome = "OtherIncome";
    public const string DirectCost = "DirectCost";
    public const string PayrollExpense = "PayrollExpense";
    public const string Overhead = "Overhead";

    public static readonly string[] All =
    [
        Bank, CreditCard, LineOfCredit, Loan, AccountsReceivable, PrepaidOrOtherAsset, FixedAsset,
        AccountsPayable, SalesTax, PayrollLiability, OtherLiability,
        Equity, OperatingRevenue, OtherIncome,
        DirectCost, PayrollExpense, Overhead
    ];

    public static string[] GetForAccountType(string? accountType) => (accountType ?? string.Empty) switch
    {
        GlAccountType.Asset => [Bank, AccountsReceivable, PrepaidOrOtherAsset, FixedAsset],
        GlAccountType.Liability => [CreditCard, LineOfCredit, Loan, AccountsPayable, SalesTax, PayrollLiability, OtherLiability],
        GlAccountType.Equity => [Equity],
        GlAccountType.Revenue => [OperatingRevenue, OtherIncome],
        GlAccountType.Expense => [DirectCost, PayrollExpense, Overhead],
        _ => All
    };

    public static string GetDefaultForAccountType(string? accountType) =>
        GetForAccountType(accountType)[0];

    public static bool IsCashOrCardPaymentAccount(string? subType) =>
        string.Equals(subType, Bank, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subType, CreditCard, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subType, LineOfCredit, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(subType, Loan, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Well-known system roles mapped automatically by <see cref="Services.Accounting.LedgerService"/>
/// when operational documents do not specify an explicit override GL account.
/// </summary>
public static class GlSystemRole
{
    public const string None = "";
    public const string PettyCash = "PettyCash";
    public const string OperatingBank = "OperatingBank";
    public const string CorporateCreditCard = "CorporateCreditCard";
    public const string AccountsReceivable = "AccountsReceivable";
    public const string AccountsPayable = "AccountsPayable";
    public const string PrepaidExpenses = "PrepaidExpenses";
    public const string FederalTaxCollected = "FederalTaxCollected";
    public const string FederalTaxRecoverable = "FederalTaxRecoverable";
    public const string RegionalTaxCollected = "RegionalTaxCollected";
    public const string RegionalTaxRecoverable = "RegionalTaxRecoverable";
    public const string SalesTaxRemittancePayable = "SalesTaxRemittancePayable";
    public const string WagesPayable = "WagesPayable";
    public const string PayrollDeductionsPayable = "PayrollDeductionsPayable";
    public const string VacationPayAccrued = "VacationPayAccrued";
    public const string EmployeeReimbursementsPayable = "EmployeeReimbursementsPayable";
    public const string CommissionsPayable = "CommissionsPayable";
    public const string OwnerEquity = "OwnerEquity";
    public const string RetainedEarnings = "RetainedEarnings";
    public const string ServiceRevenue = "ServiceRevenue";
    public const string MaterialRevenue = "MaterialRevenue";
    public const string InterestAndOtherIncome = "InterestAndOtherIncome";
    public const string DirectProjectPurchases = "DirectProjectPurchases";
    public const string DirectSubcontractors = "DirectSubcontractors";
    public const string WagesAndSalariesExpense = "WagesAndSalariesExpense";
    public const string EmployerPayrollTaxesExpense = "EmployerPayrollTaxesExpense";
    public const string CommissionsExpense = "CommissionsExpense";
    public const string TravelAndMileageExpense = "TravelAndMileageExpense";
    public const string OfficeAndOverheadExpense = "OfficeAndOverheadExpense";
    public const string RentAndUtilitiesExpense = "RentAndUtilitiesExpense";
    public const string SoftwareAndSubscriptionsExpense = "SoftwareAndSubscriptionsExpense";
    public const string BankChargesAndInterest = "BankChargesAndInterest";
    public const string ForeignExchangeGainLoss = "ForeignExchangeGainLoss";
    public const string BadDebtsAndWriteOffs = "BadDebtsAndWriteOffs";
    public const string OpeningBalanceEquity = "OpeningBalanceEquity";

    public static readonly string[] All =
    [
        None,
        PettyCash,
        OperatingBank,
        CorporateCreditCard,
        AccountsReceivable,
        AccountsPayable,
        PrepaidExpenses,
        FederalTaxCollected,
        FederalTaxRecoverable,
        RegionalTaxCollected,
        RegionalTaxRecoverable,
        SalesTaxRemittancePayable,
        WagesPayable,
        PayrollDeductionsPayable,
        VacationPayAccrued,
        EmployeeReimbursementsPayable,
        CommissionsPayable,
        OwnerEquity,
        RetainedEarnings,
        ServiceRevenue,
        MaterialRevenue,
        InterestAndOtherIncome,
        DirectProjectPurchases,
        DirectSubcontractors,
        WagesAndSalariesExpense,
        EmployerPayrollTaxesExpense,
        CommissionsExpense,
        TravelAndMileageExpense,
        OfficeAndOverheadExpense,
        RentAndUtilitiesExpense,
        SoftwareAndSubscriptionsExpense,
        BankChargesAndInterest,
        ForeignExchangeGainLoss,
        BadDebtsAndWriteOffs,
        OpeningBalanceEquity
    ];
}

/// <summary>
/// First-class General Ledger Account in Spokes' Chart of Accounts.
/// Persisted via <see cref="Data.Repositories.Accounting.GlAccountRepository"/>.
/// </summary>
public class GlAccount : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Account code (e.g. "1020", "1200", "2100", "4000", "5100").
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// English account name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// French account name (used when UI culture is French or in bilingual exports).
    /// </summary>
    public string NameFr { get; set; } = string.Empty;

    /// <summary>
    /// Primary classification: Asset, Liability, Equity, Revenue, Expense.
    /// </summary>
    public string Type { get; set; } = GlAccountType.Expense;

    /// <summary>
    /// Sub-classification (Bank, CreditCard, AccountsReceivable, AccountsPayable, SalesTax, DirectCost, Overhead, etc.).
    /// </summary>
    public string SubType { get; set; } = GlAccountSubType.Overhead;

    /// <summary>
    /// Standard tax/statutory reporting code (e.g. 4-digit Canadian GIFI code like "1000", "1060", "8000", or French PCG code).
    /// Enables direct import of Trial Balance into CPA software (CaseWare, Taxprep, Profile).
    /// </summary>
    public string StandardTaxCode { get; set; } = string.Empty;

    /// <summary>
    /// Optional <see cref="GlSystemRole"/> identifier used for default automated ledger postings.
    /// </summary>
    public string? SystemRole { get; set; }

    public string Currency { get; set; } = "CAD";
    public string Description { get; set; } = string.Empty;
    public bool IsSystemAccount { get; set; }
    public bool IsActive { get; set; } = true;

    public string GetLocalizedName(string? twoLetterLanguageName = null)
    {
        var lang = twoLetterLanguageName ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (string.Equals(lang, "fr", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(NameFr))
        {
            return NameFr;
        }
        return !string.IsNullOrWhiteSpace(Name) ? Name : NameFr;
    }

    public string DisplayLabel => $"{Code} — {GetLocalizedName()}";

    /// <summary>
    /// True when normal balance is Debit (Assets and Expenses); False when Credit (Liabilities, Equity, Revenue).
    /// </summary>
    public bool IsDebitNormal =>
        string.Equals(Type, GlAccountType.Asset, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Type, GlAccountType.Expense, StringComparison.OrdinalIgnoreCase);
}
