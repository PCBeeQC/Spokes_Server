namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests;
using Xunit;

public class CompanyBillInboxDeleteAndTriageTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly CompanyBillInboxService _inboxService;

    public CompanyBillInboxDeleteAndTriageTests()
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
    public async Task DeleteEmailAsync_WhenEmailExistsWithDraftBills_DeletesDraftBillsAndEmailFromLocalDb()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Invoice from Acme",
            FromAddress = "billing@acme.com",
            FromName = "Acme Corp",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draft1 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Corp",
            Amount = 150m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };
        var draft2 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Corp",
            Amount = 250m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = new List<string> { draft1.Id, draft2.Id };
        msg.LinkedBillId = draft1.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draft1);
        _db.Bills.Save(draft2);

        // Act
        var result = await _inboxService.DeleteEmailAsync(msg.Id, "test-user", deleteLinkedDrafts: true);

        // Assert
        Assert.True(result.Success);
        Assert.Null(_db.EmailMessages.GetById(msg.Id));
        Assert.DoesNotContain(_db.EmailMessages.GetAllForEmployee(CompanyBillInboxConstants.SystemEmployeeId), m => m.Id == msg.Id);
        Assert.Null(_db.Bills.GetById(draft1.Id));
        Assert.Null(_db.Bills.GetById(draft2.Id));
    }

    [Fact]
    public async Task DeleteEmailAsync_WhenEmailHasApprovedBill_PreservesApprovedBillAndUnlinks()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Approved Invoice & Second Draft",
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Inc",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved
        };

        var approvedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 500m,
            Status = BillStatus.Unpaid,
            SourceEmailId = msg.Id
        };
        var draftBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 100m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = new List<string> { approvedBill.Id, draftBill.Id };
        msg.LinkedBillId = approvedBill.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(approvedBill);
        _db.Bills.Save(draftBill);

        // Act
        var result = await _inboxService.DeleteEmailAsync(msg.Id, "test-user", deleteLinkedDrafts: true);

        // Assert
        Assert.True(result.Success);
        Assert.Null(_db.EmailMessages.GetById(msg.Id));
        Assert.Null(_db.Bills.GetById(draftBill.Id));

        var reloadedApproved = _db.Bills.GetById(approvedBill.Id);
        Assert.NotNull(reloadedApproved);
        Assert.Equal(BillStatus.Unpaid, reloadedApproved.Status);
        Assert.Null(reloadedApproved.SourceEmailId);
    }

    [Fact]
    public async Task BlockSenderDomainAsync_AddsDomainToIgnoredList_AndDeletesEmail()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Spam Invoice",
            FromAddress = "spammer@junk-invoices.com",
            FromName = "Junk Invoices",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(msg);

        // Act
        var result = await _inboxService.BlockSenderDomainAsync("spammer@junk-invoices.com", msg.Id, "test-user");

        // Assert
        Assert.True(result.Success);
        var settings = _inboxService.GetSettings();
        Assert.Contains("junk-invoices.com", settings.IgnoredSenderDomains);
        Assert.Null(_db.EmailMessages.GetById(msg.Id));
    }

    [Fact]
    public void NotifyInboxIngested_TriggersRegisteredEventHandlers()
    {
        // Arrange
        bool eventFired = false;
        Action handler = () => { eventFired = true; };
        CompanyBillInboxService.OnInboxIngested += handler;

        try
        {
            // Act
            CompanyBillInboxService.NotifyInboxIngested();

            // Assert
            Assert.True(eventFired);
        }
        finally
        {
            CompanyBillInboxService.OnInboxIngested -= handler;
        }
    }

    [Fact]
    public void ApproveBillFromEmail_WhenSingleDraft_MarksBillUnpaidAndEmailApproved()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Monthly Server Invoice",
            FromAddress = "billing@cloudhost.com",
            FromName = "CloudHost",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draft = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "CloudHost",
            Amount = 120m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draft.Id];
        msg.LinkedBillId = draft.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draft);

        // Act
        _inboxService.ApproveBillFromEmail(msg.Id, draft.Id, "test-user");

        // Assert
        var updatedBill = _db.Bills.GetById(draft.Id);
        Assert.NotNull(updatedBill);
        Assert.Equal(BillStatus.Unpaid, updatedBill.Status);

        var updatedEmail = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        Assert.NotNull(updatedEmail.ProcessedAt);
        Assert.Equal("test-user", updatedEmail.ProcessedByUserId);
    }

    [Fact]
    public void ApproveBillFromEmail_WhenMultipleDrafts_OnlyMarksEmailApprovedWhenAllDraftsApproved()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Multiple Invoices in Email",
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Inc",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draft1 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 100m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };
        var draft2 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 200m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draft1.Id, draft2.Id];
        msg.LinkedBillId = draft1.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draft1);
        _db.Bills.Save(draft2);

        // Act 1: Approve first draft
        _inboxService.ApproveBillFromEmail(msg.Id, draft1.Id, "test-user");

        // Assert 1: Email still AutoDrafted, pointing to draft2
        var emailAfterFirst = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(emailAfterFirst);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, emailAfterFirst.BillingStatus);
        Assert.Equal(draft2.Id, emailAfterFirst.LinkedBillId);
        Assert.Equal(BillStatus.Unpaid, _db.Bills.GetById(draft1.Id)!.Status);
        Assert.Equal(BillStatus.AwaitingApproval, _db.Bills.GetById(draft2.Id)!.Status);

        // Act 2: Approve second draft
        _inboxService.ApproveBillFromEmail(msg.Id, draft2.Id, "test-user");

        // Assert 2: All approved, email marked Approved
        var emailAfterSecond = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(emailAfterSecond);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, emailAfterSecond.BillingStatus);
        Assert.Equal(BillStatus.Unpaid, _db.Bills.GetById(draft2.Id)!.Status);
    }

    [Fact]
    public void RejectDraftBillFromEmail_WhenSingleDraft_DeletesDraftAndMarksEmailDismissed()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Invalid invoice flyer",
            FromAddress = "flyers@marketing.com",
            FromName = "Flyer Marketing",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draft = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Flyer Marketing",
            Amount = 45m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draft.Id];
        msg.LinkedBillId = draft.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draft);

        // Act
        var options = new RejectDraftBillOptions
        {
            IsDuplicate = false,
            RejectionReason = "Marketing brochure, not an invoice"
        };
        _inboxService.RejectDraftBillFromEmail(msg.Id, draft.Id, "reviewer-1", options);

        // Assert
        Assert.Null(_db.Bills.GetById(draft.Id));

        var updatedEmail = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, updatedEmail.BillingStatus);
        Assert.Null(updatedEmail.LinkedBillId);
        Assert.Empty(updatedEmail.LinkedBillIds);
        Assert.Equal("reviewer-1", updatedEmail.ProcessedByUserId);
        Assert.NotNull(updatedEmail.ProcessedAt);
    }

    [Fact]
    public void RejectDraftBillFromEmail_WhenMultipleDrafts_OnlyDeletesTargetDraftAndLeavesOtherDraftAutoDrafted()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Two Invoices in Email",
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Inc",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draft1 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 100m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };
        var draft2 = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Supplier Inc",
            Amount = 200m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draft1.Id, draft2.Id];
        msg.LinkedBillId = draft1.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draft1);
        _db.Bills.Save(draft2);

        // Act: Reject draft 1
        _inboxService.RejectDraftBillFromEmail(msg.Id, draft1.Id, "reviewer-1");

        // Assert: draft 1 deleted, draft 2 remains, email still AutoDrafted pointing to draft 2
        Assert.Null(_db.Bills.GetById(draft1.Id));
        Assert.NotNull(_db.Bills.GetById(draft2.Id));
        Assert.Equal(BillStatus.AwaitingApproval, _db.Bills.GetById(draft2.Id)!.Status);

        var updatedEmail = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, updatedEmail.BillingStatus);
        Assert.Equal(draft2.Id, updatedEmail.LinkedBillId);
        Assert.Single(updatedEmail.LinkedBillIds);
        Assert.Contains(draft2.Id, updatedEmail.LinkedBillIds);
    }

    [Fact]
    public void RejectDraftBillFromEmail_WhenRejectedAsDuplicate_DeletesDraftLinksExistingBillAndSetsDuplicateDetected()
    {
        // Arrange
        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Hydro-Quebec",
            InvoiceNumber = "HQ-999",
            Amount = 450m,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(existingBill);

        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Duplicate bill from Hydro",
            FromAddress = "facturation@hydro.qc.ca",
            FromName = "Hydro-Quebec",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draftBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Hydro-Quebec",
            InvoiceNumber = "HQ-999",
            Amount = 450m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draftBill.Id];
        msg.LinkedBillId = draftBill.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draftBill);

        // Act: Reject as duplicate of existingBill
        var options = new RejectDraftBillOptions
        {
            IsDuplicate = true,
            DuplicateOfBillId = existingBill.Id
        };
        _inboxService.RejectDraftBillFromEmail(msg.Id, draftBill.Id, "reviewer-1", options);

        // Assert
        Assert.Null(_db.Bills.GetById(draftBill.Id));
        Assert.NotNull(_db.Bills.GetById(existingBill.Id));

        var updatedEmail = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(updatedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, updatedEmail.BillingStatus);
        Assert.Equal(existingBill.Id, updatedEmail.LinkedBillId);
        Assert.Contains(existingBill.Id, updatedEmail.LinkedBillIds);
        Assert.Equal("reviewer-1", updatedEmail.ProcessedByUserId);
    }

    [Fact]
    public void RejectDraftBillFromEmail_WhenLinkedToPurchaseOrder_RecalculatesPoTotalBilled()
    {
        // Arrange
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid().ToString(),
            PoNumber = "PO-2401-001",
            ApplyTax = false,
            LineItems = [new PoItem { Quantity = 1, UnitPrice = 500m }],
            TotalBilled = 350m,
            Status = PurchaseOrderStatus.PartiallyBilled
        };
        _db.PurchaseOrders.Save(po);

        var existingBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            PurchaseOrderId = po.Id,
            SupplierName = "Supplier",
            Amount = 200m,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(existingBill);

        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Second invoice on PO",
            FromAddress = "billing@supplier.com",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted
        };

        var draftBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            PurchaseOrderId = po.Id,
            SupplierName = "Supplier",
            Amount = 150m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = msg.Id
        };

        msg.LinkedBillIds = [draftBill.Id];
        msg.LinkedBillId = draftBill.Id;

        _db.EmailMessages.Save(msg);
        _db.Bills.Save(draftBill);

        // Act
        _inboxService.RejectDraftBillFromEmail(msg.Id, draftBill.Id, "reviewer-1");

        // Assert
        Assert.Null(_db.Bills.GetById(draftBill.Id));
        var reloadedPo = _db.PurchaseOrders.GetById(po.Id);
        Assert.NotNull(reloadedPo);
        Assert.Equal(200m, reloadedPo.TotalBilled);
    }

    [Fact]
    public void RejectDraftBillFromEmail_WhenBillIsAlreadyApproved_ThrowsInvalidOperationException()
    {
        // Arrange
        var approvedBill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            SupplierName = "Acme Corp",
            Amount = 100m,
            Status = BillStatus.Unpaid
        };
        _db.Bills.Save(approvedBill);

        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Invoice",
            FromAddress = "billing@acme.com",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved
        };
        _db.EmailMessages.Save(msg);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            _inboxService.RejectDraftBillFromEmail(msg.Id, approvedBill.Id, "reviewer-1"));
    }

    [Fact]
    public async Task PrepareBillFromEmailAsync_WithValidMatchedAttachmentPath_ReturnsBillWithAttachmentAndExtractedData()
    {
        // Arrange
        var supplier = new Supplier
        {
            Id = "sup-101",
            ContactInfo = new PartyContactInfo
            {
                BusinessName = "Acme Supplies Inc",
                ContactPersonEmail = "invoicing@acme.com"
            }
        };
        _db.Suppliers.Save(supplier);

        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Invoice #INV-999 for order",
            FromAddress = "invoicing@acme.com",
            FromName = "Acme Supplies Inc",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            MatchedAttachmentPath = "/spokesapi/files/bills/upload-1/invoice.pdf",
            MatchedAttachmentName = "invoice.pdf",
            DetectedAmount = 250.75m,
            DetectedCurrency = "USD"
        };
        _db.EmailMessages.Save(msg);

        _mockFileService.Setup(f => f.FileExists("bills", "upload-1", "invoice.pdf")).Returns(true);

        // Act
        var bill = await _inboxService.PrepareBillFromEmailAsync(msg.Id);

        // Assert
        Assert.NotNull(bill);
        Assert.Equal("/spokesapi/files/bills/upload-1/invoice.pdf", bill.AttachmentPath);
        Assert.Equal("invoice.pdf", bill.AttachmentName);
        Assert.Equal("sup-101", bill.SupplierId);
        Assert.Equal("Acme Supplies Inc", bill.SupplierName);
        Assert.Equal(250.75m, bill.Amount);
        Assert.Equal("USD", bill.Currency);
        Assert.Equal(BillStatus.AwaitingApproval, bill.Status);
        Assert.False(bill.AutoIngested);
        Assert.Equal(msg.Id, bill.SourceEmailId);

        // Crucial verification: bill must NOT be persisted in the database until saved
        Assert.Null(_db.Bills.GetById(bill.Id));
    }

    [Fact]
    public async Task PrepareBillFromEmailAsync_WhenMatchedAttachmentPathMissing_RecoversFromEmailAttachmentCache()
    {
        // Arrange
        var attId = Guid.NewGuid().ToString();
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Your bill is attached",
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Co",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            MatchedAttachmentPath = null,
            MatchedAttachmentName = null,
            Attachments =
            [
                new EmailAttachmentMeta
                {
                    Id = attId,
                    FileName = "receipt.pdf",
                    ContentType = "application/pdf"
                }
            ],
            DetectedAmount = 88.50m
        };
        _db.EmailMessages.Save(msg);

        // Create cached attachment file on disk
        var cachedDir = Path.Combine(_testDataPath, CompanyBillInboxConstants.SystemEmployeeId, "Email", "Attachments", msg.Id);
        Directory.CreateDirectory(cachedDir);
        var cachedFile = Path.Combine(cachedDir, attId);
        await File.WriteAllBytesAsync(cachedFile, [0x25, 0x50, 0x44, 0x46]); // "%PDF"

        _mockFileService
            .Setup(f => f.UploadStreamAsync("bills", It.IsAny<string>(), It.IsAny<Stream>(), "receipt.pdf", null, null))
            .ReturnsAsync("/spokesapi/files/bills/recovered-ctx/receipt.pdf");

        // Act
        var bill = await _inboxService.PrepareBillFromEmailAsync(msg.Id);

        // Assert
        Assert.NotNull(bill);
        Assert.Equal("/spokesapi/files/bills/recovered-ctx/receipt.pdf", bill.AttachmentPath);
        Assert.Equal("receipt.pdf", bill.AttachmentName);
        Assert.Equal(88.50m, bill.Amount);

        // Email message should now be updated with the recovered path
        var reloadedMsg = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(reloadedMsg);
        Assert.Equal("/spokesapi/files/bills/recovered-ctx/receipt.pdf", reloadedMsg.MatchedAttachmentPath);
        Assert.Equal("receipt.pdf", reloadedMsg.MatchedAttachmentName);

        // Crucial verification: bill must NOT be persisted in the database until saved
        Assert.Null(_db.Bills.GetById(bill.Id));
    }

    [Fact]
    public async Task PrepareBillFromEmailAsync_WhenNoAttachments_ReturnsBillWithEmptyAttachmentPathWithoutThrowing()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Text only reminder",
            FromAddress = "billing@supplier.com",
            FromName = "Supplier Co",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            DetectedAmount = 45m
        };
        _db.EmailMessages.Save(msg);

        // Act
        var bill = await _inboxService.PrepareBillFromEmailAsync(msg.Id);

        // Assert
        Assert.NotNull(bill);
        Assert.Equal(string.Empty, bill.AttachmentPath);
        Assert.Equal(string.Empty, bill.AttachmentName);
        Assert.Equal(45m, bill.Amount);
        Assert.Null(_db.Bills.GetById(bill.Id));
    }

    [Fact]
    public void UpdateEmailBillingStatus_WhenLinkedBillIdProvided_AddsToLinkedBillIds()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Test",
            FromAddress = "test@example.com",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(msg);

        // Act
        _inboxService.UpdateEmailBillingStatus(msg.Id, CompanyBillInboxConstants.BillingStatus.Approved, "user-42", "bill-abc-123");

        // Assert
        var reloaded = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, reloaded.BillingStatus);
        Assert.Equal("bill-abc-123", reloaded.LinkedBillId);
        Assert.Contains("bill-abc-123", reloaded.LinkedBillIds);
    }

    [Fact]
    public void ConfirmStatement_SetsStatementConfirmedStatus_AndRecordsHumanReviewStep()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Monthly Statement of Account",
            FromAddress = "billing@vendor.com",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Statement
        };
        _db.EmailMessages.Save(msg);

        // Act
        _inboxService.ConfirmStatement(msg.Id, "user-finance-1", "bill-stmt-ref");

        // Assert
        var reloaded = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.StatementConfirmed, reloaded.BillingStatus);
        Assert.Equal("user-finance-1", reloaded.ProcessedByUserId);
        Assert.NotNull(reloaded.ProcessedAt);
        Assert.Equal("bill-stmt-ref", reloaded.LinkedBillId);
        Assert.Contains("bill-stmt-ref", reloaded.LinkedBillIds);

        Assert.NotNull(reloaded.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.StatementConfirmed, reloaded.DecisionLog.FinalStatus);
        var humanStep = Assert.Single(reloaded.DecisionLog.Steps, s => s.StepName == "Human Review — Statement Confirmation");
        Assert.Equal(DecisionStepStatus.Success, humanStep.Status);
        Assert.Equal("Confirmed as Statement of Account by reviewer.", humanStep.Outcome);
        Assert.Contains(humanStep.Details, d => d.Contains("user-finance-1"));
    }

    [Fact]
    public void ConfirmReceipt_SetsReceiptConfirmedStatus_AndRecordsHumanReviewStep()
    {
        // Arrange
        var msg = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            EmployeeId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Payment Receipt #12345",
            FromAddress = "billing@vendor.com",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt
        };
        _db.EmailMessages.Save(msg);

        // Act
        _inboxService.ConfirmReceipt(msg.Id, "user-finance-2");

        // Assert
        var reloaded = _db.EmailMessages.GetById(msg.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed, reloaded.BillingStatus);
        Assert.Equal("user-finance-2", reloaded.ProcessedByUserId);
        Assert.NotNull(reloaded.ProcessedAt);
        Assert.Null(reloaded.LinkedBillId);

        Assert.NotNull(reloaded.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed, reloaded.DecisionLog.FinalStatus);
        var humanStep = Assert.Single(reloaded.DecisionLog.Steps, s => s.StepName == "Human Review — Receipt Confirmation");
        Assert.Equal(DecisionStepStatus.Success, humanStep.Status);
        Assert.Equal("Confirmed as Payment Receipt by reviewer.", humanStep.Outcome);
        Assert.Contains(humanStep.Details, d => d.Contains("user-finance-2"));
    }
}


