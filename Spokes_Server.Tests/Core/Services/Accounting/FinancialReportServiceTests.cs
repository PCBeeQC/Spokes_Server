namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

public class FinancialReportServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledger;
    private readonly IFinancialReportService _reports;
    private static readonly Employee AdminActor = new() { Id = "fin-admin", FirstName = "Fin", LastName = "Admin", IsActive = true, IsAdmin = true };

    public FinancialReportServiceTests()
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
        services.AddScoped<IFinancialReportService, FinancialReportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _db.Initialize();
        _serviceProvider.GetRequiredService<IJurisdictionTemplateService>().EnsureChartOfAccountsSeeded();
        _ledger = _serviceProvider.GetRequiredService<ILedgerService>();
        _reports = _serviceProvider.GetRequiredService<IFinancialReportService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void CreateDraft_BuildsBalancedStatementsAndReconciledCashFlow_AndPersistsAcrossStatuses()
    {
        // Owner Equity Investment ($50,000 Dr 1020 Bank / Cr 3000 Share Capital)
        _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 1, 5),
            SourceType = JournalSourceType.Manual,
            Description = "Initial Share Capital",
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", Debit = 50000m },
                new JournalLine { GlAccountCode = "3000", Credit = 50000m }
            ]
        });

        // Cash Revenue ($10,000 Dr 1020 Bank / Cr 4000 Revenue)
        _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 2, 15),
            SourceType = JournalSourceType.Manual,
            Description = "Customer Milestone",
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", Debit = 10000m },
                new JournalLine { GlAccountCode = "4000", Credit = 10000m }
            ]
        });

        // Operating Expense ($2,500 Dr 5600 Overhead / Cr 2000 AP)
        _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 2, 20),
            SourceType = JournalSourceType.Manual,
            Description = "Cloud Hosting",
            Lines =
            [
                new JournalLine { GlAccountCode = "5600", Debit = 2500m },
                new JournalLine { GlAccountCode = "2000", Credit = 2500m }
            ]
        });

        var options = new FinancialReportOptions
        {
            IncludeComparativePriorPeriod = true,
            IncludeCashFlowStatement = true,
            IncludeFinancialRatios = true
        };

        var draft = _reports.CreateDraft(
            "Q1 2026 Investor Package",
            "Board Update",
            FinancialReportType.Quarterly,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 3, 31),
            options,
            "Strong Q1 start.",
            "No off-balance-sheet obligations.",
            "Founder / CEO",
            "u-1",
            "Founder",
            actor: AdminActor);

        Assert.NotNull(draft);
        Assert.Equal(FinancialReportStatus.Draft, draft.Status);
        Assert.Equal(1, draft.Version);
        Assert.Equal(10000m, draft.Snapshot.IncomeStatement.TotalRevenue);
        Assert.Equal(7500m, draft.Snapshot.IncomeStatement.NetIncome);
        Assert.True(draft.Snapshot.IsBalanceSheetBalanced);
        Assert.Equal(60000m, draft.Snapshot.CashFlow.ClosingCashBalance);
        Assert.Equal(0m, draft.Snapshot.CashFlow.VarianceFromBalanceSheetCash);
        Assert.True(draft.Snapshot.Ratios.GrossMarginPercent >= 99m);

        var finalized = _reports.ChangeStatus(draft.Id, FinancialReportStatus.Final, "u-1", "Founder", AdminActor);
        Assert.Equal(FinancialReportStatus.Final, finalized.Status);
        Assert.NotNull(finalized.FinalizedAt);

        var reopened = _reports.ChangeStatus(draft.Id, FinancialReportStatus.Draft, "u-1", "Founder", AdminActor);
        Assert.Equal(2, reopened.Version);
        Assert.Null(reopened.FinalizedAt);

        var reloaded = _db.FinancialReports.GetById(draft.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded!.Version);
    }
}
