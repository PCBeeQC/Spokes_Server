namespace Spokes_Server.Core.Services.HR;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Security;

public class DasAuthoritySummary
{
    public string AuthorityName { get; set; } = string.Empty; // e.g. "Revenu Québec", "CRA"
    public decimal GrossPayrollSubject { get; set; }
    public List<DasRemittanceLine> Lines { get; set; } = [];
    public decimal TotalEmployeeWithheld => Math.Round(Lines.Sum(l => l.EmployeeWithheld), 2);
    public decimal TotalEmployerContribution => Math.Round(Lines.Sum(l => l.EmployerContribution), 2);
    public decimal TotalRemittanceDue => Math.Round(TotalEmployeeWithheld + TotalEmployerContribution, 2);
}

public class DasRemittanceLine
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SlipOrFormBox { get; set; } = string.Empty;
    public decimal EmployeeWithheld { get; set; }
    public decimal EmployerContribution { get; set; }
    public decimal TotalLineDue => Math.Round(EmployeeWithheld + EmployerContribution, 2);
}

public class EmployeeYearEndSlipSummary
{
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public int TaxYear { get; set; }
    public string JurisdictionId { get; set; } = "CA-QC";

    /// <summary>
    /// T4 Box 14 / RL-1 Box A — Employment Income before deductions.
    /// </summary>
    public decimal GrossEmploymentIncome { get; set; }

    /// <summary>
    /// T4 Box 16/17 / RL-1 Box B.A — Base + 1st Additional QPP / CPP Employee Contributions.
    /// </summary>
    public decimal QppOrCppEmployee { get; set; }

    /// <summary>
    /// T4 Box 16A/17A / RL-1 Box B.B — Tier 2 QPP2 / CPP2 Employee Contributions.
    /// </summary>
    public decimal Qpp2OrCpp2Employee { get; set; }

    /// <summary>
    /// T4 Box 18 / RL-1 Box C — Employment Insurance (EI / AE) Employee Premiums.
    /// </summary>
    public decimal EiEmployee { get; set; }

    /// <summary>
    /// T4 Box 55 / RL-1 Box H — QPIP / RQAP Employee Premiums.
    /// </summary>
    public decimal QpipEmployee { get; set; }

    /// <summary>
    /// T4 Box 22 — Federal Income Tax Deducted.
    /// </summary>
    public decimal FederalIncomeTax { get; set; }

    /// <summary>
    /// RL-1 Box E — Québec / Provincial Income Tax Deducted.
    /// </summary>
    public decimal RegionalIncomeTax { get; set; }

    /// <summary>
    /// T4 Box 24 / RL-1 Box G — EI Insurable Earnings.
    /// </summary>
    public decimal EiInsurableEarnings { get; set; }

    /// <summary>
    /// T4 Box 26 / RL-1 Box G/I — QPP/CPP Pensionable Earnings.
    /// </summary>
    public decimal PensionableEarnings { get; set; }

    /// <summary>
    /// RL-1 Box I — QPIP Eligible Salary/Wages.
    /// </summary>
    public decimal QpipInsurableEarnings { get; set; }

