namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

public static class FinancialAccountType
{
    public const string Chequing = "Chequing";
    public const string Savings = "Savings";
    public const string CreditCard = "CreditCard";
    public const string LineOfCredit = "LineOfCredit";
    public const string PaymentProcessor = "PaymentProcessor";
    public const string Loan = "Loan";
    public const string CashOnHand = "CashOnHand";

    public static readonly string[] All =
    [
        Chequing,
        Savings,
        CreditCard,
        LineOfCredit,
        PaymentProcessor,
        Loan,
        CashOnHand
    ];

    public static bool IsAssetType(string? accountType) => accountType switch
    {
        Chequing or Savings or PaymentProcessor or CashOnHand => true,
        _ => false
    };

    public static bool IsLiabilityType(string? accountType) => accountType switch
    {
        CreditCard or LineOfCredit or Loan => true,
        _ => false
    };

    public static bool SupportsCreditLimit(string? accountType) => accountType switch
    {
        Chequing or CreditCard or LineOfCredit => true,
        _ => false
    };

    public static string GetGlAccountType(string? accountType) =>
        IsLiabilityType(accountType) ? GlAccountType.Liability : GlAccountType.Asset;

    public static string GetGlAccountSubType(string? accountType) => accountType switch
    {
        CreditCard => GlAccountSubType.CreditCard,
        LineOfCredit => GlAccountSubType.LineOfCredit,
        Loan => GlAccountSubType.Loan,
        _ => GlAccountSubType.Bank
    };

    public static string GetDefaultStandardTaxCode(string? accountType) => accountType switch
    {
        CashOnHand => "1001",
        Chequing or PaymentProcessor => "1002",
        Savings => "1003",
        CreditCard => "2620",
        LineOfCredit => "2700",
        Loan => "2700",
        _ => "1002"
    };

    public static (int RangeStart, int RangeEnd) GetGlCodeRange(string? accountType) => accountType switch
    {
        CashOnHand => (1010, 1019),
        Chequing => (1020, 1029),
        Savings => (1030, 1039),
        PaymentProcessor => (1040, 1049),
        CreditCard => (2120, 2129),
        LineOfCredit => (2150, 2159),
        Loan => (2500, 2549),
        _ => (1020, 1099)
    };
}

/// <summary>
/// First-class real-world Financial Account configured by the business owner in Business Settings
/// (Chequing, Savings, Credit Card, Line of Credit, Payment Processor, Loan, or Cash on Hand).
/// Linked 1:1 to a backing <see cref="GlAccount"/> via <see cref="LinkedGlAccountCode"/> so all
/// double-entry accounting happens transparently in the background.
/// Persisted via <see cref="Data.Repositories.Accounting.FinancialAccountRepository"/>.
/// </summary>
public class FinancialAccount : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-chosen real-world account name (e.g., "Desjardins Operating", "RBC Visa Corporate").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Real-world account classification from <see cref="FinancialAccountType"/>.
    /// </summary>
    public string AccountType { get; set; } = FinancialAccountType.Chequing;

    /// <summary>
    /// ISO currency code (e.g., "CAD", "USD", "EUR").
    /// </summary>
    public string Currency { get; set; } = "CAD";

    /// <summary>
    /// Optional bank, card issuer, or payment processor name (e.g., "Desjardins", "RBC", "Stripe").
    /// </summary>
    public string InstitutionName { get; set; } = string.Empty;

    /// <summary>
    /// Optional last 4 digits or short identifier for easy visual recognition.
    /// </summary>
    public string Last4Digits { get; set; } = string.Empty;

    /// <summary>
    /// Optional credit limit. Used for Credit Cards, standalone Lines of Credit, and Chequing accounts
    /// that have an attached operating line of credit / overdraft facility.
    /// </summary>
    public decimal? CreditLimit { get; set; }

    /// <summary>
    /// 1:1 link to the backing General Ledger <see cref="GlAccount.Code"/>.
    /// Managed automatically by <see cref="Services.Accounting.FinancialAccountService"/>.
    /// </summary>
    public string LinkedGlAccountCode { get; set; } = string.Empty;

    /// <summary>
    /// Default operating bank account used for deposits, bill payments, payroll disbursements, and tax remittances.
    /// </summary>
    public bool IsDefaultOperating { get; set; }

    /// <summary>
    /// Default corporate credit card used when the payment method is Credit Card.
    /// </summary>
    public bool IsDefaultCreditCard { get; set; }

    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when a Chequing account has an attached operating Line of Credit / Overdraft facility
    /// allowing its balance to go negative up to <see cref="CreditLimit"/> on the same statement.
    /// </summary>
    [JsonIgnore]
    public bool HasAttachedCreditLine =>
        string.Equals(AccountType, FinancialAccountType.Chequing, StringComparison.OrdinalIgnoreCase) &&
        CreditLimit is > 0m;

    /// <summary>
    /// Friendly real-world display label for day-to-day UI (never includes GL codes).
    /// Example: "Desjardins Operating (CAD) •••• 4821".
    /// </summary>
    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var baseName = string.IsNullOrWhiteSpace(Name) ? AccountType : Name.Trim();
            var currencyPart = !string.IsNullOrWhiteSpace(Currency) ? $" ({Currency.Trim().ToUpperInvariant()})" : string.Empty;
            var digitsPart = !string.IsNullOrWhiteSpace(Last4Digits) ? $" •••• {Last4Digits.Trim()}" : string.Empty;
            return $"{baseName}{currencyPart}{digitsPart}";
        }
    }
}
