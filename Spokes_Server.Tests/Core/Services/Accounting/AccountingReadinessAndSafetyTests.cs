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
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Xunit;

[Collection("JurisdictionStaticState")]
public class AccountingReadinessAndSafetyTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledger;
    private readonly IAccountingService _accounting;
    private readonly IAccountingMigrationService _migration;
    private readonly IBusinessOverheadService _overhead;
    private readonly IBankReconciliationService _reconciliation;
    private readonly IAccountingHealthService _health;

    private static readonly Employee AdminActor = new()
    {
        Id = "admin-test",
        FirstName = "Admin",
        LastName = "Tester",
        Email = "admin@spokes.local",
        IsActive = true,
        IsAdmin = true,
        Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Purchases.ManageBills, AppPermissions.Purchases.ApproveBills]
    };

    public AccountingReadinessAndSafetyTests()
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
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<IAccountingHealthService, AccountingHealthService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _serviceProvider.GetRequiredService<IJurisdictionTemplateService>().EnsureChartOfAccountsSeeded();
        _ledger = _serviceProvider.GetRequiredService<ILedgerService>();
        _accounting = _serviceProvider.GetRequiredService<IAccountingService>();
        _migration = _serviceProvider.GetRequiredService<IAccountingMigrationService>();
        _overhead = _serviceProvider.GetRequiredService<IBusinessOverheadService>();
        _reconciliation = _serviceProvider.GetRequiredService<IBankReconciliationService>();
        _health = _serviceProvider.GetRequiredService<IAccountingHealthService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void Migration_DeltaMode_WithZeroBalanceCoAOnly_DoesNotWipeExistingSpokesGl()
    {
        _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 10, 15),
            Description = "Existing operational revenue",
            SourceType = JournalSourceType.Manual,
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", GlAccountName = "Operating Bank", Debit = 5000m, Credit = 0m },
                new JournalLine { GlAccountCode = "4000", GlAccountName = "Services Revenue", Debit = 0m, Credit = 5000m }
            ]
        });

        var csv = "AccountNumber,AccountName,Type,Debit,Credit\n5999,Custom New Expense,Expense,0,0\n";
        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.ExistingWorkspaceDelta,
            AsOfDate = new DateTime(2026, 11, 13),
            Files = [new MigrationUploadedFile { FileName = "coa.csv", Content = Encoding.UTF8.GetBytes(csv), ExplicitKind = MigrationFileKind.ChartOfAccounts }]
        }, AdminActor);

        Assert.False(preview.HasExternalTrialBalanceBalances);
        Assert.Equal(0m, preview.TotalDeltaDebits);
        Assert.Equal(0m, preview.TotalDeltaCredits);
        Assert.DoesNotContain(preview.GlMappings, g => g.IsSystemGeneratedSpokesOnly);

        var result = _migration.ExecuteMigration(preview, AdminActor.Id, AdminActor.FullName, AdminActor);
        Assert.Null(result.OpeningJournalEntryNumber);
        Assert.Empty(result.Batch.CreatedJournalEntryIds);

        var tb = _ledger.CalculateTrialBalance(new DateTime(2026, 1, 1), new DateTime(2026, 11, 13));
        var bank = tb.Rows.First(r => r.AccountCode == "1020");
        Assert.Equal(5000m, bank.EndingBalance);
    }

    [Fact]
    public void Classifier_And_TaxReconciler_HandleCustomerPaymentAdviceAndRejectDateYearAsPayment()
    {
        var (_, isReceipt, _) = UniversalDocumentClassifier.ClassifyEmail(
            "Payment advice to ACME DEMO INC from CLIENT ALPHA INC",
            "Please find attached payment advice 01476 dated 2026-11-20 for $776.08.",
            []);
        Assert.True(isReceipt);

        var data = new ExtractedInvoiceData { TotalAmount = 776.08m };
        UniversalTaxReconciler.ReconcileTotals(
            data,
            SpatialTextDocument.FromText("Payment advice 01476 - 2026-11-20\nPayment: 2026-11-20\nTotal: 776.08"));

        Assert.Equal(0m, data.PaidAmount);
        Assert.Equal(776.08m, data.BalanceDue);
    }

    [Fact]
    public void BackfillTaxSplitOnBills_SplitsTaxIncludedAmountAndResyncsLedger()
    {
        var bill = new Bill
        {
            Id = "bill-tax-1",
            SupplierName = "Acme Parts",
            Date = new DateTime(2026, 8, 10),
            DueDate = new DateTime(2026, 9, 10),
            Amount = 1149.75m,
            SubTotal = 0m,
            TaxAmount = 0m,
            TpsAmount = 0m,
            TvqAmount = 0m,
            IsTaxExempt = false,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(bill);
        _ledger.SyncBill(bill);

        int updated = _accounting.BackfillTaxSplitOnBills([bill.Id], "Admin", AdminActor);

        Assert.Equal(1, updated);
        var saved = _db.Bills.GetById(bill.Id)!;
        Assert.Equal(1000.00m, saved.SubTotal);
        Assert.Equal(50.00m, saved.TpsAmount);
        Assert.Equal(99.75m, saved.TvqAmount);
        Assert.Equal(149.75m, saved.TaxAmount);

        var je = _db.JournalEntries.GetAll().First(e => e.SourceId == bill.Id && e.SourceType == JournalSourceType.Bill);
        Assert.Contains(je.Lines, l => l.GlAccountCode == "2165" && l.Debit == 50.00m);
        Assert.Contains(je.Lines, l => l.GlAccountCode == "2175" && l.Debit == 99.75m);
    }

    [Fact]
    public void RecurringExpense_GenerateDueBills_And_BankReconciliationMatch()
    {
        var rent = new RecurringExpense
        {
            Id = "rec-rent",
            Name = "Loyer",
            Supplier = "Immeubles Nordique",
            Amount = 1149.75m,
            Frequency = RecurringExpenseFrequency.Monthly,
            NextDueDate = new DateTime(2026, 9, 1),
            GlAccountCode = "5610"
        };
        _db.RecurringExpenses.Save(rent);

        var overdue = _overhead.GetOverdueRecurringOccurrences(new DateTime(2026, 11, 15));
        Assert.Single(overdue);
        Assert.Equal(3, overdue[0].MissedPeriodsCount);

        int generated = _overhead.GenerateDueRecurringBills([rent.Id], new DateTime(2026, 11, 15), AdminActor);
        Assert.Equal(3, generated);

        var reloaded = _db.RecurringExpenses.GetById(rent.Id)!;
        Assert.Equal(3, reloaded.History.Count);
        Assert.Equal(new DateTime(2026, 12, 1), reloaded.NextDueDate);

        var stmt = new BankStatement
        {
            Id = "stmt-1",
            GlAccountCode = "1020",
            Lines =
            [
                new BankTransactionLine
                {
                    Id = "line-1",
                    Date = new DateTime(2026, 12, 1),
                    Description = "PRE-AUTH IMMEUBLES NORDIQUE LOYER",
                    Amount = -1149.75m
                }
            ]
        };
        _db.BankStatements.Save(stmt);

        var candidates = _reconciliation.GetRecurringCandidatesForLine(stmt, stmt.Lines[0], AdminActor);
        Assert.Single(candidates);
        Assert.Equal(rent.Id, candidates[0].Profile.Id);

        var paymentJe = _reconciliation.MatchLineToRecurringExpense(stmt, "line-1", rent.Id, splitTaxIncluded: true, actor: AdminActor);
        Assert.NotNull(paymentJe);
        Assert.True(stmt.Lines[0].IsMatchedOrCategorized);
        Assert.Equal(new DateTime(2027, 1, 1), _db.RecurringExpenses.GetById(rent.Id)!.NextDueDate);
    }

    [Fact]
    public void AccountingHealthService_FlagsCriticalIssuesAndResyncsMissingJEs()
    {
        _db.Bills.Save(new Bill
        {
            Id = "bill-neg",
            SupplierName = "Vendor Alpha",
            Date = new DateTime(2026, 11, 20),
            Amount = 776.08m,
            Status = BillStatus.AwaitingApproval,
            Payments = [new PaymentRecord { Amount = 2026m, Date = new DateTime(2026, 11, 20) }]
        });

        _db.Bills.Save(new Bill
        {
            Id = "bill-missing-je",
            SupplierName = "McMaster",
            Date = new DateTime(2026, 8, 12),
            Amount = 500m,
            Status = BillStatus.Unpaid
        });

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-stale",
            InvoiceNumber = "F-2608-0001",
            Date = new DateTime(2026, 8, 15),
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Description = "Work", Quantity = 1, UnitPrice = 1000m }]
        });

        var report = _health.EvaluateHealth(new DateTime(2026, 11, 23), AdminActor);

        Assert.Contains(report.Issues, i => i.Code == "AP_NEGATIVE_BALANCE" && i.Severity == AccountingHealthSeverity.Critical);
        Assert.Contains(report.Issues, i => i.Code == "GL_MISSING_JES");
        Assert.DoesNotContain(report.Issues, i => i.Code == "AR_STALE_DRAFTS");
        Assert.Contains(report.Issues, i => i.Code == "TAX_BILLS_MISSING_SPLIT");
        Assert.Contains(report.Issues, i => i.Code == "TAX_MISSING_REG_NUMBERS");
        Assert.Equal(6, report.CloseChecklist.Count);

        int synced = _health.ResyncMissingJournalEntries(AdminActor);
        Assert.Equal(1, synced);
        var reportAfter = _health.EvaluateHealth(new DateTime(2026, 11, 23), AdminActor);
        Assert.DoesNotContain(reportAfter.Issues, i => i.Code == "GL_MISSING_JES");
    }

    [Fact]
    public void GlAccountSuggestion_RejectsIncompatibleSystemCodeCollisions_AndMapsFrenchSageSalesTaxes()
    {
        var spokesAccounts = _db.GlAccounts.GetAll();

        // Incompatible code collisions must NOT map to Spokes system accounts
        var corpTax = PartyMatchingHelper.SuggestGlAccount("2160", "Impôts sur les sociétés à payer", "2680", spokesAccounts);
        Assert.Null(corpTax.SuggestedCode);

        var vacPay = PartyMatchingHelper.SuggestGlAccount("2170", "Vacances à payer", "2624", spokesAccounts);
        Assert.Null(vacPay.SuggestedCode);

        var acctFees = PartyMatchingHelper.SuggestGlAccount("5610", "Frais comptables & judiciaires", "8862", spokesAccounts);
        Assert.Null(acctFees.SuggestedCode);

        // French Sage 50 sales tax accounts MUST map cleanly to Spokes 2160/2165/2170/2175/2180
        Assert.Equal("2160", PartyMatchingHelper.SuggestGlAccount("2310", "TPS/TVH perçue sur les ventes", "2680", spokesAccounts).SuggestedCode);
        Assert.Equal("2165", PartyMatchingHelper.SuggestGlAccount("2315", "TPS/TVH payée sur les achats", "2680", spokesAccounts).SuggestedCode);
        Assert.Equal("2170", PartyMatchingHelper.SuggestGlAccount("2340", "TVQ perçue sur les ventes", "2680", spokesAccounts).SuggestedCode);
        Assert.Equal("2175", PartyMatchingHelper.SuggestGlAccount("2345", "TVQ payée sur les achats", "2680", spokesAccounts).SuggestedCode);
        Assert.Equal("2180", PartyMatchingHelper.SuggestGlAccount("2365", "TPS-TVQ à payer (à recevoir)", "", spokesAccounts).SuggestedCode);
    }

    [Fact]
    public void BankStatementImportService_ParsesBncChequingAndMasterCardCsvWithMetadataBanner()
    {
        var importSvc = new BankStatementImportService();

        var bncChequingCsv = """
            ACME DEMO INC.,ACME DEMO INC,0006-00001-0000001,CAD
            Date,Description,Référence,Autres détails,Contrepartie,Transit émetteur,Débit,Crédit,Solde total
            2026-11-23,PERCEPTION DE CPTE,REF001,BELL CANADA,,,155.16,,2359.60
            2026-11-23,DEPOT MOBILE,,,,91601,,3607.98,2514.76
            """;
        var chqStmt = importSvc.ParseCsv(bncChequingCsv, "bnc_cad.csv", "1020");
        Assert.Equal(2, chqStmt.Lines.Count);
        Assert.Contains(chqStmt.Lines, l => l.Amount == -155.16m);
        Assert.Contains(chqStmt.Lines, l => l.Amount == 3607.98m);

        var bncCardCsv = """
            5258 81** **** 0001
            Date de la transaction,Date associée au relevé,Référence,Statut,Description,Montant
            2026-11-19,,,En attente,PENDING VENDOR,-17.25
            2026-11-17,2026-11-19,U827314496,Autorisée,OFFICE SUPPLIES DEMO,-67.49
            """;
        var cardStmt = importSvc.ParseCsv(bncCardCsv, "bnc_mc.csv", "2120");
        Assert.Single(cardStmt.Lines);
        Assert.Equal(-67.49m, cardStmt.Lines[0].Amount);
        Assert.Equal(new DateTime(2026, 11, 17), cardStmt.Lines[0].Date);
    }

    [Fact]
    public void CutoverSanity_CrossAccountReassign_And_OpenDraftInvoiceSettlement()
    {
        // Seed an Applied migration batch as of 2026-10-31 and matching GL Bank 1020 balance ($2,000)
        _db.MigrationBatches.Save(new AccountingMigrationBatch
        {
            BatchNumber = "MIG-2610-0001",
            AsOfDate = new DateTime(2026, 10, 31),
            Status = AccountingMigrationBatchStatus.Applied
        });
        _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 10, 31),
            Description = "Cutover opening bank",
            SourceType = JournalSourceType.OpeningBalance,
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", GlAccountName = "Operating Bank", Debit = 2000.00m, Credit = 0m },
                new JournalLine { GlAccountCode = "3600", GlAccountName = "Retained Earnings", Debit = 0m, Credit = 2000.00m }
            ]
        });

        // Seed a payment mistakenly booked on 2120 (Credit Card) instead of 1020 (Bank) on Nov 5
        var wrongAcctJe = _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 11, 5),
            Description = "Vendor payment booked to wrong card",
            PartyName = "Supplier Alpha",
            SourceType = JournalSourceType.Manual,
            Lines =
            [
                new JournalLine { GlAccountCode = "5600", GlAccountName = "Overhead", Debit = 250.00m, Credit = 0m },
                new JournalLine { GlAccountCode = "2120", GlAccountName = "Corporate Credit Card", Debit = 0m, Credit = 250.00m }
            ]
        });

        // Seed a Draft Invoice for $1,149.75 (matching Nov 10 deposit)
        var draftInv = new Invoice
        {
            Id = "inv-draft-nov",
            InvoiceNumber = "F-2611-0087",
            Date = new DateTime(2026, 11, 5),
            Status = InvoiceStatus.Draft,
            TaxRate = FinancialCalculationEngine.QuebecCombinedTaxRate,
            LineItems = [new InvoiceLine { Description = "Engineering", Quantity = 1, UnitPrice = 1000.00m }]
        };
        _db.Invoices.Save(draftInv);

        // Import BNC-style CSV spanning pre-cutover (Oct 30, Solde = 2000.00) and post-cutover (Nov 5 & Nov 10)
        var csv = """
            ACME DEMO INC.,ACME DEMO INC,0006-00001-0000001,CAD
            Date,Description,Référence,Autres détails,Contrepartie,Transit émetteur,Débit,Crédit,Solde total
            2026-11-10,DEPOT MOBILE F-2611-0087,,,,,0,1149.75,2899.75
            2026-11-05,PAIEMENT SUPPLIER ALPHA,,,,,250.00,,1750.00
            2026-10-30,LOYER OCTOBRE (PRE-CUTOVER),,,,,500.00,,2000.00
            """;

        var stmt = _reconciliation.ImportAndAutoMatch(csv, "bnc_full.csv", "1020", actor: AdminActor);
        Assert.Equal(2899.75m, stmt.ClosingBalance);

        // 1. Cutover Sanity Check matches Oct 31 GL ($2,000.00 == $2,000.00)
        var sanity = _reconciliation.EvaluateCutoverSanity(stmt, AdminActor);
        Assert.True(sanity.HasPreCutoverLines);
        Assert.Equal(2000.00m, sanity.BankBalanceAtCutover);
        Assert.Equal(2000.00m, sanity.GlBalanceAtCutover);
        Assert.True(sanity.IsBalancedAtCutover);
        Assert.Equal(1, sanity.UnmatchedPreCutoverCount);
        Assert.Equal(2, sanity.UnmatchedPostCutoverCount);

        // 2. 1-click Exclude Pre-Cutover leaves post-cutover lines untouched
        int excluded = _reconciliation.ExcludePreCutoverUnmatchedLines(stmt, new DateTime(2026, 10, 31), AdminActor);
        Assert.Equal(1, excluded);
        Assert.Equal(BankLineMatchStatus.Excluded, stmt.Lines.First(l => l.Date == new DateTime(2026, 10, 30)).MatchStatus);

        // 3. Cross-account mismatch detection + 1-click Reassign & Match
        var nov5Line = stmt.Lines.First(l => l.Date == new DateTime(2026, 11, 5));
        var xCands = _reconciliation.GetCrossAccountCandidatesForLine(stmt, nov5Line, AdminActor);
        Assert.Single(xCands);
        Assert.Equal(wrongAcctJe.Id, xCands[0].Entry.Id);

        _reconciliation.ReassignAccountAndMatchLine(stmt, nov5Line.Id, wrongAcctJe.Id, AdminActor);
        var updatedJe = _db.JournalEntries.GetById(wrongAcctJe.Id)!;
        Assert.Contains(updatedJe.Lines, l => l.GlAccountCode == "1020" && l.Credit == 250.00m);
        Assert.True(updatedJe.IsReconciled);

        // 4. Draft Invoice suggestion + Finalize & Pay + Match
        var nov10Line = stmt.Lines.First(l => l.Date == new DateTime(2026, 11, 10));
        var openCands = _reconciliation.GetOpenDocumentCandidatesForLine(stmt, nov10Line, AdminActor);
        Assert.Single(openCands);
        Assert.True(openCands[0].RequiresFinalizationOrApproval);

        var payJe = _reconciliation.SettleOpenDocumentAndMatchLine(stmt, nov10Line.Id, openCands[0], _accounting, AdminActor);
        Assert.NotNull(payJe);
        Assert.Equal(InvoiceStatus.Paid, _db.Invoices.GetById(draftInv.Id)!.Status);
        Assert.True(nov10Line.IsMatchedOrCategorized);
    }
}