    public decimal TotalEmployerTaxes { get; set; }
    public decimal TotalNetPay { get; set; }
    public decimal EndingVacationBankDollars { get; set; }
    public Dictionary<string, decimal> ContributionsByCode { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> TaxesByCode { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IPayrollCalculationEngine
{
    PayRun CreateDraftPayRun(DateTime periodStart, DateTime periodEnd, DateTime payDate, string payFrequency = PayFrequencyType.BiWeekly, bool assignRunNumber = true, Employee? actor = null);
    void RecalculatePayRun(PayRun payRun, Employee? actor = null);
    void RecalculateStub(PayRun payRun, PayStub stub, Employee? employee = null, JurisdictionTemplate? template = null, Employee? actor = null);
    PayRun SavePayRun(PayRun payRun, Employee? actor = null);
    PayRun FinalizePayRun(PayRun payRun, Employee? actor = null);
    PayRun MarkPayRunPaid(PayRun payRun, DateTime datePaid, string bankGlCode = "1020", string? reference = null, Employee? actor = null);
    PayRun SetPayStubsPaymentStatus(PayRun payRun, IEnumerable<string> stubIds, bool isPaid, DateTime? datePaid = null, string? reference = null, string bankGlCode = "1020", string? user = null, Employee? actor = null);
    void DeletePayRun(string payRunId, Employee? actor = null);
    PayRun RecordDasRemittance(PayRun payRun, DateTime remittedAt, string bankGlCode = "1020", string? reference = null, Employee? actor = null);
    List<DasAuthoritySummary> CalculateDasRemittanceSummary(DateTime periodStart, DateTime periodEnd, Employee? actor = null);
    List<EmployeeYearEndSlipSummary> CalculateYearEndSlipsSummary(int taxYear, Employee? actor = null);
    int SyncJurisdictionEmployerContributionsToRepository(JurisdictionTemplate? template = null, Employee? actor = null);
}

public class PayrollCalculationEngine : IPayrollCalculationEngine
{
    private readonly HrContext _hrContext;
    private readonly AccountingContext _accountingContext;
    private readonly ILedgerService _ledgerService;
    private readonly IJurisdictionTemplateService? _templateService;

    [ActivatorUtilitiesConstructor]
    public PayrollCalculationEngine(
        HrContext hrContext,
        AccountingContext accountingContext,
        ILedgerService? ledgerService = null,
        IJurisdictionTemplateService? templateService = null)
    {
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(accountingContext);
        _hrContext = hrContext;
        _accountingContext = accountingContext;
        _ledgerService = ledgerService ?? new LedgerService(accountingContext, null, hrContext, templateService);
        _templateService = templateService;
    }

    public PayRun CreateDraftPayRun(
        DateTime periodStart,
        DateTime periodEnd,
        DateTime payDate,
        string payFrequency = PayFrequencyType.BiWeekly,
        bool assignRunNumber = true,
        Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "create draft pay runs", allowSuspended: false);
        var s = periodStart.Date;
        var e = periodEnd.Date;
        var template = GetActiveTemplate();

        var runNumber = assignRunNumber
            ? (_hrContext.PayRuns != null ? _hrContext.PayRuns.GenerateRunNumber("PAY") : $"PAY-{payDate:yyMM}-0001")
            : string.Empty;

        var payRun = new PayRun
        {
            Id = Guid.NewGuid().ToString(),
            RunNumber = runNumber,
            PeriodStart = s,
            PeriodEnd = e,
            PayDate = payDate.Date,
            PayFrequency = payFrequency,
            JurisdictionId = template.JurisdictionId,
            TaxYear = payDate.Year,
            Currency = template.DefaultCurrency,
            Status = PayRunStatus.Draft
        };

        var activeEmployees = _hrContext.Employees?.GetAll()
            .Where(emp => emp.IsActive && !emp.IsSystem && (emp.HourlyRate > 0m || emp.Payroll?.AnnualSalary > 0m))
            .OrderBy(emp => emp.FullName)
            .ToList() ?? [];

        var allTimesheets = _hrContext.Timesheets?.GetAll()
            .Where(ts => !string.Equals(ts.Status, TimesheetStatus.Rejected, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        foreach (var emp in activeEmployees)
        {
            decimal loggedHours = 0m;
            decimal vacationHours = 0m;
            foreach (var ts in allTimesheets.Where(t => string.Equals(t.EmployeeId, emp.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (ts.Entries == null) continue;
                foreach (var entry in ts.Entries)
                {
                    if (entry.Date.Date >= s && entry.Date.Date <= e && entry.Hours > 0m)
                    {
                        var entryType = TimeEntryTypes.ResolveEntryType(entry.ProjectId, entry.EntryType);
                        if (string.Equals(entryType, TimeEntryTypes.Vacation, StringComparison.OrdinalIgnoreCase))
                        {
                            vacationHours += entry.Hours;
                        }
                        else if (!string.Equals(entryType, TimeEntryTypes.BankedTimeOff, StringComparison.OrdinalIgnoreCase))
                        {
                            loggedHours += entry.Hours;
                        }
                    }
                }
            }

            var periodsPerYear = PayFrequencyType.GetPeriodsPerYear(payFrequency);
            var defaultScheduledHours = Math.Round((emp.WeeklyHours > 0m ? emp.WeeklyHours : 40m) * 52m / periodsPerYear, 2);

            var regularHours = emp.Payroll?.CompensationType == PayrollCompensationType.Salaried
                ? (loggedHours > 0m ? loggedHours : defaultScheduledHours)
                : loggedHours;

            var stub = new PayStub
            {
                EmployeeId = emp.Id,
                EmployeeName = emp.FullName,
                JobTitle = emp.Position,
                CompensationType = emp.Payroll?.CompensationType ?? PayrollCompensationType.Hourly,
                HourlyRate = emp.HourlyRate,
                RegularHours = regularHours,
                VacationHoursTaken = vacationHours
            };

            RecalculateStubCore(payRun, stub, emp, template);
            payRun.Stubs.Add(stub);
        }

        return payRun;
    }

    public void RecalculatePayRun(PayRun payRun, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "recalculate pay runs", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        var template = _templateService?.GetTemplateById(payRun.JurisdictionId, payRun.TaxYear) ?? GetActiveTemplate();
        var employees = _hrContext.Employees?.GetAll().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var stub in payRun.Stubs)
        {
            employees.TryGetValue(stub.EmployeeId, out var emp);
            RecalculateStubCore(payRun, stub, emp, template);
        }
    }

    public void RecalculateStub(
        PayRun payRun,
        PayStub stub,
        Employee? employee = null,
        JurisdictionTemplate? template = null,
        Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "recalculate pay stubs", allowSuspended: false);
        RecalculateStubCore(payRun, stub, employee, template);
    }

    private void RecalculateStubCore(
        PayRun payRun,
        PayStub stub,
        Employee? employee = null,
        JurisdictionTemplate? template = null)
    {
        ArgumentNullException.ThrowIfNull(payRun);
        ArgumentNullException.ThrowIfNull(stub);

        var activeTemplate = template
                             ?? _templateService?.GetTemplateById(payRun.JurisdictionId, payRun.TaxYear)
                             ?? GetActiveTemplate();
        var emp = employee ?? _hrContext.Employees?.GetById(stub.EmployeeId);
        var periodsPerYear = PayFrequencyType.GetPeriodsPerYear(payRun.PayFrequency);

        // 1. Earnings
        if (string.Equals(stub.CompensationType, PayrollCompensationType.Salaried, StringComparison.OrdinalIgnoreCase) &&
            emp?.Payroll?.AnnualSalary > 0m)
        {
            stub.RegularEarnings = Round(emp.Payroll.AnnualSalary / periodsPerYear);
        }
        else
        {
            var rate = stub.HourlyRate > 0m ? stub.HourlyRate : (emp?.HourlyRate ?? 0m);
            stub.HourlyRate = rate;
            stub.RegularEarnings = Round(stub.RegularHours * rate);
        }

        stub.OvertimeEarnings = Round(stub.OvertimeHours * stub.HourlyRate * 1.5m);

        // 2. Vacation Rate & Accrual (4% default, or 6% after seniority threshold)
        decimal vacRate = emp?.Payroll?.VacationRate > 0m
            ? emp.Payroll.VacationRate
            : activeTemplate.PayrollRules.DefaultVacationRate;

        if (emp != null && emp.Payroll?.VacationRate == activeTemplate.PayrollRules.DefaultVacationRate)
        {
            var yearsEmployed = (payRun.PayDate.Year - emp.DateOfJoining.Year);
            if (yearsEmployed >= activeTemplate.PayrollRules.SeniorityYearsForEnhancedVacation &&
                activeTemplate.PayrollRules.SeniorVacationRate > vacRate)
            {
                vacRate = activeTemplate.PayrollRules.SeniorVacationRate;
            }
        }

        stub.VacationRate = vacRate;
        var baseForVac = stub.RegularEarnings + stub.OvertimeEarnings + stub.BonusOrCommission;
        if (emp?.Payroll?.VacationHandling == VacationPayHandling.PayOutEachPeriod)
        {
            stub.VacationPaidOut = Round(baseForVac * vacRate);
            stub.VacationAccruedDollars = 0m;
        }
        else
        {
            stub.VacationAccruedDollars = Round(baseForVac * vacRate);
        }

        var grossThisPeriod = stub.GrossPay;

        // 3. Prior YTD Totals for this employee in the same TaxYear
        var priorStubs = GetPriorStubsForEmployeeInYear(stub.EmployeeId, payRun.TaxYear, payRun.Id, payRun.PayDate);
        var priorYtdGross = priorStubs.Sum(s => s.GrossPay);

        // Preserve any manually overridden lines
        var existingContribOverrides = stub.StatutoryContributions
            .Where(c => c.IsManuallyOverridden)
            .ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var existingTaxOverrides = stub.IncomeTaxLines
            .Where(t => t.IsManuallyOverridden)
            .ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);

        var paramDefs = activeTemplate.PayrollRules?.EmployeeParameters ?? [];
        var contribRules = activeTemplate.PayrollRules?.Contributions ?? [];
        var taxTables = activeTemplate.PayrollRules?.IncomeTaxTables ?? [];

        stub.StatutoryContributions.Clear();
        var computedContribs = new Dictionary<string, PayStubContributionLine>(StringComparer.OrdinalIgnoreCase);
        var rulesByCode = contribRules
            .Where(r => !string.IsNullOrWhiteSpace(r.Code))
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        decimal deductibleFromGrossThisPeriod = 0m;
        decimal creditableContributionsThisPeriod = 0m;

        // 4. Statutory Social Contributions (Composable BaseMultiplier, Floor, Ceiling, Cap, Rate Overrides & Exemptions)
        foreach (var rule in contribRules)
        {
            var isExempt = emp?.Payroll?.IsExemptFromContribution(rule) == true;

            if (existingContribOverrides.TryGetValue(rule.Code, out var manualContrib))
            {
                stub.StatutoryContributions.Add(manualContrib);
                computedContribs[rule.Code] = manualContrib;
                deductibleFromGrossThisPeriod += rule.GetDeductibleEmployeeAmount(manualContrib.EmployeeAmount);
                creditableContributionsThisPeriod += rule.GetCreditableEmployeeAmount(manualContrib.EmployeeAmount);
                continue;
            }

            if (isExempt || grossThisPeriod <= 0m)
            {
                var zeroLine = new PayStubContributionLine
                {
                    Code = rule.Code,
                    Name = rule.Name,
                    Authority = rule.Authority,
                    SlipBoxCode = rule.SlipBoxCode,
                    InsurableEarningsThisPeriod = 0m,
                    EmployeeAmount = 0m,
                    EmployerAmount = 0m,
                    LiabilityGlCode = rule.LiabilityGlCode,
                    ExpenseGlCode = rule.ExpenseGlCode
                };
                stub.StatutoryContributions.Add(zeroLine);
                computedContribs[rule.Code] = zeroLine;
                continue;
            }

            decimal? empRateOverride = null;
            decimal? erRateOverride = null;
            if (!string.IsNullOrWhiteSpace(rule.RateOverrideParameterKey) && emp?.Payroll != null)
            {
                var overridePct = emp.Payroll.GetParameterDecimal(rule.RateOverrideParameterKey, paramDefs, 0m);
                if (overridePct > 0m)
                {
                    var overrideDecimal = overridePct / 100m;
                    if (rule.EmployeeRate <= 0m)
                        erRateOverride = overrideDecimal;
                    else
                        empRateOverride = overrideDecimal;
                }
            }

            var (insurablePeriod, empAmt, erAmt) = CalculateSingleContribution(
                rule,
                grossThisPeriod,
                priorYtdGross,
                periodsPerYear,
                priorStubs,
                empRateOverride,
                erRateOverride);

            var line = new PayStubContributionLine
            {
                Code = rule.Code,
                Name = rule.Name,
                Authority = rule.Authority,
                SlipBoxCode = rule.SlipBoxCode,
                InsurableEarningsThisPeriod = insurablePeriod,
                EmployeeAmount = empAmt,
                EmployerAmount = erAmt,
                LiabilityGlCode = rule.LiabilityGlCode,
                ExpenseGlCode = rule.ExpenseGlCode
            };
            stub.StatutoryContributions.Add(line);
            computedContribs[rule.Code] = line;

            deductibleFromGrossThisPeriod += rule.GetDeductibleEmployeeAmount(empAmt);
            creditableContributionsThisPeriod += rule.GetCreditableEmployeeAmount(empAmt);
        }

        // 5. Composable Income Tax Pipeline
        stub.IncomeTaxLines.Clear();
        for (int idx = 0; idx < taxTables.Count; idx++)
        {
            var taxTable = taxTables[idx];
            if (existingTaxOverrides.TryGetValue(taxTable.Code, out var manualTax))
            {
                stub.IncomeTaxLines.Add(manualTax);
                continue;
            }

            var (periodTaxable, withheld) = CalculatePeriodIncomeTax(
                taxTable,
                grossThisPeriod,
                deductibleFromGrossThisPeriod,
                creditableContributionsThisPeriod,
                computedContribs,
                rulesByCode,
                emp?.Payroll,
                paramDefs,
                periodsPerYear);

            stub.IncomeTaxLines.Add(new PayStubTaxLine
            {
                Code = taxTable.Code,
                Name = taxTable.Name,
                Authority = taxTable.Authority,
                SlipBoxCode = taxTable.SlipBoxCode,
                TaxableIncomeThisPeriod = periodTaxable,
                WithheldAmount = withheld,
                LiabilityGlCode = taxTable.LiabilityGlCode
            });
        }

        // 6. Custom Employee Deductions / Employer Benefits
        if (stub.CustomDeductions.Count == 0 && emp?.Payroll?.CustomDeductionsAndBenefits is { Count: > 0 } customItems)
        {
            foreach (var item in customItems)
            {
                var amt = item.IsPercentageOfGross
                    ? Round(grossThisPeriod * (item.Percentage / 100m))
                    : Round(item.AmountPerPay);

                if (amt > 0m)
                {
                    stub.CustomDeductions.Add(new PayStubCustomLine
                    {
                        Name = item.Name,
                        Amount = amt,
                        IsEmployerContribution = item.IsEmployerContribution,
                        GlAccountCode = item.GlAccountCode
                    });
                }
            }
        }

        // 7. YTD Rollup Snapshot
        stub.YtdGrossPay = Round(priorYtdGross + stub.GrossPay);
        stub.YtdNetPay = Round(priorStubs.Sum(s => s.NetPay) + stub.NetPay);
        stub.EndingVacationBankDollars = Round(
            (emp?.Payroll?.VacationBankBalanceDollars ?? 0m) + stub.VacationAccruedDollars - stub.VacationPaidOut);

        stub.YtdEmployeeContributions.Clear();
        stub.YtdEmployerContributions.Clear();
        foreach (var c in stub.StatutoryContributions)
        {
            var priorEmp = priorStubs.SelectMany(s => s.StatutoryContributions)
                .Where(x => string.Equals(x.Code, c.Code, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.EmployeeAmount);
            var priorEr = priorStubs.SelectMany(s => s.StatutoryContributions)
                .Where(x => string.Equals(x.Code, c.Code, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.EmployerAmount);

            stub.YtdEmployeeContributions[c.Code] = Round(priorEmp + c.EmployeeAmount);
            stub.YtdEmployerContributions[c.Code] = Round(priorEr + c.EmployerAmount);
        }

        if (stub.IncomeTaxLines.Count > 0)
        {
            var fedCode = stub.IncomeTaxLines[0].Code;
            var priorFed = priorStubs.SelectMany(s => s.IncomeTaxLines)
                .Where(x => string.Equals(x.Code, fedCode, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.WithheldAmount);
            stub.YtdFederalTax = Round(priorFed + stub.IncomeTaxLines[0].WithheldAmount);
        }

        if (stub.IncomeTaxLines.Count > 1)
        {
            var provCode = stub.IncomeTaxLines[1].Code;
            var priorProv = priorStubs.SelectMany(s => s.IncomeTaxLines)
                .Where(x => string.Equals(x.Code, provCode, StringComparison.OrdinalIgnoreCase))
                .Sum(x => x.WithheldAmount);
            stub.YtdRegionalTax = Round(priorProv + stub.IncomeTaxLines[1].WithheldAmount);
        }
    }

    public PayRun SavePayRun(PayRun payRun, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "save draft pay runs", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        if (string.IsNullOrWhiteSpace(payRun.RunNumber) && _hrContext.PayRuns != null)
        {
            payRun.RunNumber = _hrContext.PayRuns.GenerateRunNumber("PAY");
        }
        if (payRun.History.Count == 0)
        {
            DocumentHistoryHelper.RecordCreated(
                payRun,
                summary: $"Pay Run {payRun.RunNumber} created ({payRun.PeriodStart:yyyy-MM-dd} → {payRun.PeriodEnd:yyyy-MM-dd}, Net {payRun.TotalNetPay:C2})");
        }
        _hrContext.PayRuns?.Save(payRun);
        _ledgerService.SyncPayRun(payRun);
        return payRun;
    }

    public PayRun FinalizePayRun(PayRun payRun, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "finalize pay runs", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        if (string.IsNullOrWhiteSpace(payRun.RunNumber) && _hrContext.PayRuns != null)
        {
            payRun.RunNumber = _hrContext.PayRuns.GenerateRunNumber("PAY");
        }
        var oldStatus = payRun.Status;
        payRun.Status = PayRunStatus.Finalized;
        payRun.FinalizedAt = DateTime.UtcNow;

        DocumentHistoryHelper.RecordStatusChange(
            payRun,
            oldStatus,
            payRun.Status,
            summary: $"Pay run finalized and posted to General Ledger (Gross {payRun.TotalGrossPay:C2}, Net {payRun.TotalNetPay:C2})");

        // Update employee vacation banks
        if (_hrContext.Employees != null)
        {
            foreach (var stub in payRun.Stubs)
            {
                var emp = _hrContext.Employees.GetById(stub.EmployeeId);
                if (emp != null)
                {
                    emp.Payroll ??= new();
                    emp.Payroll.VacationBankBalanceDollars = stub.EndingVacationBankDollars;
                    _hrContext.Employees.Save(emp);
                }
            }
        }

        _hrContext.PayRuns?.Save(payRun);
        _ledgerService.SyncPayRun(payRun);
        return payRun;
    }

    public PayRun MarkPayRunPaid(PayRun payRun, DateTime datePaid, string bankGlCode = "1020", string? reference = null, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "mark pay runs paid", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        var oldStatus = payRun.Status;
        payRun.Status = PayRunStatus.Paid;
        payRun.DatePaid = datePaid;
        payRun.PaymentBankGlCode = bankGlCode;
        payRun.PaymentReference = reference;

        foreach (var stub in payRun.Stubs)
        {
            stub.IsPaid = true;
            stub.DatePaid ??= datePaid;
            if (!string.IsNullOrWhiteSpace(reference) && string.IsNullOrWhiteSpace(stub.PaymentReference))
            {
                stub.PaymentReference = reference;
            }
        }

        DocumentHistoryHelper.RecordPaymentAction(
            payRun,
            DocumentHistoryActions.PaymentRecorded,
            oldStatus,
            payRun.Status,
            $"Disbursed net payroll of {payRun.TotalNetPay:C2} from GL {bankGlCode} on {datePaid:yyyy-MM-dd}");

        _hrContext.PayRuns?.Save(payRun);
        _ledgerService.SyncPayRun(payRun);
        return payRun;
    }

    public PayRun SetPayStubsPaymentStatus(
        PayRun payRun,
        IEnumerable<string> stubIds,
        bool isPaid,
        DateTime? datePaid = null,
        string? reference = null,
        string bankGlCode = "1020",
        string? user = null,
        Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "update pay stub payment statuses", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        ArgumentNullException.ThrowIfNull(stubIds);

        if (string.Equals(payRun.Status, PayRunStatus.Draft, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payRun.Status, PayRunStatus.OpeningYtd, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(payRun.Status, PayRunStatus.Void, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Approve and finalize the pay run before tracking employee disbursements.");
        }

        var idSet = stubIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedStubs = payRun.Stubs.Where(s => idSet.Contains(s.Id)).ToList();
        if (matchedStubs.Count == 0)
        {
            return payRun;
        }

        var effectiveDate = datePaid?.Date ?? DateTime.Today;
        var trimmedRef = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        var oldStatus = payRun.Status;

        foreach (var stub in matchedStubs)
        {
            stub.IsPaid = isPaid;
            stub.DatePaid = isPaid ? effectiveDate : null;
            stub.PaymentReference = isPaid ? (trimmedRef ?? stub.PaymentReference) : null;
        }

        payRun.PaymentBankGlCode = string.IsNullOrWhiteSpace(bankGlCode) ? "1020" : bankGlCode;
        if (payRun.AreAllStubsPaid)
        {
            payRun.Status = PayRunStatus.Paid;
            payRun.DatePaid = matchedStubs.Max(s => s.DatePaid) ?? effectiveDate;
            if (!string.IsNullOrWhiteSpace(trimmedRef))
            {
                payRun.PaymentReference = trimmedRef;
            }
        }
        else if (string.Equals(payRun.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase))
        {
            payRun.Status = PayRunStatus.Finalized;
            payRun.DatePaid = null;
        }

        var names = matchedStubs.Count == 1
            ? matchedStubs[0].EmployeeName
            : $"{matchedStubs.Count} employees";
        var summary = isPaid
            ? $"Confirmed payment to {names} ({matchedStubs.Sum(s => s.NetPay):C2}) on {effectiveDate:yyyy-MM-dd}" + (trimmedRef != null ? $" [Ref: {trimmedRef}]" : "")
            : $"Marked payment as unpaid for {names}";

        DocumentHistoryHelper.RecordPaymentAction(
            payRun,
            isPaid ? DocumentHistoryActions.PaymentRecorded : DocumentHistoryActions.StatusChanged,
            oldStatus,
            payRun.Status,
            summary,
            user);

        _hrContext.PayRuns?.Save(payRun);
        _ledgerService.SyncPayRun(payRun);
        return payRun;
    }

    public void DeletePayRun(string payRunId, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "delete pay runs", allowSuspended: false);
        if (string.IsNullOrWhiteSpace(payRunId)) return;

        var existing = _hrContext.PayRuns?.GetById(payRunId);
        if (existing == null) return;

        // Guard against deleting a pay run whose posted ledger entries are already locked
        if (_accountingContext.JournalEntries != null)
        {
            string[] sourceTypes = [JournalSourceType.PayRun, JournalSourceType.PayRunPayment, JournalSourceType.DasRemittancePayment];
            foreach (var st in sourceTypes)
            {
                var je = _accountingContext.JournalEntries.GetBySource(st, payRunId);
                if (je != null && je.IsLocked)
                {
                    throw new InvalidOperationException("Cannot delete a pay run with locked journal entries (e.g. bank reconciled or DAS remitted/locked).");
                }
            }
        }

        _ledgerService.RemoveEntriesForSource(JournalSourceType.PayRun, payRunId);
        _ledgerService.RemoveEntriesForSource(JournalSourceType.PayRunPayment, payRunId);
        _ledgerService.RemoveEntriesForSource(JournalSourceType.DasRemittancePayment, payRunId);
        _hrContext.PayRuns?.Delete(payRunId);
    }

    public PayRun RecordDasRemittance(PayRun payRun, DateTime remittedAt, string bankGlCode = "1020", string? reference = null, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "record DAS remittances", allowSuspended: false);
        ArgumentNullException.ThrowIfNull(payRun);
        payRun.IsDasRemitted = true;
        payRun.DasRemittedAt = remittedAt;
        payRun.PaymentBankGlCode = bankGlCode;
        payRun.DasPaymentReference = reference;

        var totalDas = Math.Round(payRun.TotalEmployeeDeductions + payRun.TotalEmployerContributions, 2);
        DocumentHistoryHelper.RecordPaymentAction(
            payRun,
            DocumentHistoryActions.RemittanceRecorded,
            payRun.Status,
            payRun.Status,
            $"Remitted payroll source deductions (DAS) of {totalDas:C2} from GL {bankGlCode} on {remittedAt:yyyy-MM-dd}");

        _hrContext.PayRuns?.Save(payRun);
        _ledgerService.SyncPayRun(payRun);
        return payRun;
    }

    public List<DasAuthoritySummary> CalculateDasRemittanceSummary(DateTime periodStart, DateTime periodEnd, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "view DAS remittance summaries", allowSuspended: true);
        var s = periodStart.Date;
        var e = periodEnd.Date;

        var runs = _hrContext.PayRuns?.GetAll()
            .Where(r => (string.Equals(r.Status, PayRunStatus.Finalized, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(r.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase)) &&
                        r.PayDate.Date >= s && r.PayDate.Date <= e)
            .ToList() ?? [];

        var byAuthority = new Dictionary<string, DasAuthoritySummary>(StringComparer.OrdinalIgnoreCase);
        var grossTotal = runs.Sum(r => r.TotalGrossPay);

        foreach (var run in runs)
        {
            foreach (var stub in run.Stubs)
            {
                foreach (var c in stub.StatutoryContributions)
                {
                    if (c.EmployeeAmount == 0m && c.EmployerAmount == 0m) continue;
                    var auth = string.IsNullOrWhiteSpace(c.Authority) ? "Tax Authority" : c.Authority;
                    var group = GetOrAddAuthority(byAuthority, auth, grossTotal);
                    var line = GetOrAddDasLine(group, c.Code, c.Name, c.SlipBoxCode);
                    line.EmployeeWithheld = Round(line.EmployeeWithheld + c.EmployeeAmount);
                    line.EmployerContribution = Round(line.EmployerContribution + c.EmployerAmount);
                }

                foreach (var t in stub.IncomeTaxLines)
                {
                    if (t.WithheldAmount == 0m) continue;
                    var auth = string.IsNullOrWhiteSpace(t.Authority) ? "Tax Authority" : t.Authority;
                    var group = GetOrAddAuthority(byAuthority, auth, grossTotal);
                    var line = GetOrAddDasLine(group, t.Code, t.Name, t.SlipBoxCode);
                    line.EmployeeWithheld = Round(line.EmployeeWithheld + t.WithheldAmount);
                }
            }
        }

        return byAuthority.Values.OrderBy(a => a.AuthorityName).ToList();
    }

    public List<EmployeeYearEndSlipSummary> CalculateYearEndSlipsSummary(int taxYear, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "view year-end payroll slip summaries", allowSuspended: true);
        var runs = _hrContext.PayRuns?.GetByTaxYear(taxYear)
            .Where(r => string.Equals(r.Status, PayRunStatus.Finalized, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(r.Status, PayRunStatus.OpeningYtd, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        var summariesByEmp = new Dictionary<string, EmployeeYearEndSlipSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var run in runs)
        {
            foreach (var stub in run.Stubs)
            {
                if (!summariesByEmp.TryGetValue(stub.EmployeeId, out var sum))
                {
                    sum = new EmployeeYearEndSlipSummary
                    {
                        EmployeeId = stub.EmployeeId,
                        EmployeeName = stub.EmployeeName,
                        TaxYear = taxYear,
                        JurisdictionId = run.JurisdictionId
                    };
                    summariesByEmp[stub.EmployeeId] = sum;
                }

                sum.GrossEmploymentIncome = Round(sum.GrossEmploymentIncome + stub.GrossPay);
                sum.TotalNetPay = Round(sum.TotalNetPay + stub.NetPay);
                sum.TotalEmployerTaxes = Round(sum.TotalEmployerTaxes + stub.TotalEmployerContributions);
                sum.EndingVacationBankDollars = stub.EndingVacationBankDollars;

                foreach (var c in stub.StatutoryContributions)
                {
                    sum.ContributionsByCode.TryGetValue(c.Code, out var prevContrib);
                    sum.ContributionsByCode[c.Code] = Round(prevContrib + c.EmployeeAmount);

                    var code = c.Code.ToUpperInvariant();
                    if (code is "QPP" or "CPP" or "OASDI")
                    {
                        sum.QppOrCppEmployee = Round(sum.QppOrCppEmployee + c.EmployeeAmount);
                        sum.PensionableEarnings = Round(sum.PensionableEarnings + c.InsurableEarningsThisPeriod);
                    }
                    else if (code is "QPP2" or "CPP2")
                    {
                        sum.Qpp2OrCpp2Employee = Round(sum.Qpp2OrCpp2Employee + c.EmployeeAmount);
                        sum.PensionableEarnings = Round(sum.PensionableEarnings + c.InsurableEarningsThisPeriod);
                    }
                    else if (code.StartsWith("EI", StringComparison.OrdinalIgnoreCase))
                    {
                        sum.EiEmployee = Round(sum.EiEmployee + c.EmployeeAmount);
                        sum.EiInsurableEarnings = Round(sum.EiInsurableEarnings + c.InsurableEarningsThisPeriod);
                    }
                    else if (code == "QPIP")
                    {
                        sum.QpipEmployee = Round(sum.QpipEmployee + c.EmployeeAmount);
                        sum.QpipInsurableEarnings = Round(sum.QpipInsurableEarnings + c.InsurableEarningsThisPeriod);
                    }
                }

                foreach (var t in stub.IncomeTaxLines)
                {
                    sum.TaxesByCode.TryGetValue(t.Code, out var prevTax);
                    sum.TaxesByCode[t.Code] = Round(prevTax + t.WithheldAmount);
                }

                if (stub.IncomeTaxLines.Count > 0)
                {
                    sum.FederalIncomeTax = Round(sum.FederalIncomeTax + stub.IncomeTaxLines[0].WithheldAmount);
                }
                if (stub.IncomeTaxLines.Count > 1)
                {
                    sum.RegionalIncomeTax = Round(sum.RegionalIncomeTax + stub.IncomeTaxLines[1].WithheldAmount);
                }
            }
        }

        return summariesByEmp.Values.OrderBy(s => s.EmployeeName).ToList();
    }

    public int SyncJurisdictionEmployerContributionsToRepository(JurisdictionTemplate? template = null, Employee? actor = null)
    {
        if (actor != null)
        {
            if (actor.IsSystem)
            {
                throw new UnauthorizedAccessException("System actors cannot sync statutory employer contributions.");
            }
            DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "sync statutory employer contributions", allowSuspended: false);
            DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "sync statutory employer contributions", allowSuspended: false);
        }
        if (_accountingContext.EmployerContributions == null) return 0;

        var active = template ?? GetActiveTemplate();
        var statutoryItems = EmployerContribution.BuildFromJurisdictionTemplate(active);

        // Remove previous statutory items from repository before saving active template ones
        var existingStatutory = _accountingContext.EmployerContributions.GetAll()
            .Where(c => !string.IsNullOrWhiteSpace(c.StatutoryCode) || c.Id.StartsWith("stat-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var old in existingStatutory)
        {
            _accountingContext.EmployerContributions.Delete(old.Id);
        }

        foreach (var item in statutoryItems)
        {
            _accountingContext.EmployerContributions.Save(item);
        }

        return statutoryItems.Count;
    }

    private static (decimal InsurableThisPeriod, decimal EmployeeAmount, decimal EmployerAmount) CalculateSingleContribution(
        CappedContributionRule rule,
        decimal grossThisPeriod,
        decimal priorYtdGross,
        int periodsPerYear,
        IReadOnlyList<PayStub> priorStubs,
        decimal? empRateOverride = null,
        decimal? erRateOverride = null)
    {
        var multiplier = rule.BaseMultiplier > 0m ? rule.BaseMultiplier : 1.0m;
        var scaledPeriodGross = grossThisPeriod * multiplier;
        var scaledPriorYtdGross = priorYtdGross * multiplier;

        var floor = rule.AnnualFloorEarnings ?? 0m;
        var ceiling = rule.AnnualMaxInsurableEarnings;

        // Determine how much of this period's scaled gross falls between [floor, ceiling]
        decimal newCumulativeGross = scaledPriorYtdGross + scaledPeriodGross;
        decimal effectiveStart = Math.Max(scaledPriorYtdGross, floor);
        decimal effectiveEnd = ceiling.HasValue ? Math.Min(newCumulativeGross, ceiling.Value) : newCumulativeGross;
        decimal insurableThisPeriod = Math.Max(0m, effectiveEnd - effectiveStart);

        if (insurableThisPeriod <= 0m)
        {
            return (0m, 0m, 0m);
        }

        var empRate = empRateOverride ?? rule.EmployeeRate;
        var erRate = erRateOverride ?? rule.EmployerRate;

        var periodExemption = rule.AnnualExemption > 0m
            ? rule.AnnualExemption / periodsPerYear
            : 0m;

        var contributoryEarnings = Math.Max(0m, insurableThisPeriod - periodExemption);
        var rawEmp = Round(contributoryEarnings * empRate);
        var rawEr = Round(contributoryEarnings * erRate);

        // Enforce strict annual maximum dollar contribution ceiling across YTD
        if (rule.ApplyAnnualCap && ceiling.HasValue && ceiling.Value > floor)
        {
            var maxSubjectAnnual = Math.Max(0m, ceiling.Value - floor - rule.AnnualExemption);
            var maxAnnualEmp = Round(maxSubjectAnnual * empRate);
            var maxAnnualEr = Round(maxSubjectAnnual * erRate);

            var priorEmpPaid = priorStubs.SelectMany(s => s.StatutoryContributions)
                .Where(c => string.Equals(c.Code, rule.Code, StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.EmployeeAmount);
            var priorErPaid = priorStubs.SelectMany(s => s.StatutoryContributions)
                .Where(c => string.Equals(c.Code, rule.Code, StringComparison.OrdinalIgnoreCase))
                .Sum(c => c.EmployerAmount);

            if (empRate > 0m)
            {
                rawEmp = Math.Max(0m, Math.Min(rawEmp, Round(maxAnnualEmp - priorEmpPaid)));
            }
            if (erRate > 0m)
            {
                rawEr = Math.Max(0m, Math.Min(rawEr, Round(maxAnnualEr - priorErPaid)));
            }
        }

        return (Round(insurableThisPeriod), rawEmp, rawEr);
    }

    private static (decimal PeriodTaxableIncome, decimal WithheldAmount) CalculatePeriodIncomeTax(
        IncomeTaxJurisdictionTable table,
        decimal grossThisPeriod,
        decimal defaultDeductibleContribsThisPeriod,
        decimal defaultCreditableContribsThisPeriod,
        IReadOnlyDictionary<string, PayStubContributionLine> computedContribs,
        IReadOnlyDictionary<string, CappedContributionRule> rulesByCode,
        EmployeePayrollProfile? payroll,
        IReadOnlyList<EmployeeRuleParameter> paramDefs,
        int periodsPerYear)
    {
        if (grossThisPeriod <= 0m)
            return (0m, 0m);

        // Step 1: Compute scaled period taxable base and pre-bracket deductions
        var baseMultiplier = table.BaseMultiplier > 0m ? table.BaseMultiplier : 1.0m;
        var scaledPeriodGross = grossThisPeriod * baseMultiplier;
        var annualScaledGross = scaledPeriodGross * periodsPerYear;

        var deductibleContribs = table.SubtractContributionCodes is { Count: > 0 }
            ? table.SubtractContributionCodes.Sum(code =>
            {
                if (!computedContribs.TryGetValue(code, out var c)) return 0m;
                if (rulesByCode.TryGetValue(code, out var r))
                {
                    var ded = r.GetDeductibleEmployeeAmount(c.EmployeeAmount);
                    return ded > 0m ? ded : c.EmployeeAmount;
                }
                return c.EmployeeAmount;
            })
            : defaultDeductibleContribsThisPeriod;

        var periodTaxableBase = Math.Max(0m, scaledPeriodGross - deductibleContribs);
        if (periodTaxableBase <= 0m)
            return (0m, 0m);

        var method = string.IsNullOrWhiteSpace(table.Method)
            ? TaxComputationMethod.ProgressiveBrackets
            : table.Method;

        decimal standardDeduction = table.PreBracketStandardDeduction;
        List<TaxBracketRule> activeBrackets = table.Brackets ?? [];

        if (string.Equals(method, TaxComputationMethod.BracketSetByParameter, StringComparison.OrdinalIgnoreCase))
        {
            var selectedOption = payroll?.GetParameterRaw(table.BracketSetSelectorParameterKey, paramDefs) ?? string.Empty;
            var bracketSet = table.BracketSets?.FirstOrDefault(s =>
                string.Equals(s.OptionKey, selectedOption, StringComparison.OrdinalIgnoreCase))
                ?? table.BracketSets?.FirstOrDefault();

            if (bracketSet != null)
            {
                if (bracketSet.StandardDeduction > 0m)
                    standardDeduction = bracketSet.StandardDeduction;
                if (bracketSet.Brackets is { Count: > 0 })
                    activeBrackets = bracketSet.Brackets;
            }
        }

        decimal pctDeduction = 0m;
        if (table.PreBracketPercentageDeductionRate > 0m)
        {
            pctDeduction = annualScaledGross * table.PreBracketPercentageDeductionRate;
            if (table.PreBracketPercentageDeductionCap.HasValue && table.PreBracketPercentageDeductionCap.Value >= 0m)
            {
                pctDeduction = Math.Min(pctDeduction, table.PreBracketPercentageDeductionCap.Value);
            }
        }

        var extraPreBracketDeduction = !string.IsNullOrWhiteSpace(table.PreBracketDeductionParameterKey) && payroll != null
            ? Math.Max(0m, payroll.GetParameterDecimal(table.PreBracketDeductionParameterKey, paramDefs, 0m))
            : 0m;

        var annualTaxableIncome = Math.Max(0m, (periodTaxableBase * periodsPerYear) - standardDeduction - pctDeduction - extraPreBracketDeduction);
        var reportedPeriodTaxable = Round(annualTaxableIncome / periodsPerYear);

        // Step 2: Evaluate gross annual tax via configured method
        decimal annualTax = 0m;
        if (string.Equals(method, TaxComputationMethod.ParameterRateOrBracketFallback, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(table.RateOverrideParameterKey))
        {
            var overridePercent = payroll?.GetParameterDecimal(table.RateOverrideParameterKey, paramDefs, 0m) ?? 0m;
            if (overridePercent > 0m)
            {
                annualTax = annualTaxableIncome * (overridePercent / 100m);
            }
            else
            {
                annualTax = EvaluateProgressiveBrackets(activeBrackets, annualTaxableIncome);
            }
        }
        else if (string.Equals(method, TaxComputationMethod.FlatRate, StringComparison.OrdinalIgnoreCase))
        {
            annualTax = annualTaxableIncome * table.FlatRate;
        }
        else
        {
            annualTax = EvaluateProgressiveBrackets(activeBrackets, annualTaxableIncome);
        }

        // Step 3: Apply ordered post-calculation steps
        decimal extraFlatPerPeriod = 0m;
        foreach (var step in table.PostSteps ?? [])
        {
            if (string.Equals(step.StepType, TaxAdjustmentStepType.SubtractNonRefundableCredit, StringComparison.OrdinalIgnoreCase))
            {
                var paramAmount = !string.IsNullOrWhiteSpace(step.ParameterKey) && payroll != null
                    ? payroll.GetParameterDecimal(step.ParameterKey, paramDefs, step.DefaultAmount)
                    : step.DefaultAmount;

                // Apply high-income phase-out on the default basic personal amount when configured (e.g. CRA Federal BPA $16,452 -> $14,829)
                if (step.MinimumAmountAfterPhaseOut.HasValue &&
                    step.PhaseOutStartIncome.HasValue &&
                    step.PhaseOutEndIncome.HasValue &&
                    step.PhaseOutEndIncome.Value > step.PhaseOutStartIncome.Value &&
                    step.DefaultAmount > step.MinimumAmountAfterPhaseOut.Value &&
                    Math.Abs(paramAmount - step.DefaultAmount) < 0.01m &&
                    annualTaxableIncome > step.PhaseOutStartIncome.Value)
                {
                    var phaseOutRatio = Math.Clamp(
                        (annualTaxableIncome - step.PhaseOutStartIncome.Value) / (step.PhaseOutEndIncome.Value - step.PhaseOutStartIncome.Value),
                        0m,
                        1m);
                    paramAmount = step.DefaultAmount - ((step.DefaultAmount - step.MinimumAmountAfterPhaseOut.Value) * phaseOutRatio);
                }

                if (step.CapAtAnnualGrossIncome)
                {
                    paramAmount = Math.Min(paramAmount, Math.Max(0m, annualScaledGross));
                }

                decimal contribCreditPeriod = 0m;
                if (step.IncludedContributionCodes is { Count: > 0 })
                {
                    contribCreditPeriod = step.IncludedContributionCodes.Sum(code =>
                    {
                        if (!computedContribs.TryGetValue(code, out var c)) return 0m;
                        if (rulesByCode.TryGetValue(code, out var r))
                        {
                            var cred = r.GetCreditableEmployeeAmount(c.EmployeeAmount);
                            return cred > 0m ? cred : c.EmployeeAmount;
                        }
                        return c.EmployeeAmount;
                    });
                }
                else if (step.IncludeCreditableContributions)
                {
                    contribCreditPeriod = defaultCreditableContribsThisPeriod;
                }

                var annualCreditBase = Math.Max(0m, paramAmount) + (contribCreditPeriod * periodsPerYear);
                annualTax = Math.Max(0m, annualTax - (annualCreditBase * step.RateOrFactor));
            }
            else if (string.Equals(step.StepType, TaxAdjustmentStepType.MultiplyByFactor, StringComparison.OrdinalIgnoreCase))
            {
                annualTax = Math.Max(0m, annualTax * (1m + step.RateOrFactor));
            }
            else if (string.Equals(step.StepType, TaxAdjustmentStepType.AddFlatParameter, StringComparison.OrdinalIgnoreCase))
            {
                var extraVal = !string.IsNullOrWhiteSpace(step.ParameterKey) && payroll != null
                    ? payroll.GetParameterDecimal(step.ParameterKey, paramDefs, step.DefaultAmount)
                    : step.DefaultAmount;
                extraFlatPerPeriod += Math.Max(0m, extraVal);
            }
        }

        var periodTax = Math.Max(0m, Round(annualTax / periodsPerYear) + Round(extraFlatPerPeriod));
        return (reportedPeriodTaxable, periodTax);
    }

    private static decimal EvaluateProgressiveBrackets(IReadOnlyList<TaxBracketRule> brackets, decimal annualTaxableIncome)
    {
        if (annualTaxableIncome <= 0m || brackets == null || brackets.Count == 0)
            return 0m;

        decimal annualGrossTax = 0m;
        decimal previousLimit = 0m;

        foreach (var bracket in brackets)
        {
            if (annualTaxableIncome <= previousLimit)
                break;

            var upper = bracket.UpToAnnualIncome ?? annualTaxableIncome;
            var taxableInBracket = Math.Max(0m, Math.Min(annualTaxableIncome, upper) - previousLimit);
            annualGrossTax += taxableInBracket * bracket.Rate;

            if (!bracket.UpToAnnualIncome.HasValue)
                break;

            previousLimit = bracket.UpToAnnualIncome.Value;
        }

        return annualGrossTax;
    }

    private List<PayStub> GetPriorStubsForEmployeeInYear(string employeeId, int taxYear, string currentPayRunId, DateTime currentPayDate)
    {
        if (_hrContext.PayRuns == null || string.IsNullOrWhiteSpace(employeeId))
            return [];

        return _hrContext.PayRuns.GetByTaxYear(taxYear)
            .Where(r => r.Id != currentPayRunId &&
                        (string.Equals(r.Status, PayRunStatus.Finalized, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(r.Status, PayRunStatus.Paid, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(r.Status, PayRunStatus.OpeningYtd, StringComparison.OrdinalIgnoreCase)) &&
                        r.PayDate.Date <= currentPayDate.Date)
            .SelectMany(r => r.Stubs)
            .Where(s => string.Equals(s.EmployeeId, employeeId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private JurisdictionTemplate GetActiveTemplate() =>
        _templateService?.GetActiveTemplate()
        ?? FinancialCalculationEngine.ActiveTemplate
        ?? JurisdictionTemplateService.BuildFallbackQuebecTemplate();

    private static DasAuthoritySummary GetOrAddAuthority(
        Dictionary<string, DasAuthoritySummary> map,
        string authority,
        decimal grossPayroll)
    {
        if (!map.TryGetValue(authority, out var summary))
        {
            summary = new DasAuthoritySummary
            {
                AuthorityName = authority,
                GrossPayrollSubject = grossPayroll
            };
            map[authority] = summary;
        }
        return summary;
    }

    private static DasRemittanceLine GetOrAddDasLine(
        DasAuthoritySummary group,
        string code,
        string name,
        string slipBox)
    {
        var line = group.Lines.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (line == null)
        {
            line = new DasRemittanceLine
            {
                Code = code,
                Name = name,
                SlipOrFormBox = slipBox
            };
            group.Lines.Add(line);
        }
        return line;
    }

    private static decimal Round(decimal val) =>
        Math.Round(val, 2, MidpointRounding.AwayFromZero);
}
