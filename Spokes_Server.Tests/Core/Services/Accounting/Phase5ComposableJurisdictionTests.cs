using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

[Collection("JurisdictionStaticState")]
public class Phase5ComposableJurisdictionTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly JurisdictionTemplateService _templateService;
    private readonly LedgerService _ledgerService;
    private readonly PayrollCalculationEngine _payrollEngine;
    private static readonly Employee AdminActor = new() { Id = "test-admin", FirstName = "Test", LastName = "Admin", IsActive = true, IsAdmin = true };

    public Phase5ComposableJurisdictionTests()
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
        _payrollEngine = new PayrollCalculationEngine(_db.HrContext, _db.AccountingContext, _ledgerService, _templateService);
    }

    public override void Dispose()
    {
        FinancialCalculationEngine.ActiveTemplate = null;
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private static string FindMarketplaceTemplatesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Spokes_Marketplace", "accounting-templates");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Spokes_Marketplace/accounting-templates directory from test base directory.");
    }

    private JurisdictionTemplate LoadAndSaveMarketplaceTemplate(string fileName, bool activateImmediately = true)
    {
        var filePath = Path.Combine(FindMarketplaceTemplatesDir(), fileName);
        var json = File.ReadAllText(filePath);
        var (isValid, error, parsed) = _templateService.ValidateAndParseTemplate(json);
        Assert.True(isValid, $"Expected {fileName} to parse cleanly, got error: {error}");
        Assert.NotNull(parsed);
        return _templateService.SaveTemplate(parsed!, activateImmediately);
    }

    [Fact]
    public void GlAccountSubType_CustomBankAccountIncluded_NonPaymentAssetsExcluded()
    {
        // Ensure a non-payment asset account exists too
        _db.GlAccounts.Save(new GlAccount
        {
            Code = "1500",
            Name = "Office Equipment",
            Type = GlAccountType.Asset,
            SubType = GlAccountSubType.FixedAsset,
            IsActive = true
        });

        // Add custom secondary bank account under Asset / Bank
        _db.GlAccounts.Save(new GlAccount
        {
            Code = "1040",
            Name = "Savings Reserve Account",
            Type = GlAccountType.Asset,
            SubType = GlAccountSubType.Bank,
            IsActive = true
        });

        var bankAndCard = _db.GlAccounts.GetAll()
            .Where(a => a.IsActive && GlAccountSubType.IsCashOrCardPaymentAccount(a.SubType))
            .OrderBy(a => a.Code)
            .ToList();
        var codes = bankAndCard.Select(a => a.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("1020", codes);
        Assert.Contains("1030", codes);
        Assert.Contains("1040", codes);
        Assert.Contains("2120", codes);

        Assert.DoesNotContain("1200", codes); // Accounts Receivable
        Assert.DoesNotContain("1500", codes); // Fixed Asset

        Assert.Contains(GlAccountSubType.Bank, GlAccountSubType.GetForAccountType(GlAccountType.Asset));
        Assert.Contains(GlAccountSubType.CreditCard, GlAccountSubType.GetForAccountType(GlAccountType.Liability));
        Assert.True(GlAccountSubType.IsCashOrCardPaymentAccount(GlAccountSubType.Bank));
        Assert.True(GlAccountSubType.IsCashOrCardPaymentAccount(GlAccountSubType.CreditCard));
        Assert.False(GlAccountSubType.IsCashOrCardPaymentAccount(GlAccountSubType.AccountsReceivable));
    }

    [Fact]
    public void MarketplaceTemplates_AllParseWithValidComposableRules()
    {
        var files = new[]
        {
            "ca-qc-2026.spokes-jurisdiction.json",
            "ca-on-2026.spokes-jurisdiction.json",
            "us-general-2026.spokes-jurisdiction.json",
            "fr-pcg-2026.spokes-jurisdiction.json"
        };

        var dir = FindMarketplaceTemplatesDir();
        foreach (var file in files)
        {
            var json = File.ReadAllText(Path.Combine(dir, file));
            var (isValid, error, template) = _templateService.ValidateAndParseTemplate(json);
            Assert.True(isValid, $"{file} failed validation: {error}");
            Assert.NotNull(template);
            Assert.NotEmpty(template!.ChartOfAccounts);
            Assert.NotEmpty(template.PayrollRules.EmployeeParameters);
            Assert.NotEmpty(template.PayrollRules.IncomeTaxTables);
        }
    }

    [Fact]
    public void QuebecTemplate_ComposablePipeline_AppliesContributionsCreditsAndAbatement()
    {
        LoadAndSaveMarketplaceTemplate("ca-qc-2026.spokes-jurisdiction.json", activateImmediately: true);

        var defaultEmp = new Employee
        {
            Id = "emp-qc-default",
            FirstName = "Marc",
            LastName = "Tremblay",
            HourlyRate = 31.25m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly
            }
        };

        var higherCreditEmp = new Employee
        {
            Id = "emp-qc-high-td1",
            FirstName = "Julie",
            LastName = "Bouchard",
            HourlyRate = 31.25m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly,
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TD1_FEDERAL_AMOUNT"] = "30000"
                }
            }
        };

        _db.Employees.Save(defaultEmp);
        _db.Employees.Save(higherCreditEmp);

        var payRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 14),
            new DateTime(2026, 6, 15),
            PayFrequencyType.BiWeekly,
            actor: AdminActor);

        var stubDefault = payRun.Stubs.Single(s => s.EmployeeId == defaultEmp.Id);
        var stubHighCredit = payRun.Stubs.Single(s => s.EmployeeId == higherCreditEmp.Id);
        stubDefault.RegularHours = 80m; // $2,500 bi-weekly = $65,000/yr
        stubHighCredit.RegularHours = 80m;
        _payrollEngine.RecalculatePayRun(payRun, AdminActor);

        // Verify Québec statutory contributions
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "QPP" && c.EmployeeAmount > 0m);
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "QPIP" && c.EmployeeAmount > 0m);
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "EI_QC" && c.EmployeeAmount > 0m);
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "FSS" && c.EmployerAmount > 0m);
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "CNESST" && c.EmployerAmount > 0m);
        Assert.Contains(stubDefault.StatutoryContributions, c => c.Code == "CNT" && c.EmployerAmount > 0m);

        var fedDefault = stubDefault.IncomeTaxLines.Single(t => t.Code == "FED_CA_QC");
        var provDefault = stubDefault.IncomeTaxLines.Single(t => t.Code == "PROV_QC");
        var fedHighCredit = stubHighCredit.IncomeTaxLines.Single(t => t.Code == "FED_CA_QC");

        // Exact 2026 CRA T4127 & Revenu Québec TP-1015.F-V verification for $2,500 bi-weekly ($65,000/yr):
        // QPP = Round((2500 - 3500/26) * 0.063, 2) = $149.02 (1.00/6.30 = $23.6540 deductible; 5.30/6.30 = $125.3660 creditable)
        // QPIP = Round(2500 * 0.0043, 2) = $10.75 emp / $15.05 er
        // EI_QC = Round(2500 * 0.013, 2) = $32.50 emp / $45.50 er
        // CNESST = Round(2500 * 0.0154, 2) = $38.50 er; FSS = $41.25 er; CNT = $1.50 er
        Assert.Equal(149.02m, stubDefault.StatutoryContributions.Single(c => c.Code == "QPP").EmployeeAmount);
        Assert.Equal(10.75m, stubDefault.StatutoryContributions.Single(c => c.Code == "QPIP").EmployeeAmount);
        Assert.Equal(15.05m, stubDefault.StatutoryContributions.Single(c => c.Code == "QPIP").EmployerAmount);
        Assert.Equal(32.50m, stubDefault.StatutoryContributions.Single(c => c.Code == "EI_QC").EmployeeAmount);
        Assert.Equal(45.50m, stubDefault.StatutoryContributions.Single(c => c.Code == "EI_QC").EmployerAmount);
        Assert.Equal(38.50m, stubDefault.StatutoryContributions.Single(c => c.Code == "CNESST").EmployerAmount);

        // Federal ($64,385.00 taxable): bracket tax = 58523*0.14 + 5862*0.205 = $9,394.93
        // Credits = 0.14 * (16452 BPA + 1501 CEA + 26*(125.36603 + 10.75 + 32.50)) = $3,127.20 -> Basic Fed = $6,267.73
        // After 16.5% Québec abatement: 6267.73 * 0.835 / 26 = $201.29
        Assert.Equal(201.29m, fedDefault.WithheldAmount);

        // Québec ($65,000 - $615.00 QPP1A - $1,450.00 Deduction for Workers = $62,935.00 taxable):
        // Bracket tax = 54345*0.14 + 8590*0.19 = $9,240.40; Credit = 18952*0.14 = $2,653.28 (no double-counted QPP/EI/QPIP credit)
        // Period QC tax = (9240.40 - 2653.28) / 26 = $253.35
        Assert.Equal(253.35m, provDefault.WithheldAmount);

        // Higher TD1_FEDERAL_AMOUNT non-refundable credit reduces Federal tax
        Assert.True(fedHighCredit.WithheldAmount < fedDefault.WithheldAmount);
    }

    [Fact]
    public void Quebec2026Template_MatchesFallback_EnforcesBpaPhaseOutQpp2AndMileageTiers()
    {
        var jsonPath = Path.Combine(FindMarketplaceTemplatesDir(), "ca-qc-2026.spokes-jurisdiction.json");
        var (_, error, fileTemplate) = _templateService.ValidateAndParseTemplate(File.ReadAllText(jsonPath));
        Assert.Null(error);
        Assert.NotNull(fileTemplate);

        var fallback = JurisdictionTemplateService.BuildFallbackQuebecTemplate();
        Assert.Equal(
            _templateService.ExportTemplateJson(fileTemplate),
            _templateService.ExportTemplateJson(fallback));

        // 2026 CRA / Revenu Québec tax-free mileage tiers ($0.73 first 5,000 km; $0.67 thereafter)
        Assert.Equal(0.73m, fileTemplate!.MileageRules.PrimaryRatePerKm);
        Assert.Equal(5000m, fileTemplate.MileageRules.TierThresholdKm);
        Assert.Equal(0.67m, fileTemplate.MileageRules.SecondaryRatePerKm);

        _templateService.SaveTemplate(fileTemplate, activateImmediately: true);

        // Executive earning $260,000/yr ($10,000 bi-weekly) crosses QPP ($74,600), QPP2 ($85,000), and BPA phase-out ($258,482)
        var exec = new Employee
        {
            Id = "emp-qc-exec",
            FirstName = "Claire",
            LastName = "Gagnon",
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 260000m,
                PayFrequency = PayFrequencyType.BiWeekly
            }
        };
        _db.Employees.Save(exec);

        for (int p = 0; p < 10; p++)
        {
            var start = new DateTime(2026, 1, 1).AddDays(p * 14);
            var run = _payrollEngine.CreateDraftPayRun(start, start.AddDays(13), start.AddDays(14), PayFrequencyType.BiWeekly, actor: AdminActor);
            _payrollEngine.FinalizePayRun(run, AdminActor);
        }

        // After 10 bi-weekly periods ($100,000 gross YTD):
        // QPP maxed at ($74,600 - $3,500) * 6.30% = $4,479.30
        // QPP2 maxed at ($85,000 - $74,600) * 4.00% = $416.00
        // EI_QC maxed at $68,900 * 1.30% = $895.70
        // T4 Box 26 / RL-1 Box G PensionableEarnings maxed at YAMPE = $85,000.00
        var slips = _payrollEngine.CalculateYearEndSlipsSummary(2026, AdminActor);
        var execSlip = slips.Single(s => s.EmployeeId == exec.Id);
        Assert.Equal(4479.30m, execSlip.QppOrCppEmployee);
        Assert.Equal(416.00m, execSlip.Qpp2OrCpp2Employee);
        Assert.Equal(895.70m, execSlip.EiEmployee);
        Assert.Equal(85000.00m, execSlip.PensionableEarnings);

        // Period 11 ($100,000 -> $110,000 YTD): QPP, QPP2, EI_QC are $0, and QPIP caps on the final $3,000 up to $103,000 ($12.90 -> $442.90 YTD max)
        var p11Start = new DateTime(2026, 1, 1).AddDays(10 * 14);
        var p11Run = _payrollEngine.CreateDraftPayRun(p11Start, p11Start.AddDays(13), p11Start.AddDays(14), PayFrequencyType.BiWeekly, actor: AdminActor);
        var p11Stub = p11Run.Stubs.Single(s => s.EmployeeId == exec.Id);
        Assert.Equal(0m, p11Stub.StatutoryContributions.Single(c => c.Code == "QPP").EmployeeAmount);
        Assert.Equal(0m, p11Stub.StatutoryContributions.Single(c => c.Code == "QPP2").EmployeeAmount);
        Assert.Equal(0m, p11Stub.StatutoryContributions.Single(c => c.Code == "EI_QC").EmployeeAmount);
        Assert.Equal(12.90m, p11Stub.StatutoryContributions.Single(c => c.Code == "QPIP").EmployeeAmount);
        _payrollEngine.FinalizePayRun(p11Run, AdminActor);

        var slipsAfterP11 = _payrollEngine.CalculateYearEndSlipsSummary(2026, AdminActor).Single(s => s.EmployeeId == exec.Id);
        Assert.Equal(442.90m, slipsAfterP11.QpipEmployee);
        Assert.Equal(103000.00m, slipsAfterP11.QpipInsurableEarnings);

        // Period 12 ($110,000 -> $120,000 YTD): all employee contributions capped at $0; Federal BPA phased down to $14,829 minimum
        var p12Start = new DateTime(2026, 1, 1).AddDays(11 * 14);
        var p12Run = _payrollEngine.CreateDraftPayRun(p12Start, p12Start.AddDays(13), p12Start.AddDays(14), PayFrequencyType.BiWeekly, actor: AdminActor);
        var p12Stub = p12Run.Stubs.Single(s => s.EmployeeId == exec.Id);
        Assert.Equal(0m, p12Stub.StatutoryContributions.Single(c => c.Code == "QPIP").EmployeeAmount);

        // At $260,000/yr (> $258,482), Federal BPA phases down to $14,829 (+ $1,501 CEA = $16,330 credit base @ 14%)
        var fedP12 = p12Stub.IncomeTaxLines.Single(t => t.Code == "FED_CA_QC");
        var expectedBracketTax = (58523m * 0.14m)
                               + ((117045m - 58523m) * 0.205m)
                               + ((181440m - 117045m) * 0.26m)
                               + ((258482m - 181440m) * 0.29m)
                               + ((260000m - 258482m) * 0.33m);
        var expectedBasicFed = expectedBracketTax - ((14829m + 1501m) * 0.14m);
        var expectedAbatedPeriodFed = Math.Round((expectedBasicFed * (1m - 0.165m)) / 26m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedAbatedPeriodFed, fedP12.WithheldAmount);
    }

    [Fact]
    public void UsTemplate_W4FilingStatus_SwitchesBracketSetsAndExtraWithholding()
    {
        LoadAndSaveMarketplaceTemplate("us-general-2026.spokes-jurisdiction.json", activateImmediately: true);

        var singleEmp = new Employee
        {
            Id = "emp-us-single",
            FirstName = "Alex",
            LastName = "Miller",
            HourlyRate = 37.50m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly,
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["W4_FILING_STATUS"] = "SINGLE"
                }
            }
        };

        var marriedEmp = new Employee
        {
            Id = "emp-us-married",
            FirstName = "Jordan",
            LastName = "Miller",
            HourlyRate = 37.50m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly,
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["W4_FILING_STATUS"] = "MARRIED_JOINT"
                }
            }
        };

        var marriedExtraEmp = new Employee
        {
            Id = "emp-us-married-extra",
            FirstName = "Taylor",
            LastName = "Miller",
            HourlyRate = 37.50m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Hourly,
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["W4_FILING_STATUS"] = "MARRIED_JOINT",
                    ["W4_EXTRA_WITHHOLDING"] = "75"
                }
            }
        };

        _db.Employees.Save(singleEmp);
        _db.Employees.Save(marriedEmp);
        _db.Employees.Save(marriedExtraEmp);

        var payRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 14),
            new DateTime(2026, 6, 15),
            PayFrequencyType.BiWeekly,
            actor: AdminActor);

        foreach (var stub in payRun.Stubs)
        {
            stub.RegularHours = 80m; // $3,000 bi-weekly = $78,000/yr
        }
        _payrollEngine.RecalculatePayRun(payRun, AdminActor);

        var singleStub = payRun.Stubs.Single(s => s.EmployeeId == singleEmp.Id);
        var marriedStub = payRun.Stubs.Single(s => s.EmployeeId == marriedEmp.Id);
        var marriedExtraStub = payRun.Stubs.Single(s => s.EmployeeId == marriedExtraEmp.Id);

        // Social Security (6.2% of $3,000 = $186.00) and Medicare (1.45% of $3,000 = $43.50)
        Assert.Equal(186.00m, singleStub.StatutoryContributions.Single(c => c.Code == "OASDI").EmployeeAmount);
        Assert.Equal(43.50m, singleStub.StatutoryContributions.Single(c => c.Code == "MEDICARE").EmployeeAmount);

        var fedSingle = singleStub.IncomeTaxLines.Single(t => t.Code == "FED_US").WithheldAmount;
        var fedMarried = marriedStub.IncomeTaxLines.Single(t => t.Code == "FED_US").WithheldAmount;
        var fedMarriedExtra = marriedExtraStub.IncomeTaxLines.Single(t => t.Code == "FED_US").WithheldAmount;

        // SINGLE ($15,000 std deduction) withholds more federal tax than MARRIED_JOINT ($30,000 std deduction)
        Assert.True(fedSingle > fedMarried && fedMarried > 0m);
        // W4_EXTRA_WITHHOLDING adds exact $75.00 per period
        Assert.Equal(fedMarried + 75.00m, fedMarriedExtra);
    }

    [Fact]
    public void FrancePcgTemplate_CsgBaseMultiplierAndPasRateOverrideVsFallback()
    {
        LoadAndSaveMarketplaceTemplate("fr-pcg-2026.spokes-jurisdiction.json", activateImmediately: true);

        var neutralEmp = new Employee
        {
            Id = "emp-fr-neutral",
            FirstName = "Camille",
            LastName = "Dupont",
            HourlyRate = 25m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 48000m, // €4,000/month
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FR_PAS_RATE"] = "0"
                }
            }
        };

        var customPasEmp = new Employee
        {
            Id = "emp-fr-custom-pas",
            FirstName = "Lucas",
            LastName = "Bernard",
            HourlyRate = 25m,
            WeeklyHours = 40m,
            IsActive = true,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 48000m, // €4,000/month
                ParameterValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["FR_PAS_RATE"] = "12.5",
                    ["FR_EXTRA_WITHHOLDING"] = "50"
                }
            }
        };

        _db.Employees.Save(neutralEmp);
        _db.Employees.Save(customPasEmp);

        var payRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 30),
            new DateTime(2026, 6, 30),
            PayFrequencyType.Monthly,
            actor: AdminActor);

        _payrollEngine.RecalculatePayRun(payRun, AdminActor);

        var neutralStub = payRun.Stubs.Single(s => s.EmployeeId == neutralEmp.Id);
        var customStub = payRun.Stubs.Single(s => s.EmployeeId == customPasEmp.Id);

        // CSG déductible = 6.8% on 98.25% of 4000 = 267.24
        var csgDed = neutralStub.StatutoryContributions.Single(c => c.Code == "CSG_DED");
        Assert.Equal(267.24m, csgDed.EmployeeAmount);

        // CSG non déductible + CRDS = 2.9% on 98.25% of 4000 = 113.97
        var csgNd = neutralStub.StatutoryContributions.Single(c => c.Code == "CSG_CRDS_ND");
        Assert.Equal(113.97m, csgNd.EmployeeAmount);

        // URSSAF_SOC = 12.3% of 4000 = 492.00; Taxable net imposable = 4000 - 492 - 267.24 = 3240.76
        var pasNeutral = neutralStub.IncomeTaxLines.Single(t => t.Code == "PAS_FR");
        var pasCustom = customStub.IncomeTaxLines.Single(t => t.Code == "PAS_FR");

        Assert.Equal(3240.76m, pasNeutral.TaxableIncomeThisPeriod);
        Assert.True(pasNeutral.WithheldAmount > 0m);

        // Custom personalized DGFiP PAS rate of 12.5% + €50 extra withholding = Round(3240.76 * 0.125, 2) + 50 = 405.10 + 50 = 455.10
        Assert.Equal(455.10m, pasCustom.WithheldAmount);
    }

    [Fact]
    public void LegacyRemovedPropertiesInJson_DeserializeSafelyWithoutCrashing()
    {
        const string legacyTemplateJson = """
        {
          "SchemaVersion": "1.0",
          "JurisdictionId": "LEGACY-TEST",
          "Name": "Legacy JSON Compatibility Check",
          "TaxYear": 2026,
          "DefaultCurrency": "CAD",
          "PayrollRules": {
            "Contributions": [
              {
                "Code": "EI_LEGACY",
                "Name": "EI",
                "IsEmploymentInsurance": true,
                "EmployeeRate": 0.015,
                "EmployerRate": 0.021
              }
            ],
            "IncomeTaxTables": [
              {
                "Code": "FED_LEGACY",
                "Name": "Federal",
                "FormOrCreditLabel": "TD1",
                "DefaultBasicPersonalAmount": 16129,
                "PersonalCreditRate": 0.15,
                "AbatementRate": 0.165,
                "Brackets": [ { "UpToAnnualIncome": null, "Rate": 0.15 } ]
              }
            ]
          },
          "ChartOfAccounts": [
            { "Code": "1020", "Name": "Bank", "Type": "Asset", "SubType": "Bank", "SystemRole": "OperatingBank" }
          ]
        }
        """;

        const string legacyProfileJson = """
        {
          "CompensationType": "Salaried",
          "AnnualSalary": 60000,
          "FederalPersonalTaxCreditAmount": 16129,
          "RegionalPersonalTaxCreditAmount": 18571,
          "AdditionalTaxWithholdingPerPay": 25,
          "IsExemptFromEi": true,
          "ExemptContributionCodes": ["EI_LEGACY"],
          "PersonalTaxCreditsByTableCode": { "FED_LEGACY": 16129 }
        }
        """;

        var (isValid, error, template) = _templateService.ValidateAndParseTemplate(legacyTemplateJson);
        Assert.True(isValid, error);
        Assert.NotNull(template);
        Assert.NotNull(template!.PayrollRules.EmployeeParameters);
        Assert.NotNull(template.PayrollRules.IncomeTaxTables[0].PostSteps);
        Assert.NotNull(template.PayrollRules.IncomeTaxTables[0].BracketSets);

        var profile = JsonSerializer.Deserialize<EmployeePayrollProfile>(legacyProfileJson);
        Assert.NotNull(profile);
        Assert.NotNull(profile!.ParameterValues);
        Assert.Contains("EI_LEGACY", profile.ExemptContributionCodes);

        _templateService.SaveTemplate(template, activateImmediately: true);
        var emp = new Employee
        {
            Id = "emp-legacy-json",
            FirstName = "Legacy",
            LastName = "User",
            IsActive = true,
            Payroll = profile
        };
        _db.Employees.Save(emp);

        var payRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 14),
            new DateTime(2026, 6, 15),
            PayFrequencyType.BiWeekly,
            actor: AdminActor);
        _payrollEngine.RecalculatePayRun(payRun, AdminActor);

        var stub = payRun.Stubs.Single(s => s.EmployeeId == emp.Id);
        Assert.Equal(0m, stub.StatutoryContributions.Single(c => c.Code == "EI_LEGACY").EmployeeAmount);
    }
}
