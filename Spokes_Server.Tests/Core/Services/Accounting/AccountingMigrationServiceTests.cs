namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Helpers;
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
            FirstName = "Alex",
            LastName = "Tremblay",
            Email = "alex@spokes.local"
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

        var empMatch = PartyMatchingHelper.FindBestEmployeeMatch("Tremblay, Alex", null, [emp]);
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

    [Fact]
    public void Sage50ReportCentreXlsxPack_ImportsTrialBalanceGroupedAgedArApAndPayrollYtd()
    {
        var existingEmp = new Employee
        {
            FirstName = "Alex",
            LastName = "Tremblay",
            Email = "alex.tremblay@example.test",
            IsActive = true
        };
        _db.Employees.Save(existingEmp);

        // 1. Sage 50 French Balance de vérification (.xlsx with multi-line title banner & blank account-code header)
        var tbXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Balance de vérification au 2026-10-31"],
            ["", "Description", "Débits", "Crédits"],
            ["1020", "Compte bancaire courant", 85000.00m, 0m],
            ["1200", "Comptes clients", 15000.00m, 0m],
            ["2100", "Comptes fournisseurs", 0m, 6500.00m],
            ["3600", "Bénéfices non répartis", 0m, 93500.00m],
            ["Total", "", 100000.00m, 100000.00m]);

        // 2. Sage 50 French Chronologique clients - Détail (.xlsx with grouped customer rows + Excel serial dates)
        var arXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Chronologique clients - Détail au 2026-10-31"],
            ["Source", "Date", "Date d'échéance", "Total", "Courant", "31 à 60"],
            ["Client Alpha Inc."],
            ["INV-2610-001", new DateTime(2026, 10, 5), new DateTime(2026, 11, 4), 10000.00m, 10000.00m, 0m],
            ["Total pour Client Alpha Inc.", "", "", 10000.00m, 10000.00m, 0m],
            ["Client Beta Ltée"],
            ["INV-2609-019", new DateTime(2026, 9, 20), new DateTime(2026, 10, 20), 5000.00m, 0m, 5000.00m],
            ["Total pour Client Beta Ltée", "", "", 5000.00m, 0m, 5000.00m],
            ["Total général", "", "", 15000.00m, 10000.00m, 5000.00m]);

        // 3. Sage 50 French Chronologique fournisseurs - Détail (.xlsx with grouped vendor rows)
        var apXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Chronologique fournisseurs - Détail au 2026-10-31"],
            ["Source", "Date", "Échéance", "Total", "Courant"],
            ["Fournisseur Gamma"],
            ["FAC-8812", new DateTime(2026, 10, 12), new DateTime(2026, 11, 11), 6500.00m, 6500.00m],
            ["Total pour Fournisseur Gamma", "", "", 6500.00m, 6500.00m]);

        // 4. Sage 50 French Sommaire des employés (.xlsx with Quebec deductions)
        var payXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Sommaire des employés du 2026-01-01 au 2026-10-31"],
            ["Employé", "Revenu brut", "Vacances", "Impôt", "Impôt du Québec", "RRQ", "RRQ2", "AE", "RQAP"],
            ["Tremblay, Alex", 72000.00m, 2880.00m, 9100.00m, 10400.00m, 4160.00m, 188.00m, 834.24m, 355.68m],
            ["Total", 72000.00m, 2880.00m, 9100.00m, 10400.00m, 4160.00m, 188.00m, 834.24m, 355.68m]);

        // Auto-detection must identify all 4 .xlsx reports immediately from title/headers
        Assert.Equal(MigrationFileKind.TrialBalance, AccountingMigrationParser.DetectBannerKind(tbXlsx, "Balance_verification.xlsx"));
        Assert.Equal(MigrationFileKind.OpenReceivables, AccountingMigrationParser.DetectBannerKind(arXlsx, "Chrono_clients.xlsx"));
        Assert.Equal(MigrationFileKind.OpenPayables, AccountingMigrationParser.DetectBannerKind(apXlsx, "Chrono_fournisseurs.xlsx"));
        Assert.Equal(MigrationFileKind.PayrollYtd, AccountingMigrationParser.DetectBannerKind(payXlsx, "Sommaire_employes.xlsx"));

        var cutoverDate = new DateTime(2026, 10, 31);
        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            Format = AccountingMigrationSourceFormat.AutoDetect,
            AsOfDate = cutoverDate,
            Files =
            [
                new MigrationUploadedFile { FileName = "Balance_verification.xlsx", Content = tbXlsx },
                new MigrationUploadedFile { FileName = "Chrono_clients.xlsx", Content = arXlsx },
                new MigrationUploadedFile { FileName = "Chrono_fournisseurs.xlsx", Content = apXlsx },
                new MigrationUploadedFile { FileName = "Sommaire_employes.xlsx", Content = payXlsx }
            ]
        }, AdminActor);

        Assert.True(preview.CanExecute, string.Join("; ", preview.BlockingErrors));
        Assert.Equal(4, preview.GlMappings.Count);
        Assert.Equal(100000.00m, preview.TotalExternalDebits);
        Assert.Equal(100000.00m, preview.TotalExternalCredits);

        Assert.Equal(2, preview.OpenInvoices.Count);
        Assert.Contains(preview.OpenInvoices, i => i.PartyName == "Client Alpha Inc." && i.DocumentNumber == "INV-2610-001" && i.OpenAmount == 10000.00m && i.IssueDate == new DateTime(2026, 10, 5));
        Assert.Contains(preview.OpenInvoices, i => i.PartyName == "Client Beta Ltée" && i.DocumentNumber == "INV-2609-019" && i.OpenAmount == 5000.00m && i.DueDate == new DateTime(2026, 10, 20));

        var bill = Assert.Single(preview.OpenBills);
        Assert.Equal("Fournisseur Gamma", bill.PartyName);
        Assert.Equal("FAC-8812", bill.DocumentNumber);
        Assert.Equal(6500.00m, bill.OpenAmount);

        var ytd = Assert.Single(preview.PayrollYtds);
        Assert.Equal(existingEmp.Id, ytd.MatchedEmployeeId);
        Assert.Equal(72000.00m, ytd.GrossPayYtd);
        Assert.Equal(9100.00m, ytd.FederalTaxYtd);
        Assert.Equal(10400.00m, ytd.RegionalTaxYtd);
        Assert.Equal(4160.00m, ytd.QppCppEmployeeYtd);
        Assert.Equal(355.68m, ytd.QpipEmployeeYtd);

        // Account 3900 clearing nets to exact $0.00 because AR ($15k) and AP ($6.5k) match TB control accounts
        Assert.Equal(0m, preview.ProjectedClearing3900Residual);

        var res = _migration.ExecuteMigration(preview, "admin-1", "Admin", AdminActor);
        Assert.Equal(2, res.OpenInvoicesCreated);
        Assert.Equal(1, res.OpenBillsCreated);
        Assert.Equal(1, res.PayrollYtdStubsCreated);
        Assert.Equal(0m, res.Batch.ResidualClearingBalance3900);
    }

    [Fact]
    public void LegacyBinaryXls_EmitsClearWarningInsteadOfFailing()
    {
        var fakeLegacyXls = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };
        Assert.True(XlsxReaderHelper.IsLegacyBinaryXls(fakeLegacyXls));

        var parsed = AccountingMigrationParser.ParseFiles(
            [new MigrationUploadedFile { FileName = "old_report.xls", Content = fakeLegacyXls }],
            AccountingMigrationSourceFormat.AutoDetect,
            new DateTime(2026, 10, 31));

        Assert.Contains(parsed.Warnings, w => w.Contains(".xlsx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sage50FrenchDecimalStringsAndGroupedEmployeeDetail_ParseAccuratelyAndNetPaidSubledger()
    {
        // Replicates Sage 50 Canada French .xlsx formatting quirks using synthetic fixtures:
        // 1) 1-decimal comma strings ("12345,5", "150,2") stored as shared strings
        // 2) Grouped "Détail des employés" with multiple paycheques per employee & "A-E", "RRQ", "RPC", "Impôt", "Impôt Qc"
        // 3) Historical "Paiement" rows in Aged Detail are automatically netted against their preceding "Facture"
        var tbXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Balance de vérification au 2026-10-31"],
            ["N° compte", "Desc. du compte", "Débits", "Crédits"],
            ["1060", "Compte bancaire principal", "0", "12345,5"],
            ["1200", "Comptes clients", "12345,5", "0"]);

        var emplDetXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Détail des employés du 2026-01-01 au 2026-10-31"],
            ["", "Date", "N° chèque", "Brut", "Avances payées", "Prêts payés", "Retenues", "Salaire net", "A-E", "RQAP", "RPC", "RPC2", "RRQ", "RRQ2", "Impôt", "Impôt Qc"],
            ["Sophie Martin"],
            ["", new DateTime(2026, 1, 8), "PAY-001", "1000", "0", "0", "250,5", "749,5", "13,20", "4,50", "0", "0", "60,10", "0", "80,25", "92,45"],
            ["", new DateTime(2026, 1, 22), "PAY-002", "2500", "0", "0", "625,5", "1874,5", "32,50", "11,25", "0", "0", "150,2", "0", "200,50", "231,05"],
            ["Total", "", "", "3500", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0"]);

        var paidApXlsx = BuildTestXlsx(
            ["Acme Demo Inc."],
            ["Détails chronologiques des fournisseurs au 2026-10-31"],
            ["", "Source", "Date", "Type", "Total", "Courant"],
            ["Fournisseur Delta"],
            ["", "INV-OLD-1", new DateTime(2025, 4, 10), "Facture", "215,5", "0"],
            ["", "CHQ-OLD-1", new DateTime(2025, 4, 20), "Paiement", "-215,5", "0"],
            ["Total impayé:", "", "", "", "0", "0"],
            ["Fournisseur Epsilon"],
            ["", "BILL-501", new DateTime(2026, 2, 1), "Facture", "600,00", "0"],
            ["", "CHQ-100", new DateTime(2026, 2, 15), "Paiement", "-250,75", "0"],
            ["Total impayé:", "", "", "", "349,25", "0"]);

        Assert.Equal(MigrationFileKind.TrialBalance, AccountingMigrationParser.DetectBannerKind(tbXlsx, "Balance.xlsx"));
        Assert.Equal(MigrationFileKind.PayrollYtd, AccountingMigrationParser.DetectBannerKind(emplDetXlsx, "EmplDet.xlsx"));
        Assert.Equal(MigrationFileKind.OpenPayables, AccountingMigrationParser.DetectBannerKind(paidApXlsx, "FournDet.xlsx"));

        var parsed = AccountingMigrationParser.ParseFiles(
            [
                new MigrationUploadedFile { FileName = "Balance.xlsx", Content = tbXlsx },
                new MigrationUploadedFile { FileName = "EmplDet.xlsx", Content = emplDetXlsx },
                new MigrationUploadedFile { FileName = "FournDet.xlsx", Content = paidApXlsx }
            ],
            AccountingMigrationSourceFormat.AutoDetect,
            new DateTime(2026, 10, 31));

        // 1-decimal French comma "12345,5" parsed as 12345.50 (NOT 123455!)
        Assert.Equal(12345.50m, parsed.GlRows.Single(r => r.Code == "1060").Credit);
        Assert.Equal(12345.50m, parsed.GlRows.Single(r => r.Code == "1200").Debit);

        // Grouped EmplDet rows merged & summed cleanly across paycheques (ignoring Sage's broken 0-valued Total row)
        var emp = Assert.Single(parsed.PayrollYtds);
        Assert.Equal("Sophie Martin", emp.EmployeeName);
        Assert.Equal(3500.00m, emp.GrossPay);
        Assert.Equal(45.70m, emp.EiEmployee);
        Assert.Equal(15.75m, emp.QpipEmployee);
        Assert.Equal(210.30m, emp.QppCppEmployee);
        Assert.Equal(280.75m, emp.FederalTax);
        Assert.Equal(323.50m, emp.RegionalTax);

        // Fully paid Fournisseur Delta bill filtered out; partially paid Fournisseur Epsilon bill kept at exact remaining $349.25
        var openBill = Assert.Single(parsed.OpenBills);
        Assert.Equal("Fournisseur Epsilon", openBill.PartyName);
        Assert.Equal("BILL-501", openBill.DocumentNumber);
        Assert.Equal(349.25m, openBill.OpenAmount);
        Assert.Contains(parsed.InformationalNotices, n => n.Contains("automatically netted", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] BuildTestXlsx(params object?[][] rows)
    {
        var sharedStrings = new List<string>();
        var sharedStringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        int InternString(string s)
        {
            if (!sharedStringIndex.TryGetValue(s, out var idx))
            {
                idx = sharedStrings.Count;
                sharedStrings.Add(s);
                sharedStringIndex[s] = idx;
            }
            return idx;
        }

        var sheetSb = new StringBuilder();
        sheetSb.Append("""<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (int r = 0; r < rows.Length; r++)
        {
            sheetSb.Append($"""<row r="{r + 1}">""");
            for (int c = 0; c < rows[r].Length; c++)
            {
                var val = rows[r][c];
                if (val == null || (val is string sEmpty && string.IsNullOrEmpty(sEmpty))) continue;
                var cellRef = $"{(char)('A' + c)}{r + 1}";
                if (val is DateTime dt)
                {
                    var serial = (dt.Date - new DateTime(1899, 12, 30)).TotalDays;
                    sheetSb.Append($"""<c r="{cellRef}" s="1"><v>{serial.ToString(CultureInfo.InvariantCulture)}</v></c>""");
                }
                else if (val is decimal dec)
                {
                    sheetSb.Append($"""<c r="{cellRef}"><v>{dec.ToString(CultureInfo.InvariantCulture)}</v></c>""");
                }
                else
                {
                    var idx = InternString(val.ToString()!);
                    sheetSb.Append($"""<c r="{cellRef}" t="s"><v>{idx}</v></c>""");
                }
            }
            sheetSb.Append("</row>");
        }
        sheetSb.Append("</sheetData></worksheet>");

        var ssSb = new StringBuilder();
        ssSb.Append($"""<?xml version="1.0" encoding="UTF-8"?><sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{sharedStrings.Count}" uniqueCount="{sharedStrings.Count}">""");
        foreach (var s in sharedStrings)
            ssSb.Append($"<si><t>{SecurityElement.Escape(s)}</t></si>");
        ssSb.Append("</sst>");

        const string workbookXml = """<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>""";
        const string relsXml = """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""";
        const string stylesXml = """<?xml version="1.0" encoding="UTF-8"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>""";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void AddEntry(string path, string xml)
            {
                var entry = zip.CreateEntry(path);
                using var sw = new StreamWriter(entry.Open(), Encoding.UTF8);
                sw.Write(xml);
            }
            AddEntry("xl/workbook.xml", workbookXml);
            AddEntry("xl/_rels/workbook.xml.rels", relsXml);
            AddEntry("xl/styles.xml", stylesXml);
            AddEntry("xl/sharedStrings.xml", ssSb.ToString());
            AddEntry("xl/worksheets/sheet1.xml", sheetSb.ToString());
        }
        return ms.ToArray();
    }
}
