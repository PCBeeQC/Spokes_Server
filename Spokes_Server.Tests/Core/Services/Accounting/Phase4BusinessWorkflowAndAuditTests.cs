using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

[Collection("JurisdictionStaticState")]
public class Phase4BusinessWorkflowAndAuditTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly JurisdictionTemplateService _templateService;
    private readonly LedgerService _ledgerService;
    private readonly AccountingService _accountingService;
    private readonly PayrollCalculationEngine _payrollEngine;
    private readonly OvertimeService _overtimeService;
    private static readonly Employee AdminActor = new() { Id = "p4-admin", FirstName = "Phase4", LastName = "Admin", IsActive = true, IsAdmin = true };

    public Phase4BusinessWorkflowAndAuditTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _templateService = new JurisdictionTemplateService(_db.GlAccounts, _db.CompanyProfile, mockConfig.Object);
        _templateService.EnsureChartOfAccountsSeeded();

        _ledgerService = new LedgerService(_db.AccountingContext, _db.ProjectsContext, _db.HrContext, _templateService);
        _accountingService = new AccountingService(_db.AccountingContext, _db.ProjectsContext, _db.CoreContext, ledgerService: _ledgerService);
        _payrollEngine = new PayrollCalculationEngine(_db.HrContext, _db.AccountingContext, _ledgerService, _templateService);
        _overtimeService = new OvertimeService(_db.Employees, _db.Timesheets, _db.HourBankAdjustments, _db.CompanyProfile);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void OvertimeService_LeaveAndBankedTimeOff_KeepHourBankAccurateWithoutFakeProjects()
    {
        var profile = _db.CompanyProfile.Get();
        profile.HRConfig.EnableHourBank = true;
        profile.HRConfig.OvertimeCalculationMode = "Weekly";
        _db.CompanyProfile.Save(profile);

        var week1Monday = new DateTime(2025, 6, 2);
        var emp = new Employee
        {
            Id = "emp-leave-1",
            FirstName = "Marc",
            LastName = "Tremblay",
            WeeklyHours = 40m,
            HourlyRate = 45m,
            DateOfJoining = DateOnly.FromDateTime(week1Monday),
            EmploymentEndDate = DateOnly.FromDateTime(week1Monday.AddDays(20)),
            IsActive = true
        };
        _db.Employees.Save(emp);

        // Week 1: 32h Project Work + 8h Statutory Holiday = 40h -> 0h Hour Bank delta
        var tsWeek1 = new Timesheet
        {
            Id = "ts-w1",
            EmployeeId = emp.Id,
            Year = ISOWeek.GetYear(week1Monday),
            WeekNumber = ISOWeek.GetWeekOfYear(week1Monday),
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Date = week1Monday, ProjectId = "proj-1", Hours = 32m, EntryType = TimeEntryTypes.ProjectWork },
                new TimeEntry
                {
                    Date = week1Monday.AddDays(4),
                    ProjectId = TimeEntryTypes.ToProjectKey(TimeEntryTypes.StatutoryHoliday),
                    EntryType = TimeEntryTypes.StatutoryHoliday,
                    Hours = 8m
                }
            ]
        };
        _db.Timesheets.Save(tsWeek1);

        var week1Result = _overtimeService.CalculateWeeklyOvertime(emp.Id, tsWeek1.Year, tsWeek1.WeekNumber);
        Assert.Equal(40m, week1Result.WorkedHours);
        Assert.Equal(0m, week1Result.Difference);

        // Week 2: 45h Project Work -> +5h banked
        var week2Monday = week1Monday.AddDays(7);
        var tsWeek2 = new Timesheet
        {
            Id = "ts-w2",
            EmployeeId = emp.Id,
            Year = ISOWeek.GetYear(week2Monday),
            WeekNumber = ISOWeek.GetWeekOfYear(week2Monday),
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Date = week2Monday, ProjectId = "proj-1", Hours = 45m, EntryType = TimeEntryTypes.ProjectWork }
            ]
        };
        _db.Timesheets.Save(tsWeek2);

        var week2Result = _overtimeService.CalculateWeeklyOvertime(emp.Id, tsWeek2.Year, tsWeek2.WeekNumber);
        Assert.Equal(5m, week2Result.Difference);

        // Week 3: 36h Project Work + 4h BankedTimeOff -> deducts 4h from Hour Bank -> +1h net cumulative HourBank
        var week3Monday = week2Monday.AddDays(7);
        var tsWeek3 = new Timesheet
        {
            Id = "ts-w3",
            EmployeeId = emp.Id,
            Year = ISOWeek.GetYear(week3Monday),
            WeekNumber = ISOWeek.GetWeekOfYear(week3Monday),
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Date = week3Monday, ProjectId = "proj-1", Hours = 36m, EntryType = TimeEntryTypes.ProjectWork },
                new TimeEntry
                {
                    Date = week3Monday.AddDays(4),
                    ProjectId = TimeEntryTypes.ToProjectKey(TimeEntryTypes.BankedTimeOff),
                    EntryType = TimeEntryTypes.BankedTimeOff,
                    Hours = 4m
                }
            ]
        };
        _db.Timesheets.Save(tsWeek3);

        var totalBank = _overtimeService.CalculateHourBank(emp.Id);
        Assert.Equal(1m, totalBank.HourBank);

        var auditTrail = _overtimeService.GetHourBankAuditTrail(emp.Id);
        Assert.Contains(auditTrail, h => h.Description.Contains("Banked Time Off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComposableJurisdictionTemplate_CustomTemplateAndCompoundSalesTax_WorkGroundUp()
    {
        var customTemplate = new JurisdictionTemplate
        {
            JurisdictionId = "CUSTOM-TEST",
            Name = "Custom Test Jurisdiction",
            CountryCode = "CA",
            RegionCode = "TEST",
            TaxYear = 2026,
            DefaultCurrency = "CAD",
            SalesTaxes =
            [
                new SalesTaxRule
                {
                    Code = "BASE_TAX",
                    Name = "Base Tax",
                    Rate = 0.05m,
                    RegistrationNumberLabel = "Base Tax #",
                    IsCompound = false,
                    IsRecoverable = true,
                    CollectedLiabilityGlCode = "2160",
                    RecoverableAssetGlCode = "2165"
                },
                new SalesTaxRule
                {
                    Code = "COMP_TAX",
                    Name = "Compound Regional Tax",
                    Rate = 0.10m,
                    RegistrationNumberLabel = "Regional Tax #",
                    IsCompound = true,
                    IsRecoverable = true,
                    CollectedLiabilityGlCode = "2170",
                    RecoverableAssetGlCode = "2175"
                }
            ],
            PayrollRules = new JurisdictionPayrollRules
            {
                EmployeeParameters =
                [
                    new EmployeeRuleParameter
                    {
                        Key = "NATIONAL_CREDIT_BASE",
                        Label = "National Personal Exemption",
                        DataType = EmployeeRuleParameterType.Currency,
                        DefaultValue = "10000",
                        ScopeCode = "NATIONAL_TAX"
                    }
                ],
                Contributions =
                [
                    new CappedContributionRule
                    {
                        Code = "SOC1",
                        Name = "Social Contribution 1",
                        EmployeeRate = 0.05m,
                        EmployerRate = 0.05m,
                        AnnualExemption = 0m,
                        AnnualMaxInsurableEarnings = 50000m
                    }
                ],
                IncomeTaxTables =
                [
                    new IncomeTaxJurisdictionTable
                    {
                        Code = "NATIONAL_TAX",
                        Name = "National Income Tax",
                        Method = TaxComputationMethod.ProgressiveBrackets,
                        Brackets =
                        [
                            new TaxBracketRule { UpToAnnualIncome = null, Rate = 0.20m }
                        ],
                        PostSteps =
                        [
                            new TaxAdjustmentStep
                            {
                                StepType = TaxAdjustmentStepType.SubtractNonRefundableCredit,
                                ParameterKey = "NATIONAL_CREDIT_BASE",
                                RateOrFactor = 0.10m
                            }
                        ]
                    }
                ]
            },
            ChartOfAccounts =
            [
                new GlAccountTemplateItem { Code = "1020", Name = "Operating Bank", Type = GlAccountType.Asset },
                new GlAccountTemplateItem { Code = "2160", Name = "Base Tax Payable", Type = GlAccountType.Liability },
                new GlAccountTemplateItem { Code = "2170", Name = "Regional Tax Payable", Type = GlAccountType.Liability },
                new GlAccountTemplateItem { Code = "5100", Name = "Salaries & Wages", Type = GlAccountType.Expense }
            ]
        };

        var saved = _templateService.SaveTemplate(customTemplate, activateImmediately: true);
        Assert.Equal("CUSTOM-TEST", saved.JurisdictionId);
        Assert.Equal("CUSTOM-TEST", _templateService.GetActiveTemplate().JurisdictionId);

        // Compound sales tax: $100 subtotal -> Base Tax = $5.00, Compound Tax = 10% of ($100 + $5) = $10.50 -> Total Tax = $15.50
        var taxBreakdown = FinancialCalculationEngine.CalculateFromSubTotal(100m, isTaxExempt: false);
        Assert.Equal(100m, taxBreakdown.SubTotal);
        Assert.Equal(5.00m, taxBreakdown.TpsAmount);
        Assert.Equal(10.50m, taxBreakdown.TvqAmount);
        Assert.Equal(15.50m, taxBreakdown.TotalTax);
        Assert.Equal(115.50m, taxBreakdown.GrandTotal);

        // Employee with custom dictionary credit and exempt contribution code
        var emp = new Employee
        {
            Id = "emp-custom-jur",
            FirstName = "Sophie",
            LastName = "Gagnon",
            HourlyRate = 50m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly,
                ExemptContributionCodes = ["SOC1"],
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NATIONAL_CREDIT_BASE"] = "20000"
                }
            }
        };
        _db.Employees.Save(emp);

        var payRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 14),
            new DateTime(2026, 7, 15),
            PayFrequencyType.BiWeekly,
            actor: new Employee { Id = "test-admin", FirstName = "Test", LastName = "Admin", IsActive = true, IsAdmin = true });

        var stub = payRun.Stubs.Single(s => s.EmployeeId == emp.Id);
        stub.RegularHours = 80m;
        _payrollEngine.RecalculatePayRun(payRun, new Employee { Id = "test-admin", FirstName = "Test", LastName = "Admin", IsActive = true, IsAdmin = true });

        // Because SOC1 is in ExemptContributionCodes, both employee & employer SOC1 amounts must be 0
        var socLine = stub.StatutoryContributions.Single(c => c.Code == "SOC1");
        Assert.Equal(0m, socLine.EmployeeAmount);
        Assert.Equal(0m, socLine.EmployerAmount);
    }

    [Fact]
    public void DocumentHistoryAndUnifiedPaymentPipeline_LogsDiffsAndSyncsGeneralLedger()
    {
        var bill = new Bill
        {
            Id = "bill-audit-1",
            SupplierName = "Hydro-Québec",
            InvoiceNumber = "HQ-2026-01",
            Date = new DateTime(2026, 8, 1),
            DueDate = new DateTime(2026, 8, 31),
            SubTotal = 200m,
            TpsAmount = 10m,
            TvqAmount = 19.95m,
            TaxAmount = 29.95m,
            Amount = 229.95m,
            GlAccountCode = "5200",
            Status = BillStatus.Unpaid
        };

        // 1. Save new bill -> logs Created entry + posts JournalEntry
        _accountingService.SaveBill(bill, "Owner", AdminActor);
        Assert.Single(bill.History);
        Assert.Equal(DocumentHistoryActions.Created, bill.History[0].Action);

        // 2. Edit bill amount & GL code -> logs EditedInFinal field-level diff entry
        bill.SubTotal = 250m;
        bill.Amount = 279.95m;
        bill.GlAccountCode = "5300";
        _accountingService.SaveBill(bill, "Owner", AdminActor);

        var reloadedBill = _db.Bills.GetById(bill.Id)!;
        Assert.Equal(2, reloadedBill.History.Count);
        Assert.Equal(DocumentHistoryActions.EditedInFinal, reloadedBill.History[1].Action);
        Assert.Contains("Amount:", reloadedBill.History[1].Summary);
        Assert.Contains("GL Account: 5200 → 5300", reloadedBill.History[1].Summary);

        // 3. Expense Report unified payment -> logs PaymentRecorded + posts ExpensePayment JournalEntry
        var expReport = new ExpenseReport
        {
            Id = "exp-audit-1",
            ReportNumber = "EXP-2026-001",
            EmployeeId = "emp-1",
            EmployeeName = "Alex Roy",
            DateCreated = new DateTime(2026, 8, 5),
            DateSubmitted = new DateTime(2026, 8, 6),
            LineItems =
            [
                new ExpenseItem
                {
                    Date = new DateTime(2026, 8, 5),
                    Description = "Client site parking",
                    Amount = 45m,
                    IsTaxExempt = true,
                    GlAccountCode = "5600"
                }
            ]
        };
        _db.ExpenseReports.Save(expReport);
        _ledgerService.SyncExpenseReport(expReport);

        _accountingService.MarkExpenseReportPaid(expReport, new DateTime(2026, 8, 10), "1020", "Owner", AdminActor);
        var reloadedExp = _db.ExpenseReports.GetById(expReport.Id)!;
        Assert.Equal(ExpenseReportStatus.Paid, reloadedExp.Status);
        Assert.Single(reloadedExp.History);
        Assert.Equal(DocumentHistoryActions.PaymentRecorded, reloadedExp.History[0].Action);

        var expPaymentEntry = _db.JournalEntries.GetAll()
            .FirstOrDefault(j => j.SourceType == JournalSourceType.ExpensePayment && j.SourceId == expReport.Id);
        Assert.NotNull(expPaymentEntry);
        Assert.True(expPaymentEntry.IsBalanced);
    }
}
