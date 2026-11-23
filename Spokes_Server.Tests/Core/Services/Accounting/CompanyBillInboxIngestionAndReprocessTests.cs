namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests;
using Xunit;

public class CompanyBillInboxIngestionAndReprocessTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly CompanyBillInboxService _inboxService;
    private readonly Dictionary<string, byte[]> _storedFiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Employee AdminActor = new()
    {
        Id = "admin-bill-test",
        FirstName = "Admin",
        LastName = "Tester",
        IsAdmin = true,
        IsActive = true
    };

    private static readonly Employee ManageBillsOnlyActor = new()
    {
        Id = "ap-clerk-test",
        FirstName = "AP",
        LastName = "Clerk",
        IsAdmin = false,
        IsActive = true,
        Permissions = [AppPermissions.Purchases.ManageBills]
    };

    private static readonly Employee UnprivilegedActor = new()
    {
        Id = "regular-emp-test",
        FirstName = "Regular",
        LastName = "User",
        IsAdmin = false,
        IsActive = true,
        Permissions = []
    };

    public CompanyBillInboxIngestionAndReprocessTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        var encryptionService = _serviceProvider.GetRequiredService<EncryptionService>();

        _mockFileService = new Mock<IFileService>();
        _mockFileService
            .Setup(f => f.UploadStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync((string category, string entityId, Stream stream, string fileName, string? _, string? __) =>
            {
                using var copy = new MemoryStream();
                if (stream.CanSeek) stream.Position = 0;
                stream.CopyTo(copy);
                var path = $"/spokesapi/files/{category}/{entityId}/{fileName}";
                _storedFiles[$"{category}/{entityId}/{fileName}"] = copy.ToArray();
                return path;
            });

        _mockFileService
            .Setup(f => f.OpenRead(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .Returns((string category, string entityId, string fileName, string? _) =>
            {
                if (_storedFiles.TryGetValue($"{category}/{entityId}/{fileName}", out var bytes))
                {
                    return new MemoryStream(bytes);
                }
                return null!;
            });

        _mockCacheMaintenance = new Mock<IEmailCacheMaintenanceService>();
        _mockCacheMaintenance.Setup(c => c.EmailBodyPath).Returns(_testDataPath);

        _inboxService = new CompanyBillInboxService(
            _db,
            _mockFileService.Object,
            new PdfInvoiceInspectionService(),
            new SmartPoMatchingEngine(_db),
            encryptionService,
            _mockCacheMaintenance.Object);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private void UpdateInboxSettings(Action<CompanyBillInboxSettings> configure)
    {
        var profile = _db.CompanyProfile.GetOrCreate();
        profile.Accounting.BillInbox ??= new CompanyBillInboxSettings();
        configure(profile.Accounting.BillInbox);
        _db.CompanyProfile.Save(profile);
    }

    private static MimeMessage BuildEmail(
        string fromEmail,
        string fromName,
        string subject,
        string bodyText,
        params (string FileName, byte[] Content, string MediaType, string MediaSubType)[] attachments)
    {
        var message = new MimeMessage();
        message.MessageId = $"<{Guid.NewGuid():N}@test.local>";
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(new MailboxAddress("AP", "ap@company.local"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        var builder = new BodyBuilder { TextBody = bodyText };
        foreach (var (fileName, content, mediaType, mediaSubType) in attachments)
        {
            builder.Attachments.Add(fileName, content, new ContentType(mediaType, mediaSubType));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static byte[] BuildInvoicePdf(
        string supplierName,
        string invoiceNumber,
        decimal subTotal,
        decimal tps,
        decimal tvq,
        decimal total,
        string? poNumber = null)
    {
        var lines = new List<string>
        {
            "FACTURE",
            supplierName,
            $"Facture No: {invoiceNumber}",
            "Date: 2026-11-10"
        };
        if (!string.IsNullOrWhiteSpace(poNumber))
        {
            lines.Add($"Purchase Order: {poNumber}");
        }
        lines.Add($"Sous-total: {subTotal:0.00} $");
        lines.Add($"TPS: {tps:0.00} $");
        lines.Add($"TVQ: {tvq:0.00} $");
        lines.Add($"Total: {total:0.00} $");

        return PdfTestBuilderHelper.BuildTextPdf([.. lines]);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_ActionablePdfWithMatchingPo_AutoDraftsBillAndCachesMimeAndAttachments()
    {
        var supplier = new Supplier
        {
            Id = "sup-acier",
            ContactInfo = new PartyContactInfo
            {
                BusinessName = "Acier Quebec Inc.",
                ContactPersonEmail = "billing@acierquebec.test"
            }
        };
        _db.Suppliers.Save(supplier);

        var po = new PurchaseOrder
        {
            Id = "po-2026-088",
            PoNumber = "PO-2026-088",
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "Acier Quebec Inc." },
            LineItems = [new PoItem { Description = "Raw Steel Plate", Quantity = 1m, UnitPrice = 100.00m }]
        };
        _db.PurchaseOrders.Save(po);

        var pdfBytes = BuildInvoicePdf("Acier Quebec Inc.", "INV-2026-801", 100.00m, 5.00m, 9.98m, 114.98m, "PO-2026-088");
        var mime = BuildEmail(
            "billing@acierquebec.test",
            "Acier Quebec Inc.",
            "Facture INV-2026-801",
            "Please find attached invoice INV-2026-801.",
            ("INV-2026-801.pdf", pdfBytes, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 101);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.Equal(114.98m, email.DetectedAmount);
        Assert.True(email.HasAttachments);
        Assert.Single(email.Attachments);
        Assert.Equal("INV-2026-801.pdf", email.MatchedAttachmentName);
        Assert.Equal(po.Id, email.LinkedPoId);
        Assert.NotNull(email.LinkedBillId);
        Assert.Single(email.LinkedBillIds);

        var draftBill = _db.Bills.GetById(email.LinkedBillId!);
        Assert.NotNull(draftBill);
        Assert.True(draftBill.AutoIngested);
        Assert.Equal(BillStatus.AwaitingApproval, draftBill.Status);
        Assert.Equal("INV-2026-801", draftBill.InvoiceNumber);
        Assert.Equal(114.98m, draftBill.Amount);
        Assert.Equal(100.00m, draftBill.SubTotal);
        Assert.Equal(5.00m, draftBill.TpsAmount);
        Assert.Equal(9.98m, draftBill.TvqAmount);
        Assert.Equal(po.Id, draftBill.PurchaseOrderId);
        Assert.Equal(supplier.Id, draftBill.SupplierId);
        Assert.Equal(email.Id, draftBill.SourceEmailId);

        Assert.NotNull(email.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.DecisionLog.FinalStatus);

        var mimeCacheFile = Path.Combine(_testDataPath, email.AccountId, "Mime", $"{email.Id}.eml");
        Assert.True(File.Exists(mimeCacheFile));
        var cachedAttachmentFile = Path.Combine(_testDataPath, email.AccountId, "Attachments", email.Id, email.Attachments[0].Id);
        Assert.True(File.Exists(cachedAttachmentFile));
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_StatementPdf_SetsStatementStatusAndSuppressesDraft()
    {
        var stmtPdf = PdfTestBuilderHelper.BuildTextPdf(
            "Acier Quebec Inc.",
            "Account Summary",
            "Statement Date: 2026-11-01",
            "INV-1001   250.00",
            "INV-1002   250.00",
            "Total: 500.00");

        var mime = BuildEmail(
            "ar@acierquebec.test",
            "Acier Quebec Inc.",
            "Monthly Account Update",
            "Here is your monthly summary.",
            ("account_doc.pdf", stmtPdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 102);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, email.BillingStatus);
        Assert.Equal(500.00m, email.DetectedAmount);
        Assert.Empty(_db.Bills.GetAll());
        Assert.NotNull(email.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, email.DecisionLog.FinalStatus);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_PaymentReceiptEmail_SetsReceiptStatusAndSuppressesDraft()
    {
        var invoicePdf = BuildInvoicePdf("Client Corp", "INV-REF-22", 300.00m, 15.00m, 29.93m, 344.93m);
        var mime = BuildEmail(
            "notify@payments.test",
            "Interac Notification",
            "Interac e-Transfer: Client Corp sent you a payment",
            "Your funds have been automatically deposited into your business bank account.",
            ("reference.pdf", invoicePdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 103);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Receipt, email.BillingStatus);
        Assert.Empty(_db.Bills.GetAll());
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_DuplicateInvoice_SetsDuplicateDetectedAndLinksExistingBill()
    {
        var supplier = new Supplier
        {
            Id = "sup-dup",
            ContactInfo = new PartyContactInfo { BusinessName = "Bolt Supply Co." }
        };
        _db.Suppliers.Save(supplier);

        var existingBill = new Bill
        {
            Id = "bill-existing-900",
            SupplierId = supplier.Id,
            SupplierName = "Bolt Supply Co.",
            InvoiceNumber = "INV-2026-900",
            Amount = 200.00m,
            Date = new DateTime(2026, 11, 8),
            Status = BillStatus.Unpaid,
            SourceEmailId = "earlier-email"
        };
        _db.Bills.Save(existingBill);

        var pdf = BuildInvoicePdf("Bolt Supply Co.", "INV-2026-900", 173.95m, 8.70m, 17.35m, 200.00m);
        var mime = BuildEmail(
            "billing@boltsupply.test",
            "Bolt Supply Co.",
            "Reminder: Invoice INV-2026-900",
            "Attached copy of invoice INV-2026-900.",
            ("INV-2026-900.pdf", pdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 104);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, email.BillingStatus);
        Assert.Equal(existingBill.Id, email.LinkedBillId);
        Assert.Single(email.LinkedBillIds, existingBill.Id);
        Assert.Single(_db.Bills.GetAll());
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_IgnoredSenderDomain_DismissesWithoutUploadingAttachments()
    {
        UpdateInboxSettings(s => s.IgnoredSenderDomains = ["noise-vendor.test"]);

        var pdf = BuildInvoicePdf("Noise Vendor", "INV-NOISE-1", 50.00m, 2.50m, 4.99m, 57.49m);
        var mime = BuildEmail(
            "mailer@noise-vendor.test",
            "Noise Vendor",
            "Promo Invoice",
            "Ignore me",
            ("promo.pdf", pdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 105);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, email.BillingStatus);
        Assert.NotNull(email.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, email.DecisionLog.FinalStatus);
        _mockFileService.Verify(
            f => f.UploadStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_MultiplePdfInvoices_CreatesNewDraftAndLinksDuplicate()
    {
        var supplier = new Supplier
        {
            Id = "sup-multi",
            ContactInfo = new PartyContactInfo { BusinessName = "Multi Hardware Inc." }
        };
        _db.Suppliers.Save(supplier);

        var existingBill = new Bill
        {
            Id = "bill-existing-split-2",
            SupplierId = supplier.Id,
            SupplierName = "Multi Hardware Inc.",
            InvoiceNumber = "INV-SPLIT-2",
            Amount = 229.95m,
            Date = new DateTime(2026, 11, 9),
            Status = BillStatus.Unpaid,
            SourceEmailId = "earlier-email"
        };
        _db.Bills.Save(existingBill);

        var pdfNew = BuildInvoicePdf("Multi Hardware Inc.", "INV-SPLIT-1", 100.00m, 5.00m, 9.98m, 114.98m);
        var pdfDup = BuildInvoicePdf("Multi Hardware Inc.", "INV-SPLIT-2", 200.00m, 10.00m, 19.95m, 229.95m);

        var mime = BuildEmail(
            "ar@multihardware.test",
            "Multi Hardware Inc.",
            "Two Invoices Attached",
            "Please process both attached invoices.",
            ("INV-SPLIT-1.pdf", pdfNew, "application", "pdf"),
            ("INV-SPLIT-2.pdf", pdfDup, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 106);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        Assert.Equal(2, email.LinkedBillIds.Count);
        Assert.Contains(existingBill.Id, email.LinkedBillIds);

        var allBills = _db.Bills.GetAll();
        Assert.Equal(2, allBills.Count);
        var createdDraft = allBills.Single(b => b.Id != existingBill.Id);
        Assert.Equal("INV-SPLIT-1", createdDraft.InvoiceNumber);
        Assert.Equal(114.98m, createdDraft.Amount);
        Assert.Equal(createdDraft.Id, email.LinkedBillId);
    }

    [Fact]
    public async Task ProcessMimeMessageAsync_RecurringExpenseWithoutPdf_AutoDraftsBillWhenConfigured()
    {
        UpdateInboxSettings(s =>
        {
            s.AutoMatchRecurringExpenses = true;
            s.AllowDraftWithoutPdfForRecurring = true;
        });

        var recurring = new RecurringExpense
        {
            Id = "rec-isp",
            Name = "Office Fiber Internet",
            Supplier = "ISP Telecom",
            Amount = 89.95m
        };
        _db.RecurringExpenses.Save(recurring);

        var mime = BuildEmail(
            "billing@isptelecom.test",
            "ISP Telecom",
            "Monthly Fiber Statement Ready",
            "Your automatic portal statement total is $89.95 for this billing cycle.");

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 107);

        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, email.BillingStatus);
        var draft = _db.Bills.GetById(email.LinkedBillId!);
        Assert.NotNull(draft);
        Assert.Equal(89.95m, draft.Amount);
        Assert.Equal(recurring.Id, draft.LinkedRecurringExpenseId);
        Assert.Equal(BillStatus.AwaitingApproval, draft.Status);
    }

    [Fact]
    public async Task ReprocessEmailAsync_EnforcesPermissionsAndRequiresConfirmationForApprovedBills()
    {
        var pdf = BuildInvoicePdf("Precision Tools Ltd.", "INV-REPROC-1", 100.00m, 5.00m, 9.98m, 114.98m);
        var mime = BuildEmail(
            "ar@precisiontools.test",
            "Precision Tools Ltd.",
            "Invoice INV-REPROC-1",
            "Attached invoice INV-REPROC-1.",
            ("INV-REPROC-1.pdf", pdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 201);
        Assert.NotNull(email);
        var originalBill = _db.Bills.GetById(email.LinkedBillId!)!;
        originalBill.Status = BillStatus.Approved;
        _db.Bills.Save(originalBill);

        // 1. Missing Purchases.ManageBills permission
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _inboxService.ReprocessEmailAsync(email.Id, UnprivilegedActor.Id, actor: UnprivilegedActor));

        // 2. Non-forced reprocess on approved bill asks for confirmation without deleting bill
        var confirmResult = await _inboxService.ReprocessEmailAsync(
            email.Id, ManageBillsOnlyActor.Id, forceEvenIfApproved: false, actor: ManageBillsOnlyActor);
        Assert.False(confirmResult.Success);
        Assert.True(confirmResult.RequiresConfirmation);
        Assert.Equal(1, confirmResult.ApprovedBillsCount);
        Assert.NotNull(_db.Bills.GetById(originalBill.Id));

        // 3. Forced reprocess requires Purchases.ApproveBills
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _inboxService.ReprocessEmailAsync(email.Id, ManageBillsOnlyActor.Id, forceEvenIfApproved: true, actor: ManageBillsOnlyActor));

        // 4. Forced reprocess by Admin replaces approved bill cleanly
        var forcedResult = await _inboxService.ReprocessEmailAsync(
            email.Id, AdminActor.Id, forceEvenIfApproved: true, actor: AdminActor);
        Assert.True(forcedResult.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, forcedResult.FinalStatus);
        Assert.Null(_db.Bills.GetById(originalBill.Id));

        var recreatedBill = Assert.Single(_db.Bills.GetAll());
        Assert.NotEqual(originalBill.Id, recreatedBill.Id);
        Assert.Equal("INV-REPROC-1", recreatedBill.InvoiceNumber);
        Assert.Equal(114.98m, recreatedBill.Amount);

        var updatedEmail = _db.EmailMessages.GetById(email.AccountId, email.Id)!;
        Assert.Equal(AdminActor.Id, updatedEmail.ProcessedByUserId);
        Assert.NotNull(updatedEmail.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, updatedEmail.DecisionLog.FinalStatus);
    }

    [Fact]
    public async Task ReprocessEmailAsync_ErasesOwnedDraftsWhilePreservingPreExistingDuplicateBills()
    {
        var supplier = new Supplier
        {
            Id = "sup-keep-dup",
            ContactInfo = new PartyContactInfo { BusinessName = "Omega Metals Inc." }
        };
        _db.Suppliers.Save(supplier);

        var preExistingBill = new Bill
        {
            Id = "bill-preexisting-omega",
            SupplierId = supplier.Id,
            SupplierName = "Omega Metals Inc.",
            InvoiceNumber = "INV-OMEGA-55",
            Amount = 250.00m,
            Date = new DateTime(2026, 11, 5),
            Status = BillStatus.Unpaid,
            SourceEmailId = "different-email-id"
        };
        _db.Bills.Save(preExistingBill);

        var pdf = BuildInvoicePdf("Omega Metals Inc.", "INV-OMEGA-55", 217.44m, 10.87m, 21.69m, 250.00m);
        var mime = BuildEmail(
            "ap@omegametals.test",
            "Omega Metals Inc.",
            "Invoice INV-OMEGA-55",
            "Attached invoice INV-OMEGA-55.",
            ("INV-OMEGA-55.pdf", pdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 202);
        Assert.NotNull(email);

        // Attach an owned stale draft alongside the pre-existing duplicate bill link
        var ownedStaleDraft = new Bill
        {
            Id = "bill-stale-owned",
            SupplierId = supplier.Id,
            SupplierName = "Omega Metals Inc.",
            Amount = 10.00m,
            Status = BillStatus.AwaitingApproval,
            SourceEmailId = email.Id
        };
        _db.Bills.Save(ownedStaleDraft);
        email.LinkedBillIds.Add(ownedStaleDraft.Id);
        _db.EmailMessages.Save(email);

        var result = await _inboxService.ReprocessEmailAsync(email.Id, AdminActor.Id, actor: AdminActor);

        Assert.True(result.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, result.FinalStatus);
        Assert.Null(_db.Bills.GetById(ownedStaleDraft.Id));
        Assert.NotNull(_db.Bills.GetById(preExistingBill.Id));
        var linked = Assert.Single(result.LinkedBills);
        Assert.Equal(preExistingBill.Id, linked.Id);
    }

    [Fact]
    public async Task ReprocessEmailAsync_FallsBackToMatchedAttachmentPath_WhenMimeMissingAndUniqueIdZero()
    {
        var pdfBytes = BuildInvoicePdf("Fallback Mfg", "INV-FALLBACK-77", 300.00m, 15.00m, 29.93m, 344.93m);
        _storedFiles["bills/ctx-fallback/inv-fallback.pdf"] = pdfBytes;

        var email = new EmailMessage
        {
            Id = "msg-fallback-only",
            AccountId = CompanyBillInboxConstants.SystemEmployeeId,
            FolderPath = "INBOX",
            UniqueId = 0,
            FromAddress = "billing@fallbackmfg.test",
            FromName = "Fallback Mfg",
            Subject = "Invoice INV-FALLBACK-77",
            Snippet = "Attached PDF",
            Date = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero),
            MatchedAttachmentPath = "/spokesapi/files/bills/ctx-fallback/inv-fallback.pdf",
            MatchedAttachmentName = "inv-fallback.pdf",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed
        };
        _db.EmailMessages.Save(email);

        var result = await _inboxService.ReprocessEmailAsync(email.Id, AdminActor.Id, actor: AdminActor);

        Assert.True(result.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, result.FinalStatus);
        var draft = Assert.Single(result.LinkedBills);
        Assert.Equal("INV-FALLBACK-77", draft.InvoiceNumber);
        Assert.Equal(344.93m, draft.Amount);
        Assert.Equal("/spokesapi/files/bills/ctx-fallback/inv-fallback.pdf", draft.AttachmentPath);
    }

    [Fact]
    public async Task ReprocessEmailAsync_NewlyIgnoredDomain_DeletesDraftAndDismissesEmail()
    {
        var pdf = BuildInvoicePdf("Late Block Corp", "INV-BLOCK-9", 100.00m, 5.00m, 9.98m, 114.98m);
        var mime = BuildEmail(
            "alerts@lateblock.test",
            "Late Block Corp",
            "Invoice INV-BLOCK-9",
            "Attached",
            ("INV-BLOCK-9.pdf", pdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 203);
        Assert.NotNull(email);
        var createdDraftId = email.LinkedBillId!;
        Assert.NotNull(_db.Bills.GetById(createdDraftId));

        UpdateInboxSettings(s => s.IgnoredSenderDomains = ["lateblock.test"]);

        var result = await _inboxService.ReprocessEmailAsync(email.Id, AdminActor.Id, actor: AdminActor);

        Assert.True(result.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, result.FinalStatus);
        Assert.Empty(result.LinkedBills);
        Assert.Null(_db.Bills.GetById(createdDraftId));

        var updatedEmail = _db.EmailMessages.GetById(email.AccountId, email.Id)!;
        Assert.NotNull(updatedEmail.DecisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Dismissed, updatedEmail.DecisionLog.FinalStatus);
    }

    [Fact]
    public async Task ReprocessEmailAsync_StatementPdfInMime_ReclassifiesAsStatementAndSuppressesDraft()
    {
        var stmtPdf = PdfTestBuilderHelper.BuildTextPdf(
            "Acier Quebec Inc.",
            "Account Summary",
            "Previous Balance: 750.00",
            "Total: 750.00");

        var mime = BuildEmail(
            "ar@acierquebec.test",
            "Acier Quebec Inc.",
            "Monthly Balance Summary",
            "Please review your summary.",
            ("summary_nov.pdf", stmtPdf, "application", "pdf"));

        var email = await _inboxService.ProcessMimeMessageAsync(mime, uid: 204);
        Assert.NotNull(email);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, email.BillingStatus);

        var reprocessResult = await _inboxService.ReprocessEmailAsync(email.Id, AdminActor.Id, actor: AdminActor);

        Assert.True(reprocessResult.Success);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Statement, reprocessResult.FinalStatus);
        Assert.Equal(750.00m, _db.EmailMessages.GetById(email.AccountId, email.Id)!.DetectedAmount);
        Assert.Empty(reprocessResult.LinkedBills);
        Assert.Empty(_db.Bills.GetAll());
    }
}