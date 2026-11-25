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

        // French words containing "rti"/"cti" substrings (amortissement, actif, actionnaire, portion, articles) MUST NOT match 2165/2175
        Assert.Null(PartyMatchingHelper.SuggestGlAccount("1815", "Amortissement cumulé Amél.locatives", "", spokesAccounts).SuggestedCode);
        Assert.Null(PartyMatchingHelper.SuggestGlAccount("1920", "Actif disponible à la location", "2012", spokesAccounts).SuggestedCode);
        Assert.Null(PartyMatchingHelper.SuggestGlAccount("2630", "Dû actionnaire Jolain", "3141", spokesAccounts).SuggestedCode);
        Assert.Null(PartyMatchingHelper.SuggestGlAccount("3350", "Actions ordinaires", "3500", spokesAccounts).SuggestedCode);
        Assert.Null(PartyMatchingHelper.SuggestGlAccount("5630", "Interet long terme", "9271", spokesAccounts).SuggestedCode);

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

    [Fact]
    public void Migration_AutoLinksProjectsByNormalizedCode_AndMatchesConfiguredFinancialAccounts()
    {
        // Seed existing Spokes projects without spaces in ProjectNumber
        var proj005 = new Spokes_Server.Core.Models.Projects.Project
        {
            Id = "proj-005",
            ProjectNumber = "ABB005",
            Name = "Client Alpha 005"
        };
        var proj035 = new Spokes_Server.Core.Models.Projects.Project
        {
            Id = "proj-035",
            ProjectNumber = "ABB035",
            Name = "Completely Different Name"
        };
        _db.Projects.Save(proj005);
        _db.Projects.Save(proj035);

        // Seed FinancialAccounts & backing GL accounts
        _db.GlAccounts.Save(new GlAccount { Code = "1021", Name = "Compte BNC USD", Type = GlAccountType.Asset, SubType = GlAccountSubType.Bank, Currency = "USD" });
        _db.GlAccounts.Save(new GlAccount { Code = "2121", Name = "MasterCard 1", Type = GlAccountType.Liability, SubType = GlAccountSubType.CreditCard, Currency = "CAD" });
        _db.FinancialAccounts.Save(new FinancialAccount { Name = "Operating Bank Account (Chequing)", AccountType = FinancialAccountType.Chequing, Currency = "CAD", LinkedGlAccountCode = "1020", IsDefaultOperating = true });
        _db.FinancialAccounts.Save(new FinancialAccount { Name = "Compte BNC USD", AccountType = FinancialAccountType.Chequing, Currency = "USD", InstitutionName = "BNC", LinkedGlAccountCode = "1021" });
        _db.FinancialAccounts.Save(new FinancialAccount { Name = "MasterCard 1", AccountType = FinancialAccountType.CreditCard, Currency = "CAD", InstitutionName = "BNC", LinkedGlAccountCode = "2121", IsDefaultCreditCard = true });

        var sageProjects = "33002,3,Project\n\"ABB 005 Client Alpha\",3/30/2015,\n\"ABB 035 Unrelated Sage Title\",5/1/2018,\n";
        var tbCsv = """
            AccountNumber,AccountName,Type,Debit,Credit
            1060,Compte courant canadien BNC,Asset,0,1000.00
            1067,Compte courant $E.-U. BNC (USD),Asset,60.45,0
            1068,Conversion devise BNC US,Asset,21.53,0
            1071,Compte courant canadien Scotia,Asset,0,0
            2134,MasterCard BNC à payer,Liability,0,500.00
            3600,Retained Earnings,Equity,1418.02,0
            """;

        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = new DateTime(2026, 10, 31),
            Files =
            [
                new MigrationUploadedFile { FileName = "projects.txt", Content = Encoding.UTF8.GetBytes(sageProjects) },
                new MigrationUploadedFile { FileName = "tb.csv", Content = Encoding.UTF8.GetBytes(tbCsv), ExplicitKind = MigrationFileKind.TrialBalance }
            ]
        }, AdminActor);

        // Normalized project code matching: "ABB 005" -> "ABB005", "ABB 035" -> "ABB035"
        var m005 = Assert.Single(preview.Projects, p => p.ExtractedCode == "ABB 005");
        Assert.Equal(MigrationProjectAction.LinkExisting, m005.Action);
        Assert.Equal("proj-005", m005.MatchedExistingProjectId);

        var m035 = Assert.Single(preview.Projects, p => p.ExtractedCode == "ABB 035");
        Assert.Equal(MigrationProjectAction.LinkExisting, m035.Action);
        Assert.Equal("proj-035", m035.MatchedExistingProjectId);

        // Smart FinancialAccount auto-mapping
        Assert.Equal("1020", preview.GlMappings.First(g => g.SourceCode == "1060").TargetGlCode);
        Assert.Equal("1021", preview.GlMappings.First(g => g.SourceCode == "1067").TargetGlCode);
        Assert.Equal("2121", preview.GlMappings.First(g => g.SourceCode == "2134").TargetGlCode);
        // FX conversion account & dormant Scotia account must NOT hijack BNC accounts
        Assert.Equal(MigrationGlAction.CreateAccount, preview.GlMappings.First(g => g.SourceCode == "1068").Action);
        Assert.Equal(MigrationGlAction.CreateAccount, preview.GlMappings.First(g => g.SourceCode == "1071").Action);
    }

    [Fact]
    public void BankRules_AutoPostOnlyPostCutoverLines_AndHealthSurfacesCutoverCluesAndSkipsObBillTaxes()
    {
        // Seed Line of Credit GL account & Applied migration batch as of 2026-10-31
        _db.GlAccounts.Save(new GlAccount { Code = "2127", Name = "Marge de crédit BNC", Type = GlAccountType.Liability, SubType = GlAccountSubType.LineOfCredit });
        var obJe = _ledger.SaveManualJournalEntry(new JournalEntry
        {
            Date = new DateTime(2026, 10, 31),
            Description = "Cutover opening bank",
            SourceType = JournalSourceType.OpeningBalance,
            Lines =
            [
                new JournalLine { GlAccountCode = "1020", GlAccountName = "Operating Bank", Debit = 0m, Credit = 100000.00m },
                new JournalLine { GlAccountCode = "3600", GlAccountName = "Retained Earnings", Debit = 100000.00m, Credit = 0m }
            ]
        });
        _db.MigrationBatches.Save(new AccountingMigrationBatch
        {
            BatchNumber = "MIG-2610-0002",
            AsOfDate = new DateTime(2026, 10, 31),
            Status = AccountingMigrationBatchStatus.Applied,
            OpeningTotalDebits = 100000.00m,
            OpeningTotalCredits = 100000.00m,
            CreatedJournalEntryIds = [obJe.Id]
        });

        // Seed an OpeningBalance bill with $0 tax -> must NOT trigger TAX_BILLS_MISSING_SPLIT
        _db.Bills.Save(new Bill
        {
            Id = "ob-bill-1",
            SupplierName = "Legacy Vendor",
            Date = new DateTime(2026, 5, 1),
            Amount = 500m,
            IsOpeningBalance = true,
            Status = BillStatus.Unpaid
        });

        // Create a universal BankRule for credit-line sweeps with AutoPostOnImport = true
        _reconciliation.SaveBankRule(new BankRule
        {
            Name = "BNC Credit Line Sweep",
            Keywords = "DEBOURSE MCR, REMB. MCR",
            Direction = BankRuleDirection.Any,
            Action = BankRuleAction.CategorizeToAccount,
            TargetGlAccountCode = "2127",
            AutoPostOnImport = true
        }, AdminActor);

        var csv = """
            ACME DEMO INC.,ACME DEMO INC,0006-00001-0000001,CAD
            Date,Description,Référence,Autres détails,Contrepartie,Transit émetteur,Débit,Crédit,Solde total
            2026-11-05,DEBOURSE MCR,,,,,0,5000.00,4920.15
            2026-10-30,DEBOURSE MCR,,,,,0,2000.00,-79.85
            """;

        var stmt = _reconciliation.ImportAndAutoMatch(csv, "bnc_mcr.csv", "1020", actor: AdminActor);

        // Post-cutover line (Nov 5) MUST be auto-posted to 2127; Pre-cutover line (Oct 30) MUST remain Unmatched!
        var novLine = stmt.Lines.First(l => l.Date == new DateTime(2026, 11, 5));
        var octLine = stmt.Lines.First(l => l.Date == new DateTime(2026, 10, 30));
        Assert.Equal(BankLineMatchStatus.CategorizedDirect, novLine.MatchStatus);
        Assert.Equal("2127", novLine.DirectGlAccountCode);
        Assert.Equal(BankLineMatchStatus.Unmatched, octLine.MatchStatus);

        // Health Check must skip OB bill from TAX_BILLS_MISSING_SPLIT and flag BANK_CUTOVER_DISCREPANCY with MCR clue
        var healthReport = _health.EvaluateHealth(new DateTime(2026, 11, 24), AdminActor);
        Assert.DoesNotContain(healthReport.Issues, i => i.Code == "TAX_BILLS_MISSING_SPLIT");
        var cutoverIssue = Assert.Single(healthReport.Issues, i => i.Code == "BANK_CUTOVER_DISCREPANCY");
        Assert.Contains("MCR/LOC", cutoverIssue.Details);

        // Excluding pre-cutover line must leave statement balanced and ready to lock
        Assert.Equal(1, _reconciliation.ExcludePreCutoverUnmatchedLines(stmt, new DateTime(2026, 10, 31), AdminActor));
        Assert.Equal(0m, stmt.OutOfBalanceDifference);
        Assert.True(stmt.IsReadyToReconcile);
        _reconciliation.ReconcileAndLockStatement(stmt, "Tester", AdminActor);
        Assert.Equal(BankStatementStatus.Reconciled, stmt.Status);
    }

    [Fact]
    public void Migration_DeltaMode_Clears3900ToZero_AndHealthHandlesCreditNotes_AndBncDetailsParsed()
    {
        // Pre-existing Spokes bill before cutover
        var preBill = new Bill
        {
            SupplierName = "Acme Pre-Cutover Vendor",
            Date = new DateTime(2026, 10, 10),
            Amount = 200m,
            SubTotal = 200m,
            IsTaxExempt = true,
            Status = BillStatus.Approved
        };
        _db.Bills.Save(preBill);
        _ledger.SyncBill(preBill);

        // Seed offsetting normal bill + credit note bill (+100 and -100) to verify Health AP subledger handles credit notes
        var cnBill = new Bill
        {
            SupplierName = "Acme Credit Vendor",
            Date = new DateTime(2026, 10, 15),
            Amount = 100m,
            SubTotal = 100m,
            IsTaxExempt = true,
            IsCreditNote = true,
            Status = BillStatus.Approved
        };
        _db.Bills.Save(cnBill);
        _ledger.SyncBill(cnBill);

        var tbCsv = """
            AccountNumber,AccountName,Type,Debit,Credit
            1020,Operating Bank,Asset,500.00,0
            1200,Accounts Receivable,Asset,400.00,0
            2100,Accounts Payable,Liability,0,150.00
            3600,Retained Earnings,Equity,0,750.00
            """;
        var preview = _migration.BuildPreview(new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.ExistingWorkspaceDelta,
            AsOfDate = new DateTime(2026, 10, 31),
            Files = [new MigrationUploadedFile { FileName = "tb.csv", Content = Encoding.UTF8.GetBytes(tbCsv), ExplicitKind = MigrationFileKind.TrialBalance }]
        }, AdminActor);
        preview.OpenInvoices.Add(new OpenSubledgerMigrationRow { Include = true, IsReceivable = true, DocumentNumber = "INV-OB-1", PartyName = "Client Alpha Inc.", IssueDate = new DateTime(2026, 9, 1), DueDate = new DateTime(2026, 10, 1), OpenAmount = 500m });
        preview.OpenInvoices.Add(new OpenSubledgerMigrationRow { Include = true, IsReceivable = true, DocumentNumber = "CN-OB-1", PartyName = "Client Alpha Inc.", IssueDate = new DateTime(2026, 9, 5), DueDate = new DateTime(2026, 10, 5), OpenAmount = -100m });
        preview.OpenBills.Add(new OpenSubledgerMigrationRow { Include = true, IsReceivable = false, DocumentNumber = "BILL-OB-1", PartyName = "Vendor Beta Inc.", IssueDate = new DateTime(2026, 9, 10), DueDate = new DateTime(2026, 10, 10), OpenAmount = 150m });

        _migration.ExecuteMigration(preview, AdminActor.Id, AdminActor.FullName, AdminActor);
        var tb = _ledger.CalculateTrialBalance(new DateTime(2026, 1, 1), new DateTime(2026, 10, 31), includeZeroAccounts: true);
        Assert.Equal(0m, tb.Rows.FirstOrDefault(r => r.AccountCode == "3900")?.EndingBalance ?? 0m);

        // Verify BNC "Autres détails" column is preserved into Description & Payee
        var bncCsv = """
            ACME DEMO INC.,ACME DEMO INC,0006-00001-0000001,CAD
            Date,Description,Référence,Autres détails,Contrepartie,Transit émetteur,Débit,Crédit,Solde total
            2026-11-05,DPA ENTREPRISE,,BDC DEMO,,,500.00,,1000.00
            """;
        var parsed = new BankStatementImportService().ParseCsv(bncCsv, "bnc.csv", "1020");
        var line = Assert.Single(parsed.Lines);
        Assert.Contains("BDC DEMO", line.Description);
        Assert.Equal("BDC DEMO", line.Payee);

        // Verify Health does not report false AP credit-note drift when GL matches subledger
        var report = _health.EvaluateHealth(new DateTime(2026, 10, 31), AdminActor);
        Assert.DoesNotContain(report.Issues, i => i.Code == "GL_3900_RESIDUAL");
    }
}
