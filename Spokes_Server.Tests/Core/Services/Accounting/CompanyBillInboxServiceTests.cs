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
    private static readonly Employee AdminActor = new() { Id = "admin-test", FirstName = "Admin", LastName = "Test", IsAdmin = true, IsActive = true };

    public CompanyBillInboxServiceTests()
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
            AccountId = "acc-bill-inbox",
            EmailAddress = "ap@company.com"
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
            var result = await _inboxService.TestConnectionAsync(settings, caller);
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

        var encryptionService = _serviceProvider.GetRequiredService<EncryptionService>();
        var profile = _db.CompanyProfile.GetOrCreate();
        profile.EmailSettings.IncomingServers =
        [
            new IncomingServerConfig
            {
                Id = "imap-closed-local",
                Name = "Local IMAP",
                Host = "127.0.0.1",
                Port = closedPort,
                UseSsl = false
            }
        ];
        _db.CompanyProfile.Save(profile);

        var account = new Spokes_Server.Core.Models.Communication.EmailAccount
        {
            Id = "acc-bill-closed",
            IsServerManaged = true,
            EmailAddress = "ap@company.com",
            Username = "ap@company.com",
            IncomingServerConfigId = "imap-closed-local",
            EncryptedPassword = encryptionService.Encrypt("secret-pw")
        };
        _db.EmailAccounts.Save(account);

        var settings = new CompanyBillInboxSettings
        {
            AccountId = account.Id,
            EmailAddress = "ap@company.com"
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

        var financeResult = await _inboxService.TestConnectionAsync(settings, financeUser);
        Assert.False(financeResult.Success);
        Assert.Equal(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, financeResult.Message);

        var adminResult = await _inboxService.TestConnectionAsync(settings, adminUser);
        Assert.False(adminResult.Success);
        Assert.StartsWith("Connection failed: ", adminResult.Message, StringComparison.Ordinal);
        Assert.NotEqual(MailTransportSecurityHelper.SanitizedConnectionErrorMessage, adminResult.Message);
    }

    [Fact]
    public void GetBillInboxMessages_WhenBoundToRealAndVirtualInbox_FiltersByVirtualInboxAndExcludesTrashAndDrafts()
    {
        var account = new Spokes_Server.Core.Models.Communication.EmailAccount
        {
            Id = "acc-shared-catchall",
            IsServerManaged = true,
            Name = "Company Catch-All",
            EmailAddress = "info@company.com"
        };
        _db.EmailAccounts.Save(account);

        var billsVirtualInbox = new Spokes_Server.Core.Models.Communication.EmailInbox
        {
            Id = "vinbox-bills",
            AccountId = account.Id,
            Name = "Accounts Payable",
            IsServerManaged = true,
            TargetAddresses = ["bills@company.com", "factures@company.com"]
        };
        _db.EmailInboxes.Save(billsVirtualInbox);

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-bill-match",
            UniqueId = 1,
            AccountId = account.Id,
            FolderPath = "INBOX",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Invoice #101",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            DecisionLog = new Spokes_Server.Core.Models.Accounting.BillInboxDecisionLog(),
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-webmail-null-status",
            UniqueId = 5,
            AccountId = account.Id,
            FolderPath = "INBOX",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Old Read Email Synced By Webmail",
            BillingStatus = null,
            IsRead = true,
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-webmail-legacy-unprocessed",
            UniqueId = 6,
            AccountId = account.Id,
            FolderPath = "INBOX",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Legacy Read Email Synced With Default Unprocessed",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            DecisionLog = null,
            IsRead = true,
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-bill-sent",
            UniqueId = 7,
            AccountId = account.Id,
            FolderPath = "Sent",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Re: Invoice #101",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Approved,
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-sales-other",
            UniqueId = 2,
            AccountId = account.Id,
            FolderPath = "INBOX",
            ReceivingAddresses = ["sales@company.com"],
            Subject = "Sales Inquiry",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-bill-trash",
            UniqueId = 3,
            AccountId = account.Id,
            FolderPath = "Trash",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Deleted Invoice",
            Date = DateTimeOffset.UtcNow
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-bill-draft",
            UniqueId = 4,
            AccountId = account.Id,
            FolderPath = "LocalDrafts",
            ReceivingAddresses = ["bills@company.com"],
            Subject = "Draft Reply",
            Date = DateTimeOffset.UtcNow
        });

        var settings = new CompanyBillInboxSettings
        {
            Enabled = true,
            AccountId = account.Id,
            InboxId = billsVirtualInbox.Id
        };
        _inboxService.SaveSettings(settings, actor: AdminActor);

        var filtered = _inboxService.GetBillInboxMessages();
        var single = Assert.Single(filtered);
        Assert.Equal("msg-bill-match", single.Id);
    }

    [Fact]
    public async Task ImapConnectionFactory_WhenStoredPasswordUndecryptable_ReturnsDecryptionErrorWithoutThrowing()
    {
        var encryptionService = _serviceProvider.GetRequiredService<EncryptionService>();
        var profile = _db.CompanyProfile.GetOrCreate();
        profile.EmailSettings.IncomingServers =
        [
            new IncomingServerConfig
            {
                Id = "imap-decryption-test",
                Name = "Test IMAP",
                Host = "imap.example.com",
                Port = 993,
                UseSsl = true
            }
        ];
        _db.CompanyProfile.Save(profile);

        var brokenAccount = new Spokes_Server.Core.Models.Communication.EmailAccount
        {
            Id = "acc-broken-cipher",
            IsServerManaged = true,
            EmailAddress = "bills@acme.test",
            Username = "bills@acme.test",
            IncomingServerConfigId = "imap-decryption-test",
            EncryptedPassword = "short-non-v2-ciphertext"
        };
        _db.EmailAccounts.Save(brokenAccount);

        var factory = new ImapConnectionFactory(
            _db.CompanyProfile,
            encryptionService,
            _db.EmailAccounts,
            null,
            _db.Employees);

        using var result = await factory.ConnectAsync(brokenAccount);
        Assert.False(result.IsSuccess);
        Assert.Equal(ImapConnectionStatus.DecryptionError, result.Status);
    }

    [Fact]
    public async Task PurgeUnsharedWebmailCacheAsync_RemovesStaleWebmailMessagesOnlyOnUnsharedServerManagedAccounts()
    {
        var unsharedBillAccount = new Spokes_Server.Core.Models.Communication.EmailAccount
        {
            Id = "acc-bill-dedicated",
            IsServerManaged = true,
            Name = "AP Dedicated",
            EmailAddress = "bills@acme.test"
        };
        _db.EmailAccounts.Save(unsharedBillAccount);

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-ingested-bill",
            UniqueId = 10,
            AccountId = unsharedBillAccount.Id,
            FolderPath = "INBOX",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            DecisionLog = new Spokes_Server.Core.Models.Accounting.BillInboxDecisionLog(),
            Subject = "New Unread Vendor Bill"
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-stale-legacy-read",
            UniqueId = 11,
            AccountId = unsharedBillAccount.Id,
            FolderPath = "INBOX",
            BillingStatus = CompanyBillInboxConstants.BillingStatus.Unprocessed,
            DecisionLog = null,
            IsRead = true,
            Subject = "2024 Old Read Message"
        });

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-stale-null-status",
            UniqueId = 12,
            AccountId = unsharedBillAccount.Id,
            FolderPath = "Archive",
            BillingStatus = null,
            IsRead = true,
            Subject = "Archived Webmail Message"
        });

        var sharedAccount = new Spokes_Server.Core.Models.Communication.EmailAccount
        {
            Id = "acc-shared-webmail",
            IsServerManaged = true,
            Name = "Shared Info",
            EmailAddress = "info@acme.test",
            AllowedUserIds = ["emp-1"]
        };
        _db.EmailAccounts.Save(sharedAccount);

        _db.EmailMessages.Save(new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-shared-keep",
            UniqueId = 20,
            AccountId = sharedAccount.Id,
            FolderPath = "INBOX",
            BillingStatus = null,
            IsRead = true,
            Subject = "General Webmail Message"
        });

        var purgedUnshared = await _inboxService.PurgeUnsharedWebmailCacheAsync(unsharedBillAccount.Id);
        var purgedShared = await _inboxService.PurgeUnsharedWebmailCacheAsync(sharedAccount.Id);

        Assert.Equal(2, purgedUnshared);
        Assert.Equal(0, purgedShared);

        Assert.NotNull(_db.EmailMessages.GetById(unsharedBillAccount.Id, "msg-ingested-bill"));
        Assert.Null(_db.EmailMessages.GetById(unsharedBillAccount.Id, "msg-stale-legacy-read"));
        Assert.Null(_db.EmailMessages.GetById(unsharedBillAccount.Id, "msg-stale-null-status"));
        Assert.NotNull(_db.EmailMessages.GetById(sharedAccount.Id, "msg-shared-keep"));

        _mockCacheMaintenance.Verify(c => c.DeleteLocalMessageFilesAsync(unsharedBillAccount.Id, "msg-stale-legacy-read"), Times.Once);
        _mockCacheMaintenance.Verify(c => c.DeleteLocalMessageFilesAsync(unsharedBillAccount.Id, "msg-stale-null-status"), Times.Once);
        _mockCacheMaintenance.Verify(c => c.DeleteLocalMessageFilesAsync(unsharedBillAccount.Id, "msg-ingested-bill"), Times.Never);
    }

    [Fact]
    public void LinkReceiptToInvoice_RejectsDraftInvoice()
    {
        var project = new Spokes_Server.Core.Models.Projects.Project { Id = "proj-bill-draft", Name = "Bill Draft" };
        _db.Projects.Save(project);

        var draft = new Spokes_Server.Core.Models.Accounting.Invoice
        {
            Id = "inv-bill-draft",
            ProjectId = project.Id,
            InvoiceNumber = "INV-BILL-DRAFT",
            Status = Spokes_Server.Core.Models.Accounting.InvoiceStatus.Draft,
            LineItems = [new Spokes_Server.Core.Models.Accounting.InvoiceLine { Quantity = 1m, UnitPrice = 250m }]
        };
        _db.Invoices.Save(draft);

        var msg = new Spokes_Server.Core.Models.Communication.EmailMessage
        {
            Id = "msg-receipt-draft",
            AccountId = "acc-receipt-draft",
            FolderPath = "INBOX",
            Subject = "Payment advice for INV-BILL-DRAFT"
        };
        _db.EmailMessages.Save(msg);

        var admin = new Spokes_Server.Core.Models.HR.Employee { Id = "emp-bill-admin", IsActive = true, IsAdmin = true };

        Assert.Throws<InvalidOperationException>(() =>
            _inboxService.LinkReceiptToInvoice(msg.Id, draft.Id, admin.Id, actor: admin));
    }
}
