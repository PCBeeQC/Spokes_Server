namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;

public enum AccountingHealthSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public static class AccountingHealthActionKey
{
    public const string OpenGeneralLedger = "general-ledger";
    public const string OpenMigration = "migration";
    public const string OpenReceivables = "receivables";
    public const string OpenPayables = "payables";
    public const string OpenPayablesMissingTax = "payables-missing-tax";
    public const string OpenBankReconciliation = "bank-reconciliation";
    public const string OpenTaxFiling = "tax-filing";
    public const string OpenPayroll = "payroll";
    public const string OpenRecurringExpenses = "recurring-expenses";
    public const string OpenBusinessSettings = "business-settings";
    public const string ResyncMissingJEs = "resync-missing-jes";
    public const string BackfillBillTaxes = "backfill-bill-taxes";
    public const string GenerateDueRecurringBills = "generate-due-recurring";
}

public sealed class AccountingHealthIssue
{
    public string Code { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public AccountingHealthSeverity Severity { get; set; } = AccountingHealthSeverity.Warning;
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public int AffectedCount { get; set; }
    public decimal? AffectedAmount { get; set; }
    public string ActionKey { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = string.Empty;
}

public sealed class AccountingCloseChecklistStep
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsComplete { get; set; }
    public bool HasBlocker { get; set; }
    public string ActionKey { get; set; } = string.Empty;
}

public sealed class AccountingHealthReport
{
    public DateTime EvaluatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime AsOfDate { get; set; } = DateTime.Today;
    public List<AccountingHealthIssue> Issues { get; set; } = [];
    public List<AccountingCloseChecklistStep> CloseChecklist { get; set; } = [];

    public int CriticalCount => Issues.Count(i => i.Severity == AccountingHealthSeverity.Critical);
    public int WarningCount => Issues.Count(i => i.Severity == AccountingHealthSeverity.Warning);
    public int InfoCount => Issues.Count(i => i.Severity == AccountingHealthSeverity.Info);
    public int CompletedChecklistCount => CloseChecklist.Count(s => s.IsComplete);
    public bool IsReadyForClose => CriticalCount == 0 && WarningCount == 0;
}
