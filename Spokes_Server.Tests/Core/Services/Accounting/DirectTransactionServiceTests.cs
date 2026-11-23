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
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;
using Xunit;

[Collection("JurisdictionStaticState")]
public class DirectTransactionServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IDirectTransactionService _directTxService;
    private readonly ILedgerService _ledgerService;
    private readonly ITaxRemittanceService _taxRemittanceService;
    private readonly IBankReconciliationService _reconciliationService;
    private readonly IAccountingService _accountingService;
    private readonly IBusinessOverheadService _overheadService;
    private readonly ProjectReportService _projectReportService;
    private readonly IAccountantExportService _exportService;

    private static readonly Employee AdminActor = new()
    {
        Id = "direct-tx-admin",
        FirstName = "Finance",
        LastName = "Admin",
        IsActive = true,
        IsAdmin = true
    };

    private static readonly Employee UnauthorizedActor = new()
    {
        Id = "direct-tx-unauth",
        FirstName = "Basic",
        LastName = "User",
        IsActive = true,
        IsAdmin = false,
        Permissions = []
    };

    public DirectTransactionServiceTests()
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
        services.AddScoped<IDirectTransactionService, DirectTransactionService>();
        services.AddSingleton<BankStatementImportService>();
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<ProjectReportService>();
        services.AddScoped<IAccountantExportService, AccountantExportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _directTxService = _serviceProvider.GetRequiredService<IDirectTransactionService>();
        _ledgerService = _serviceProvider.GetRequiredService<ILedgerService>();
        _taxRemittanceService = _serviceProvider.GetRequiredService<ITaxRemittanceService>();
        _reconciliationService = _serviceProvider.GetRequiredService<IBankReconciliationService>();
        _accountingService = _serviceProvider.GetRequiredService<IAccountingService>();
        _overheadService = _serviceProvider.GetRequiredService<IBusinessOverheadService>();
        _projectReportService = _serviceProvider.GetRequiredService<ProjectReportService>();
        _exportService = _serviceProvider.GetRequiredService<IAccountantExportService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void MoneyIn_NoTax_IntoPettyCash_Seeds1010AndPostsBalancedLedgerAndStatements()
    {
        var pettyCash = _db.GlAccounts.GetBySystemRole(GlSystemRole.PettyCash);
        Assert.NotNull(pettyCash);
        Assert.Equal("1010", pettyCash!.Code);

        var tx = new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 6, 12),
            PartyName = "Metal Recycler Inc.",
            Description = "Sale of scrap steel",
            PaymentMethod = "Cash",
            AccountGlCode = "1010",
            TaxMode = DirectTransactionTaxMode.NoTax,
            Lines =
            [
                new DirectTransactionLine
                {
                    GlAccountCode = "4800",
                    EnteredAmount = 3000m,
                    Description = "Scrap steel lot"
                }
            ]
        };

        var saved = _directTxService.SaveTransaction(tx, AdminActor);
        Assert.StartsWith("TXN-", saved.TransactionNumber);
        Assert.Equal(3000m, saved.SubTotal);
        Assert.Equal(0m, saved.TaxAmount);
        Assert.Equal(3000m, saved.GrandTotal);

        var je = _db.JournalEntries.GetBySource(JournalSourceType.DirectReceipt, saved.Id);
        Assert.NotNull(je);
        Assert.True(je!.IsBalanced);
        Assert.Equal(3000m, je.Lines.Single(l => l.GlAccountCode == "1010").Debit);
        Assert.Equal(3000m, je.Lines.Single(l => l.GlAccountCode == "4800").Credit);

        var pl = _ledgerService.CalculateIncomeStatement(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        Assert.Equal(3000m, pl.TotalRevenue);
        Assert.Equal(3000m, pl.NetIncome);

        var bs = _ledgerService.CalculateBalanceSheet(new DateTime(2026, 12, 31));
        Assert.True(bs.IsBalanced);
        Assert.Equal(3000m, bs.TotalAssets);
    }

    [Fact]
    public void MoneyIn_TaxIncluded_And_MoneyOut_PlusTax_FeedTaxRemittanceService()
    {
        // Money In: $3,000.00 tax-included (CA-QC TPS 5% + TVQ 9.975%)
        var receipt = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 7, 10),
            PartyName = "Industrial Buyer",
            Description = "Taxable salvage sale",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.TaxIncluded,
            Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 3000m }]
        }, AdminActor);

        Assert.Equal(3000m, receipt.GrandTotal);
        Assert.Equal(2609.27m, receipt.SubTotal);
        Assert.Equal(130.46m, receipt.TpsAmount);
        Assert.Equal(260.27m, receipt.TvqAmount);
        Assert.Equal(390.73m, receipt.TaxAmount);

        // Money Out: $200.00 + TPS ($10.00) + TVQ ($19.95) = $229.95
        var payment = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyOut,
            Date = new DateTime(2026, 7, 15),
            PartyName = "Local Hardware",
            Description = "Shop supplies",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.PlusTax,
            Lines = [new DirectTransactionLine { GlAccountCode = "5600", EnteredAmount = 200m }]
        }, AdminActor);

        Assert.Equal(200m, payment.SubTotal);
        Assert.Equal(10m, payment.TpsAmount);
        Assert.Equal(19.95m, payment.TvqAmount);
        Assert.Equal(229.95m, payment.GrandTotal);

        var receiptJe = _db.JournalEntries.GetBySource(JournalSourceType.DirectReceipt, receipt.Id);
        var paymentJe = _db.JournalEntries.GetBySource(JournalSourceType.DirectPayment, payment.Id);
        Assert.NotNull(receiptJe);
        Assert.NotNull(paymentJe);
        Assert.True(receiptJe!.IsBalanced);
        Assert.True(paymentJe!.IsBalanced);

        // Tax Remittance Summary must reflect both collected taxes and recoverable ITC/ITRs
        var period = _taxRemittanceService.CalculateTaxPeriodSummary(new DateTime(2026, 7, 1), new DateTime(2026, 7, 31));
        Assert.Equal(2609.27m, period.TotalSalesBeforeTax);
        Assert.Equal(200m, period.TotalPurchasesBeforeTax);

        var tpsLine = period.AuthorityLines.Single(a => a.TaxCode == "TPS");
        var tvqLine = period.AuthorityLines.Single(a => a.TaxCode == "TVQ");
        Assert.Equal(130.46m, tpsLine.TaxCollectedOnSales);
        Assert.Equal(10.00m, tpsLine.InputTaxCreditsOnPurchases);
        Assert.Equal(260.27m, tvqLine.TaxCollectedOnSales);
        Assert.Equal(19.95m, tvqLine.InputTaxCreditsOnPurchases);
    }

    [Fact]
    public void MoneyOut_MultiLineSplit_And_Transfer_PostBalancedAndExcludeTransferFromPlAndTax()
    {
        var splitPay = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyOut,
            Date = new DateTime(2026, 8, 1),
            PartyName = "Business Bank",
            Description = "Monthly loan & fee payment",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            Lines =
            [
                new DirectTransactionLine { GlAccountCode = "5600", EnteredAmount = 400m, Description = "Admin fee" },
                new DirectTransactionLine { GlAccountCode = "5800", EnteredAmount = 100m, Description = "Interest" }
            ]
        }, AdminActor);

        var splitJe = _db.JournalEntries.GetBySource(JournalSourceType.DirectPayment, splitPay.Id);
        Assert.NotNull(splitJe);
        Assert.True(splitJe!.IsBalanced);
        Assert.Equal(3, splitJe.Lines.Count);
        Assert.Equal(500m, splitJe.Lines.Single(l => l.GlAccountCode == "1020").Credit);

        var transfer = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.Transfer,
            Date = new DateTime(2026, 8, 5),
            Description = "Pay off corporate credit card",
            AccountGlCode = "1020",
            DestinationAccountGlCode = "2120",
            GrandTotal = 1500m
        }, AdminActor);

        var transferJe = _db.JournalEntries.GetBySource(JournalSourceType.AccountTransfer, transfer.Id);
        Assert.NotNull(transferJe);
        Assert.True(transferJe!.IsBalanced);
        Assert.Equal(1500m, transferJe.Lines.Single(l => l.GlAccountCode == "2120").Debit);
        Assert.Equal(1500m, transferJe.Lines.Single(l => l.GlAccountCode == "1020").Credit);

        var pl = _ledgerService.CalculateIncomeStatement(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        Assert.Equal(0m, pl.TotalRevenue);
        Assert.Equal(500m, pl.TotalOperatingExpenses);

        var taxPeriod = _taxRemittanceService.CalculateTaxPeriodSummary(new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        Assert.Equal(0m, taxPeriod.TotalSalesBeforeTax);
        Assert.Equal(0m, taxPeriod.TotalPurchasesBeforeTax);
    }

    [Fact]
    public void BankReconciliation_AutoMatchesDirectTransaction_AndCategorizeCreatesLinkedDirectTransactionWithTaxes()
    {
        // 1. Pre-entered $3,000 MoneyIn should auto-match bank deposit line
        var preEntered = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 9, 5),
            PartyName = "RECYCLER CORP",
            Description = "Scrap steel deposit",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 3000m }]
        }, AdminActor);

        const string ofx = """
            <OFX>
            <BANKMSGSRSV1>
            <STMTTRNRS>
            <STMTRS>
            <CURDEF>CAD
            <BANKACCTFROM><ACCTID>99999</ACCTID></BANKACCTFROM>
            <BANKTRANLIST>
            <DTSTART>20260901</DTSTART>
            <DTEND>20260930</DTEND>
            <STMTTRN>
            <TRNTYPE>DEP</TRNTYPE>
            <DTPOSTED>20260905120000</DTPOSTED>
            <TRNAMT>3000.00</TRNAMT>
            <FITID>FIT-DEP-3000</FITID>
            <NAME>RECYCLER CORP EFT</NAME>
            </STMTTRN>
            <STMTTRN>
            <TRNTYPE>DEP</TRNTYPE>
            <DTPOSTED>20260912120000</DTPOSTED>
            <TRNAMT>1149.75</TRNAMT>
            <FITID>FIT-DEP-TAX</FITID>
            <NAME>DIRECT TAXABLE RECEIPT</NAME>
            </STMTTRN>
            </BANKTRANLIST>
            <LEDGERBAL><BALAMT>4149.75</BALAMT></LEDGERBAL>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            </OFX>
            """;

        var stmt = _reconciliationService.ImportAndAutoMatch(ofx, "sep-2026.ofx", "1020", actor: AdminActor);
        var matchedLine = stmt.Lines.Single(l => l.ExternalFitId == "FIT-DEP-3000");
        Assert.Equal(BankLineMatchStatus.AutoMatched, matchedLine.MatchStatus);

        // 2. Categorizing an unmatched deposit with sales tax creates a linked DirectTransaction & flows into TaxRemittanceService
        var unmatchedDeposit = stmt.Lines.Single(l => l.ExternalFitId == "FIT-DEP-TAX");
        var catEntry = _reconciliationService.CategorizeUnmatchedLineDirect(
            stmt, unmatchedDeposit.Id, "4800", "Direct taxable receipt", includeSalesTaxes: true, actor: AdminActor);
        Assert.True(catEntry.IsBalanced);

        var linkedTx = _db.DirectTransactions.GetByBankLine(stmt.Id, unmatchedDeposit.Id);
        Assert.NotNull(linkedTx);
        Assert.Equal(1000m, linkedTx!.SubTotal);
        Assert.Equal(149.75m, linkedTx.TaxAmount);

        var taxSummary = _taxRemittanceService.CalculateTaxPeriodSummary(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        Assert.Equal(1000m, taxSummary.TotalSalesBeforeTax);
        Assert.Equal(50m, taxSummary.AuthorityLines.Single(a => a.TaxCode == "TPS").TaxCollectedOnSales);
        Assert.Equal(99.75m, taxSummary.AuthorityLines.Single(a => a.TaxCode == "TVQ").TaxCollectedOnSales);

        // 3. Unmatching deletes the linked DirectTransaction cleanly
        _reconciliationService.UnmatchLine(stmt, unmatchedDeposit.Id, actor: AdminActor);
        Assert.Null(_db.DirectTransactions.GetByBankLine(stmt.Id, unmatchedDeposit.Id));

        // 4. Re-categorize and finalize statement -> locks pre-entered DirectTransaction against edits/voids
        _reconciliationService.CategorizeUnmatchedLineDirect(stmt, unmatchedDeposit.Id, "4800", "Direct receipt", includeSalesTaxes: false, actor: AdminActor);
        _reconciliationService.ReconcileAndLockStatement(stmt, "Tester", actor: AdminActor);

        var (isLocked, _) = _directTxService.IsTransactionLocked(preEntered);
        Assert.True(isLocked);
        Assert.Throws<InvalidOperationException>(() => _directTxService.VoidTransaction(preEntered.Id, "Too late", AdminActor));
    }

    [Fact]
    public void ProjectLinkedDirectTransactions_FlowIntoProjectReport_Commissions_And_InternalOverhead()
    {
        var clientProject = new Project
        {
            Id = "proj-client-1",
            ProjectNumber = "PRJ-100",
            Name = "Client Automation Cell",
            IsInternal = false,
            Status = ProjectStatus.Completed,
            Commissions =
            [
                new CommissionBeneficiary
                {
                    Name = "Sales Rep",
                    Percentage = 10m,
                    CommissionBasis = CommissionBasisTypes.Margin
                }
            ]
        };
        _db.Projects.Save(clientProject);

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-proj-1",
            ProjectId = clientProject.Id,
            InvoiceNumber = "INV-100",
            Date = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 4, 30),
            Status = InvoiceStatus.Paid,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Integration", Quantity = 1m, UnitPrice = 10000m }],
            Payments = [new PaymentRecord { Date = new DateTime(2026, 4, 10), Amount = 10000m }]
        });

        // Direct MoneyIn ($1,000) & MoneyOut ($400) linked to client project
        _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 4, 15),
            PartyName = "Scrap Buyer",
            Description = "Project steel scrap recovery",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            ProjectId = clientProject.Id,
            Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 1000m }]
        }, AdminActor);

        _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyOut,
            Date = new DateTime(2026, 4, 18),
            PartyName = "Courier",
            Description = "Direct project freight cash payment",
            AccountGlCode = "1010",
            TaxMode = DirectTransactionTaxMode.NoTax,
            ProjectId = clientProject.Id,
            Lines = [new DirectTransactionLine { GlAccountCode = "5100", EnteredAmount = 400m }]
        }, AdminActor);

        // 1. ProjectReportService snapshot includes both direct income and direct expense
        var snapshot = _projectReportService.BuildSnapshot(clientProject, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30));
        Assert.Equal(2, snapshot.DirectTransactions.Count);
        Assert.Equal(1000m, snapshot.TotalDirectIncome);
        Assert.Equal(400m, snapshot.TotalDirectExpense);
        Assert.Equal(11000m, snapshot.BaseRevenueIncTax);
        Assert.Equal(400m, snapshot.TotalInternalSpent);
        Assert.Equal(10600m, snapshot.GrossMargin);
        Assert.Equal(10600m, snapshot.CashPosition);

        // Ensure CompanyProfile maps Completed status for commission evaluation
        var profile = _db.CompanyProfile.Get();
        var completedStatus = profile.ProjectStatuses.FirstOrDefault(s => s.Name == ProjectStatus.Completed);
        if (completedStatus != null)
        {
            completedStatus.SystemMapping = SystemStatusMappings.Completed;
            _db.CompanyProfile.Save(profile);
        }

        // 2. Project Commissions gross margin reflects direct income (+1000) and direct expense (-400)
        var grossMargin = _accountingService.CalculateProjectGrossMargin(clientProject.Id);
        Assert.Equal(10600m, grossMargin);

        _accountingService.EvaluateProjectCommissions(clientProject.Id);
        var comm = _db.CommissionLedgerRecords.GetByProject(clientProject.Id).Single();
        Assert.Equal(10600m, comm.BaseAmount);
        Assert.Equal(1060m, comm.CommissionAmount);

        // 3. Internal Project Overhead Pool: MoneyOut adds to internal cost pool, MoneyIn offsets it
        var internalProject = new Project
        {
            Id = "proj-int-1",
            ProjectNumber = "INT-01",
            Name = "Shop R&D",
            IsInternal = true,
            Status = ProjectStatus.InProduction
        };
        _db.Projects.Save(internalProject);

        _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyOut,
            Date = new DateTime(2026, 4, 20),
            Description = "Shop consumables",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            ProjectId = internalProject.Id,
            Lines = [new DirectTransactionLine { GlAccountCode = "5600", EnteredAmount = 750m }]
        }, AdminActor);

        _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 4, 22),
            Description = "Shop rebate",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            ProjectId = internalProject.Id,
            Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 250m }]
        }, AdminActor);

        var overhead = _overheadService.CalculatePeriodOverhead(new DateTime(2026, 4, 1), new DateTime(2026, 4, 30), actor: AdminActor);
        var intSummary = overhead.InternalProjects.Single(p => p.ProjectId == internalProject.Id);
        Assert.Equal(500m, intSummary.ExpensesPreTax); // 750 - 250
    }

    [Fact]
    public void Voiding_PeriodLock_Rbac_RebuildIdempotency_And_CpaExport()
    {
        // RBAC rejects unauthorized user
        Assert.Throws<UnauthorizedAccessException>(() =>
            _directTxService.SaveTransaction(new DirectTransaction
            {
                Direction = DirectTransactionDirection.MoneyIn,
                Date = new DateTime(2026, 10, 5),
                AccountGlCode = "1020",
                Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 100m }]
            }, UnauthorizedActor));

        var saved = _directTxService.SaveTransaction(new DirectTransaction
        {
            Direction = DirectTransactionDirection.MoneyIn,
            Date = new DateTime(2026, 10, 5),
            PartyName = "Partner Inc.",
            Description = "Grant installment",
            AccountGlCode = "1020",
            TaxMode = DirectTransactionTaxMode.NoTax,
            Lines = [new DirectTransactionLine { GlAccountCode = "4800", EnteredAmount = 2500m }]
        }, AdminActor);

        // RebuildOperationalLedger is idempotent (no duplicate JEs)
        _ledgerService.RebuildOperationalLedger();
        _ledgerService.RebuildOperationalLedger();
        var matchingJes = _db.JournalEntries.GetAll().Where(j => j.SourceId == saved.Id).ToList();
        Assert.Single(matchingJes);

        // CPA Export includes DirectReceipt in General Ledger CSV
        var glCsv = _exportService.GenerateGeneralLedgerCsv(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), AdminActor);
        Assert.Contains(JournalSourceType.DirectReceipt, glCsv);
        Assert.Contains("Grant installment", glCsv);

        // Voiding removes GL entry and records audit trail
        var voided = _directTxService.VoidTransaction(saved.Id, "Entered twice", AdminActor);
        Assert.True(voided.IsVoid);
        Assert.Equal("Entered twice", voided.VoidReason);
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.DirectReceipt, saved.Id));

        // Period lock blocks posting on or before PeriodLockDate
        var profile = _db.CompanyProfile.Get();
        profile.Accounting.PeriodLockDate = new DateTime(2026, 9, 30);
        _db.CompanyProfile.Save(profile);

        Assert.Throws<InvalidOperationException>(() =>
            _directTxService.SaveTransaction(new DirectTransaction
            {
                Direction = DirectTransactionDirection.MoneyOut,
                Date = new DateTime(2026, 9, 30),
                AccountGlCode = "1020",
                Lines = [new DirectTransactionLine { GlAccountCode = "5800", EnteredAmount = 25m }]
            }, AdminActor));
    }
}
