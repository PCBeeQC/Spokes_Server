namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
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
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Xunit;

[Collection("JurisdictionStaticState")]
public class BankReconciliationAndTaxServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledgerService;
    private readonly IAccountingService _accountingService;
    private readonly IBankReconciliationService _reconciliationService;
    private readonly ITaxRemittanceService _taxRemittanceService;
    private static readonly Employee AdminActor = new() { Id = "bank-admin", FirstName = "Bank", LastName = "Admin", IsActive = true, IsAdmin = true };

    public BankReconciliationAndTaxServiceTests()
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
        services.AddScoped<BankStatementImportService>();
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _ledgerService = _serviceProvider.GetRequiredService<ILedgerService>();
        _accountingService = _serviceProvider.GetRequiredService<IAccountingService>();
        _reconciliationService = _serviceProvider.GetRequiredService<IBankReconciliationService>();
        _taxRemittanceService = _serviceProvider.GetRequiredService<ITaxRemittanceService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void ImportAndAutoMatch_OfxStatement_MatchesInvoicePaymentAndCategorizesBankFee_AndLocksStatement()
    {
        var invoice = new Invoice
        {
            Id = "inv-bank-1",
            InvoiceNumber = "INV-2026-010",
            Date = new DateTime(2026, 5, 1),
            DueDate = new DateTime(2026, 5, 31),
            Status = InvoiceStatus.Paid,
            TaxRate = 0.14975m,
            LineItems =
            [
                new InvoiceLine { Description = "Consulting", Quantity = 10m, UnitPrice = 100m }
            ],
            Payments =
            [
                new PaymentRecord { Id = "pay-bank-1", Date = new DateTime(2026, 5, 5), Amount = 1149.75m, Method = "Wire" }
            ]
        };
        _db.Invoices.Save(invoice);
        _ledgerService.SyncInvoice(invoice, "Beta Client");

        const string ofx = """
            <OFX>
            <BANKMSGSRSV1>
            <STMTTRNRS>
            <STMTRS>
            <CURDEF>CAD
            <BANKACCTFROM>
            <ACCTID>1234567
            </BANKACCTFROM>
            <BANKTRANLIST>
            <DTSTART>20260501
            <DTEND>20260531
            <STMTTRN>
            <TRNTYPE>DEP
            <DTPOSTED>20260505120000
            <TRNAMT>1149.75
            <FITID>TXN-001
            <NAME>BETA CLIENT WIRE INV-2026-010
            </STMTTRN>
            <STMTTRN>
            <TRNTYPE>FEE
            <DTPOSTED>20260531120000
            <TRNAMT>-15.00
            <FITID>TXN-002
            <NAME>MONTHLY ACCOUNT FEE
            </STMTTRN>
            </BANKTRANLIST>
            <LEDGERBAL>
            <BALAMT>1134.75
            </LEDGERBAL>
            </STMTRS>
            </STMTTRNRS>
            </BANKMSGSRSV1>
            </OFX>
            """;

        var statement = _reconciliationService.ImportAndAutoMatch(ofx, "may-2026.ofx", "1020", actor: AdminActor);
        Assert.Equal(2, statement.Lines.Count);

        var depositLine = statement.Lines.First(l => l.ExternalFitId == "TXN-001");
        Assert.Equal(BankLineMatchStatus.AutoMatched, depositLine.MatchStatus);
        Assert.NotNull(depositLine.MatchedJournalEntryId);

        var feeLine = statement.Lines.First(l => l.ExternalFitId == "TXN-002");
        Assert.Equal(BankLineMatchStatus.Unmatched, feeLine.MatchStatus);

        // Categorize the bank fee directly to 5720 Bank Charges
        var feeEntry = _reconciliationService.CategorizeUnmatchedLineDirect(statement, feeLine.Id, "5720", "Monthly bank fee", actor: AdminActor);
        Assert.True(feeEntry.IsBalanced);
        Assert.True(statement.IsReadyToReconcile);

        _reconciliationService.ReconcileAndLockStatement(statement, "Tester", actor: AdminActor);
        Assert.Equal(BankStatementStatus.Reconciled, statement.Status);
    }

    [Fact]
    public void TaxRemittanceService_ComputesCollectedTaxesMinusItcs_FilesAndRecordsPayment()
    {
        var invoice = new Invoice
        {
            Id = "inv-tax-1",
            InvoiceNumber = "INV-2026-020",
            Date = new DateTime(2026, 4, 10),
            DueDate = new DateTime(2026, 5, 10),
            Status = InvoiceStatus.Final,
            TaxRate = 0.14975m,
            LineItems =
            [
                new InvoiceLine { Description = "Integration Project", Quantity = 1m, UnitPrice = 2000m }
            ]
        };
        _db.Invoices.Save(invoice);
        _ledgerService.SyncInvoice(invoice, "Gamma Corp");

        var bill = new Bill
        {
            Id = "bill-tax-1",
            SupplierName = "Cloud Host Inc.",
            Description = "Dedicated Servers",
            Date = new DateTime(2026, 4, 15),
            DueDate = new DateTime(2026, 5, 15),
            SubTotal = 500m,
            TpsAmount = 25m,
            TvqAmount = 49.88m,
            TaxAmount = 74.88m,
            Amount = 574.88m,
            Status = BillStatus.Unpaid,
            GlAccountCode = "5540"
        };
        _accountingService.SaveBill(bill, actor: AdminActor);

        var q2Start = new DateTime(2026, 4, 1);
        var q2End = new DateTime(2026, 6, 30);

        var filedPeriod = _taxRemittanceService.FileAndClosePeriod(q2Start, q2End, "Quarterly", "CFO");
        Assert.Equal(TaxFilingStatus.Filed, filedPeriod.Status);
        // Collected on $2,000 = $100 TPS + $199.50 TVQ = $299.50
        Assert.Equal(299.50m, filedPeriod.TotalTaxCollected);
        // ITCs on $500 = $25 TPS + $49.88 TVQ = $74.88
        Assert.Equal(74.88m, filedPeriod.TotalInputTaxCredits);
        // Net owing = $299.50 - $74.88 = $224.62
        Assert.Equal(224.62m, filedPeriod.NetTaxRemittanceDue);
        Assert.NotNull(filedPeriod.ClosingJournalEntryId);

        var paidPeriod = _taxRemittanceService.RecordTaxRemittancePayment(filedPeriod.Id, new DateTime(2026, 7, 15), "1020", "RQ-CRA-2026Q2");
        Assert.Equal(TaxFilingStatus.Paid, paidPeriod.Status);
        Assert.NotNull(paidPeriod.PaymentJournalEntryId);
    }

    [Fact]
    public void ImportAndAutoMatch_WhenActorLacksAccountingPermission_ThrowsUnauthorizedAccessException()
    {
        var nonAccountingUser = new Employee
        {
            Id = "bank-nonacct",
            FirstName = "No",
            LastName = "Access",
            IsActive = true,
            IsAdmin = false,
            Permissions = []
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            _reconciliationService.ImportAndAutoMatch("<OFX></OFX>", "unauth.ofx", "1020", actor: nonAccountingUser));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _reconciliationService.ImportAndAutoMatch("<OFX></OFX>", "unauth.ofx", "1020", actor: null));
    }
}
