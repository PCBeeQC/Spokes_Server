namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.HR;
using Xunit;

[Collection("JurisdictionStaticState")]
public class BalanceWriteOffTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledgerService;
    private readonly IAccountingService _accountingService;
    private readonly IBankReconciliationService _bankReconciliationService;
    private readonly ITaxRemittanceService _taxRemittanceService;
    private readonly IAccountantExportService _exportService;
    private readonly IJurisdictionTemplateService _templateService;

    private static readonly Employee AdminActor = new()
    {
        Id = "wo-admin",
        FirstName = "WriteOff",
        LastName = "Admin",
        IsActive = true,
        IsAdmin = true
    };

    private static readonly Employee ReadOnlyEmployee = new()
    {
        Id = "wo-readonly",
        FirstName = "Read",
        LastName = "Only",
        IsActive = true,
        Permissions = [AppPermissions.Projects.View]
    };

    public BalanceWriteOffTests()
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
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IAccountantExportService, AccountantExportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _templateService = _serviceProvider.GetRequiredService<IJurisdictionTemplateService>();
        _ledgerService = _serviceProvider.GetRequiredService<ILedgerService>();
        _taxRemittanceService = _serviceProvider.GetRequiredService<ITaxRemittanceService>();
        _bankReconciliationService = _serviceProvider.GetRequiredService<IBankReconciliationService>();
        _accountingService = _serviceProvider.GetRequiredService<IAccountingService>();
        _exportService = _serviceProvider.GetRequiredService<IAccountantExportService>();

        var profile = _db.CompanyProfile.Get();
        var completedStatus = profile.ProjectStatuses.FirstOrDefault(s => s.Name == ProjectStatus.Completed);
        if (completedStatus != null)
        {
            completedStatus.SystemMapping = SystemStatusMappings.Completed;
            _db.CompanyProfile.Save(profile);
        }
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void RecordInvoiceWriteOff_SmallRoundingBalanceOnPartiallyPaidInvoice_SetsPaidClearsArWithoutTouchingBank()
    {
        // Arrange: Invoice with $0.06 remaining rounding balance after payment
        var invoice = new Invoice
        {
            Id = "inv-rounding-01",
            InvoiceNumber = "F-2606-0051",
            Date = new DateTime(2026, 6, 29),
            DueDate = new DateTime(2026, 7, 3),
            Status = InvoiceStatus.Partial,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Transfert", Quantity = 1m, UnitPrice = 14946.81m }],
            Payments =
            [
                new PaymentRecord
                {
                    Id = "pay-real-1",
                    Date = new DateTime(2026, 7, 9),
                    Amount = 14946.75m,
                    Method = "Wire Transfer",
                    BankGlAccountCode = "1020"
                }
            ]
        };
        _db.Invoices.Save(invoice);
        _ledgerService.SyncInvoice(invoice, "Cosinus");

        Assert.Equal(0.06m, invoice.BalanceDue);

        // Act: Close out the $0.06 residual balance
        _accountingService.RecordInvoiceWriteOff(
            invoice,
            new BalanceWriteOff
            {
                Date = new DateTime(2026, 7, 10),
                Amount = 0.06m,
                Reason = "Past rounding discrepancy ($0.06)"
            },
            "Accountant",
            AdminActor);

        // Assert: Domain state & persistence
        var reloaded = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Paid, reloaded.Status);
        Assert.Equal(14946.75m, reloaded.AmountPaid);
        Assert.Equal(0.06m, reloaded.AmountWrittenOff);
        Assert.Equal(0.00m, reloaded.BalanceDue);
        Assert.Single(reloaded.WriteOffs);
        Assert.Contains(reloaded.History, h => h.Action == DocumentHistoryActions.BalanceWrittenOff && h.Reason == "Past rounding discrepancy ($0.06)");

        // Assert: General Ledger — AR (1200) is $0.00, Bank (1020) is strictly $14,946.75, 5860 is $0.06
        var writeOffJe = _db.JournalEntries.GetBySource(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:{reloaded.WriteOffs[0].Id}");
        Assert.NotNull(writeOffJe);
        Assert.True(writeOffJe!.IsBalanced);
        Assert.DoesNotContain(writeOffJe.Lines, l => l.GlAccountCode == "1020" || l.GlAccountCode == "2120" || l.GlAccountCode == "1010");
        Assert.Contains(writeOffJe.Lines, l => l.GlAccountCode == "5860" && l.Debit == 0.06m && l.Credit == 0m);
        Assert.Contains(writeOffJe.Lines, l => l.GlAccountCode == "1200" && l.Credit == 0.06m && l.Debit == 0m);

        var tb = _ledgerService.CalculateTrialBalance(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        Assert.True(tb.IsBalanced);
        var bankRow = tb.Rows.Single(r => r.AccountCode == "1020");
        Assert.Equal(14946.75m, bankRow.EndingBalance);
        var arRow = tb.Rows.FirstOrDefault(r => r.AccountCode == "1200");
        Assert.Equal(0m, arRow?.EndingBalance ?? 0m);
        var writeOffRow = tb.Rows.Single(r => r.AccountCode == "5860");
        Assert.Equal(0.06m, writeOffRow.EndingBalance);

        // Assert: Bank reconciliation never proposes the write-off JE
        var stmt = new BankStatement
        {
            GlAccountCode = "1020",
            Lines = [new BankTransactionLine { Date = new DateTime(2026, 7, 10), Amount = 0.06m, Description = "Noise" }]
        };
        var candidates = _bankReconciliationService.GetCandidatesForLine(stmt, stmt.Lines[0], AdminActor);
        Assert.DoesNotContain(candidates, c => c.Entry.Id == writeOffJe.Id);

        // Assert: Aged AR CSV excludes the closed invoice
        var agedCsv = _exportService.GenerateAgedReceivablesAndPayablesCsv(new DateTime(2026, 7, 15), AdminActor);
        Assert.DoesNotContain("F-2606-0051", agedCsv);
    }

    [Fact]
    public void RecordInvoiceWriteOff_UnpaidInvoiceFullWriteOff_SetsWrittenOffAndPreservesSalesTaxAndCommissionSettlement()
    {
        var project = new Project
        {
            Id = "proj-wo-comm",
            Name = "WriteOff Project",
            Status = ProjectStatus.Completed,
            Commissions = [new CommissionBeneficiary { Name = "Sales Rep", Percentage = 10m, CommissionBasis = CommissionBasisTypes.Revenue }]
        };
        _db.Projects.Save(project);

        var invoice = new Invoice
        {
            Id = "inv-baddebt-01",
            ProjectId = project.Id,
            InvoiceNumber = "INV-BAD-01",
            Date = new DateTime(2026, 5, 10),
            DueDate = new DateTime(2026, 6, 10),
            Status = InvoiceStatus.Final,
            TaxRate = FinancialCalculationEngine.QuebecCombinedTaxRate,
            LineItems = [new InvoiceLine { Description = "Consulting", Quantity = 1m, UnitPrice = 1000m }]
        };
        _db.Invoices.Save(invoice);
        _ledgerService.SyncInvoice(invoice, "Insolvent Co");

        // Act: 100% write-off (zero cash payments ever recorded)
        _accountingService.RecordInvoiceWriteOff(
            invoice,
            new BalanceWriteOff
            {
                Date = new DateTime(2026, 6, 20),
                Amount = invoice.GrandTotal,
                Reason = "Client insolvent — bad debt write-off"
            },
            "Controller",
            AdminActor);

        var reloaded = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.WrittenOff, reloaded.Status);
        Assert.Null(reloaded.DatePaid);
        Assert.Equal(0m, reloaded.AmountPaid);
        Assert.Equal(invoice.GrandTotal, reloaded.AmountWrittenOff);
        Assert.Equal(0m, reloaded.BalanceDue);

        // Original invoice JE stays posted AND write-off JE clears AR to $0
        Assert.NotNull(_db.JournalEntries.GetBySource(JournalSourceType.Invoice, invoice.Id));
        var tb = _ledgerService.CalculateTrialBalance(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        Assert.True(tb.IsBalanced);
        Assert.Equal(0m, tb.Rows.FirstOrDefault(r => r.AccountCode == "1200")?.EndingBalance ?? 0m);

        // Sales tax filing still includes the invoiced sales tax
        var taxSummary = _taxRemittanceService.CalculateTaxPeriodSummary(new DateTime(2026, 4, 1), new DateTime(2026, 6, 30));
        Assert.Equal(1000m, taxSummary.TotalSalesBeforeTax);
        Assert.True(taxSummary.TotalTaxCollected > 0m);

        // Commission evaluation treats WrittenOff as settled
        Assert.NotEmpty(_db.CommissionLedgerRecords.GetByProject(project.Id));
    }

    [Fact]
    public void RecordInvoiceWriteOff_PartialWriteOff_KeepsPartialStatusAndReversesCleanly()
    {
        var invoice = new Invoice
        {
            Id = "inv-partial-wo",
            InvoiceNumber = "INV-PART-01",
            Date = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 5, 1),
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Design", Quantity = 1m, UnitPrice = 500m }]
        };
        _db.Invoices.Save(invoice);

        // Partial courtesy discount write-off to custom non-cash GL account 5600
        _accountingService.RecordInvoiceWriteOff(
            invoice,
            new BalanceWriteOff
            {
                Date = new DateTime(2026, 4, 10),
                Amount = 50m,
                GlAccountCode = "5600",
                Reason = "Courtesy discount"
            },
            "Manager",
            AdminActor);

        var afterPartial = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Partial, afterPartial.Status);
        Assert.Equal(50m, afterPartial.AmountWrittenOff);
        Assert.Equal(450m, afterPartial.BalanceDue);

        var je = _db.JournalEntries.GetBySource(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:{afterPartial.WriteOffs[0].Id}");
        Assert.NotNull(je);
        Assert.Contains(je!.Lines, l => l.GlAccountCode == "5600" && l.Debit == 50m);

        // Reverse write-off
        _accountingService.DeleteInvoiceWriteOff(afterPartial, afterPartial.WriteOffs[0].Id, "Manager", "Entered on wrong invoice", AdminActor);

        var afterReverse = _db.Invoices.GetById(invoice.Id)!;
        Assert.Equal(InvoiceStatus.Final, afterReverse.Status);
        Assert.Empty(afterReverse.WriteOffs);
        Assert.Equal(500m, afterReverse.BalanceDue);
        Assert.Null(_db.JournalEntries.GetBySource(JournalSourceType.InvoiceWriteOff, $"{invoice.Id}:{je.SourceId!.Split(':')[1]}"));
    }

    [Fact]
    public void RecordBillWriteOff_AndRecurringBillWriteOff_ClearApWithoutTouchingBank_AndSupportReversal()
    {
        // 1. Supplier Bill with $0.01 remaining balance
        var bill = new Bill
        {
            Id = "bill-wo-01",
            SupplierName = "Acme Parts",
            Description = "Parts Order",
            InvoiceNumber = "SUP-991",
            Date = new DateTime(2026, 5, 1),
            DueDate = new DateTime(2026, 5, 31),
            SubTotal = 100.01m,
            Amount = 100.01m,
            IsTaxExempt = true,
            Status = BillStatus.Unpaid,
            Payments = [new PaymentRecord { Id = "bpay-1", Date = new DateTime(2026, 5, 15), Amount = 100.00m, Method = "ACH", BankGlAccountCode = "1020" }]
        };
        _accountingService.SaveBill(bill, "Admin", AdminActor);

        _accountingService.RecordBillWriteOff(
            bill,
            new BalanceWriteOff { Date = new DateTime(2026, 5, 16), Amount = 0.01m, Reason = "1 cent supplier rounding" },
            "Admin",
            AdminActor);

        var reloadedBill = _db.Bills.GetById(bill.Id)!;
        Assert.Equal(BillStatus.Paid, reloadedBill.Status);
        Assert.Equal(100.00m, reloadedBill.AmountPaid);
        Assert.Equal(0.01m, reloadedBill.AmountWrittenOff);
        Assert.Equal(0m, reloadedBill.BalanceDue);

        var billWoJe = _db.JournalEntries.GetBySource(JournalSourceType.BillWriteOff, $"{bill.Id}:{reloadedBill.WriteOffs[0].Id}");
        Assert.NotNull(billWoJe);
        Assert.True(billWoJe!.IsBalanced);
        Assert.Contains(billWoJe.Lines, l => l.GlAccountCode == "2100" && l.Debit == 0.01m);
        Assert.Contains(billWoJe.Lines, l => l.GlAccountCode == "5860" && l.Credit == 0.01m);
        Assert.DoesNotContain(billWoJe.Lines, l => l.GlAccountCode == "1020");

        // Cannot void bill while write-off exists
        Assert.Throws<InvalidOperationException>(() => _accountingService.VoidBill(reloadedBill, "Duplicate", "Admin", AdminActor));

        // Reversal blocked if JE is locked
        billWoJe.IsLocked = true;
        _db.JournalEntries.Save(billWoJe);
        Assert.Throws<InvalidOperationException>(() => _accountingService.DeleteBillWriteOff(reloadedBill, reloadedBill.WriteOffs[0].Id, "Admin", "Oops", AdminActor));

        // Unlock & reverse cleanly
        billWoJe.IsLocked = false;
        _db.JournalEntries.Save(billWoJe);
        _accountingService.DeleteBillWriteOff(reloadedBill, reloadedBill.WriteOffs[0].Id, "Admin", "Reopening balance", AdminActor);
        var reopenedBill = _db.Bills.GetById(bill.Id)!;
        Assert.Equal(BillStatus.Unpaid, reopenedBill.Status);
        Assert.Equal(0.01m, reopenedBill.BalanceDue);

        // 2. Recurring Bill full write-off ($0 paid -> Written Off)
        var recRecord = new RecurringBillRecord
        {
            Id = "rec-hist-1",
            Date = new DateTime(2026, 6, 1),
            Amount = 45.00m,
            IsTaxExempt = true
        };
        var profile = new RecurringExpense
        {
            Id = "rec-prof-1",
            Name = "Old Subscription",
            Supplier = "SaaS Vendor",
            IsTaxExempt = true,
            History = [recRecord]
        };
        _accountingService.SaveRecurringExpense(profile, AdminActor);

        _accountingService.RecordRecurringBillWriteOff(
            profile,
            recRecord,
            new BalanceWriteOff { Date = new DateTime(2026, 6, 5), Amount = 45.00m, Reason = "Vendor waived final invoice" },
            "Admin",
            AdminActor);

        var reloadedProfile = _db.RecurringExpenses.GetById(profile.Id)!;
        var reloadedRec = reloadedProfile.History.Single();
        Assert.Equal(BillStatus.WrittenOff, reloadedRec.Status);
        Assert.Equal(0m, reloadedRec.AmountPaid);
        Assert.Equal(45.00m, reloadedRec.AmountWrittenOff);
        Assert.Equal(0m, reloadedRec.BalanceDue);

        var recWoJe = _db.JournalEntries.GetBySource(JournalSourceType.RecurringWriteOff, $"{profile.Id}:{recRecord.Id}:{reloadedRec.WriteOffs[0].Id}");
        Assert.NotNull(recWoJe);
        Assert.True(recWoJe!.IsBalanced);
        Assert.Contains(recWoJe.Lines, l => l.GlAccountCode == "2100" && l.Debit == 45.00m);
        Assert.Contains(recWoJe.Lines, l => l.GlAccountCode == "5860" && l.Credit == 45.00m);

        // RebuildOperationalLedger deterministically reconstructs write-off JEs
        _db.JournalEntries.Delete(recWoJe.Id);
        _ledgerService.RebuildOperationalLedger();
        Assert.NotNull(_db.JournalEntries.GetBySource(JournalSourceType.RecurringWriteOff, $"{profile.Id}:{recRecord.Id}:{reloadedRec.WriteOffs[0].Id}"));
    }

    [Fact]
    public void WriteOffValidation_RejectsBankAccountsExceedingBalanceBlankReasonAndUnauthorizedActors()
    {
        var invoice = new Invoice
        {
            Id = "inv-val-01",
            InvoiceNumber = "INV-VAL-01",
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Quantity = 1m, UnitPrice = 10.00m }]
        };
        _db.Invoices.Save(invoice);

        // Reject Bank GL account (1020)
        Assert.Throws<InvalidOperationException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 1.00m, Reason = "Test", GlAccountCode = "1020" },
                "Admin",
                AdminActor));

        // Reject Credit Card GL account (2120)
        Assert.Throws<InvalidOperationException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 1.00m, Reason = "Test", GlAccountCode = "2120" },
                "Admin",
                AdminActor));

        // Reject amount exceeding remaining balance
        Assert.Throws<InvalidOperationException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 10.01m, Reason = "Over" },
                "Admin",
                AdminActor));

        // Reject blank reason
        Assert.Throws<ArgumentException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 1.00m, Reason = "   " },
                "Admin",
                AdminActor));

        // Reject unauthorized actor
        Assert.Throws<UnauthorizedAccessException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 1.00m, Reason = "Unauthorized" },
                "ReadOnly",
                ReadOnlyEmployee));

        // Reject Draft invoice
        invoice.Status = InvoiceStatus.Draft;
        Assert.Throws<InvalidOperationException>(() =>
            _accountingService.RecordInvoiceWriteOff(
                invoice,
                new BalanceWriteOff { Amount = 1.00m, Reason = "Draft" },
                "Admin",
                AdminActor));
    }

    [Fact]
    public void JurisdictionTemplateService_EnsureBadDebtsAndWriteOffsSeeded_BackfillsExistingChartOfAccounts()
    {
        // Delete 5860 to simulate an older workspace seeded before this role existed
        var existing5860 = _db.GlAccounts.GetByCode("5860");
        if (existing5860 != null)
        {
            _db.GlAccounts.Delete(existing5860.Id);
        }
        Assert.Null(_db.GlAccounts.GetByCode("5860"));

        var seeded = _templateService.EnsureBadDebtsAndWriteOffsSeeded();
        Assert.Equal("5860", seeded.Code);
        Assert.Equal(GlSystemRole.BadDebtsAndWriteOffs, seeded.SystemRole);
        Assert.NotNull(_db.GlAccounts.GetByCode("5860"));
    }

    [Fact]
    public void Models_DeserializeLegacyJsonWithoutWriteOffs_AndDuplicateAsDraftClearsWriteOffs()
    {
        var legacyInvoiceJson = "{\"Id\":\"leg-inv\",\"InvoiceNumber\":\"INV-OLD\",\"TaxRate\":0,\"LineItems\":[{\"Quantity\":1,\"UnitPrice\":100}],\"Payments\":[{\"Amount\":99.99}]}";
        var inv = JsonSerializer.Deserialize<Invoice>(legacyInvoiceJson)!;
        Assert.NotNull(inv.WriteOffs);
        Assert.Empty(inv.WriteOffs);
        Assert.Equal(0.01m, inv.BalanceDue);

        inv.WriteOffs.Add(new BalanceWriteOff { Amount = 0.01m, Reason = "Rounding" });
        Assert.Equal(0m, inv.BalanceDue);

        var copy = inv.DuplicateAsDraft("INV-COPY", "Tester");
        Assert.Empty(copy.WriteOffs);
        Assert.Equal(100m, copy.BalanceDue);
    }
}
