namespace Spokes_Server.Tests.Core.Services.HR;

using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

[Collection("JurisdictionStaticState")]
public class PayrollCalculationEngineTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IPayrollCalculationEngine _payrollEngine;
    private static readonly Employee AdminActor = new() { Id = "payroll-admin", FirstName = "Payroll", LastName = "Admin", IsActive = true, IsAdmin = true };

    public PayrollCalculationEngineTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _payrollEngine = _serviceProvider.GetRequiredService<IPayrollCalculationEngine>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void CreateFinalizeAndPayRun_ComputesQuebecDasDeductions_PostsBalancedGl_AndProducesT4Releve1Summary()
    {
        var emp = new Employee
        {
            Id = "emp-qc-1",
            FirstName = "Marc",
            LastName = "Tremblay",
            WeeklyHours = 40.0m,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 78000m, // $3,000.00 per bi-weekly period (26 periods)
                PayFrequency = PayFrequencyType.BiWeekly,
                VacationRate = 0.04m,
                VacationHandling = VacationPayHandling.AccrueInBank
            }
        };
        _db.Employees.Save(emp);

        var periodStart = new DateTime(2026, 2, 1);
        var periodEnd = new DateTime(2026, 2, 14);
        var payDate = new DateTime(2026, 2, 19);

        var run = _payrollEngine.CreateDraftPayRun(periodStart, periodEnd, payDate, PayFrequencyType.BiWeekly, actor: AdminActor);
        Assert.Single(run.Stubs);

        var stub = run.Stubs[0];
        Assert.Equal(3000.00m, stub.RegularEarnings);
        Assert.Equal(3000.00m, stub.GrossPay);
        Assert.Equal(120.00m, stub.VacationAccruedDollars); // 4% of $3,000 accrued in bank
        Assert.True(stub.TotalIncomeTaxWithheld > 0m);
        Assert.Contains(stub.StatutoryContributions, c => c.Code == "QPP" && c.EmployeeAmount > 0m && c.EmployerAmount > 0m);
        Assert.Contains(stub.StatutoryContributions, c => c.Code.StartsWith("EI", StringComparison.OrdinalIgnoreCase) && c.EmployeeAmount > 0m && c.EmployerAmount > 0m);
        Assert.Contains(stub.StatutoryContributions, c => c.Code == "QPIP" && c.EmployeeAmount > 0m && c.EmployerAmount > 0m);
        Assert.Contains(stub.StatutoryContributions, c => c.Code == "FSS" && c.EmployerAmount > 0m);
        Assert.Contains(stub.StatutoryContributions, c => c.Code == "CNESST" && c.EmployerAmount > 0m);
        Assert.True(stub.NetPay > 0m && stub.NetPay < stub.GrossPay);

        var finalized = _payrollEngine.FinalizePayRun(run, AdminActor);
        Assert.Equal(PayRunStatus.Finalized, finalized.Status);

        var journalEntry = _db.JournalEntries!.GetBySource(JournalSourceType.PayRun, finalized.Id);
        Assert.NotNull(journalEntry);
        Assert.True(journalEntry!.IsBalanced);

        var paid = _payrollEngine.MarkPayRunPaid(finalized, payDate, "1020", "PAY-WIRE-01", AdminActor);
        Assert.Equal(PayRunStatus.Paid, paid.Status);

        var paymentEntry = _db.JournalEntries.GetBySource(JournalSourceType.PayRunPayment, paid.Id);
        Assert.NotNull(paymentEntry);
        Assert.True(paymentEntry!.IsBalanced);

        var dasSummaries = _payrollEngine.CalculateDasRemittanceSummary(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), AdminActor);
        Assert.NotEmpty(dasSummaries);
        Assert.All(dasSummaries, s => Assert.True(s.TotalRemittanceDue > 0m));

        var annualSlips = _payrollEngine.CalculateYearEndSlipsSummary(2026, AdminActor);
        Assert.Single(annualSlips);
        Assert.Equal(3000.00m, annualSlips[0].GrossEmploymentIncome);
        Assert.Equal(stub.NetPay, annualSlips[0].TotalNetPay);
    }

    [Fact]
    public void SyncJurisdictionEmployerContributionsToRepository_PopulatesOperatingCostsEmployerContributions()
    {
        var syncedCount = _payrollEngine.SyncJurisdictionEmployerContributionsToRepository();
        Assert.True(syncedCount >= 5);

        var allContribs = _db.EmployerContributions.GetAll().ToList();
        Assert.Contains(allContribs, c => c.Name.Contains("QPP", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("RRQ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allContribs, c => c.Name.Contains("QPIP", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("RQAP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allContribs, c => c.Name.Contains("CNESST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreviewDeleteAndPerStubPaymentTracking_TracksEmployeePaymentsOneByOneAndInBulk()
    {
        _db.Employees.Save(new Employee
        {
            Id = "emp-pay-1",
            FirstName = "Alice",
            LastName = "Bouchard",
            WeeklyHours = 40.0m,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 52000m,
                PayFrequency = PayFrequencyType.BiWeekly
            }
        });
        _db.Employees.Save(new Employee
        {
            Id = "emp-pay-2",
            FirstName = "Bob",
            LastName = "Lavoie",
            WeeklyHours = 40.0m,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 65000m,
                PayFrequency = PayFrequencyType.BiWeekly
            }
        });

        // 1. Preview without saving does not persist to DB or assign a RunNumber
        var previewRun = _payrollEngine.CreateDraftPayRun(
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 14),
            new DateTime(2026, 3, 19),
            PayFrequencyType.BiWeekly,
            assignRunNumber: false,
            actor: AdminActor);
        Assert.Equal(string.Empty, previewRun.RunNumber);
        Assert.Null(_db.PayRuns!.GetById(previewRun.Id));
        Assert.Equal(2, previewRun.Stubs.Count);

        // 2. Saving Draft assigns RunNumber and persists
        _payrollEngine.SavePayRun(previewRun, AdminActor);
        Assert.False(string.IsNullOrWhiteSpace(previewRun.RunNumber));
        Assert.NotNull(_db.PayRuns.GetById(previewRun.Id));

        // 3. Finalize and track per-employee payout 1-by-1
        var finalized = _payrollEngine.FinalizePayRun(previewRun, AdminActor);
        Assert.Equal(PayRunStatus.Finalized, finalized.Status);

        var stub1 = finalized.Stubs[0];
        var stub2 = finalized.Stubs[1];

        _payrollEngine.SetPayStubsPaymentStatus(finalized, [stub1.Id], isPaid: true, new DateTime(2026, 3, 19), "EFT-101", actor: AdminActor);
        Assert.True(stub1.IsPaid);
        Assert.False(stub2.IsPaid);
        Assert.Equal(PayRunStatus.Finalized, finalized.Status);

        var partialJe = _db.JournalEntries!.GetBySource(JournalSourceType.PayRunPayment, finalized.Id);
        Assert.NotNull(partialJe);
        Assert.Equal(stub1.NetPay, partialJe!.TotalDebits);

        // 4. Mark second employee paid -> auto-transitions PayRun to Paid and syncs full net payment
        _payrollEngine.SetPayStubsPaymentStatus(finalized, [stub2.Id], isPaid: true, new DateTime(2026, 3, 20), "EFT-102", actor: AdminActor);
        Assert.True(finalized.AreAllStubsPaid);
        Assert.Equal(PayRunStatus.Paid, finalized.Status);

        var fullJe = _db.JournalEntries.GetBySource(JournalSourceType.PayRunPayment, finalized.Id);
        Assert.NotNull(fullJe);
        Assert.Equal(finalized.TotalNetPay, fullJe!.TotalDebits);

        // 5. Deleting pay run removes unlocked GL entries and the run record
        _payrollEngine.DeletePayRun(finalized.Id, AdminActor);
        Assert.Null(_db.PayRuns.GetById(finalized.Id));
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.PayRun, finalized.Id));
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.PayRunPayment, finalized.Id));
    }
}
