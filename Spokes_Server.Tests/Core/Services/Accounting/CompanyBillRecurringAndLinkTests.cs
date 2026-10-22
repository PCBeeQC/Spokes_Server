namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Components.Dialogs.Accounting;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests;
using Xunit;

public class CompanyBillRecurringAndLinkTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly CompanyBillInboxService _inboxService;

    public CompanyBillRecurringAndLinkTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        var encryptionService = _serviceProvider.GetRequiredService<EncryptionService>();

        _mockFileService = new Mock<IFileService>();
        _mockCacheMaintenance = new Mock<IEmailCacheMaintenanceService>();
        _mockCacheMaintenance.Setup(c => c.EmailBodyPath).Returns(_testDataPath);

        var pdfService = new PdfInvoiceInspectionService();
        var poEngine = new SmartPoMatchingEngine(_db);

        _inboxService = new CompanyBillInboxService(
            _db,
            _mockFileService.Object,
            pdfService,
            poEngine,
            encryptionService,
            _mockCacheMaintenance.Object);
    }

    [Fact]
    public void AttachAttachmentToExistingBill_FallsBackToEmailAttachmentWhenPathNull()
    {
        // 1. Arrange a bill with no attachment
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Bell Canada",
            Amount = 125.00m,
            Date = DateTime.Today,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(bill);

        // 2. Arrange an email with an attachment but MatchedAttachmentPath is null
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "Bell Canada",
            FromAddress = "billing@bell.ca",
            Subject = "Your Monthly Statement",
            Date = DateTimeOffset.UtcNow,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            Attachments =
            [
                new EmailAttachmentMeta
                {
                    FileName = "Bell_Invoice_Oct2026.pdf",
                    ContentType = "application/pdf",
                    Size = 1024
                }
            ]
        };
        _db.EmailMessages.Save(email);

        // 3. Act: Link email to bill without explicitly passing attachmentPath
        _inboxService.AttachAttachmentToExistingBill(email.Id, bill.Id, "user1");

        // 4. Assert: Bill should have attachment from email, and email is approved
        var updatedBill = _db.Bills.GetById(bill.Id);
        Assert.NotNull(updatedBill);
        Assert.Equal("Bell_Invoice_Oct2026.pdf", updatedBill.AttachmentName);
        Assert.Contains("Bell_Invoice_Oct2026.pdf", updatedBill.AttachmentPath);

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal(bill.Id, updatedEmail.LinkedBillId);
    }

    [Fact]
    public void AttachAttachmentToExistingRecurringBill_UpdatesAttachmentPathAndMarksApproved()
    {
        // 1. Arrange a recurring expense with a history record
        var expense = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Hydro Electricity",
            Supplier = "Hydro-Québec",
            Amount = 300.00m,
            IsEstimate = false,
            Frequency = RecurringExpenseFrequency.Monthly
        };
        var record = new RecurringBillRecord
        {
            Id = Guid.NewGuid().ToString(),
            Date = DateTime.Today,
            Amount = 312.45m,
            IsPaid = false
        };
        expense.History.Add(record);
        _db.RecurringExpenses.Save(expense);

        // 2. Arrange incoming email
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "Hydro-Québec",
            Subject = "Facture d'électricité",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            Attachments =
            [
                new EmailAttachmentMeta
                {
                    FileName = "Hydro_Oct2026.pdf",
                    ContentType = "application/pdf"
                }
            ]
        };
        _db.EmailMessages.Save(email);

        // 3. Act: Link to recurring record
        _inboxService.AttachAttachmentToExistingRecurringBill(
            email.Id,
            expense.Id,
            record.Id,
            "user1");

        // 4. Assert
        var updatedExpense = _db.RecurringExpenses.GetById(expense.Id);
        Assert.NotNull(updatedExpense);
        var updatedRecord = updatedExpense.History.First(r => r.Id == record.Id);
        Assert.Equal("Hydro_Oct2026.pdf", updatedRecord.AttachmentName);
        Assert.Contains("Hydro_Oct2026.pdf", updatedRecord.AttachmentPath);

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal(expense.Id, updatedEmail.LinkedRecurringExpenseId);
        Assert.Equal(record.Id, updatedEmail.LinkedRecurringRecordId);
    }

    [Fact]
    public void LinkEmailToRecurringExpense_MarksApprovedAndSavesLinks()
    {
        var expense = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AWS Cloud",
            Supplier = "Amazon Web Services",
            Amount = 450.00m,
            IsEstimate = false
        };
        var record = new RecurringBillRecord
        {
            Id = Guid.NewGuid().ToString(),
            Date = DateTime.Today,
            Amount = 480.00m
        };
        expense.History.Add(record);
        _db.RecurringExpenses.Save(expense);

        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "AWS Billing",
            Subject = "Your AWS bill is ready",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(email);

        _inboxService.LinkEmailToRecurringExpense(email.Id, expense.Id, record.Id, "user1");

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal(expense.Id, updatedEmail.LinkedRecurringExpenseId);
        Assert.Equal(record.Id, updatedEmail.LinkedRecurringRecordId);
    }

    [Fact]
    public void ExistingBillItem_ListingIncludesPaidBillsAndAwaitingApproval_SortedMostRecentFirst()
    {
        // 1. Arrange a mix of bills with various dates and statuses
        var olderPaidBill = new Bill
        {
            Id = "b1",
            SupplierName = "Acme Corp",
            Amount = 100m,
            Date = DateTime.Today.AddDays(-30),
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today.AddDays(-20)
        };
        var newerAwaitingApprovalBill = new Bill
        {
            Id = "b2",
            SupplierName = "Beta LLC",
            Amount = 250m,
            Date = DateTime.Today.AddDays(-2),
            Status = BillStatus.AwaitingApproval
        };
        var recurringExpense = new RecurringExpense
        {
            Id = "re1",
            Name = "Internet Service",
            Supplier = "Telco Inc",
            Amount = 90m,
            IsEstimate = false,
            History =
            [
                new RecurringBillRecord
                {
                    Id = "rec1",
                    Date = DateTime.Today.AddDays(-10),
                    Amount = 95m,
                    IsPaid = true
                }
            ]
        };

        _db.Bills.Save(olderPaidBill);
        _db.Bills.Save(newerAwaitingApprovalBill);
        _db.RecurringExpenses.Save(recurringExpense);

        // 2. Build the unified existing bills list as done in LinkBillToEmailDialog
        var items = _db.Bills.GetAll().Where(b => b.Status != BillStatus.Void)
            .Select(b => new LinkBillToEmailDialog.ExistingBillItem
            {
                Id = b.Id,
                SupplierName = b.SupplierName,
                Date = b.Date,
                Amount = b.Amount,
                Status = b.BalanceDue <= 0 || b.Status == BillStatus.Paid ? BillStatus.Paid : b.Status,
                IsRecurring = false
            })
            .Concat(
                _db.RecurringExpenses.GetAll().Where(e => !e.IsEstimate)
                    .SelectMany(re => re.History.Select(rec => new LinkBillToEmailDialog.ExistingBillItem
                    {
                        Id = rec.Id,
                        SupplierName = !string.IsNullOrWhiteSpace(re.Supplier) ? re.Supplier : re.Name,
                        Date = rec.Date,
                        Amount = rec.Amount,
                        Status = rec.IsPaid ? BillStatus.Paid : BillStatus.Unpaid,
                        IsRecurring = true
                    }))
            )
            .OrderByDescending(b => b.Date)
            .ToList();

        // 3. Assert all 3 bills are present
        Assert.Equal(3, items.Count);

        // Confirm ordered from newest to oldest
        Assert.Equal("b2", items[0].Id); // -2 days
        Assert.Equal("rec1", items[1].Id); // -10 days
        Assert.Equal("b1", items[2].Id); // -30 days

        // Confirm Paid bills are included
        Assert.Contains(items, i => i.Status == BillStatus.Paid && i.Id == "b1");
        Assert.Contains(items, i => i.Status == BillStatus.Paid && i.Id == "rec1");

        // Confirm AwaitingApproval is included
        Assert.Contains(items, i => i.Status == BillStatus.AwaitingApproval && i.Id == "b2");
    }

    [Fact]
    public void ExistingBillItem_MultiTermSearch_MatchesSupplierName()
    {
        var items = new[]
        {
            new LinkBillToEmailDialog.ExistingBillItem { SupplierName = "Hydro-Québec", Description = "Monthly Electricity", Amount = 145m },
            new LinkBillToEmailDialog.ExistingBillItem { SupplierName = "Bell Canada", Description = "Fiber 500", Amount = 85m },
            new LinkBillToEmailDialog.ExistingBillItem { SupplierName = "Amazon Web Services", Description = "Cloud Hosting", Amount = 500m }
        };

        // Multi-term search function
        Func<string, LinkBillToEmailDialog.ExistingBillItem[]> search = q =>
        {
            var terms = q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return items.Where(b =>
                terms.All(term =>
                    (!string.IsNullOrEmpty(b.SupplierName) && b.SupplierName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(b.Description) && b.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    b.Amount.ToString("F2").Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
        };

        // Supplier name search
        var bellResults = search("bell");
        Assert.Single(bellResults);
        Assert.Equal("Bell Canada", bellResults[0].SupplierName);

        // Partial match with accent / hyphen
        var hydroResults = search("Hydro");
        Assert.Single(hydroResults);
        Assert.Equal("Hydro-Québec", hydroResults[0].SupplierName);

        // Multi-term match (supplier + amount)
        var awsResults = search("amazon 500");
        Assert.Single(awsResults);
        Assert.Equal("Amazon Web Services", awsResults[0].SupplierName);
    }
}
