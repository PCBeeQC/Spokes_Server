namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Xunit;

[Collection("JurisdictionStaticState")]
public class AccountingMigrationServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledger;
    private readonly IAccountingService _accounting;
    private readonly ITaxRemittanceService _taxService;
    private readonly IPayrollCalculationEngine _payroll;
    private readonly IAccountingMigrationService _migration;
    private static readonly Employee AdminActor = new() { Id = "admin-test", FirstName = "Admin", LastName = "Tester", Email = "admin@spokes.local", IsActive = true, IsAdmin = true };

    public AccountingMigrationServiceTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
        services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _ledger = _serviceProvider.GetRequiredService<ILedgerService>();
        _accounting = _serviceProvider.GetRequiredService<IAccountingService>();
        _taxService = _serviceProvider.GetRequiredService<ITaxRemittanceService>();
        _payroll = _serviceProvider.GetRequiredService<IPayrollCalculationEngine>();
        _migration = _serviceProvider.GetRequiredService<IAccountingMigrationService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void ParserAndMatcher_HandlesWindows1252SageImpAndFrenchAccents()
    {
        var existingSupplier = new Supplier
        {
            ContactInfo = new PartyContactInfo
            {
                BusinessName = "Acier Québec",
                ContactPersonEmail = "comptes@acierquebec.ca"
            }
        };
        _db.Suppliers.Save(existingSupplier);

        var emp = new Employee
        {
            FirstName = "Mats",
            LastName = "Dallaire",
            Email = "mats@spokes.local"
        };
        _db.Employees.Save(emp);

        var impText = """
                      <Version>
                      "12001", "1"
                      </Version>
                      <COA>
                      "1020", "Banque Opérations Québec", "Asset", "1001", "15000.00", "0.00"
                      "3600", "Bénéfices non répartis", "Equity", "3600", "0.00", "15000.00"
                      </COA>
                      <Vendor>
                      "Acier Québec Ltée Inc.", "Jean Tremblay", "100 Rue Équipement", "", "Montréal", "QC", "Canada", "H1A 1A1", "514-555-0100", "", "", "comptes@acierquebec.ca", "", "123456789RT0001"
                      </Vendor>
                      """;

        var win1252Bytes = Encoding.GetEncoding(1252).GetBytes(impText);
        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = new DateTime(2026, 6, 30),
            Files = [new MigrationUploadedFile { FileName = "sage_export.imp", Content = win1252Bytes }]
        }, AdminActor);

        Assert.Equal(AccountingMigrationSourceFormat.Sage50DesktopImp, preview.DetectedFormat);
        Assert.Equal(2, preview.GlMappings.Count);
        Assert.Contains(preview.GlMappings, g => g.SourceName.Contains("Québec") && g.TargetGlCode == "1020");

        var matchedVendor = Assert.Single(preview.Vendors);
        Assert.Equal(MigrationPartyAction.LinkExisting, matchedVendor.Action);
        Assert.Equal(existingSupplier.Id, matchedVendor.MatchedEntityId);
        Assert.Contains("Équipement", matchedVendor.ContactInfo.BusinessAddressStreet);

        var empMatch = PartyMatchingHelper.FindBestEmployeeMatch("Dallaire, Mats", null, [emp]);
        Assert.NotNull(empMatch);
        Assert.Equal(emp.Id, empMatch.MatchedEntity.Id);
    }

    [Fact]
    public void FreshCutover_ReconcilesAccount3900ToZero_AndExcludesOpeningDocsFromTaxAndMargin()
    {
        var tbCsv = """
                    Account Number,Account Name,Account Type,Debit,Credit
                    1020,Operating Bank,Asset,50000.00,0.00
                    1200,Accounts Receivable,Asset,12000.00,0.00
                    2100,Accounts Payable,Liability,0.00,4000.00
                    3600,Retained Earnings,Equity,0.00,58000.00
                    """;

        var arCsv = """
                    Invoice #,Customer,Date,Due Date,Open Balance
                    SAGE-101,Client Alpha Inc.,2026-06-15,2026-07-15,7000.00
                    SAGE-102,Client Beta Ltée,2026-06-20,2026-07-20,5000.00
                    """;

        var apCsv = """
                    Bill #,Vendor,Date,Due Date,Amount
                    BILL-901,Fournisseur Gamma,2026-06-22,2026-07-22,4000.00
                    """;

        var cutoverDate = new DateTime(2026, 6, 30);
        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = cutoverDate,
            Files =
            [
                new MigrationUploadedFile { FileName = "tb.csv", Content = Encoding.UTF8.GetBytes(tbCsv), ExplicitKind = MigrationFileKind.TrialBalance },
                new MigrationUploadedFile { FileName = "ar.csv", Content = Encoding.UTF8.GetBytes(arCsv), ExplicitKind = MigrationFileKind.OpenReceivables },
                new MigrationUploadedFile { FileName = "ap.csv", Content = Encoding.UTF8.GetBytes(apCsv), ExplicitKind = MigrationFileKind.OpenPayables }
            ]
        }, AdminActor);

        Assert.True(preview.CanExecute);
        Assert.Equal(0m, preview.ProjectedClearing3900Residual);

        var result = _migration.ExecuteMigration(preview, "user-1", "Tester", AdminActor);
        Assert.Equal(2, result.OpenInvoicesCreated);
        Assert.Equal(1, result.OpenBillsCreated);
        Assert.Equal(0m, result.Batch.ResidualClearingBalance3900);

        var bs = _ledger.CalculateBalanceSheet(cutoverDate);
        Assert.True(bs.IsBalanced);
        Assert.Equal(62000m, bs.TotalAssets);
        Assert.Equal(4000m, bs.TotalLiabilities);
        Assert.Equal(58000m, bs.TotalEquity);
        Assert.DoesNotContain(bs.EquityLines, e => e.AccountCode == "3900");

        // Opening invoices & bills must NOT double-count into sales tax remittance or project gross margin
        var taxSummary = _taxService.CalculateTaxPeriodSummary(new DateTime(2026, 6, 1), cutoverDate);
        Assert.Equal(0m, taxSummary.TotalSalesBeforeTax);
        Assert.Equal(0m, taxSummary.TotalPurchasesBeforeTax);

        var projectId = _db.Invoices.GetAll().First(i => i.IsOpeningBalance).ProjectId;
        Assert.Equal(0m, _accounting.CalculateProjectGrossMargin(projectId));
    }

    [Fact]
    public void ExistingWorkspaceDelta_PostsOnlyMissingNonSpokesBalances()
    {
        var cutoverDate = new DateTime(2026, 6, 30);

        // Existing invoice already recorded in Spokes ($1,000 + $50 GST + $99.75 QST = $1,149.75)
        var existingInv = new Invoice
        {
            Id = "spk-inv-1",
            InvoiceNumber = "INV-2026-010",
            Date = new DateTime(2026, 6, 10),
            DueDate = new DateTime(2026, 7, 10),
            Status = InvoiceStatus.Final,
            TaxRate = 0.14975m,
            TaxComponents =
            [
                new DocumentTaxLine { Code = "TPS", Name = "TPS", Rate = 0.05m, Amount = 50m, GlAccountCode = "2160" },
                new DocumentTaxLine { Code = "TVQ", Name = "TVQ", Rate = 0.09975m, Amount = 99.75m, GlAccountCode = "2170" }
            ],
            LineItems = [new InvoiceLine { Description = "Existing Work", Type = "General", Quantity = 1m, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(existingInv);
        _ledger.SyncInvoice(existingInv, "Spokes Client");

        // Full Sage TB includes BOTH the $1,149.75 AR / $1,000 Rev / $149.75 Tax AND $25,000 Bank + $25,000 Equity
        var sageTb = """
                     Account,Name,Debit,Credit
                     1020,Operating Bank,25000.00,0.00
                     1200,Accounts Receivable,1149.75,0.00
                     2160,TPS Collected,0.00,50.00
                     2170,TVQ Collected,0.00,99.75
                     3600,Retained Earnings,0.00,25000.00
                     4000,Service Revenue,0.00,1000.00
                     """;

        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.ExistingWorkspaceDelta,
            AsOfDate = cutoverDate,
            Files = [new MigrationUploadedFile { FileName = "sage_tb.csv", Content = Encoding.UTF8.GetBytes(sageTb) }]
        }, AdminActor);

        Assert.True(preview.CanExecute);
        Assert.Equal(25000m, preview.TotalDeltaDebits);
        Assert.Equal(25000m, preview.TotalDeltaCredits);

        var result = _migration.ExecuteMigration(preview, "user-1", "Tester", AdminActor);
        Assert.NotNull(result.OpeningJournalEntryNumber);

        var tb = _ledger.CalculateTrialBalance(new DateTime(2026, 1, 1), cutoverDate);
        Assert.Equal(25000m, tb.Rows.First(r => r.AccountCode == "1020").EndingBalance);
        Assert.Equal(1149.75m, tb.Rows.First(r => r.AccountCode == "1200").EndingBalance);
        Assert.Equal(1000m, tb.Rows.First(r => r.AccountCode == "4000").EndingBalance);
    }

    [Fact]
    public void MidYearPayrollYtd_SeedsCapsAndYearEndSlips_WithoutPostingToGl()
    {
        var emp = new Employee
        {
            FirstName = "Émilie",
            LastName = "Gagnon",
            Email = "emilie@spokes.local",
            HourlyRate = 50m,
            WeeklyHours = 40m
        };
        _db.Employees.Save(emp);

        // Annual EI insurable cap is 65,700 (EE max 860.67). Seed 65,000 gross & 851.50 EI YTD!
        var ytdCsv = """
                     Employee,Gross Pay,Federal Tax,Provincial Tax,QPP EE,EI EE,QPIP EE
                     "Gagnon, Émilie",65000.00,9200.00,10400.00,3936.00,851.50,321.10
                     """;

        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = new DateTime(2026, 6, 30),
            Files = [new MigrationUploadedFile { FileName = "payroll_ytd.csv", Content = Encoding.UTF8.GetBytes(ytdCsv), ExplicitKind = MigrationFileKind.PayrollYtd }]
        }, AdminActor);

        Assert.True(preview.CanExecute);
        var result = _migration.ExecuteMigration(preview, "user-1", "Tester", AdminActor);
        Assert.Equal(1, result.PayrollYtdStubsCreated);
        Assert.Empty(_db.JournalEntries.GetAll()); // Must NOT post OpeningYtd to GL

        // Next pay run with $2,000 gross should cap EI insurable earnings at remaining $700!
        var nextRun = _payroll.CreateDraftPayRun(
            new DateTime(2026, 7, 1),
            new DateTime(2026, 7, 14),
            new DateTime(2026, 7, 15),
            PayFrequencyType.BiWeekly,
            actor: AdminActor);

        var nextStub = Assert.Single(nextRun.Stubs);
        nextStub.RegularHours = 40m;
        _payroll.RecalculatePayRun(nextRun, AdminActor);

        var eiLine = Assert.Single(nextStub.StatutoryContributions, c => c.Code == "EI_QC");
        Assert.Equal(700m, eiLine.InsurableEarningsThisPeriod);
        Assert.Equal(9.17m, eiLine.EmployeeAmount);

        var slips = _payroll.CalculateYearEndSlipsSummary(2026, AdminActor);
        var empSlip = Assert.Single(slips);
        Assert.Equal(65000m, empSlip.GrossEmploymentIncome);
        Assert.Equal(9200m, empSlip.FederalIncomeTax);
    }

    [Fact]
    public void RollbackMigrationBatch_RemovesSeededData_AndBlocksWhenPaymentRecorded()
    {
        var tbCsv = """
                    Account Number,Account Name,Debit,Credit
                    1020,Bank,10000.00,0.00
                    1200,AR,2000.00,0.00
                    3600,Retained Earnings,0.00,12000.00
                    """;
        var arCsv = """
                    Invoice #,Customer,Date,Open Balance
                    INV-RB-1,Client Rollback,2026-05-01,2000.00
                    """;

        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = new DateTime(2026, 5, 31),
            Files =
            [
                new MigrationUploadedFile { FileName = "tb.csv", Content = Encoding.UTF8.GetBytes(tbCsv) },
                new MigrationUploadedFile { FileName = "ar.csv", Content = Encoding.UTF8.GetBytes(arCsv) }
            ]
        }, AdminActor);

        var res = _migration.ExecuteMigration(preview, "u1", "Admin", AdminActor);
        var importedInv = Assert.Single(_db.Invoices.GetAll());

        // Simulate recording a payment against imported invoice -> rollback must refuse cleanly
        importedInv.Payments.Add(new PaymentRecord { Amount = 500m, Date = new DateTime(2026, 6, 5) });
        _db.Invoices.Save(importedInv);

        Assert.Throws<InvalidOperationException>(() => _migration.RollbackMigrationBatch(res.Batch.Id, "Admin", AdminActor));

        // Reverse payment -> rollback succeeds cleanly
        importedInv.Payments.Clear();
        _db.Invoices.Save(importedInv);

        _migration.RollbackMigrationBatch(res.Batch.Id, "Admin", AdminActor);

        Assert.Empty(_db.Invoices.GetAll());
        Assert.Empty(_db.JournalEntries.GetAll());
        Assert.Empty(_db.Projects.GetAll());
        var updatedBatch = _db.MigrationBatches.GetById(res.Batch.Id);
        Assert.Equal(AccountingMigrationBatchStatus.RolledBack, updatedBatch?.Status);
    }

    [Fact]
    public void Sage50ExportRecordsBanner_ParsesAllSixFormats_UnifiesDirectory_AndRollsBackEmployees()
    {
        var accountsFile = """
            33002,3,Accounts
            1000,ACTIFS,H,GIFI:
            1060,Banque TD CAD,A,GIFI:1001
            1065,Compte USD,A,GIFI:1001
            1090,Total encaisse,T,GIFI:
            """;

        var vendorsFile = """
            33002,3,Vendors
            "Revenu Québec","Tax Dept","100 Rue","", "Québec","QC","","Canada","","418-555-0101","","","","","rev@gouv.qc.ca","","CAD"
            "Jean-Luc Picard","Jean-Luc Picard","200 Ave","","Montréal","QC","","Canada","","514-555-0102","","","","","jlp@spokes.local","","CAD"
            "McMaster-Carr","Sales Desk","300 Industrial","","Aurora","OH","44202","USA","","330-555-0199","","","","","sales@mcmaster.com","https://mcmaster.com","USD"
            """;

        var customersFile = """
            33002,3,Customers
            "Client Alpha Inc.","Sophie Martin","50 Blvd","","Laval","QC","H7N 1A1","Canada","","450-555-0155","","","","","sophie@alpha.com","https://alpha.com","CAD"
            """;

        var employeesFile = """
            33002,3,Employees
            "Jean-Luc Picard","200 Ave","","Montréal","QC","H2X 1Y4","514-555-0102","","123-456-782","01-01-1990","15-03-2022","QC","26"
            "Picard, Jean-Luc","200 Ave","","Montréal","QC","H2X 1Y4","514-555-0102","","123-456-782","01-01-1990","15-03-2022","QC","26"
            "Placeholder Bob","10 Rd","","Quebec","QC","G1R 1A1","418-555-0100","","000-000-000","05-05-1995","01-01-2025","QC","26"
            """;

        var projectsFile = """
            33002,3,Project
            "PRJ101 - Client Alpha Robot Cell","2026-02-01",""
            "Administration - Internal Overhead","2026-01-01",""
            """;

        var inventoryFile = """
            33002,3,Inventory
            "ITEM-01","Servo Motor","Stock","10","250.00"
            """;

        var files = new List<MigrationUploadedFile>
        {
            new() { FileName = "1_Accounts.txt", Content = Encoding.UTF8.GetBytes(accountsFile) },
            new() { FileName = "2_Vendors.txt", Content = Encoding.UTF8.GetBytes(vendorsFile) },
            new() { FileName = "3_Customers.txt", Content = Encoding.UTF8.GetBytes(customersFile) },
            new() { FileName = "4_Employees.txt", Content = Encoding.UTF8.GetBytes(employeesFile) },
            new() { FileName = "5_Projects.txt", Content = Encoding.UTF8.GetBytes(projectsFile) },
            new() { FileName = "6_Inventory.txt", Content = Encoding.UTF8.GetBytes(inventoryFile) }
        };

        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            Format = AccountingMigrationSourceFormat.AutoDetect,
            AsOfDate = new DateTime(2026, 6, 30),
            Files = files
        }, AdminActor);

        Assert.Equal(AccountingMigrationSourceFormat.Sage50ExportCsv, preview.DetectedFormat);

        // Header (H) and Total (T) rows skipped; two distinct Bank accounts kept as CreateAccount
        Assert.Equal(2, preview.GlMappings.Count);
        Assert.All(preview.GlMappings, g => Assert.Equal(MigrationGlAction.CreateAccount, g.Action));

        // Tax authority & matching Employee vendor flagged to Skip; real vendor (McMaster-Carr) + customer (Client Alpha Inc.) kept
        var revQc = preview.Vendors.Single(v => v.ContactInfo.BusinessName == "Revenu Québec");
        Assert.True(revQc.IsFlaggedEmployeeOrTaxAgency);
        Assert.Equal(MigrationPartyAction.Skip, revQc.Action);

        var empVendor = preview.Vendors.Single(v => v.ContactInfo.BusinessName == "Jean-Luc Picard");
        Assert.True(empVendor.IsFlaggedEmployeeOrTaxAgency);
        Assert.Equal(MigrationPartyAction.Skip, empVendor.Action);

        var mcmaster = preview.Vendors.Single(v => v.ContactInfo.BusinessName == "McMaster-Carr");
        Assert.Equal(MigrationPartyAction.CreateNew, mcmaster.Action);

        // Duplicate SIN employees merged into 1 record; placeholder 000000000 SIN cleared
        Assert.Equal(2, preview.PayrollYtds.Count);
        var jlp = preview.PayrollYtds.Single(e => e.SocialInsuranceNumber == "123456782");
        Assert.True(jlp.HasDuplicateSinWarning);
        var bob = preview.PayrollYtds.Single(e => e.SourceEmployeeName.Contains("Bob"));
        Assert.Equal(string.Empty, bob.SocialInsuranceNumber);

        // Inventory notice emitted
        Assert.NotEmpty(preview.InformationalNotices);

        // Projects: real project included, internal bucket skipped by default
        Assert.Equal(2, preview.Projects.Count);
        var realProj = preview.Projects.Single(p => p.ExtractedCode == "PRJ101");
        Assert.True(realProj.Include);
        var internalProj = preview.Projects.Single(p => p.IsInternalBucket);
        Assert.False(internalProj.Include);

        // Execute migration -> creates Employees, Address Book (both Vendors & Customers), Projects, GL accounts
        var res = _migration.ExecuteMigration(preview, "admin-1", "Admin", AdminActor);
        Assert.Equal(2, res.Batch.CreatedEmployeeIds.Count);
        Assert.Equal(2, res.Batch.CreatedSupplierIds.Count); // McMaster-Carr (Vendor) + Client Alpha Inc. (Customer)
        Assert.Single(res.Batch.CreatedProjectIds);

        Assert.Contains(_db.Suppliers.GetAll(), s => s.ContactInfo.BusinessName == "Client Alpha Inc.");
        Assert.Contains(_db.Suppliers.GetAll(), s => s.ContactInfo.BusinessName == "McMaster-Carr");

        // Rollback removes created Employees, Suppliers, Projects, and GL accounts cleanly
        _migration.RollbackMigrationBatch(res.Batch.Id, "Admin", AdminActor);
        Assert.Empty(_db.Employees.GetAll());
        Assert.Empty(_db.Suppliers.GetAll());
        Assert.Empty(_db.Projects.GetAll());
    }
}
