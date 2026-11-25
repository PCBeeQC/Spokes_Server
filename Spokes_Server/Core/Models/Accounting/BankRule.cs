namespace Spokes_Server.Core.Models.Accounting;

using System;
using Spokes_Server.Core.Data;

public static class BankRuleDirection
{
    public const string Any = "Any";
    public const string MoneyOut = "MoneyOut";
    public const string MoneyIn = "MoneyIn";
}

public static class BankRuleAction
{
    public const string CategorizeToAccount = "CategorizeToAccount";
    public const string Exclude = "Exclude";
}

/// <summary>
/// Universal user-defined bank categorization rule matching statement descriptions/payees
/// to a target GL account (or exclusion). Evaluated in ascending Priority order.
/// Auto-posting is strictly guarded against pre-cutover dates to prevent double-counting.
/// </summary>
public class BankRule : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Comma-separated case-insensitive keywords matched against bank line Description and Payee
    /// (e.g., "DEBOURSE MCR, REMB. MCR" or "FRAIS FORFAIT").
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Direction filter: Any, MoneyOut (negative amount), or MoneyIn (positive amount).
    /// </summary>
    public string Direction { get; set; } = BankRuleDirection.Any;

    /// <summary>
    /// Optional bank/card GL account scope (null or empty applies to all financial accounts).
    /// </summary>
    public string? BankGlAccountCode { get; set; }

    /// <summary>
    /// Action to take when matched: CategorizeToAccount or Exclude.
    /// </summary>
    public string Action { get; set; } = BankRuleAction.CategorizeToAccount;

    /// <summary>
    /// Target GL account code when Action is CategorizeToAccount (e.g., "2127", "5800", "2210").
    /// </summary>
    public string TargetGlAccountCode { get; set; } = string.Empty;

    /// <summary>
    /// Whether to extract sales taxes (ITC/ITR or collected) when posting via this rule.
    /// </summary>
    public bool IncludeSalesTax { get; set; }

    /// <summary>
    /// When true, automatically posts/excludes matching post-cutover lines upon statement import.
    /// Default false (pre-fills and suggests for 1-click user review).
    /// </summary>
    public bool AutoPostOnImport { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
