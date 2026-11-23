namespace Spokes_Server.Core.Models.HR;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;

public static class PayRunStatus
{
    public const string Draft = "Draft";
    public const string Finalized = "Finalized";
    public const string Paid = "Paid";
    public const string Void = "Void";
    /// <summary>Mid-year migration seed containing pre-cutover payroll YTD totals for T4/RL-1 and annual cap tracking without reposting GL.</summary>
    public const string OpeningYtd = "OpeningYtd";
}

/// <summary>
/// Represents a Payroll Run covering a pay period (<see cref="PeriodStart"/> .. <see cref="PeriodEnd"/>)
/// and containing calculated <see cref="PayStub"/> records for employees.
/// Persisted via <see cref="Data.Repositories.HR.PayRunRepository"/>.
/// </summary>
public class PayRun : IDataEntity, IHasDocumentHistory, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Sequential run number generated via <see cref="SequenceService"/> (e.g. "PAY-2611-0001").
    /// </summary>
    public string RunNumber { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime PayDate { get; set; } = DateTime.Today;
    public string PayFrequency { get; set; } = PayFrequencyType.BiWeekly;
    public string JurisdictionId { get; set; } = "CA-QC";
    public int TaxYear { get; set; } = DateTime.Today.Year;
    public string Currency { get; set; } = "CAD";

    public string Status { get; set; } = PayRunStatus.Draft;
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizedAt { get; set; }
    public DateTime? DatePaid { get; set; }
    public string? PaymentBankGlCode { get; set; } = "1020";
    public string? PaymentReference { get; set; }

    public bool IsDasRemitted { get; set; }
    /// <summary>Links this OpeningYtd pay run to its AccountingMigrationBatch for rollback.</summary>
    public string? MigrationBatchId { get; set; }
    public DateTime? DasRemittedAt { get; set; }
    public string? DasPaymentReference { get; set; }

    public List<PayStub> Stubs { get; set; } = [];
    public List<DocumentHistoryEntry> History { get; set; } = [];

    public decimal TotalGrossPay => Math.Round(Stubs.Sum(s => s.GrossPay), 2);
    public decimal TotalEmployeeDeductions => Math.Round(Stubs.Sum(s => s.TotalEmployeeDeductions), 2);
    public decimal TotalNetPay => Math.Round(Stubs.Sum(s => s.NetPay), 2);
    public decimal TotalEmployerContributions => Math.Round(Stubs.Sum(s => s.TotalEmployerContributions), 2);
    public decimal TotalVacationAccrued => Math.Round(Stubs.Sum(s => s.VacationAccruedDollars), 2);
    public decimal TotalEmployerCost => Math.Round(TotalGrossPay + TotalEmployerContributions + TotalVacationAccrued, 2);

    [JsonIgnore]
    public int PaidStubsCount => Stubs.Count(s => s.IsPaid);

    [JsonIgnore]
    public bool AreAllStubsPaid => Stubs.Count > 0 && Stubs.All(s => s.IsPaid);

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        foreach (var stub in Stubs)
        {
            if (!string.IsNullOrWhiteSpace(stub.PdfPath))
            {
                yield return stub.PdfPath;
            }
        }
    }
}

public class PayStub
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string CompensationType { get; set; } = PayrollCompensationType.Hourly;
    public decimal HourlyRate { get; set; }

    // Hours
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal VacationHoursTaken { get; set; }

    // Earnings
    public decimal RegularEarnings { get; set; }
    public decimal OvertimeEarnings { get; set; }
    public decimal VacationPaidOut { get; set; }
    public decimal BonusOrCommission { get; set; }
    public decimal GrossPay => Math.Round(RegularEarnings + OvertimeEarnings + VacationPaidOut + BonusOrCommission, 2);

    // Vacation Accrual
    public decimal VacationRate { get; set; } = 0.04m;
    public decimal VacationAccruedDollars { get; set; }
    public decimal EndingVacationBankDollars { get; set; }

    // Statutory Social Contributions (Employee & Employer portions)
    public List<PayStubContributionLine> StatutoryContributions { get; set; } = [];

    // Income Tax Withholdings
    public List<PayStubTaxLine> IncomeTaxLines { get; set; } = [];

    // Custom Employee Deductions
    public List<PayStubCustomLine> CustomDeductions { get; set; } = [];

    public decimal TotalStatutoryEmployeeDeductions =>
        Math.Round(StatutoryContributions.Sum(c => c.EmployeeAmount), 2);

    public decimal TotalIncomeTaxWithheld =>
        Math.Round(IncomeTaxLines.Sum(t => t.WithheldAmount), 2);

    public decimal TotalCustomEmployeeDeductions =>
        Math.Round(CustomDeductions.Where(d => !d.IsEmployerContribution).Sum(d => d.Amount), 2);

    public decimal TotalEmployeeDeductions =>
        Math.Round(TotalStatutoryEmployeeDeductions + TotalIncomeTaxWithheld + TotalCustomEmployeeDeductions, 2);

    public decimal NetPay =>
        Math.Max(0m, Math.Round(GrossPay - TotalEmployeeDeductions, 2));

    public decimal TotalEmployerContributions =>
        Math.Round(StatutoryContributions.Sum(c => c.EmployerAmount) +
                   CustomDeductions.Where(d => d.IsEmployerContribution).Sum(d => d.Amount), 2);

    // Year-to-Date Snapshot (including this PayStub)
    public decimal YtdGrossPay { get; set; }
    public decimal YtdNetPay { get; set; }
    public decimal YtdFederalTax { get; set; }
    public decimal YtdRegionalTax { get; set; }
    public Dictionary<string, decimal> YtdEmployeeContributions { get; set; } = [];
    public Dictionary<string, decimal> YtdEmployerContributions { get; set; } = [];

    // Individual employee disbursement tracking (since Spokes does not initiate bank transfers)
    public bool IsPaid { get; set; }
    public DateTime? DatePaid { get; set; }
    public string? PaymentReference { get; set; }

    public string PdfPath { get; set; } = string.Empty;
}

public class PayStubContributionLine
{
    public string Code { get; set; } = string.Empty; // "QPP", "QPP2", "QPIP", "EI_QC", "FSS", "CNESST", "CNT"
    public string Name { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty; // "Revenu Québec", "CRA"
    public string SlipBoxCode { get; set; } = string.Empty;
    public decimal InsurableEarningsThisPeriod { get; set; }
    public decimal EmployeeAmount { get; set; }
    public decimal EmployerAmount { get; set; }
    public bool IsManuallyOverridden { get; set; }
    public string LiabilityGlCode { get; set; } = "2210";
    public string ExpenseGlCode { get; set; } = "5420";
}

public class PayStubTaxLine
{
    public string Code { get; set; } = string.Empty; // "FED_CA_QC", "PROV_QC"
    public string Name { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty; // "CRA", "Revenu Québec"
    public string SlipBoxCode { get; set; } = string.Empty;
    public decimal TaxableIncomeThisPeriod { get; set; }
    public decimal WithheldAmount { get; set; }
    public bool IsManuallyOverridden { get; set; }
    public string LiabilityGlCode { get; set; } = "2210";
}

public class PayStubCustomLine
{
    public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsEmployerContribution { get; set; }
    public string GlAccountCode { get; set; } = "2210";
}
