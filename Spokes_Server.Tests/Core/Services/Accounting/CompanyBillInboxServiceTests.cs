namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Tests;
using Xunit;

public class CompanyBillInboxServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IFileService> _mockFileService;
    private readonly Mock<IEmailCacheMaintenanceService> _mockCacheMaintenance;
    private readonly Mock<IEmailDeliveryService> _mockEmailDeliveryService;
    private readonly CompanyBillInboxService _inboxService;

    public CompanyBillInboxServiceTests()
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
        _mockEmailDeliveryService = new Mock<IEmailDeliveryService>();

        var pdfService = new PdfInvoiceInspectionService();
        var poEngine = new SmartPoMatchingEngine(_db);

        _inboxService = new CompanyBillInboxService(
            _db,
            _mockFileService.Object,
            pdfService,
            poEngine,
            encryptionService,
            _mockCacheMaintenance.Object,
            _mockEmailDeliveryService.Object);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public async Task SendEmailAsync_DelegatesToSendSystemBillInboxEmailAsync_WithSystemAccountAndAutomatedFlag()
    {
        var existingAttachment = Path.Combine(_testDataPath, "po-1024.pdf");
        await File.WriteAllTextAsync(existingAttachment, "PDF-STUB");
        var missingAttachment = Path.Combine(_testDataPath, "does-not-exist.pdf");

        SendEmailRequest? capturedRequest = null;
        _mockEmailDeliveryService
            .Setup(d => d.SendSystemBillInboxEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
            .Returns(Task.CompletedTask);

        await _inboxService.SendEmailAsync(
            "vendor@acme.com",
            "PO #1024 Question",
            "<p>Please confirm delivery date.</p>",
            [existingAttachment, missingAttachment]);

        _mockEmailDeliveryService.Verify(
            d => d.SendSystemBillInboxEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockEmailDeliveryService.Verify(
            d => d.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.NotNull(capturedRequest);
        Assert.Equal(CompanyBillInboxConstants.SystemEmployeeId, capturedRequest!.AccountId);
        Assert.Equal("vendor@acme.com", capturedRequest.To);
        Assert.Equal("PO #1024 Question", capturedRequest.Subject);
        Assert.Equal("<p>Please confirm delivery date.</p>", capturedRequest.Body);
        Assert.True(capturedRequest.IsSystemAutomatedSend);
        Assert.NotNull(capturedRequest.FileAttachmentPaths);
        var att = Assert.Single(capturedRequest.FileAttachmentPaths!);
        Assert.Equal(existingAttachment, att.FilePath);
        Assert.Equal("po-1024.pdf", att.FileName);
    }

    [Fact]
    public async Task TestConnectionAsync_RejectsNullInactiveBannedSuspendedAndUnprivilegedCallers()
    {
        var settings = new CompanyBillInboxSettings
        {
            EmailAddress = "ap@company.com",
            ImapHost = "127.0.0.1",
            ImapPort = 993,
            ImapSsl = true
        };

        var unauthorizedCallers = new Employee?[]
        {
            null,
            new Employee { Id = "emp-inactive", IsActive = false, IsAdmin = true },
            new Employee { Id = "emp-banned", IsActive = true, IsBanned = true, IsAdmin = true },
            new Employee { Id = "emp-suspended", IsActive = true, IsSuspended = true, IsAdmin = true },
            new Employee { Id = "emp-regular", IsActive = true, IsAdmin = false }
        };

        foreach (var caller in unauthorizedCallers)
        {
            var result = await _inboxService.TestConnectionAsync(settings, "secret-pw", caller);
            Assert.False(result.Success);
            Assert.Contains("Not authorized", result.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TestConnectionAsync_SanitizesErrorsForNonAdminFinanceCallers_AndPreservesDetailsForAdmins()
    {
        // Reserve and immediately close an ephemeral loopback port so connection fails deterministically.
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        var settings = new CompanyBillInboxSettings
        {
            EmailAddress = "ap@company.com",
            ImapHost = "127.0.0.1",
            ImapPort = closedPort,
            ImapSsl = false,
            AllowPlaintextTransport = true
        };

        var financeUser = new Employee
        {
            Id = "emp-finance",
            FirstName = "Finance",
            IsActive = true,
            IsAdmin = false,
            Permissions = [AppPermissions.Finance.AccessAccounting]
        };

        var adminUser = new Employee
        {
            Id = "emp-admin",
            FirstName = "Admin",
            IsActive = true,
            IsAdmin = true
        };

        var financeResult = await _inboxService.TestConnectionAsync(settings, "secret-pw", financeUser);
        Assert.False(financeResult.Success);
        Assert.Equal(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, financeResult.Message);

        var adminResult = await _inboxService.TestConnectionAsync(settings, "secret-pw", adminUser);
        Assert.False(adminResult.Success);
        Assert.StartsWith("Connection failed: ", adminResult.Message, StringComparison.Ordinal);
        Assert.NotEqual(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, adminResult.Message);
    }
}
