using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Spokes_Server.Tests;

public class InspectRealPdfTest
{
    private readonly ITestOutputHelper _output;

    public InspectRealPdfTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string GetAlumaxPdfPath()
    {
        var candidates = new[]
        {
            @"C:\Users\Mats\Downloads\Facture1631_de_Alumax_Usinage.pdf",
            @"C:\Users\MatsDallaire\Downloads\639268993137291094_6d5037e5.pdf",
            @"C:\Users\MatsDallaire\Downloads\Facture1631_de_Alumax_Usinage.pdf"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return candidates[0];
    }

    [Fact]
    public void TestExtractionOnRealPdf()
    {
        var path = GetAlumaxPdfPath();
        if (!File.Exists(path))
        {
            _output.WriteLine($"Sample PDF not found at {path}, skipping test.");
            return;
        }
        using var stream = File.OpenRead(path);
        var inspectionService = new PdfInvoiceInspectionService();
        var result = inspectionService.Inspect(stream, "Facture1631_de_Alumax_Usinage.pdf");

        Assert.NotNull(result);
        _output.WriteLine($"Source: {result.Source}");
        _output.WriteLine($"Supplier Name: {result.SupplierName}");
        _output.WriteLine($"Supplier Email: {result.SupplierEmail}");
        _output.WriteLine($"Supplier TaxId: {result.SupplierTaxId}");
        _output.WriteLine($"Buyer: {result.BuyerName}");
        _output.WriteLine($"Invoice #: {result.InvoiceNumber}");
        _output.WriteLine($"Date: {result.InvoiceDate:yyyy-MM-dd}");
        _output.WriteLine($"Due: {result.DueDate:yyyy-MM-dd}");
        _output.WriteLine($"SubTotal: {result.SubTotal}");
        _output.WriteLine($"TPS: {result.TpsAmount}");
        _output.WriteLine($"TVQ: {result.TvqAmount}");
        _output.WriteLine($"Tax: {result.TaxAmount}");
        _output.WriteLine($"Total: {result.TotalAmount}");
        _output.WriteLine($"Currency: {result.Currency}");
        _output.WriteLine($"IsConfident: {result.IsConfident}");
        _output.WriteLine($"Line Items Count: {result.LineItems.Count}");
        foreach (var li in result.LineItems)
        {
            _output.WriteLine($"  - {li.Quantity} x {li.UnitPrice} = {li.Total} : {li.Description}");
        }

        Assert.Equal("1631", result.InvoiceNumber);
        Assert.Equal("Alumax Usinage", result.SupplierName);
        Assert.Equal("sdesbiens@alumaxusinage.com", result.SupplierEmail);
        Assert.Equal(new DateTime(2026, 6, 22), result.InvoiceDate);
        Assert.Equal(new DateTime(2026, 7, 7), result.DueDate);
        Assert.Equal(6397.10m, result.SubTotal);
        Assert.Equal(319.86m, result.TpsAmount);
        Assert.Equal(638.10m, result.TvqAmount);
        Assert.Equal(957.96m, result.TaxAmount);
        Assert.Equal(7355.06m, result.TotalAmount);
        Assert.Equal(3677.53m, result.PaidAmount);
        Assert.Equal(3677.53m, result.BalanceDue);
        Assert.True(result.HasPaymentDetected);
        Assert.Equal("CAD", result.Currency);
        Assert.True(result.IsConfident);
        Assert.Equal(9, result.LineItems.Count);
    }

    [Fact]
    public void InspectAutoEntryPdf()
    {
        var path = @"C:\Users\Mats\Downloads\639270620289325474_fcccd7a5.pdf";
        if (!File.Exists(path)) return;

        using var stream = File.OpenRead(path);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(stream);
        _output.WriteLine("=== RAW PDF TEXT ===");
        for (int i = 1; i <= pdf.NumberOfPages; i++)
        {
            var p = pdf.GetPage(i);
            _output.WriteLine($"--- Page {i} ---");
            _output.WriteLine(p.Text);
        }

        stream.Position = 0;
        var spatialDoc = SpatialTextDocument.FromPdf(pdf);
        _output.WriteLine("=== SPATIAL LINES ===");
        foreach (var line in spatialDoc.AllLines)
        {
            _output.WriteLine($"[Y={line.Y:F1}] {line.Text}");
        }

        stream.Position = 0;
        var service = new PdfInvoiceInspectionService();
        var result = service.Inspect(stream, "AutoEntry-Facture-1661502.pdf");
        _output.WriteLine("=== INSPECTION RESULT ===");
        if (result != null)
        {
            _output.WriteLine($"Source: {result.Source}");
            _output.WriteLine($"Supplier Name: '{result.SupplierName}'");
            _output.WriteLine($"Supplier Email: '{result.SupplierEmail}'");
            _output.WriteLine($"Invoice #: '{result.InvoiceNumber}'");
            _output.WriteLine($"Date: {result.InvoiceDate}");
            _output.WriteLine($"Due: {result.DueDate}");
            _output.WriteLine($"SubTotal: {result.SubTotal}");
            _output.WriteLine($"Tax: {result.TaxAmount}");
            _output.WriteLine($"Total: {result.TotalAmount}");
            _output.WriteLine($"Paid: {result.PaidAmount}");
            _output.WriteLine($"BalanceDue: {result.BalanceDue}");
            _output.WriteLine($"HasPayment: {result.HasPaymentDetected}");
            _output.WriteLine($"RawDocType: {result.RawDocumentType}");
            _output.WriteLine($"Line Items: {result.LineItems.Count}");
            foreach (var li in result.LineItems)
            {
                _output.WriteLine($"  Line: {li.Description} | Qty: {li.Quantity} | Price: {li.UnitPrice} | Total: {li.Total}");
            }
        }
        else
        {
            _output.WriteLine("Result is NULL");
        }
    }

    [Fact]
    public async Task TestEndToEndBillInboxProcessing()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "spokes_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbPath);
        try
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            var mockConfig = new Moq.Mock<Microsoft.Extensions.Configuration.IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDbPath);
            services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(mockConfig.Object);
            services.AddLogging(b => b.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Security.EncryptionService>();
            services.AddSpokesDatabase();

            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var encService = sp.GetRequiredService<Spokes_Server.Core.Services.Security.EncryptionService>();

            var path = GetAlumaxPdfPath();
            if (!File.Exists(path))
            {
                _output.WriteLine($"Sample PDF not found at {path}, skipping test.");
                return;
            }
            var mockFileService = new Moq.Mock<Spokes_Server.Core.Services.Storage.IFileService>();
            mockFileService.Setup(f => f.UploadStreamAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Stream>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<string?>()))
                .ReturnsAsync("/spokesapi/files/bills/test/Facture1631_de_Alumax_Usinage.pdf");
            mockFileService.Setup(f => f.OpenRead(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>()))
                .Returns(() => new MemoryStream(File.ReadAllBytes(path)));

