namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MimeKit;
using Spokes_Server.Aggregate;
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

public class CompanyBillDuplicateDetectionTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly CompanyBillInboxService _inboxService;

    public CompanyBillDuplicateDetectionTests()
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

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void FindExistingBill_ExactInvoiceNumber_MatchesManualBill()
    {
        // 1. Manually entered bill from paper mail
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Robot Parts Co",
            InvoiceNumber = "INV-99882",
            Amount = 450.75m,
            Date = DateTime.Today.AddDays(-5),
            Status = BillStatus.Unpaid,
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // 2. Incoming invoice extraction from email
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Robot Parts Co",
            InvoiceNumber = "INV-99882",
            TotalAmount = 450.75m,
            InvoiceDate = DateTime.Today.AddDays(-5)
        };

        var match = _inboxService.FindExistingBill(extracted, null, "Robot Parts Co", "Invoice INV-99882");

        Assert.NotNull(match);
        Assert.Equal(manualBill.Id, match.Id);
    }

    [Fact]
    public void FindExistingBill_NormalizedInvoiceNumber_MatchesManualBill()
    {
        // Manually entered as "INV-2026-004"
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "DigiKey",
            InvoiceNumber = "INV-2026-004",
            Amount = 120.00m,
            Date = DateTime.Today.AddDays(-2),
            Status = BillStatus.AwaitingApproval,
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // Extraction detected numeric suffix "2026004"
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Digi-Key Corporation",
            InvoiceNumber = "2026004",
            TotalAmount = 120.00m,
            InvoiceDate = DateTime.Today.AddDays(-2)
        };

        var match = _inboxService.FindExistingBill(extracted, null, "DigiKey");

        Assert.NotNull(match);
        Assert.Equal(manualBill.Id, match.Id);
    }

    [Fact]
    public void FindExistingBill_SupplierAmountAndDateProximity_MatchesManualBill()
    {
        // Bill entered manually without invoice number (or typo in number)
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Hydro-Québec",
            InvoiceNumber = null,
            Description = "Monthly electricity",
            Amount = 875.40m,
            Date = DateTime.Today.AddDays(-10),
            Status = BillStatus.Unpaid,
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // Incoming PDF has supplier and exact amount
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Hydro Quebec",
            InvoiceNumber = "HQ-88772",
            TotalAmount = 875.40m,
            InvoiceDate = DateTime.Today.AddDays(-10)
        };

        var match = _inboxService.FindExistingBill(extracted, null, "Hydro-Québec");

        Assert.NotNull(match);
        Assert.Equal(manualBill.Id, match.Id);
    }

    [Fact]
    public void FindExistingBill_SubjectMatchesInvoiceNumber_MatchesManualBill()
    {
        // Manual bill
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "McMaster-Carr",
            InvoiceNumber = "MC-88123",
            Amount = 230.15m,
            Date = DateTime.Today.AddDays(-3),
            Status = BillStatus.AwaitingApproval
        };
        _db.Bills.Save(manualBill);

        // Extraction was empty (e.g. non-OCR scanned image)
        var match = _inboxService.FindExistingBill(
            null,
            null,
            "McMaster-Carr",
            emailSubject: "Your order MC-88123 has shipped with invoice");

        Assert.NotNull(match);
        Assert.Equal(manualBill.Id, match.Id);
    }

    [Fact]
    public void FindExistingBill_VoidedBill_IsIgnored()
    {
        var voidedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Supplies",
            InvoiceNumber = "ACME-100",
            Amount = 50.00m,
            Status = BillStatus.Void
        };
        _db.Bills.Save(voidedBill);

        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Acme Supplies",
            InvoiceNumber = "ACME-100",
            TotalAmount = 50.00m
        };

        var match = _inboxService.FindExistingBill(extracted, null, "Acme Supplies");

        Assert.Null(match);
    }

    [Fact]
    public void AttachAttachmentToExistingBill_AttachesPdfAndMarksDealtWith()
    {
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Test Supplier",
            Amount = 200m,
            Status = BillStatus.AwaitingApproval,
            AttachmentPath = ""
        };
        _db.Bills.Save(manualBill);

        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected,
            LinkedBillId = manualBill.Id,
            MatchedAttachmentPath = "/spokesapi/files/bills/123/invoice.pdf",
            MatchedAttachmentName = "invoice.pdf"
        };
        _db.EmailMessages.Save(email);

        _inboxService.AttachAttachmentToExistingBill(email.Id, manualBill.Id, "user-42");

        var updatedBill = _db.Bills.GetById(manualBill.Id);
        var updatedEmail = _db.EmailMessages.GetById(email.Id);

        Assert.NotNull(updatedBill);
        Assert.Equal("/spokesapi/files/bills/123/invoice.pdf", updatedBill.AttachmentPath);
        Assert.Equal("invoice.pdf", updatedBill.AttachmentName);

        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal("user-42", updatedEmail.ProcessedByUserId);
        Assert.NotNull(updatedEmail.ProcessedAt);
    }

    [Fact]
    public async Task CreateDraftAnywayAsync_CreatesNewDraftBillAndUpdatesEmail()
    {
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected,
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Inc",
            Subject = "Invoice 55443",
            Date = DateTimeOffset.UtcNow,
            MatchedAttachmentPath = "/spokesapi/files/bills/ctx9/inv.pdf",
            MatchedAttachmentName = "inv.pdf"
        };
        _db.EmailMessages.Save(email);

        var draft = await _inboxService.CreateDraftAnywayAsync(email.Id, "user-42");

        Assert.NotNull(draft);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.Equal(draft.Id, email.LinkedBillId);
        Assert.Equal("/spokesapi/files/bills/ctx9/inv.pdf", draft.AttachmentPath);
        Assert.True(draft.AutoIngested);

        var savedBill = _db.Bills.GetById(draft.Id);
        Assert.NotNull(savedBill);
    }

    [Fact]
    public void FindExistingBill_ConflictingInvoiceNumbers_HighConfidenceNull_PossibleDuplicateFlagged()
    {
        // Existing bill: INV-04, $300.00, today
        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Apex Robotics",
            InvoiceNumber = "INV-04",
            Amount = 300.00m,
            Date = DateTime.Today,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(existingBill);

        // Incoming invoice: INV-05, $300.00, today (same supplier and amount, but distinct invoice number)
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Apex Robotics",
            InvoiceNumber = "INV-05",
            TotalAmount = 300.00m,
            InvoiceDate = DateTime.Today
        };

        // Pass 1 (high-confidence only): conflicting invoice numbers must not match Step 3 date-window rule
        var strictMatch = _inboxService.FindExistingBillMatch(
            extracted, null, "Apex Robotics", duplicateWindowDays: 20, includePossibleAmountFallback: false);
        Assert.Null(strictMatch);

        // Pass 2 (assistive possible-duplicate fallback): flags same supplier + same amount as a possible duplicate for user review
        var possibleMatch = _inboxService.FindExistingBillMatch(
            extracted, null, "Apex Robotics", duplicateWindowDays: 20, includePossibleAmountFallback: true);
        Assert.NotNull(possibleMatch);
        Assert.Equal(existingBill.Id, possibleMatch.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.SupplierAndAmountPossibleDuplicate, possibleMatch.Method);
        Assert.True(possibleMatch.IsPossibleDuplicate);
    }

    [Fact]
    public void FindExistingBill_BeyondDateWindow_HighConfidenceNull_PossibleDuplicateFlagged()
    {
        // Existing bill: monthly subscription from 45 days ago without invoice #
        var previousBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Cloud Services",
            Amount = 199.00m,
            Date = DateTime.Today.AddDays(-45),
            Status = BillStatus.Paid
        };
        _db.Bills.Save(previousBill);

        // Incoming bill: new month bill, same amount, but today (window is 20 days)
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Cloud Services",
            TotalAmount = 199.00m,
            InvoiceDate = DateTime.Today
        };

        // Pass 1 (high-confidence date-window match): beyond 20 days returns null
        var strictMatch = _inboxService.FindExistingBillMatch(
            extracted, null, "Cloud Services", duplicateWindowDays: 20, includePossibleAmountFallback: false);
        Assert.Null(strictMatch);

        // Pass 2 (assistive possible-duplicate fallback): flags same supplier + same amount as a possible duplicate
        var possibleMatch = _inboxService.FindExistingBillMatch(
            extracted, null, "Cloud Services", duplicateWindowDays: 20, includePossibleAmountFallback: true);
        Assert.NotNull(possibleMatch);
        Assert.Equal(previousBill.Id, possibleMatch.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.SupplierAndAmountPossibleDuplicate, possibleMatch.Method);
        Assert.True(possibleMatch.IsPossibleDuplicate);
    }

    [Fact]
    public void UpdateExistingBillAmount_UpdatesAmountAndMarksEmailApproved()
    {
        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Steel Suppliers Ltd",
            InvoiceNumber = "ST-100",
            Amount = 1000.00m, // initial estimated amount
            Status = BillStatus.AwaitingApproval
        };
        _db.Bills.Save(existingBill);

        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected,
            LinkedBillId = existingBill.Id,
            MatchedAttachmentPath = "/spokesapi/files/bills/100/final_invoice.pdf",
            MatchedAttachmentName = "final_invoice.pdf"
        };
        _db.EmailMessages.Save(email);

        // Final invoice was $1,050.25 (discrepancy resolved by reviewer)
        _inboxService.UpdateExistingBillAmount(email.Id, existingBill.Id, 1050.25m, "reviewer-1");

        var updatedBill = _db.Bills.GetById(existingBill.Id);
        var updatedEmail = _db.EmailMessages.GetById(email.Id);

        Assert.NotNull(updatedBill);
        Assert.Equal(1050.25m, updatedBill.Amount);
        Assert.Equal("/spokesapi/files/bills/100/final_invoice.pdf", updatedBill.AttachmentPath);

        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal("reviewer-1", updatedEmail.ProcessedByUserId);
    }

    [Fact]
    public async Task UploadAttachmentToEmailAsync_AttachesFileAndCreatesDraftBill()
    {
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Inc",
            Subject = "Your monthly invoice is ready on the portal",
            Date = DateTimeOffset.UtcNow
        };
        _db.EmailMessages.Save(email);

        _mockFileService.Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), null, null))
            .ReturnsAsync("/spokesapi/files/bills/upload/Uploaded_Invoice.pdf");

        using var testStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 dummy invoice content"));
        var draft = await _inboxService.UploadAttachmentToEmailAsync(email.Id, testStream, "Uploaded_Invoice.pdf", "user-123");

        Assert.NotNull(draft);
        Assert.Equal("Uploaded_Invoice.pdf", draft.AttachmentName);

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.True(updatedEmail.HasAttachments);
        Assert.Single(updatedEmail.Attachments);
        Assert.Equal("Uploaded_Invoice.pdf", updatedEmail.Attachments[0].FileName);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, updatedEmail.BillingStatus);
        Assert.Equal(draft.Id, updatedEmail.LinkedBillId);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_StatementKeywords_ClassifiesAsStatementWithoutDraft()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Vendor Accounts", "statements@vendor.com"));
        mime.Subject = "Monthly Statement of Account - March 2026";
        mime.Body = new TextPart("plain") { Text = "Please find attached your statement of account for review." };

        var countBefore = _db.Bills.GetAll().Count;
        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, email.BillingStatus);
        Assert.Null(email.LinkedBillId);

        var countAfter = _db.Bills.GetAll().Count;
        Assert.Equal(countBefore, countAfter); // No draft bill should be created
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_ReceiptKeywords_ClassifiesAsReceiptWithoutDraft()
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Online Payment", "noreply@payment-processor.com"));
        mime.Subject = "Reçu de paiement pour votre commande #9910";
        mime.Body = new TextPart("plain") { Text = "Merci pour votre paiement. Ceci est une confirmation de transaction." };

        var countBefore = _db.Bills.GetAll().Count;
        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Receipt, email.BillingStatus);
        Assert.Null(email.LinkedBillId);

        var countAfter = _db.Bills.GetAll().Count;
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_IgnoredDomain_ClassifiesAsDismissed()
    {
        var settings = _inboxService.GetSettings();
        settings.IgnoredSenderDomains.Add("spammer-promo.com");
        _inboxService.SaveSettings(settings);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Marketing Team", "news@spammer-promo.com"));
        mime.Subject = "Exclusive deal on office supplies!";
        mime.Body = new TextPart("plain") { Text = "Special discounts today only." };

        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, email.BillingStatus);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_RecurringExpensePortalNotification_MatchesAndDraftsBill()
    {
        // 1. Setup recurring expense
        var rec = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Bell Business Internet",
            Supplier = "Bell Canada",
            Frequency = RecurringExpenseFrequency.Monthly,
            Amount = 145.50m
        };
        _db.RecurringExpenses.Save(rec);

        var settings = _inboxService.GetSettings();
        settings.AutoMatchRecurringExpenses = true;
        settings.AllowDraftWithoutPdfForRecurring = true;
        _inboxService.SaveSettings(settings);

        // 2. Incoming portal email with amount in body
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Bell Canada", "ebill@bell.ca"));
        mime.Subject = "Your Bell e-bill is now ready";
        mime.Body = new TextPart("plain") { Text = "Your total amount due for this cycle is $145.50 CAD. Please log into MyBell." };

        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.NotNull(email.LinkedBillId);

        var bill = _db.Bills.GetById(email.LinkedBillId);
        Assert.NotNull(bill);
        Assert.Equal(145.50m, bill.Amount);
        Assert.Equal(rec.Id, bill.LinkedRecurringExpenseId);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_PopulatesDecisionLog()
    {
        var settings = _inboxService.GetSettings();
        settings.IgnoredSenderDomains = ["spammer-promo.com"];
        _inboxService.SaveSettings(settings);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Marketing Team", "news@spammer-promo.com"));
        mime.Subject = "Exclusive deal on office supplies!";
        mime.Body = new TextPart("plain") { Text = "Special discounts today only." };

        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.NotNull(email);
        Assert.NotNull(email.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, email.DecisionLog.FinalStatus);
        Assert.Contains("spammer-promo.com", email.DecisionLog.SummaryReason);
        Assert.NotEmpty(email.DecisionLog.Steps);
        Assert.Equal("1", email.DecisionLog.Steps[0].StepNumber);
        Assert.Equal(DecisionStepStatus.Warning, email.DecisionLog.Steps[0].Status);
    }

    [Fact]
    public async Task GenerateDecisionLogAsync_OnDemand_EvaluatesAllSteps()
    {
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "billing@supplier-tech.com",
            FromName = "Supplier Tech Inc",
            Subject = "Invoice #INV-2026-0042 for Software Licenses",
            Snippet = "Total due is $1,250.00 CAD by October 31, 2026.",
            Date = DateTimeOffset.UtcNow,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(email);

        var log = await _inboxService.GenerateDecisionLogAsync(email);

        Assert.NotNull(log);
        Assert.NotEmpty(log.Steps);
        Assert.NotEmpty(log.FieldSources);
        Assert.Contains(log.FieldSources, f => f.FieldName == "Supplier");
        Assert.Contains(log.FieldSources, f => f.FieldName.StartsWith("Invoice"));
        Assert.Contains(log.FieldSources, f => f.FieldName == "Total Amount");
    }

    [Fact]
    public async Task ReprocessEmailAsync_ErasesExistingDraft_AndCreatesNewDraft()
    {
        // 1. Setup recurring expense template so re-processing automatically drafts a bill
        var recurring = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Bell Internet Service",
            Supplier = "Bell Canada",
            Amount = 145.00m
        };
        _db.RecurringExpenses.Save(recurring);

        var settings = _inboxService.GetSettings();
        settings.AutoMatchRecurringExpenses = true;
        settings.AllowDraftWithoutPdfForRecurring = true;
        _inboxService.SaveSettings(settings);

        // 2. Create an initial draft bill linked to the email
        var initialDraftBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Bell Canada",
            Description = "Old Draft Bill",
            Amount = 145.00m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = "email-123"
        };
        _db.Bills.Save(initialDraftBill);

        var email = new EmailMessage
        {
            Id = "email-123",
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "billing@bell.ca",
            FromName = "Bell Canada",
            Subject = "Bell Internet Bill: Your monthly balance of $145.00 is ready",
            Snippet = "Amount due: $145.00",
            Date = DateTimeOffset.UtcNow,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            LinkedBillId = initialDraftBill.Id,
            LinkedBillIds = [initialDraftBill.Id]
        };
        _db.EmailMessages.Save(email);

        // 3. Reprocess
        var result = await _inboxService.ReprocessEmailAsync(email.Id, "test-user", forceEvenIfApproved: false);

        // 4. Assert
        Assert.True(result.Success);
        Assert.False(result.RequiresConfirmation);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, result.FinalStatus);

        // Previous draft should have been deleted
        Assert.Null(_db.Bills.GetById(initialDraftBill.Id));

        // New draft should have been created
        Assert.Single(result.LinkedBills);
        var newDraft = result.LinkedBills[0];
        Assert.NotEqual(initialDraftBill.Id, newDraft.Id);
        Assert.Equal(145.00m, newDraft.Amount);
        Assert.Equal("Bell Canada", newDraft.SupplierName);
        Assert.Equal(BillStatus.AwaitingApproval, newDraft.Status);

        // Email entity updated
        var refreshedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(refreshedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, refreshedEmail.BillingStatus);
        Assert.Equal(newDraft.Id, refreshedEmail.LinkedBillId);
        Assert.Contains(newDraft.Id, refreshedEmail.LinkedBillIds);
    }

    [Fact]
    public async Task ReprocessEmailAsync_WhenApprovedWithoutForce_ReturnsConfirmationRequired()
    {
        // 1. Bill already approved into AP (Status = Unpaid)
        var approvedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "AWS",
            Description = "AWS Cloud Services",
            Amount = 520.00m,
            Status = BillStatus.Unpaid,
            SourceEmailId = "email-aws"
        };
        _db.Bills.Save(approvedBill);

        var email = new EmailMessage
        {
            Id = "email-aws",
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "no-reply@amazon.com",
            FromName = "Amazon Web Services",
            Subject = "AWS Invoice",
            Snippet = "Total: $520.00",
            Date = DateTimeOffset.UtcNow,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved,
            LinkedBillId = approvedBill.Id,
            LinkedBillIds = [approvedBill.Id]
        };
        _db.EmailMessages.Save(email);

        // 2. Reprocess without force
        var result = await _inboxService.ReprocessEmailAsync(email.Id, "test-user", forceEvenIfApproved: false);

        // 3. Assert confirmation required
        Assert.False(result.Success);
        Assert.True(result.RequiresConfirmation);
        Assert.Equal(1, result.ApprovedBillsCount);

        // Approved bill must still exist in DB untouched
        var billInDb = _db.Bills.GetById(approvedBill.Id);
        Assert.NotNull(billInDb);
        Assert.Equal(BillStatus.Unpaid, billInDb.Status);
    }

    [Fact]
    public async Task ReprocessEmailAsync_WhenApprovedWithForce_ErasesApprovedBillAndReprocesses()
    {
        var recurring = new RecurringExpense
        {
            Id = Guid.NewGuid().ToString(),
            Name = "AWS Cloud Services",
            Supplier = "Amazon Web Services",
            Amount = 520.00m
        };
        _db.RecurringExpenses.Save(recurring);

        var settings = _inboxService.GetSettings();
        settings.AutoMatchRecurringExpenses = true;
        settings.AllowDraftWithoutPdfForRecurring = true;
        _inboxService.SaveSettings(settings);

        var approvedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Amazon Web Services",
            Description = "AWS Cloud Services",
            Amount = 520.00m,
            Status = BillStatus.Unpaid,
            SourceEmailId = "email-aws-force"
        };
        _db.Bills.Save(approvedBill);

        var email = new EmailMessage
        {
            Id = "email-aws-force",
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "no-reply@amazon.com",
            FromName = "Amazon Web Services",
            Subject = "AWS Invoice: Total $520.00",
            Snippet = "Total: $520.00",
            Date = DateTimeOffset.UtcNow,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved,
            LinkedBillId = approvedBill.Id,
            LinkedBillIds = [approvedBill.Id]
        };
        _db.EmailMessages.Save(email);

        // Reprocess with forceEvenIfApproved = true
        var result = await _inboxService.ReprocessEmailAsync(email.Id, "test-user", forceEvenIfApproved: true);

        Assert.True(result.Success);
        Assert.False(result.RequiresConfirmation);

        // Previous approved bill should be erased
        Assert.Null(_db.Bills.GetById(approvedBill.Id));

        // New draft bill created
        Assert.Single(result.LinkedBills);
        Assert.Equal(BillStatus.AwaitingApproval, result.LinkedBills[0].Status);
    }

    [Fact]
    public async Task GetOrFetchEmailBodyAsync_WhenLocalEmlExists_ReturnsHtmlBody()
    {
        var email = new EmailMessage
        {
            Id = "msg-body-test",
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "vendor@test.com",
            FromName = "Vendor Test",
            Subject = "Test Email Body",
            Snippet = "Short snippet",
            Date = DateTimeOffset.UtcNow
        };
        _db.EmailMessages.Save(email);

        // Create sample .eml file in the mock cache directory
        var mimeDir = Path.Combine(_testDataPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Mime");
        Directory.CreateDirectory(mimeDir);
        var mimeFile = Path.Combine(mimeDir, $"{email.Id}.eml");

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("Vendor Test", "vendor@test.com"));
        mime.To.Add(new MailboxAddress("Spokes Bills", "bills@spokes.local"));
        mime.Subject = "Test Email Body";
        mime.Body = new TextPart("html") { Text = "<html><body><h1>Invoice Details</h1><p>Please find attached.</p></body></html>" };
        using (var fs = File.Create(mimeFile))
        {
            await mime.WriteToAsync(fs);
        }

        var htmlBody = await _inboxService.GetOrFetchEmailBodyAsync(email);

        Assert.NotNull(htmlBody);
        Assert.Contains("Invoice Details", htmlBody);
        Assert.Contains("Please find attached.", htmlBody);
    }

    [Fact]
    public async Task GetOrFetchEmailBodyAsync_WhenNoEmlOrImap_FallsBackToSnippet()
    {
        var email = new EmailMessage
        {
            Id = "msg-snippet-fallback",
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "vendor@test.com",
            FromName = "Vendor Test",
            Subject = "Test Email Without MIME",
            Snippet = "Plain snippet line 1\nPlain snippet line 2",
            Date = DateTimeOffset.UtcNow
        };
        _db.EmailMessages.Save(email);

        var body = await _inboxService.GetOrFetchEmailBodyAsync(email);

        Assert.NotNull(body);
        Assert.Contains("Plain snippet line 1", body);
        Assert.Contains("Plain snippet line 2", body);
        Assert.StartsWith("<div", body);
    }

    [Fact]
    public async Task CheckInboxAsync_WhenNotConfigured_ExitsImmediatelyWithZeroCount()
    {
        var settings = _inboxService.GetSettings();
        settings.Enabled = false;
        var count = await _inboxService.CheckInboxAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public void FindExistingBillMatch_NormalizedInvoiceNumber_TrimsLeadingZerosAndPrefixes()
    {
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Apex Robotics",
            InvoiceNumber = "INV-0042",
            Amount = 1450.00m,
            Date = DateTime.Today.AddDays(-3),
            Status = BillStatus.Paid,
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // Incoming extraction has "FACT-42"
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Apex Robotics Inc",
            InvoiceNumber = "FACT-42",
            TotalAmount = 1450.00m,
            InvoiceDate = DateTime.Today.AddDays(-3)
        };

        var match = _inboxService.FindExistingBillMatch(extracted, null, "Apex Robotics Inc", "Invoice");
        Assert.NotNull(match);
        Assert.Equal(manualBill.Id, match.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.NormalizedInvoiceNumber, match.Method);
    }

    [Fact]
    public void FindExistingBillMatch_DocumentDateVsEmailDate_DoesNotUseEmailDateForProximity()
    {
        // Manual bill created 100 days ago
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Hydro-Québec",
            InvoiceNumber = null,
            Description = "Monthly electricity",
            Amount = 600.00m,
            Date = DateTime.Today.AddDays(-100),
            Status = BillStatus.Paid,
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // Incoming extraction where document date is null (e.g. could not be read from image)
        var extractedWithoutDate = new ExtractedInvoiceData
        {
            SupplierName = "Hydro-Québec",
            InvoiceNumber = null,
            TotalAmount = 600.00m,
            InvoiceDate = null // Missing invoice date on document!
        };

        // Pass 1 (high-confidence date-window match): proximity check MUST NOT match when InvoiceDate is missing
        var strictMatch = _inboxService.FindExistingBillMatch(
            extractedWithoutDate,
            null,
            "Hydro-Québec",
            emailSubject: "Recent Monthly Notice",
            duplicateWindowDays: 20,
            includePossibleAmountFallback: false);

        Assert.Null(strictMatch);

        // Pass 2 (assistive possible-duplicate fallback): flags same supplier + same amount as a possible duplicate
        var possibleMatch = _inboxService.FindExistingBillMatch(
            extractedWithoutDate,
            null,
            "Hydro-Québec",
            emailSubject: "Recent Monthly Notice",
            duplicateWindowDays: 20,
            includePossibleAmountFallback: true);

        Assert.NotNull(possibleMatch);
        Assert.Equal(manualBill.Id, possibleMatch.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.SupplierAndAmountPossibleDuplicate, possibleMatch.Method);
        Assert.True(possibleMatch.IsPossibleDuplicate);
    }

    [Fact]
    public void ApproveBillFromEmail_PreservesPaidStatus()
    {
        var existingPaidBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Belair Direct",
            InvoiceNumber = "POL-9911",
            Amount = 300.00m,
            Date = DateTime.Today.AddDays(-15),
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today.AddDays(-10)
        };
        _db.Bills.Save(existingPaidBill);

        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected,
            LinkedBillId = existingPaidBill.Id,
            LinkedBillIds = [existingPaidBill.Id]
        };
        _db.EmailMessages.Save(email);

        _inboxService.ApproveBillFromEmail(email.Id, existingPaidBill.Id, "user-123");

        var reloaded = _db.Bills.GetById(existingPaidBill.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(BillStatus.Paid, reloaded.Status);
        Assert.True(reloaded.DatePaid.HasValue);
    }

    [Fact]
    public async Task ScanForDuplicateBillsAsync_IdentifiesExistingManualBillsAndCleansUpDrafts()
    {
        // 1. Existing manual bill entered months ago
        var manualBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Tools Corp",
            InvoiceNumber = "ACME-5544",
            Amount = 1250.00m,
            Date = DateTime.Today.AddDays(-30),
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today.AddDays(-20),
            AutoIngested = false
        };
        _db.Bills.Save(manualBill);

        // 2. An email that auto-drafted a redundant bill before
        var redundantDraft = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Tools Corp",
            InvoiceNumber = "ACME-5544",
            Amount = 1250.00m,
            Status = BillStatus.AwaitingApproval,
            AutoIngested = true
        };
        _db.Bills.Save(redundantDraft);

        var autoDraftedEmail = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            UniqueId = 1001,
            FromAddress = "billing@acmetools.com",
            FromName = "Acme Tools Corp",
            Subject = "Invoice ACME-5544",
            Snippet = "Invoice ACME-5544 Total $1,250.00",
            DetectedAmount = 1250.00m,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            LinkedBillId = redundantDraft.Id,
            LinkedBillIds = [redundantDraft.Id]
        };
        _db.EmailMessages.Save(autoDraftedEmail);

        // 3. An unprocessed email with matching subject / amount
        var unprocessedEmail = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            UniqueId = 1002,
            FromAddress = "billing@acmetools.com",
            FromName = "Acme Tools Corp",
            Subject = "Acme Tools invoice ACME-5544 for your records",
            Snippet = "Thank you for your business",
            DetectedAmount = 1250.00m,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(unprocessedEmail);

        // Run Scan
        var scanResult = await _inboxService.ScanForDuplicateBillsAsync();

        Assert.Equal(2, scanResult.ScannedEmailsCount);
        Assert.Equal(2, scanResult.DuplicatesFoundCount);
        Assert.Equal(1, scanResult.DraftsCleanedUpCount);

        // Redundant draft bill was deleted from database
        Assert.Null(_db.Bills.GetById(redundantDraft.Id));

        // Original manual bill is completely intact with Paid status
        var existingBillAfterScan = _db.Bills.GetById(manualBill.Id);
        Assert.NotNull(existingBillAfterScan);
        Assert.Equal(BillStatus.Paid, existingBillAfterScan.Status);

        // Both emails are now classified as DuplicateDetected and linked to manual bill
        var email1 = _db.EmailMessages.GetById(autoDraftedEmail.Id);
        Assert.NotNull(email1);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, email1.BillingStatus);
        Assert.Equal(manualBill.Id, email1.LinkedBillId);

        var email2 = _db.EmailMessages.GetById(unprocessedEmail.Id);
        Assert.NotNull(email2);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, email2.BillingStatus);
        Assert.Equal(manualBill.Id, email2.LinkedBillId);
    }

    [Fact]
    public async Task IsAccountStatement_InspectionAndReprocess_ClassifiesStatementWithoutDrafting()
    {
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "comptabilite@fournisseur.qc.ca",
            FromName = "Fournisseur Industriel",
            Subject = "Relevé de compte mensuel au 31 décembre",
            Snippet = "Veuillez trouver ci-joint votre état de compte périodique",
            DetectedAmount = 4500.00m,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(email);

        var result = await _inboxService.ReprocessEmailAsync(email.Id, "user-123");

        Assert.True(result.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, result.FinalStatus);
        Assert.Empty(result.LinkedBills);

        var reloaded = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, reloaded.BillingStatus);
    }

    [Fact]
    public void FindExistingBillMatch_StoreTicketPrefixMatch_MatchesPrefixTicketToLongerInvoiceNumber()
    {
        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin-Williams",
            InvoiceNumber = "4262-9",
            Amount = 114.06m,
            Date = DateTime.Today.AddDays(-5),
            Status = BillStatus.Paid,
            AutoIngested = false
        };
        _db.Bills.Save(existingBill);

        // Incoming extraction has the longer terminal sequence "42629130040726"
        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Sherwin-Williams",
            InvoiceNumber = "42629130040726",
            TotalAmount = 114.06m,
            InvoiceDate = DateTime.Today.AddDays(-5)
        };

        var match = _inboxService.FindExistingBillMatch(extracted, null, "Sherwin-Williams", "Facture 4262-9");
        Assert.NotNull(match);
        Assert.Equal(existingBill.Id, match.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.NormalizedInvoiceNumber, match.Method);
    }

    [Fact]
    public void FindExistingBillMatch_PaidBillMatchedAsDuplicate()
    {
        var paidBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin-Williams",
            InvoiceNumber = "42470130040726",
            Amount = 114.06m,
            Date = DateTime.Today.AddDays(-2),
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today.AddDays(-1),
            AutoIngested = false
        };
        _db.Bills.Save(paidBill);

        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Sherwin-Williams",
            InvoiceNumber = "42470130040726",
            TotalAmount = 114.06m,
            InvoiceDate = DateTime.Today.AddDays(-2)
        };

        var match = _inboxService.FindExistingBillMatch(extracted, null, "Sherwin-Williams", "Invoice Reminder");
        Assert.NotNull(match);
        Assert.Equal(paidBill.Id, match.Bill.Id);
        Assert.Equal(BillStatus.Paid, match.Bill.Status);
    }

    [Fact]
    public void ReopenBillForFailedPayment_RevertsStatusAndPayment()
    {
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin-Williams",
            InvoiceNumber = "42629130040726",
            Amount = 114.06m,
            Date = DateTime.Today.AddDays(-10),
            Status = BillStatus.Paid,
            DatePaid = DateTime.Today.AddDays(-5),
            Payments = [
                new PaymentRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    Amount = 114.06m,
                    Date = DateTime.Today.AddDays(-5),
                    Method = "PAD"
                }
            ]
        };
        _db.Bills.Save(bill);

        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.DuplicateDetected,
            LinkedBillId = bill.Id,
            LinkedBillIds = [bill.Id],
            MatchedAttachmentPath = "files/bills/reminder.pdf",
            MatchedAttachmentName = "reminder.pdf"
        };
        _db.EmailMessages.Save(email);

        _inboxService.ReopenBillForFailedPayment(email.Id, bill.Id, "user-456");

        var updatedBill = _db.Bills.GetById(bill.Id);
        Assert.NotNull(updatedBill);
        Assert.Equal(BillStatus.Unpaid, updatedBill.Status);
        Assert.Null(updatedBill.DatePaid);
        Assert.Empty(updatedBill.Payments);
        Assert.Equal("files/bills/reminder.pdf", updatedBill.AttachmentPath);

        var updatedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.Equal("user-456", updatedEmail.ProcessedByUserId);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_AutomatedOutOfTheBoxClassification_WorksWithoutSettingsKeywords()
    {
        // Even with zero manual keywords configured in settings, UniversalDocumentClassifier works out of the box
        var settings = _inboxService.GetSettings();
        settings.StatementKeywords.Clear();
        settings.ReceiptKeywords.Clear();
        settings.CreditNoteKeywords.Clear();
        _inboxService.SaveSettings(settings);

        // 1. German Kontoauszug (Statement of Account)
        var stmtMime = new MimeMessage();
        stmtMime.From.Add(new MailboxAddress("Buchhaltung", "buchhaltung@vendor.de"));
        stmtMime.Subject = "Ihr monatlicher Kontoauszug Oktober 2026";
        stmtMime.Body = new TextPart("plain") { Text = "Anbei erhalten Sie Ihren aktuellen Kontoauszug." };

        var stmtEmail = await _inboxService.ProcessMimeMessageAsync(stmtMime);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, stmtEmail.BillingStatus);
        Assert.NotNull(stmtEmail.DecisionLog);
        Assert.Contains("Kontoauszug", stmtEmail.DecisionLog.SummaryReason, StringComparison.OrdinalIgnoreCase);

        // 2. Payment Receipt vs "Due upon receipt"
        var receiptMime = new MimeMessage();
        receiptMime.From.Add(new MailboxAddress("Stripe", "receipts@stripe.com"));
        receiptMime.Subject = "Payment Receipt for Order #4421";
        receiptMime.Body = new TextPart("plain") { Text = "Thank you for your payment. Paid by Visa ending in 4242." };

        var receiptEmail = await _inboxService.ProcessMimeMessageAsync(receiptMime);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Receipt, receiptEmail.BillingStatus);
        Assert.NotNull(receiptEmail.DecisionLog);
        Assert.Contains("Payment Receipt", receiptEmail.DecisionLog.SummaryReason, StringComparison.OrdinalIgnoreCase);

        // 3. "Due upon receipt" must NOT be flagged as a payment receipt
        var dueUponReceiptMime = new MimeMessage();
        dueUponReceiptMime.From.Add(new MailboxAddress("Supplier", "billing@supplier.com"));
        dueUponReceiptMime.Subject = "Invoice #9012 - Due upon receipt";
        dueUponReceiptMime.Body = new TextPart("plain") { Text = "Terms: Payable upon receipt of invoice." };

        var dueEmail = await _inboxService.ProcessMimeMessageAsync(dueUponReceiptMime);
        Assert.NotEqual(CompanyBillInboxConstants.BillingStatus.Receipt, dueEmail.BillingStatus);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_MatchesSupplierByTaxId_AndNormalizesCreditNote()
    {
        var supplier = new Supplier
        {
            Id = Guid.NewGuid().ToString(),
            ContactInfo = new Spokes_Server.Core.Models.Core.PartyContactInfo
            {
                BusinessName = "EuroTech Industrial Solutions",
                TaxId = "DE 998 877 665"
            }
        };
        _db.Suppliers.Save(supplier);

        _mockFileService.Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), null, null))
            .ReturnsAsync("/spokesapi/files/bills/cn/credit_note.xml");

        const string creditNoteXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rsm:CrossIndustryInvoice xmlns:rsm=""urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100""
                          xmlns:ram=""urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100""
                          xmlns:udt=""urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"">
    <rsm:ExchangedDocument>
        <ram:ID>CN-2026-501</ram:ID>
        <ram:TypeCode>381</ram:TypeCode>
        <ram:IssueDateTime>
            <udt:DateTimeString format=""102"">20261010</udt:DateTimeString>
        </ram:IssueDateTime>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>Different Legal Header Name GmbH</ram:Name>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID=""VA"">DE998877665</ram:ID>
                </ram:SpecifiedTaxRegistration>
            </ram:SellerTradeParty>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:TaxBasisTotalAmount>100.00</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount>19.00</ram:TaxTotalAmount>
                <ram:GrandTotalAmount>119.00</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
    </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>";

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("NoReply Gateway", "noreply@einvoice-portal.org"));
        mime.Subject = "Electronic Document CN-2026-501";

        var bodyBuilder = new BodyBuilder { TextBody = "Attached electronic document." };
        bodyBuilder.Attachments.Add("credit_note.xml", System.Text.Encoding.UTF8.GetBytes(creditNoteXml), new ContentType("application", "xml"));
        mime.Body = bodyBuilder.ToMessageBody();

        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.NotNull(email.LinkedBillId);

        var draftBill = _db.Bills.GetById(email.LinkedBillId);
        Assert.NotNull(draftBill);
        Assert.Equal(supplier.Id, draftBill.SupplierId);
        Assert.Equal(supplier.Name, draftBill.SupplierName);
        Assert.True(draftBill.IsCreditNote);
        Assert.Equal("EUR", draftBill.Currency);
        Assert.Equal(-119.00m, draftBill.Amount);
        Assert.Equal(-100.00m, draftBill.SubTotal);
        Assert.Equal(-19.00m, draftBill.TaxAmount);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_MultiAttachmentSherwinWilliamsScenario_MatchesExistingBillsAndDraftsRemainingWithWarning()
    {
        // 1. Registered supplier in Spokes
        var supplier = new Supplier
        {
            Id = "60c2cbda-78aa-475e-a885-d366d1c33402",
            ContactInfo = new Spokes_Server.Core.Models.Core.PartyContactInfo
            {
                BusinessName = "Sherwin William"
            },
            Contacts =
            [
                new ContactPerson
                {
                    Name = "Billing",
                    Email = "billing@sherwin.com"
                }
            ]
        };
        _db.Suppliers.Save(supplier);

        // 2. Two pre-existing bills in Spokes for Sherwin William (entered manually without the 14-digit PDF invoice number, and outside the 20-day window)
        var existingBill114 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierId = supplier.Id,
            SupplierName = "Sherwin William",
            InvoiceNumber = null,
            Description = "Peinture atelier",
            Amount = 114.06m,
            SubTotal = 99.20m,
            TaxAmount = 14.86m,
            Date = new DateTime(2026, 9, 10),
            Status = BillStatus.Paid,
            AutoIngested = false
        };
        _db.Bills.Save(existingBill114);

        var existingBill396 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierId = supplier.Id,
            SupplierName = "Sherwin William",
            InvoiceNumber = null,
            Description = "Peinture industrielle projet",
            Amount = 396.56m,
            SubTotal = 344.91m,
            TaxAmount = 51.65m,
            Date = new DateTime(2026, 9, 12),
            Status = BillStatus.Unpaid,
            AutoIngested = false
        };
        _db.Bills.Save(existingBill396);

        _mockFileService.Setup(f => f.UploadStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), null, null))
            .ReturnsAsync((string ctx, string _, Stream _, string fn, string? _, string? _) => $"/spokesapi/files/bills/{ctx}/{fn}");

        // 3. Incoming email from Theodore.Eloundou@sherwin.com with 3 invoice attachments ($114.06, $396.56, $114.06)
        static string BuildCiiXml(string invoiceNo, string dateStr, string total, string subTotal, string tax) => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<rsm:CrossIndustryInvoice xmlns:rsm=""urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100""
                          xmlns:ram=""urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100""
                          xmlns:udt=""urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"">
    <rsm:ExchangedDocument>
        <ram:ID>{invoiceNo}</ram:ID>
        <ram:TypeCode>380</ram:TypeCode>
        <ram:IssueDateTime>
            <udt:DateTimeString format=""102"">{dateStr}</udt:DateTimeString>
        </ram:IssueDateTime>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>Sherwin-Williams</ram:Name>
            </ram:SellerTradeParty>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>CAD</ram:InvoiceCurrencyCode>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:TaxBasisTotalAmount>{subTotal}</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount>{tax}</ram:TaxTotalAmount>
                <ram:GrandTotalAmount>{total}</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
    </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>";

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("", "Theodore.Eloundou@sherwin.com"));
        mime.Subject = "Invoices from Sherwin-Williams";

        var bodyBuilder = new BodyBuilder { TextBody = "Please find attached your 3 invoices." };
        bodyBuilder.Attachments.Add("793938382_42629_07152026.xml", System.Text.Encoding.UTF8.GetBytes(BuildCiiXml("42629130040726", "20260715", "114.06", "99.20", "14.86")), new ContentType("application", "xml"));
        bodyBuilder.Attachments.Add("793938382_39742_07132026.xml", System.Text.Encoding.UTF8.GetBytes(BuildCiiXml("39742199340726", "20260713", "396.56", "344.91", "51.65")), new ContentType("application", "xml"));
        bodyBuilder.Attachments.Add("793938382_42470_07142026.xml", System.Text.Encoding.UTF8.GetBytes(BuildCiiXml("42470130040726", "20260714", "114.06", "99.20", "14.86")), new ContentType("application", "xml"));
        mime.Body = bodyBuilder.ToMessageBody();

        var email = await _inboxService.ProcessMimeMessageAsync(mime);

        // Verify:
        // - 2 existing bills ($114.06 and $396.56) are matched 1-to-1 as possible duplicates
        // - Only 1 new draft bill is created (for the second $114.06 invoice: 42470130040726)
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.Equal(3, email.LinkedBillIds.Count);
        Assert.Contains(existingBill114.Id, email.LinkedBillIds);
        Assert.Contains(existingBill396.Id, email.LinkedBillIds);

        var createdDrafts = _db.Bills.GetAll().Where(b => b.SourceEmailId == email.Id).ToList();
        Assert.Single(createdDrafts);
        var singleDraft = createdDrafts[0];
        Assert.Equal(114.06m, singleDraft.Amount);
        Assert.Equal("42470130040726", singleDraft.InvoiceNumber);

        // Verify DuplicateMatches in DecisionLog has the 2 matched attachments
        Assert.NotNull(email.DecisionLog);
        Assert.Equal(2, email.DecisionLog.DuplicateMatches.Count);
        Assert.All(email.DecisionLog.DuplicateMatches, m => Assert.True(m.IsPossibleDuplicate));
        Assert.Contains(email.DecisionLog.DuplicateMatches, m => m.ExistingBillId == existingBill114.Id && m.ExtractedInvoiceNumber == "42629130040726");
        Assert.Contains(email.DecisionLog.DuplicateMatches, m => m.ExistingBillId == existingBill396.Id && m.ExtractedInvoiceNumber == "39742199340726");

        // Verify GetSameSupplierSameAmountBills flags the remaining $114.06 draft as also sharing its amount with existingBill114
        var sameAmountCandidates = _inboxService.GetSameSupplierSameAmountBills(singleDraft);
        Assert.Contains(sameAmountCandidates, b => b.Id == existingBill114.Id);

        // Verify user can override a flagged duplicate and create a separate draft anyway for that specific attachment
        var dupSummary114 = email.DecisionLog.DuplicateMatches.First(m => m.ExistingBillId == existingBill114.Id);
        var overriddenDraft = await _inboxService.CreateDraftAnywayAsync(
            email.Id,
            "user-1",
            replaceDuplicateBillId: existingBill114.Id,
            specificAttachmentPath: dupSummary114.AttachmentPath,
            specificAttachmentName: dupSummary114.AttachmentFileName);

        Assert.NotNull(overriddenDraft);
        Assert.Equal(114.06m, overriddenDraft.Amount);
        Assert.Equal(email.Id, overriddenDraft.SourceEmailId);

        var updatedEmail = _db.EmailMessages.GetById(email.Id)!;
        Assert.DoesNotContain(existingBill114.Id, updatedEmail.LinkedBillIds);
        Assert.Contains(overriddenDraft.Id, updatedEmail.LinkedBillIds);
        Assert.Single(updatedEmail.DecisionLog!.DuplicateMatches);
    }

    [Fact]
    public void FindExistingBillMatch_InvoiceNumberInDescriptionTokens_MatchesPrefixStoreTicket()
    {
        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin William",
            InvoiceNumber = null,
            Description = "Facture #4262-9 peinture",
            Amount = 114.06m,
            Date = DateTime.Today.AddDays(-60),
            Status = BillStatus.Paid
        };
        _db.Bills.Save(existingBill);

        var extracted = new ExtractedInvoiceData
        {
            SupplierName = "Sherwin William",
            InvoiceNumber = "42629130040726",
            TotalAmount = 114.06m,
            InvoiceDate = DateTime.Today.AddDays(-60)
        };

        var match = _inboxService.FindExistingBillMatch(extracted, null, "Sherwin William", includePossibleAmountFallback: false);
        Assert.NotNull(match);
        Assert.Equal(existingBill.Id, match.Bill.Id);
        Assert.Equal(ExistingBillMatchMethod.DescriptionInvoiceNumber, match.Method);
        Assert.False(match.IsPossibleDuplicate);
    }

    [Fact]
    public async Task ReprocessEmailAsync_DoesNotDeletePreExistingBillsLinkedAsDuplicates()
    {
        var preExistingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin William",
            InvoiceNumber = "SW-100",
            Amount = 396.56m,
            Status = BillStatus.AwaitingApproval, // Even if AwaitingApproval, it was NOT created by this email!
            SourceEmailId = null,
            AutoIngested = false
        };
        _db.Bills.Save(preExistingBill);

        var emailId = Guid.NewGuid().ToString();
        var autoDraftedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Sherwin William",
            InvoiceNumber = "SW-101",
            Amount = 114.06m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = emailId,
            AutoIngested = true
        };
        _db.Bills.Save(autoDraftedBill);

        var email = new EmailMessage
        {
            Id = emailId,
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            FromAddress = "billing@sherwin.com",
            FromName = "Sherwin William",
            Subject = "Invoice SW-100",
            Snippet = "Invoice SW-100 Total $396.56",
            DetectedAmount = 396.56m,
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            LinkedBillId = autoDraftedBill.Id,
            LinkedBillIds = [autoDraftedBill.Id, preExistingBill.Id]
        };
        _db.EmailMessages.Save(email);

        var result = await _inboxService.ReprocessEmailAsync(email.Id, "user-123");

        Assert.True(result.Success);
        // Auto-drafted bill created by this email was cleaned up during re-evaluation
        Assert.Null(_db.Bills.GetById(autoDraftedBill.Id));
        // Pre-existing bill was NEVER deleted and was re-matched as duplicate
        Assert.NotNull(_db.Bills.GetById(preExistingBill.Id));
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, result.FinalStatus);
    }
}
