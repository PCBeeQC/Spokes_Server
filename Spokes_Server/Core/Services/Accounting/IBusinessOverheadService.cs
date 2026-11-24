namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

public interface IBusinessOverheadService
{
    /// <summary>
    /// Normalizes any legacy <c>IsEstimate = true</c> recurring expense records into unified profiles
    /// so that a single recurring profile list powers both forecasting/proration and actual payables.
    /// </summary>
    void NormalizeLegacyEstimates(Employee? actor = null);

    /// <summary>
    /// Returns the monthly-prorated pre-tax amount for a recurring expense profile in a given calendar month.
    /// </summary>
    decimal GetRecurringProfileMonthlyPreTax(RecurringExpense profile, int year, int month);

    /// <summary>
    /// Computes the monthly-prorated business overhead pool across the calendar months in
    /// [<paramref name="startDate"/> .. <paramref name="endDate"/>] and calculates the objective
    /// Direct Labor Hours allocation share for <paramref name="targetProjectId"/> (when specified).
    /// </summary>
    PeriodOverheadCalculationResult CalculatePeriodOverhead(
        DateTime startDate,
        DateTime endDate,
        string? targetProjectId = null,
        Employee? actor = null);

    /// <summary>
    /// Returns active scheduled recurring expense profiles whose <see cref="RecurringExpense.NextDueDate"/>
    /// is on or before <paramref name="asOfDate"/>.
    /// </summary>
    List<DueRecurringOccurrence> GetOverdueRecurringOccurrences(DateTime? asOfDate = null);

    /// <summary>
    /// On-demand generator that creates unpaid <see cref="RecurringBillRecord"/> entries for all missed
    /// periods up to <paramref name="upToDate"/>, advances <see cref="RecurringExpense.NextDueDate"/>,
    /// persists to disk, and syncs the General Ledger.
    /// </summary>
    int GenerateDueRecurringBills(IEnumerable<string>? expenseIds = null, DateTime? upToDate = null, Employee? actor = null);
}

public sealed class DueRecurringOccurrence
{
    public string RecurringExpenseId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Supplier { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime NextDueDate { get; set; }
    public int MissedPeriodsCount { get; set; }
}

public class PeriodOverheadCalculationResult
{
    public DateTime PeriodStartDate { get; set; }
    public DateTime PeriodEndDate { get; set; }
    public int MonthCount { get; set; }

    // Company-Wide Direct Client Labor (Allocation Driver)
    public decimal TotalCompanyDirectLaborHours { get; set; }
    public decimal TotalCompanyDirectLaborCost { get; set; }
    public decimal TargetProjectDirectLaborHours { get; set; }
    public decimal AllocationShareRatio { get; set; }
    public decimal AllocationSharePercent { get; set; }

    // Pillar 1: Recurring Fixed Costs (Prorated Monthly)
    public decimal RecurringFixedPreTax { get; set; }
    public decimal RecurringFixedTax { get; set; }
    public decimal RecurringFixedTotal => RecurringFixedPreTax + RecurringFixedTax;

    // Pillar 2: One-Time Overhead Bills (Amortized over 12 Months)
    public decimal OneTimeAmortizedPreTax { get; set; }
    public decimal OneTimeAmortizedTax { get; set; }
    public decimal OneTimeAmortizedTotal => OneTimeAmortizedPreTax + OneTimeAmortizedTax;

    // Pillar 3: Internal Project Unbillable Labor
    public decimal InternalLaborHours { get; set; }
    public decimal InternalLaborCost { get; set; }

    // Pillar 4: Internal Project Purchases & Expenses
    public decimal InternalPurchasesAndExpensesPreTax { get; set; }
    public decimal InternalPurchasesAndExpensesTax { get; set; }
    public decimal InternalPurchasesAndExpensesTotal => InternalPurchasesAndExpensesPreTax + InternalPurchasesAndExpensesTax;

    // Combined Company Overhead Pool
    public decimal CompanyOverheadPoolPreTax =>
        RecurringFixedPreTax + OneTimeAmortizedPreTax + InternalLaborCost + InternalPurchasesAndExpensesPreTax;

    public decimal CompanyOverheadPoolTax =>
        RecurringFixedTax + OneTimeAmortizedTax + InternalPurchasesAndExpensesTax;

    public decimal CompanyOverheadPoolTotal => CompanyOverheadPoolPreTax + CompanyOverheadPoolTax;

    // Split Pools (Fixed Business Overhead vs. Internal Project Costs)
    public decimal FixedOverheadPoolPreTax => RecurringFixedPreTax + OneTimeAmortizedPreTax;
    public decimal FixedOverheadPoolTax => RecurringFixedTax + OneTimeAmortizedTax;
    public decimal FixedOverheadPoolTotal => FixedOverheadPoolPreTax + FixedOverheadPoolTax;

    public decimal InternalCostsPoolPreTax => InternalLaborCost + InternalPurchasesAndExpensesPreTax;
    public decimal InternalCostsPoolTax => InternalPurchasesAndExpensesTax;
    public decimal InternalCostsPoolTotal => InternalCostsPoolPreTax + InternalCostsPoolTax;

    // Target Project Allocated Overhead (Fixed Business Overhead vs. Internal Costs + Combined Total)
    public decimal AllocatedFixedOverheadPreTax { get; set; }
    public decimal AllocatedFixedOverheadTax { get; set; }
    public decimal AllocatedFixedOverheadTotal => AllocatedFixedOverheadPreTax + AllocatedFixedOverheadTax;

    public decimal AllocatedInternalCostsPreTax { get; set; }
    public decimal AllocatedInternalCostsTax { get; set; }
    public decimal AllocatedInternalCostsTotal => AllocatedInternalCostsPreTax + AllocatedInternalCostsTax;

    public decimal AllocatedOverheadPreTax { get; set; }
    public decimal AllocatedOverheadTax { get; set; }
    public decimal AllocatedOverheadTotal => AllocatedOverheadPreTax + AllocatedOverheadTax;

    // Effective Hourly Overhead Burden Rates
    public decimal EffectiveOverheadRatePerDirectHourPreTax =>
        TotalCompanyDirectLaborHours > 0m
            ? Math.Round(CompanyOverheadPoolPreTax / TotalCompanyDirectLaborHours, 2, MidpointRounding.AwayFromZero)
            : 0m;

    public decimal EffectiveOverheadRatePerDirectHourTotal =>
        TotalCompanyDirectLaborHours > 0m
            ? Math.Round(CompanyOverheadPoolTotal / TotalCompanyDirectLaborHours, 2, MidpointRounding.AwayFromZero)
            : 0m;

    // Structured Breakdown Rows (for Project Reports & Operating Costs Overview)
    public List<ReportOverheadBreakdownRow> BreakdownRows { get; set; } = [];

    // Detailed Per-Project Internal Activity (for Operating Costs -> Internal Projects Tab)
    public List<InternalProjectOverheadSummary> InternalProjects { get; set; } = [];
}

public class InternalProjectOverheadSummary
{
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectNumber { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public List<string> ContributorNames { get; set; } = [];
    public decimal LaborCost { get; set; }
    public decimal PurchasesPreTax { get; set; }
    public decimal PurchasesTax { get; set; }
    public decimal ExpensesPreTax { get; set; }
    public decimal ExpensesTax { get; set; }

    public decimal TotalPreTax => LaborCost + PurchasesPreTax + ExpensesPreTax;
    public decimal TotalTax => PurchasesTax + ExpensesTax;
    public decimal GrandTotal => TotalPreTax + TotalTax;
}
