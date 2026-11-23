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
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests;
using Xunit;

public class CompanyBillReceiptInvoiceLinkingTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly CompanyBillInboxService _inboxService;
    private static readonly Employee AdminActor = new() { Id = "admin-test", FirstName = "Admin", LastName = "Test", IsAdmin = true, IsActive = true };

    public CompanyBillReceiptInvoiceLinkingTests()
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
    public void LinkReceiptToInvoice_LinksEmailToInvoiceAndMarksReceiptConfirmed()
    {
        // 1. Arrange an invoice
        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "INV-2026-999",
            Status = InvoiceStatus.Final,
            LineItems =
            [
                new InvoiceLine { Description = "Consulting", Quantity = 10, UnitPrice = 100m }
            ]
        };
        _db.Invoices.Save(invoice);

        // 2. Arrange a payment receipt email
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "Acme Corp Accounts Payable",
            FromAddress = "ap@acme.com",
            Subject = "Payment Confirmation for INV-2026-999",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt,
            DetectedAmount = 1000m
        };
        _db.EmailMessages.Save(email);

        // 3. Act: Link payment receipt email to invoice without recording new payment
        _inboxService.LinkReceiptToInvoice(email.Id, invoice.Id, "finance-officer", actor: AdminActor);

        // 4. Assert
        var reloadedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(reloadedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed, reloadedEmail.BillingStatus);
        Assert.Equal(invoice.Id, reloadedEmail.LinkedInvoiceId);
        Assert.Contains(invoice.Id, reloadedEmail.LinkedInvoiceIds);
        Assert.Equal("finance-officer", reloadedEmail.ProcessedByUserId);
        Assert.NotNull(reloadedEmail.ProcessedAt);

        var decisionLog = reloadedEmail.DecisionLog;
        Assert.NotNull(decisionLog);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed, decisionLog.FinalStatus);
        Assert.Contains(decisionLog.Steps, s => s.StepName.Contains("Linked to Invoice"));

        var reloadedInvoice = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(reloadedInvoice);
        Assert.Contains(reloadedInvoice.History, h => h.Action == "Receipt Linked");
    }

    [Fact]
    public void LinkReceiptToInvoice_WithPayment_RecordsPaymentAndUpdatesBalance()
    {
        // 1. Arrange an invoice with total $500
        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "INV-2026-888",
            Status = InvoiceStatus.Final,
            TaxRate = 0m,
            LineItems =
            [
                new InvoiceLine { Description = "Design Services", Quantity = 1, UnitPrice = 500m }
            ]
        };
        _db.Invoices.Save(invoice);

        // 2. Arrange payment receipt email
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "Customer Accounts",
            FromAddress = "billing@customer.com",
            Subject = "Wire confirmation INV-2026-888",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt,
            DetectedAmount = 500m
        };
        _db.EmailMessages.Save(email);

        var payment = new PaymentRecord
        {
            Amount = 500m,
            Date = DateTime.Today,
            Method = "Wire",
            Reference = "WIRE-998877",
            Notes = "Wire confirmation received by email"
        };

        // 3. Act: Link payment receipt and record payment
        _inboxService.LinkReceiptToInvoice(email.Id, invoice.Id, "treasury-user", payment, actor: AdminActor);

        // 4. Assert
        var reloadedEmail = _db.EmailMessages.GetById(email.Id);
        Assert.NotNull(reloadedEmail);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.ReceiptConfirmed, reloadedEmail.BillingStatus);
        Assert.Equal(invoice.Id, reloadedEmail.LinkedInvoiceId);

        var reloadedInvoice = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(reloadedInvoice);
        Assert.Single(reloadedInvoice.Payments);
        Assert.Equal(500m, reloadedInvoice.AmountPaid);
        Assert.Equal(0m, reloadedInvoice.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, reloadedInvoice.Status);
    }

    [Fact]
    public void LinkReceiptToInvoice_WithTaxRounding_MarksInvoiceAsPaidWhenBalanceBecomesZero()
    {
        // 1. Arrange an invoice with fractional tax rate (Quebec GST/QST combined: 14.975%)
        // SubTotal = 100.01, TaxRate = 0.14975 => unrounded Tax = 14.9764975, rounded Tax = 14.98
        // GrandTotal = 100.01 + 14.98 = 114.99
        var invoice = new Invoice
        {
            Id = Guid.NewGuid().ToString(),
            InvoiceNumber = "INV-2026-TAX",
            Status = InvoiceStatus.Final,
            TaxRate = 0.14975m,
            LineItems =
            [
                new InvoiceLine { Description = "Hardware Component", Quantity = 1, UnitPrice = 100.01m }
            ]
        };
        _db.Invoices.Save(invoice);

        // Verify computed grand total and balance due are properly rounded to 2 decimals
        Assert.Equal(14.98m, invoice.TaxAmount);
        Assert.Equal(114.99m, invoice.GrandTotal);
        Assert.Equal(114.99m, invoice.BalanceDue);

        // 2. Arrange payment receipt email for exact balance due ($114.99)
        var email = new EmailMessage
        {
            Id = Guid.NewGuid().ToString(),
            FromName = "Client Accounts",
            FromAddress = "ap@client.ca",
            Subject = "Payment for INV-2026-TAX",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Receipt,
            DetectedAmount = 114.99m
        };
        _db.EmailMessages.Save(email);

        var payment = new PaymentRecord
        {
            Amount = 114.99m,
            Date = DateTime.Today,
            Method = "Interac e-Transfer",
            Reference = "ETR-554433"
        };

        // 3. Act: Link payment receipt and record payment
        _inboxService.LinkReceiptToInvoice(email.Id, invoice.Id, "accountant-user", payment, actor: AdminActor);

        // 4. Assert: Status must be Paid, BalanceDue must be 0m, AmountPaid must equal GrandTotal
        var reloadedInvoice = _db.Invoices.GetById(invoice.Id);
        Assert.NotNull(reloadedInvoice);
        Assert.Equal(114.99m, reloadedInvoice.AmountPaid);
        Assert.Equal(0m, reloadedInvoice.BalanceDue);
        Assert.Equal(InvoiceStatus.Paid, reloadedInvoice.Status);
    }

}