            var pdfService = new PdfInvoiceInspectionService();
            var poEngine = new SmartPoMatchingEngine(db);

            var inboxService = new CompanyBillInboxService(
                db,
                mockFileService.Object,
                pdfService,
                poEngine,
                encService);

            // Construct MimeMessage with the real PDF attachment
            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress("", "accomp@poly-robotics.com"));
            message.Subject = "Facture Alumax Usinage 1631";

            var builder = new MimeKit.BodyBuilder { TextBody = "Veuillez trouver ci-joint la facture." };
            builder.Attachments.Add("Facture1631_de_Alumax_Usinage.pdf", File.ReadAllBytes(path), new MimeKit.ContentType("application", "pdf"));
            message.Body = builder.ToMessageBody();

            var processedEmail = await inboxService.ProcessMimeMessageAsync(message, 1);
            Assert.NotNull(processedEmail);

            _output.WriteLine($"Billing Status: {processedEmail.BillingStatus}");
            _output.WriteLine($"Detected Amount: {processedEmail.DetectedAmount}");
            _output.WriteLine($"Linked Bill ID: {processedEmail.LinkedBillId}");

            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, processedEmail.BillingStatus);
            Assert.Equal(7355.06m, processedEmail.DetectedAmount);
            Assert.NotNull(processedEmail.LinkedBillId);

            var createdBill = db.Bills.GetById(processedEmail.LinkedBillId);
            Assert.NotNull(createdBill);

            _output.WriteLine($"Bill Supplier: {createdBill.SupplierName}");
            _output.WriteLine($"Bill Invoice #: {createdBill.InvoiceNumber}");
            _output.WriteLine($"Bill Date: {createdBill.Date:yyyy-MM-dd}");
            _output.WriteLine($"Bill Due: {createdBill.DueDate:yyyy-MM-dd}");
            _output.WriteLine($"Bill Amount: {createdBill.Amount}");
            _output.WriteLine($"Bill SubTotal: {createdBill.SubTotal}");
            _output.WriteLine($"Bill Tax: {createdBill.TaxAmount}");
            _output.WriteLine($"Bill TPS: {createdBill.TpsAmount}");
            _output.WriteLine($"Bill TVQ: {createdBill.TvqAmount}");
            _output.WriteLine($"Bill Status: {createdBill.Status}");
            _output.WriteLine($"Bill Line Items: {createdBill.LineItems.Count}");

            Assert.Equal("Alumax Usinage", createdBill.SupplierName);
            Assert.Equal("1631", createdBill.InvoiceNumber);
            Assert.Equal(new DateTime(2026, 6, 22), createdBill.Date);
            Assert.Equal(new DateTime(2026, 7, 7), createdBill.DueDate);
            Assert.Equal(7355.06m, createdBill.Amount);
            Assert.Equal(6397.10m, createdBill.SubTotal);
            Assert.Equal(957.96m, createdBill.TaxAmount);
            Assert.Equal(319.86m, createdBill.TpsAmount);
            Assert.Equal(638.10m, createdBill.TvqAmount);
            Assert.Equal("CAD", createdBill.Currency);
            Assert.Equal(BillStatus.AwaitingApproval, createdBill.Status);
            Assert.Equal(9, createdBill.LineItems.Count);

            // Verify prior payment was detected and recorded
            Assert.Equal(3677.53m, processedEmail.DetectedPaidAmount);
            Assert.Equal(3677.53m, processedEmail.DetectedBalanceDue);
            Assert.Single(createdBill.Payments);
            Assert.Equal(3677.53m, createdBill.Payments[0].Amount);
            Assert.Equal("Unknown", createdBill.Payments[0].Method);
            Assert.Equal(3677.53m, createdBill.AmountPaid);
            Assert.Equal(3677.53m, createdBill.BalanceDue);

            // Test approval transitions to Unpaid because remaining balance > 0
            inboxService.ApproveBillFromEmail(processedEmail.Id, createdBill.Id, "test-user");
            var approvedBill = db.Bills.GetById(createdBill.Id);
            Assert.NotNull(approvedBill);
            Assert.Equal(BillStatus.Unpaid, approvedBill.Status);
            Assert.Equal(3677.53m, approvedBill.AmountPaid);
            Assert.Equal(3677.53m, approvedBill.BalanceDue);

            // Verify Decision Log
            var log = processedEmail.DecisionLog;
            Assert.NotNull(log);
            _output.WriteLine($"Decision Log Status: {log.FinalStatus}");
            _output.WriteLine($"Decision Log Summary: {log.SummaryReason}");
            foreach (var step in log.Steps)
            {
                _output.WriteLine($"Step {step.StepNumber} [{step.StepName}]: {step.Status} - {step.Outcome}");
            }
            foreach (var f in log.FieldSources)
            {
                _output.WriteLine($"Field [{f.FieldName}]: {f.Value} (Source: {f.Source}, Method: {f.ExtractionMethod})");
            }

            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, log.FinalStatus);
            var suppField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Supplier");
            Assert.NotNull(suppField);
            Assert.Equal("Alumax Usinage", suppField.Value);

            var invField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Invoice #");
            Assert.NotNull(invField);
            Assert.Equal("1631", invField.Value);

            var amtField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Total Amount");
            Assert.NotNull(amtField);
            Assert.Contains("7,355.06", amtField.Value);
        }
        finally
        {
            if (Directory.Exists(tempDbPath))
            {
                try { Directory.Delete(tempDbPath, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task TestDuplicateBillWithUnrecordedPayment()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "spokes_test_dup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbPath);
        try
        {
            var services = new ServiceCollection();
            var mockConfig = new Moq.Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDbPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);
            services.AddLogging(b => b.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Security.EncryptionService>();
            services.AddSpokesDatabase();

            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var encService = sp.GetRequiredService<Spokes_Server.Core.Services.Security.EncryptionService>();

            var path = GetAlumaxPdfPath();
            if (!File.Exists(path)) return;

            // Seed an existing bill for this supplier and invoice # WITHOUT the payment
            var existingBill = new Bill
            {
                Id = Guid.NewGuid().ToString(),
                SupplierName = "Alumax Usinage",
                InvoiceNumber = "1631",
                Amount = 7355.06m,
                SubTotal = 6397.10m,
                TaxAmount = 957.96m,
                Status = BillStatus.Unpaid,
                Date = new DateTime(2026, 6, 22),
                DueDate = new DateTime(2026, 7, 7),
                Currency = "CAD"
            };
            db.Bills.Save(existingBill);

            var mockFileService = new Moq.Mock<Spokes_Server.Core.Services.Storage.IFileService>();
            mockFileService.Setup(f => f.UploadStreamAsync(
                Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<Stream>(),
                Moq.It.IsAny<string>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<string?>()))
                .ReturnsAsync("/spokesapi/files/bills/test/Facture1631_de_Alumax_Usinage.pdf");
            mockFileService.Setup(f => f.OpenRead(
                Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<string?>()))
                .Returns(() => new MemoryStream(File.ReadAllBytes(path)));

            var pdfService = new PdfInvoiceInspectionService();
            var poEngine = new SmartPoMatchingEngine(db);
            var inboxService = new CompanyBillInboxService(db, mockFileService.Object, pdfService, poEngine, encService);

            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress("", "accomp@poly-robotics.com"));
            message.Subject = "Rappel de paiement: Facture 1631";
            var builder = new MimeKit.BodyBuilder { TextBody = "Ci-joint le rappel avec solde." };
            builder.Attachments.Add("Facture1631_de_Alumax_Usinage.pdf", File.ReadAllBytes(path), new MimeKit.ContentType("application", "pdf"));
            message.Body = builder.ToMessageBody();

            var processedEmail = await inboxService.ProcessMimeMessageAsync(message, 1);
            Assert.NotNull(processedEmail);
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.DuplicateDetected, processedEmail.BillingStatus);
            Assert.Equal(existingBill.Id, processedEmail.LinkedBillId);
            Assert.NotNull(processedEmail.DetectedPaidAmount);
            Assert.Equal(3677.53m, processedEmail.DetectedPaidAmount);

            // Record payment on existing bill
            inboxService.RecordPaymentOnExistingBill(processedEmail.Id, existingBill.Id, processedEmail.DetectedPaidAmount!.Value, "test-user");

            var updatedBill = db.Bills.GetById(existingBill.Id);
            Assert.NotNull(updatedBill);
            Assert.Single(updatedBill.Payments);
            Assert.Equal(3677.53m, updatedBill.Payments[0].Amount);
            Assert.Equal("Unknown", updatedBill.Payments[0].Method);
            Assert.Equal(3677.53m, updatedBill.AmountPaid);
            Assert.Equal(3677.53m, updatedBill.BalanceDue);

            var updatedEmail = db.EmailMessages.GetById(processedEmail.Id);
            Assert.NotNull(updatedEmail);
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, updatedEmail.BillingStatus);
        }
        finally
        {
            if (Directory.Exists(tempDbPath))
            {
                try { Directory.Delete(tempDbPath, true); } catch { }
            }
        }
    }

    [Fact]
    public void TestExtractionOnSecondRealPdf()
    {
        var path = @"C:\Users\MatsDallaire\Downloads\639268993135999413_14b519e5.pdf";
        if (!File.Exists(path))
        {
            _output.WriteLine($"Sample PDF not found at {path}, skipping test.");
            return;
        }
        using var stream = File.OpenRead(path);
        var service = new PdfInvoiceInspectionService();
        var result = service.Inspect(stream, "F-359504.pdf");

        Assert.NotNull(result);
        _output.WriteLine($"SupplierName: {result.SupplierName}");
        _output.WriteLine($"SupplierEmail: {result.SupplierEmail}");
        _output.WriteLine($"SupplierTaxId: {result.SupplierTaxId}");
        _output.WriteLine($"InvoiceNumber: {result.InvoiceNumber}");
        _output.WriteLine($"InvoiceDate: {result.InvoiceDate}");
        _output.WriteLine($"DueDate: {result.DueDate}");
        _output.WriteLine($"SubTotal: {result.SubTotal}");
        _output.WriteLine($"TaxAmount: {result.TaxAmount}");
        _output.WriteLine($"TpsAmount: {result.TpsAmount}");
        _output.WriteLine($"TvqAmount: {result.TvqAmount}");
        _output.WriteLine($"TotalAmount: {result.TotalAmount}");
        _output.WriteLine($"Line items count: {result.LineItems.Count}");
        foreach (var item in result.LineItems)
        {
            _output.WriteLine($"  Line: {item.Description} | Qty: {item.Quantity} | Price: {item.UnitPrice} | Total: {item.Total}");
        }

        Assert.Equal("Les Attaches Trans-Québec Inc.", result.SupplierName);
        Assert.Contains("882929151RT0001", result.SupplierTaxId);
        Assert.Contains("1010485891TQ0001", result.SupplierTaxId);
        Assert.Equal("359504", result.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 8, 31), result.InvoiceDate);
        Assert.Equal(new DateTime(2026, 9, 30), result.DueDate);
        Assert.Equal(15.04m, result.SubTotal);
        Assert.Equal(0.75m, result.TpsAmount);
        Assert.Equal(1.50m, result.TvqAmount);
        Assert.Equal(2.25m, result.TaxAmount);
        Assert.Equal(17.29m, result.TotalAmount);
        Assert.Equal("CAD", result.Currency);
        Assert.Equal(4, result.LineItems.Count);
        Assert.Equal("Button socket cap 1/4-20 x 3/8", result.LineItems[0].Description);
        Assert.Equal(16m, result.LineItems[0].Quantity);
        Assert.Equal(0.44m, result.LineItems[0].UnitPrice);
        Assert.Equal(7.04m, result.LineItems[0].Total);
        Assert.Equal("Flat w yellow 5/16", result.LineItems[3].Description);
        Assert.Equal(15.04m, result.LineItems.Sum(li => li.Total));
    }

    [Fact]
    public async Task TestEndToEndBillInboxProcessingSecondRealPdf()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "Spokes_TestDb_BillInbox2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbPath);

        try
        {
            var services = new ServiceCollection();
            var mockConfig = new Moq.Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDbPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);
            services.AddLogging(b => b.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Security.EncryptionService>();
            services.AddSpokesDatabase();

            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var encService = sp.GetRequiredService<Spokes_Server.Core.Services.Security.EncryptionService>();

            var path = @"C:\Users\MatsDallaire\Downloads\639268993135999413_14b519e5.pdf";
            if (!File.Exists(path))
            {
                _output.WriteLine($"Sample PDF not found at {path}, skipping test.");
                return;
            }
            var mockFileService = new Moq.Mock<Spokes_Server.Core.Services.Storage.IFileService>();
            mockFileService.Setup(f => f.UploadStreamAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Stream>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<string?>()))
                .ReturnsAsync("/spokesapi/files/bills/test/F-359504.pdf");
            mockFileService.Setup(f => f.OpenRead(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>()))
                .Returns(() => new MemoryStream(File.ReadAllBytes(path)));

            var pdfService = new PdfInvoiceInspectionService();
            var poEngine = new SmartPoMatchingEngine(db);

            var inboxService = new CompanyBillInboxService(
                db,
                mockFileService.Object,
                pdfService,
                poEngine,
                encService);

            // Construct MimeMessage with the real PDF attachment
            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress("", "accomp@poly-robotics.com"));
            message.Subject = "Facture F-359504 Les Attaches Trans-Québec";

            var builder = new MimeKit.BodyBuilder { TextBody = "Facture ci-jointe pour votre commande." };
            builder.Attachments.Add("F-359504.pdf", File.ReadAllBytes(path), new MimeKit.ContentType("application", "pdf"));
            message.Body = builder.ToMessageBody();

            var processedEmail = await inboxService.ProcessMimeMessageAsync(message, 2);
            Assert.NotNull(processedEmail);

            _output.WriteLine($"Billing Status: {processedEmail.BillingStatus}");
            _output.WriteLine($"Detected Amount: {processedEmail.DetectedAmount}");
            _output.WriteLine($"Linked Bill ID: {processedEmail.LinkedBillId}");

            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, processedEmail.BillingStatus);
            Assert.Equal(17.29m, processedEmail.DetectedAmount);
            Assert.NotNull(processedEmail.LinkedBillId);

            var createdBill = db.Bills.GetById(processedEmail.LinkedBillId);
            Assert.NotNull(createdBill);

            _output.WriteLine($"Bill Supplier: {createdBill.SupplierName}");
            _output.WriteLine($"Bill Invoice #: {createdBill.InvoiceNumber}");
            _output.WriteLine($"Bill Date: {createdBill.Date:yyyy-MM-dd}");
            _output.WriteLine($"Bill Due: {createdBill.DueDate:yyyy-MM-dd}");
            _output.WriteLine($"Bill Amount: {createdBill.Amount}");
            _output.WriteLine($"Bill SubTotal: {createdBill.SubTotal}");
            _output.WriteLine($"Bill Tax: {createdBill.TaxAmount}");
            _output.WriteLine($"Bill TPS: {createdBill.TpsAmount}");
            _output.WriteLine($"Bill TVQ: {createdBill.TvqAmount}");
            _output.WriteLine($"Bill Status: {createdBill.Status}");
            _output.WriteLine($"Bill Line Items: {createdBill.LineItems.Count}");

            Assert.Equal("Les Attaches Trans-Québec Inc.", createdBill.SupplierName);
            Assert.Equal("359504", createdBill.InvoiceNumber);
            Assert.Equal(new DateTime(2026, 8, 31), createdBill.Date);
            Assert.Equal(new DateTime(2026, 9, 30), createdBill.DueDate);
            Assert.Equal(17.29m, createdBill.Amount);
            Assert.Equal(15.04m, createdBill.SubTotal);
            Assert.Equal(2.25m, createdBill.TaxAmount);
            Assert.Equal(0.75m, createdBill.TpsAmount);
            Assert.Equal(1.50m, createdBill.TvqAmount);
            Assert.Equal("CAD", createdBill.Currency);
            Assert.Equal(BillStatus.AwaitingApproval, createdBill.Status);
            Assert.Equal(4, createdBill.LineItems.Count);

            // Verify Decision Log
            var log = processedEmail.DecisionLog;
            Assert.NotNull(log);
            _output.WriteLine($"Decision Log Status: {log.FinalStatus}");
            _output.WriteLine($"Decision Log Summary: {log.SummaryReason}");

            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, log.FinalStatus);
            var suppField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Supplier");
            Assert.NotNull(suppField);
            Assert.Equal("Les Attaches Trans-Québec Inc.", suppField.Value);

            var invField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Invoice #");
            Assert.NotNull(invField);
            Assert.Equal("359504", invField.Value);

            var amtField = log.FieldSources.FirstOrDefault(f => f.FieldName == "Total Amount");
            Assert.NotNull(amtField);
            Assert.Contains("17.29", amtField.Value);
        }
        finally
        {
            if (Directory.Exists(tempDbPath))
            {
                try { Directory.Delete(tempDbPath, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task TestEndToEndBillInboxProcessingSherwinWilliamsMultiPdf()
    {
        var tempDbPath = Path.Combine(Path.GetTempPath(), "Spokes_TestDb_Sherwin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDbPath);

        try
        {
            var pdf1 = @"C:\Users\MatsDallaire\Downloads\793938382_42629_07152026.pdf";
            var pdf2 = @"C:\Users\MatsDallaire\Downloads\793938382_39742_07132026.pdf";
            var pdf3 = @"C:\Users\MatsDallaire\Downloads\793938382_42470_07142026.pdf";

            if (!File.Exists(pdf1) || !File.Exists(pdf2) || !File.Exists(pdf3))
            {
                _output.WriteLine("Sherwin-Williams test PDFs not found, skipping test.");
                return;
            }

            var services = new ServiceCollection();
            var mockConfig = new Moq.Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(tempDbPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);
            services.AddLogging(b => b.AddConsole());
            services.AddSingleton<Spokes_Server.Core.Services.Security.EncryptionService>();
            services.AddSpokesDatabase();

            using var sp = services.BuildServiceProvider();
            var db = sp.GetRequiredService<Spokes_Server.Aggregate.Database>();
            var encService = sp.GetRequiredService<Spokes_Server.Core.Services.Security.EncryptionService>();

            // 1. Pre-seed Supplier: Sherwin Williams
            var supplier = new Supplier
            {
                Id = Guid.NewGuid().ToString(),
                ContactInfo = new PartyContactInfo
                {
                    BusinessName = "Sherwin William",
                    ContactPersonEmail = "Theodore.Eloundou@sherwin.com"
                }
            };
            db.Suppliers.Save(supplier);

            // 2. Pre-seed an Existing Paid Bill for invoice #42470130040726 ($114.06) matching pdf3
            var existingPaidBill = new Bill
            {
                Id = Guid.NewGuid().ToString(),
                SupplierId = supplier.Id,
                SupplierName = "Sherwin William",
                InvoiceNumber = "42470130040726",
                Amount = 114.06m,
                Date = new DateTime(2026, 7, 14),
                DueDate = new DateTime(2026, 8, 14),
                DatePaid = new DateTime(2026, 9, 17),
                Status = BillStatus.Paid,
                Currency = "CAD",
                Description = "Sherwin-Williams Paint"
            };
            db.Bills.Save(existingPaidBill);

            // 3. Mock FileService
            var fileMap = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
            var mockFileService = new Moq.Mock<Spokes_Server.Core.Services.Storage.IFileService>();
            mockFileService.Setup(f => f.UploadStreamAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Stream>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<string?>()))
                .ReturnsAsync((string category, string contextId, Stream stm, string fn, string? sub, string? ct) =>
                {
                    using var ms = new MemoryStream();
                    stm.CopyTo(ms);
                    var bytes = ms.ToArray();
                    var key = $"{category}/{contextId}/{fn}";
                    fileMap[key] = bytes;
                    return $"/spokesapi/files/{key}";
                });

            mockFileService.Setup(f => f.OpenRead(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<string?>()))
                .Returns((string category, string contextId, string fn, string? sub) =>
                {
                    var key = $"{category}/{contextId}/{fn}";
                    if (fileMap.TryGetValue(key, out var b))
                    {
                        return new MemoryStream(b);
                    }
                    if (fn.Contains("42629")) return new MemoryStream(File.ReadAllBytes(pdf1));
                    if (fn.Contains("39742")) return new MemoryStream(File.ReadAllBytes(pdf2));
                    if (fn.Contains("42470")) return new MemoryStream(File.ReadAllBytes(pdf3));
                    return new MemoryStream(File.ReadAllBytes(pdf1));
                });

            var pdfService = new PdfInvoiceInspectionService();
            var poEngine = new SmartPoMatchingEngine(db);

            var inboxService = new CompanyBillInboxService(
                db,
                mockFileService.Object,
                pdfService,
                poEngine,
                encService);

            // 4. Construct email with 3 attachments
            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress("", "Theodore.Eloundou@sherwin.com"));
            message.Subject = "Acct: 7939-3838-2 Vos Factures en Souffrance";

            var builder = new MimeKit.BodyBuilder { TextBody = "Veuillez trouver ci-joint vos factures en souffrance." };
            builder.Attachments.Add("793938382_42629_07152026.pdf", File.ReadAllBytes(pdf1), new MimeKit.ContentType("application", "pdf"));
            builder.Attachments.Add("793938382_39742_07132026.pdf", File.ReadAllBytes(pdf2), new MimeKit.ContentType("application", "pdf"));
            builder.Attachments.Add("793938382_42470_07142026.pdf", File.ReadAllBytes(pdf3), new MimeKit.ContentType("application", "pdf"));
            message.Body = builder.ToMessageBody();

            var processedEmail = await inboxService.ProcessMimeMessageAsync(message, 1);
            Assert.NotNull(processedEmail);

            _output.WriteLine($"Processed Status: {processedEmail.BillingStatus}");
            _output.WriteLine($"LinkedBillIds count: {processedEmail.LinkedBillIds.Count}");

            // Verification A: Email must be AutoDrafted because 2 of the 3 bills are actionable drafts!
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, processedEmail.BillingStatus);
            Assert.Equal(3, processedEmail.LinkedBillIds.Count);

            var linkedBills = processedEmail.LinkedBillIds
                .Select(id => db.Bills.GetById(id))
                .Where(b => b != null)
                .Select(b => b!)
                .ToList();

            var drafts = linkedBills.Where(b => b.Status == BillStatus.AwaitingApproval).ToList();
            var duplicates = linkedBills.Where(b => b.Status != BillStatus.AwaitingApproval).ToList();

            Assert.Equal(2, drafts.Count);
            Assert.Single(duplicates);

            Assert.Equal(existingPaidBill.Id, duplicates[0].Id);
            Assert.Equal(BillStatus.Paid, duplicates[0].Status);

            var draft1 = drafts.FirstOrDefault(d => d.Amount == 114.06m && d.InvoiceNumber == "42629130040726");
            var draft2 = drafts.FirstOrDefault(d => d.Amount == 396.56m && d.InvoiceNumber == "39742199340726");
            Assert.NotNull(draft1);
            Assert.NotNull(draft2);

            // Verification B: Decision Log contains multi-invoice duplicate detection details
            var log = processedEmail.DecisionLog;
            Assert.NotNull(log);
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, log.FinalStatus);
            Assert.Contains("2 bills created", log.SummaryReason);
            Assert.Contains("1 existing bill(s) recognized", log.SummaryReason);

            var dupStep = log.Steps.FirstOrDefault(s => s.StepName == "Duplicate Bill Detection");
            Assert.NotNull(dupStep);
            Assert.Contains(dupStep.Details, d => d.Contains("42629130040726"));
            Assert.Contains(dupStep.Details, d => d.Contains("39742199340726"));
            Assert.Contains(dupStep.Details, d => d.Contains("42470130040726"));

            // Verification C: Approving draft 1 preserves AutoDrafted for remaining draft 2
            inboxService.ApproveBillFromEmail(processedEmail.Id, draft1.Id, "test-user");
            var refreshed1 = db.EmailMessages.GetById(processedEmail.Id);
            Assert.NotNull(refreshed1);
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.AutoDrafted, refreshed1.BillingStatus);
            Assert.Equal(draft2.Id, refreshed1.LinkedBillId);

            var approved1 = db.Bills.GetById(draft1.Id);
            Assert.NotNull(approved1);
            Assert.Equal(BillStatus.Unpaid, approved1.Status);

            // Existing paid bill remains untouched and Paid!
            var checkPaidBill = db.Bills.GetById(existingPaidBill.Id);
            Assert.NotNull(checkPaidBill);
            Assert.Equal(BillStatus.Paid, checkPaidBill.Status);

            // Verification D: Approving draft 2 marks email as Approved (Dealt With)
            inboxService.ApproveBillFromEmail(processedEmail.Id, draft2.Id, "test-user");
            var refreshed2 = db.EmailMessages.GetById(processedEmail.Id);
            Assert.NotNull(refreshed2);
            Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, refreshed2.BillingStatus);

            var approved2 = db.Bills.GetById(draft2.Id);
            Assert.NotNull(approved2);
            Assert.Equal(BillStatus.Unpaid, approved2.Status);

            // Paid bill is STILL Paid!
            checkPaidBill = db.Bills.GetById(existingPaidBill.Id);
            Assert.NotNull(checkPaidBill);
            Assert.Equal(BillStatus.Paid, checkPaidBill.Status);
        }
        finally
        {
            if (Directory.Exists(tempDbPath))
            {
                try { Directory.Delete(tempDbPath, true); } catch { }
            }
        }
    }
}
