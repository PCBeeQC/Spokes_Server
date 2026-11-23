namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.HR;
using Xunit;

[Collection("JurisdictionStaticState")]
public class LedgerAndExportServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledgerService;
    private readonly IAccountingService _accountingService;
    private readonly IAccountantExportService _exportService;
    private static readonly Employee AdminActor = new() { Id = "ledger-admin", FirstName = "Ledger", LastName = "Admin", IsActive = true, IsAdmin = true };

    public LedgerAndExportServiceTests()
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
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IAccountantExportService, AccountantExportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _ledgerService = _serviceProvider.GetRequiredService<ILedgerService>();
        _accountingService = _serviceProvider.GetRequiredService<IAccountingService>();
        _exportService = _serviceProvider.GetRequiredService<IAccountantExportService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void SyncInvoiceAndBill_PostsBalancedDoubleEntryJournalEntries_AndProducesBalancedStatements()
    {
        var invoice = new Invoice
        {
            Id = "inv-100",
            InvoiceNumber = "INV-2026-001",
            Date = new DateTime(2026, 3, 10),
            DueDate = new DateTime(2026, 4, 10),
            Status = InvoiceStatus.Final,
            TaxRate = 0.14975m,
            LineItems =
            [
                new InvoiceLine { Description = "Robotics Engineering", Quantity = 10m, UnitPrice = 100m }
            ],
            Payments =
            [
                new PaymentRecord { Id = "pay-1", Date = new DateTime(2026, 3, 20), Amount = 1149.75m, Method = "Wire" }
            ]
        };
        _db.Invoices.Save(invoice);
        _ledgerService.SyncInvoice(invoice, "Acme Robotics");

        var bill = new Bill
        {
            Id = "bill-200",
            SupplierName = "Hydro-Québec",
            Description = "Electricity March",
            InvoiceNumber = "HQ-2026-03",
            Date = new DateTime(2026, 3, 15),
            DueDate = new DateTime(2026, 4, 15),
            SubTotal = 200m,
            TpsAmount = 10m,
            TvqAmount = 19.95m,
            TaxAmount = 29.95m,
            Amount = 229.95m,
            Status = BillStatus.Unpaid,
            GlAccountCode = "5520"
        };
        _accountingService.SaveBill(bill, actor: AdminActor);

        var start = new DateTime(2026, 1, 1);
        var end = new DateTime(2026, 12, 31);

        var tb = _ledgerService.CalculateTrialBalance(start, end);
        Assert.True(tb.IsBalanced);
        Assert.Equal(tb.TotalEndingDebitColumn, tb.TotalEndingCreditColumn);

        var pl = _ledgerService.CalculateIncomeStatement(start, end);
        Assert.Equal(1000m, pl.TotalRevenue);
        Assert.Equal(200m, pl.TotalOperatingExpenses);
        Assert.Equal(800m, pl.NetIncome);

        var bs = _ledgerService.CalculateBalanceSheet(end);
        Assert.True(bs.IsBalanced);
    }

    [Fact]
    public void GenerateAccountantPackageZip_IncludesAll8Reports_AndImportsCpaAdjustingCsv()
    {
        var start = new DateTime(2026, 1, 1);
        var end = new DateTime(2026, 12, 31);

        var cpaCsv = """
                     Reference,Date,AccountCode,Description,Debit,Credit
                     CPA-ADJ-01,2026-12-31,5800,Year-End Amortization Expense,1500.00,0.00
                     CPA-ADJ-01,2026-12-31,1500,Accumulated Amortization Equipment,0.00,1500.00
                     """;

        var importRes = _exportService.ImportAdjustingEntriesCsv(cpaCsv, "External CPA", AdminActor);
        Assert.Equal(1, importRes.ImportedEntryCount);
        Assert.Equal(2, importRes.ImportedLineCount);
        Assert.Empty(importRes.Warnings);

        var zipBytes = _exportService.GenerateAccountantPackageZip(start, end, includeVouchers: false, AdminActor);
        Assert.NotEmpty(zipBytes);

        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("01_Trial_Balance.csv", entryNames);
        Assert.Contains("02_General_Ledger_Transactions.csv", entryNames);
        Assert.Contains("03_Income_Statement_PL.csv", entryNames);
        Assert.Contains("04_Balance_Sheet.csv", entryNames);
        Assert.Contains("05_Aged_Receivables_And_Payables.csv", entryNames);
        Assert.Contains("06_Sales_Tax_Summary.csv", entryNames);
        Assert.Contains("07_Payroll_And_DAS_Summary.csv", entryNames);
        Assert.Contains("08_Business_Overhead_And_Project_Allocation.csv", entryNames);
    }

    [Fact]
    public void SyncInvoice_PostsPartialAndSentStatusesToGl()
    {
        var partialInv = new Invoice
        {
            Id = "inv-partial",
            InvoiceNumber = "INV-2026-P1",
            Date = new DateTime(2026, 4, 1),
            Status = InvoiceStatus.Partial,
            LineItems = [new InvoiceLine { Description = "Phase 1", Quantity = 1m, UnitPrice = 500m }]
        };
        var sentInv = new Invoice
        {
            Id = "inv-sent",
            InvoiceNumber = "INV-2026-S1",
            Date = new DateTime(2026, 4, 2),
            Status = InvoiceStatus.Sent,
            LineItems = [new InvoiceLine { Description = "Phase 2", Quantity = 1m, UnitPrice = 300m }]
        };

        _ledgerService.SyncInvoice(partialInv, "Client A");
        _ledgerService.SyncInvoice(sentInv, "Client B");

        Assert.NotNull(_db.JournalEntries.GetBySource(JournalSourceType.Invoice, partialInv.Id));
        Assert.NotNull(_db.JournalEntries.GetBySource(JournalSourceType.Invoice, sentInv.Id));
    }

    [Fact]
    public void CalculateBalanceSheet_RollsPriorFiscalYearNetIncomeIntoRetainedEarnings()
    {
        // Configure fiscal year starting April 1
        var profile = _db.CompanyProfile.Get();
        profile.Accounting.FiscalYearStartMonth = 4;
        _db.CompanyProfile.Save(profile);

        // Prior fiscal year (FY ending March 31, 2026): Revenue $10,000 Cr, Bank $10,000 Dr
        var priorEntry = new JournalEntry
        {
            Date = new DateTime(2026, 2, 15),
            SourceType = JournalSourceType.Manual,
            Description = "Prior FY Revenue",
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", Debit = 10000m },
                new JournalLine { GlAccountCode = "4000", Credit = 10000m }
            ]
        };
        _ledgerService.SaveManualJournalEntry(priorEntry);

        // Current fiscal year (FY 2026-2027, starting 2026-04-01): Expense $2,500 Dr, Bank $2,500 Cr
        var currentEntry = new JournalEntry
        {
            Date = new DateTime(2026, 5, 10),
            SourceType = JournalSourceType.Manual,
            Description = "Current FY Overhead",
            Lines =
            [
                new JournalLine { GlAccountCode = "5600", Debit = 2500m },
                new JournalLine { GlAccountCode = "1020", Credit = 2500m }
            ]
        };
        _ledgerService.SaveManualJournalEntry(currentEntry);

        var bs = _ledgerService.CalculateBalanceSheet(new DateTime(2026, 6, 30));
        Assert.True(bs.IsBalanced);
        Assert.Equal(7500m, bs.TotalAssets);
        Assert.Equal(-2500m, bs.CurrentYearNetIncome);
        Assert.Contains(bs.EquityLines, l => l.AccountCode == "3600" && l.Amount == 10000m);
    }
}
