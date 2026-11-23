using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailConfinementAndHtmlIsolationTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IConfiguration _config;
    private readonly Database _db;
    private readonly EncryptionService _encryption;
    private readonly EmailSyncStateService _syncState;
    private readonly EmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailInboxService _inboxService;
    private readonly EmailDraftService _draftService;
    private readonly EmailDeliveryService _deliveryService;
    private readonly CompanyBillInboxService _billInboxService;
    private readonly Mock<IImapConnectionFactory> _mockImapFactory;
    private readonly EmailFolderService _folderService;
    private readonly EmailMessageLifecycleService _lifecycleService;

    public EmailConfinementAndHtmlIsolationTests()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataPath , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var services = new ServiceCollection();
        services.AddSingleton(_config);
        services.AddLogging();
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _encryption = _serviceProvider.GetRequiredService<EncryptionService>();

        var profile = _db.CompanyProfile.GetOrCreate();
        profile.Edition = "Pro";
        _db.CompanyProfile.Save(profile);

        _syncState = new EmailSyncStateService();
        _cacheMaintenance = new EmailCacheMaintenanceService(
            _db.EmailFolders,
            _db.EmailMessages,
            _syncState,
            _config,
            logger: null,
            employees: _db.Employees,
            accounts: _db.EmailAccounts,
            teams: _db.Teams,
            companyProfile: _db.CompanyProfile);

        _inboxService = new EmailInboxService(
            _db.EmailAccounts,
            _db.EmailInboxes,
            _db.EmailSendingIdentities,
            _db.EmailFolders,
            _db.EmailMessages,
            _db.Employees,
            _db.Teams,
            _db.CompanyProfile,
            _syncState);

        _draftService = new EmailDraftService(
            _db.EmailFolders,
            _db.EmailMessages,
            _cacheMaintenance,
            _db.EmailAccounts,
            _db.EmailSendingIdentities,
            logger: null,
            employees: _db.Employees,
            companyProfile: _db.CompanyProfile);

        _mockImapFactory = new Mock<IImapConnectionFactory>(MockBehavior.Strict);

        _deliveryService = new EmailDeliveryService(
            _db.CompanyProfile,
            _db.EmailFolders,
            _encryption,
            _cacheMaintenance,
            _db.EmailMessages,
            _db.EmailSendingIdentities,
            _db.EmailInboxes,
            _db.EmailAccounts,
            logger: null,
            imapFactory: _mockImapFactory.Object,
            employees: _db.Employees,
            inboxService: _inboxService,
            teams: _db.Teams,
            projects: _db.Projects,
            projectGroups: _db.ProjectGroups,
            quotes: _db.Quotes,
            invoices: _db.Invoices,
            projectDocuments: _db.ProjectDocuments,
            projectReports: _db.ProjectReports,
            purchaseOrders: _db.PurchaseOrders,
            config: _config);

        _billInboxService = new CompanyBillInboxService(
            _db,
            new Mock<IFileService>().Object,
            new PdfInvoiceInspectionService(),
            new SmartPoMatchingEngine(_db),
            _encryption,
            _cacheMaintenance,
            _deliveryService);

        _folderService = new EmailFolderService(
            _db.CompanyProfile,
            _db.EmailFolders,
            _db.EmailMessages,
            _encryption,
            _db.EmailAccounts,
            logger: null,
            imapFactory: _mockImapFactory.Object,
            syncStateService: _syncState,
            employees: _db.Employees,
            teams: _db.Teams);

        _lifecycleService = new EmailMessageLifecycleService(
            _db.CompanyProfile,
            _db.EmailFolders,
            _db.EmailMessages,
            _encryption,
            _syncState,
            _cacheMaintenance,
            _db.EmailAccounts,
            logger: null,
            imapFactory: _mockImapFactory.Object,
            employees: _db.Employees,
            inboxService: _inboxService);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    private SendingServerConfig ConfigureLoopbackSmtpServer(int port, string id = "smtp-confinement-local")
    {
        var server = new SendingServerConfig
        {
            Id = id,
            Name = "Local Test SMTP",
            Host = "127.0.0.1",
            Port = port,
            UseSsl = false,
            UseSharedCredentials = true,
            SharedUsername = "relay-user",
            EncryptedSharedPassword = _encryption.Encrypt("relay-secret")
        };

        var profile = _db.CompanyProfile.GetOrCreate();
        profile.EmailSettings.SendingServers = [server];
        _db.CompanyProfile.Save(profile);
        return server;
    }

    // #9: CompanyBillInbox server-managed confinement and CanUserAccessEmailMessage fallback
    [Fact]
    public void CompanyBillInbox_RejectsPersonalBindings_AndEnforcesBillAccessFallback()
    {
        var admin = new Employee { Id = "emp-admin", FirstName = "Admin", IsAdmin = true, IsActive = true };
        var apUser = new Employee
        {
            Id = "emp-ap",
            FirstName = "AP",
            IsActive = true,
            Permissions = [AppPermissions.Purchases.ManageBills]
        };
        var regularUser = new Employee
        {
            Id = "emp-regular",
            FirstName = "Regular",
            IsActive = true,
            Permissions = [AppPermissions.Email.Use]
        };
        _db.Employees.Save(admin);
        _db.Employees.Save(apUser);
        _db.Employees.Save(regularUser);

        var personalAcc = new EmailAccount
        {
            Id = "acc-personal-bill",
            IsServerManaged = false,
            OwnerEmployeeId = regularUser.Id,
            EmailAddress = "regular@personal.test"
        };
        var personalInbox = new EmailInbox
        {
            Id = "inb-personal-bill",
            IsServerManaged = false,
            OwnerEmployeeId = regularUser.Id,
            AccountId = personalAcc.Id,
            Name = "Personal"
        };
        var personalIdentity = new EmailSendingIdentity
        {
            Id = "id-personal-bill",
            IsServerManaged = false,
            OwnerEmployeeId = regularUser.Id,
            Username = "regular",
            Domain = "personal.test"
        };
        _db.EmailAccounts.Save(personalAcc);
        _db.EmailInboxes.Save(personalInbox);
        _db.EmailSendingIdentities.Save(personalIdentity);

        var serverAcc = new EmailAccount
        {
            Id = "acc-server-bill",
            IsServerManaged = true,
            EmailAddress = "bills@company.test"
        };
        var serverVirtualInbox = new EmailInbox
        {
            Id = "inb-server-bill",
            IsServerManaged = true,
            AccountId = serverAcc.Id,
            Name = "AP Bills",
            TargetAddresses = ["bills@company.test"]
        };
        var serverIdentity = new EmailSendingIdentity
        {
            Id = "id-server-bill",
            IsServerManaged = true,
            SourceAccountId = serverAcc.Id,
            SourceType = EmailIdentitySourceType.RealInbox,
            Username = "bills",
            Domain = "company.test"
        };
        _db.EmailAccounts.Save(serverAcc);
        _db.EmailInboxes.Save(serverVirtualInbox);
        _db.EmailSendingIdentities.Save(serverIdentity);

        // Non-admin / non-ManageSettings caller cannot save settings
        Assert.Throws<UnauthorizedAccessException>(() =>
            _billInboxService.SaveSettings(new CompanyBillInboxSettings
            {
                Enabled = true,
                AccountId = serverAcc.Id
            }, apUser));

        // Admin cannot bind personal account, virtual inbox, or sending identity
        Assert.Throws<UnauthorizedAccessException>(() =>
            _billInboxService.SaveSettings(new CompanyBillInboxSettings
            {
                Enabled = true,
                AccountId = personalAcc.Id
            }, admin));

        Assert.Throws<UnauthorizedAccessException>(() =>
            _billInboxService.SaveSettings(new CompanyBillInboxSettings
            {
                Enabled = true,
                AccountId = serverAcc.Id,
                InboxId = personalInbox.Id
            }, admin));

        Assert.Throws<UnauthorizedAccessException>(() =>
            _billInboxService.SaveSettings(new CompanyBillInboxSettings
            {
                Enabled = true,
                AccountId = serverAcc.Id,
                SendingIdentityId = personalIdentity.Id
            }, admin));

        // Even if legacy/corrupted config stores personal IDs, resolvers return null
        var corruptedSettings = new CompanyBillInboxSettings
        {
            AccountId = personalAcc.Id,
            InboxId = personalInbox.Id,
            SendingIdentityId = personalIdentity.Id
        };
        Assert.Null(_billInboxService.GetBoundAccount(corruptedSettings));
        Assert.Null(_billInboxService.GetBoundVirtualInbox(corruptedSettings));
        Assert.Null(_billInboxService.ResolveSendingIdentity(corruptedSettings));

        // Save valid server-managed binding
        _billInboxService.SaveSettings(new CompanyBillInboxSettings
        {
            Enabled = true,
            AccountId = serverAcc.Id,
            InboxId = serverVirtualInbox.Id,
            SendingIdentityId = serverIdentity.Id,
            EmailAddress = "bills@company.test"
        }, admin);

        Assert.Equal(serverAcc.Id, _billInboxService.GetBoundAccount()?.Id);
        Assert.Equal(serverVirtualInbox.Id, _billInboxService.GetBoundVirtualInbox()?.Id);
        Assert.Equal(serverIdentity.Id, _billInboxService.ResolveSendingIdentity()?.Id);

        // Store one message matching bills@ virtual inbox and one outside it
        _db.EmailMessages.Save(new EmailMessage
        {
            Id = "msg-bill-match",
            AccountId = serverAcc.Id,
            FolderPath = "INBOX",
            ToAddresses = ["bills@company.test"],
            ReceivingAddresses = ["bills@company.test"]
        });
        _db.EmailMessages.Save(new EmailMessage
        {
            Id = "msg-bill-other",
            AccountId = serverAcc.Id,
            FolderPath = "INBOX",
            ToAddresses = ["hr@company.test"],
            ReceivingAddresses = ["hr@company.test"]
        });

        Assert.True(_inboxService.CanUserAccessEmailMessage(apUser, serverAcc.Id, "msg-bill-match"));
        Assert.False(_inboxService.CanUserAccessEmailMessage(apUser, serverAcc.Id, "msg-bill-other"));
        Assert.False(_inboxService.CanUserAccessEmailMessage(regularUser, serverAcc.Id, "msg-bill-match"));
    }

    // #13: FileAttachmentPaths restricted to IsSystemAutomatedSend + {DataPath}; DocumentAttachments & ForwardedEmailAttachments enforce ACLs + containment
    [Fact]
    public async Task AttachmentConfinement_EnforcesAutomatedFlagDataPathDocumentAclsAndForwardedAccess()
    {
        var alice = new Employee
        {
            Id = "emp-att-alice",
            FirstName = "Alice",
            IsActive = true,
            Permissions = [AppPermissions.Email.Use, AppPermissions.Projects.View]
        };
        var bob = new Employee
        {
            Id = "emp-att-bob",
            FirstName = "Bob",
            IsActive = true,
            Permissions = [AppPermissions.Email.Use, AppPermissions.Projects.View]
        };
        _db.Employees.Save(alice);
        _db.Employees.Save(bob);
        _db.EmailSendingIdentities.Save(new EmailSendingIdentity
        {
            Id = "id-att-alice",
            OwnerEmployeeId = alice.Id,
            SourceType = EmailIdentitySourceType.OutgoingServer,
            Username = "alice",
            Domain = "company.test"
        });

        var insideDataFile = Path.Combine(_testDataPath, "automated-report.txt");
        await File.WriteAllTextAsync(insideDataFile, "Inside DataPath");

        var outsideTempFile = Path.Combine(Path.GetTempPath(), $"spokes-outside-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(outsideTempFile, "Sensitive Outside File");

        try
        {
            // Interactive SendEmailAsync must reject FileAttachmentPaths even when file is inside DataPath
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.SendEmailAsync(new SendEmailRequest
                {
                    EmployeeId = alice.Id,
                    AccountId = alice.Id,
                    To = "ext@example.com",
                    Subject = "Interactive Arbitrary File Attempt",
                    Body = "<p>Test</p>",
                    FileAttachmentPaths = [(insideDataFile, "report.txt")]
                }));

            // BuildMimeMessageAsync interactive path rejects FileAttachmentPaths
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
                {
                    EmployeeId = alice.Id,
                    To = "ext@example.com",
                    Subject = "Direct Path",
                    Body = "<p>Test</p>",
                    FileAttachmentPaths = [(insideDataFile, "report.txt")]
                }, "Alice", "alice@company.test", null, alice, isAutomatedSystemSend: false));

            // Automated system send succeeds for canonical path under DataPath
            var automatedMime = await _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
            {
                To = "vendor@example.com",
                Subject = "Auto Bill Notice",
                Body = "<p>Notice</p>",
                FileAttachmentPaths = [(insideDataFile, "report.txt")]
            }, "AP", "ap@company.test", null, null, isAutomatedSystemSend: true);
            Assert.Single(automatedMime.Attachments);

            // Automated system send rejects paths escaping DataPath
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
                {
                    To = "vendor@example.com",
                    Subject = "Escape Attempt",
                    Body = "<p>Escape</p>",
                    FileAttachmentPaths = [(outsideTempFile, "leak.txt")]
                }, "AP", "ap@company.test", null, null, isAutomatedSystemSend: true));

            // DocumentAttachments enforce project ACLs and PDFs directory containment
            var pdfDir = Path.Combine(_testDataPath, "PDFs");
            Directory.CreateDirectory(pdfDir);
            var validQuotePdf = Path.Combine(pdfDir, "Q-100.pdf");
            await File.WriteAllBytesAsync(validQuotePdf, Encoding.ASCII.GetBytes("%PDF-1.4\n"));

            var restrictedProject = new Project
            {
                Id = "proj-restricted",
                Name = "Secret Project",
                AccessPolicy = ProjectAccessPolicy.Restricted,
                AllowedUserIds = [bob.Id]
            };
            _db.Projects.Save(restrictedProject);

            var quote = new Quote
            {
                Id = "quote-100",
                ProjectId = restrictedProject.Id,
                QuoteNumber = "Q-100",
                PdfPath = validQuotePdf
            };
            _db.Quotes.Save(quote);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
                {
                    EmployeeId = alice.Id,
                    To = "client@example.com",
                    Subject = "Quote",
                    Body = "<p>Hi</p>",
                    DocumentAttachments = [new OutboundDocumentAttachmentRef(OutboundDocumentKind.Quote, quote.Id)]
                }, "Alice", "alice@company.test", null, alice, isAutomatedSystemSend: false));

            var bobQuoteMime = await _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
            {
                EmployeeId = bob.Id,
                To = "client@example.com",
                Subject = "Quote",
                Body = "<p>Hi</p>",
                DocumentAttachments = [new OutboundDocumentAttachmentRef(OutboundDocumentKind.Quote, quote.Id)]
            }, "Bob", "bob@company.test", null, bob, isAutomatedSystemSend: false);
            Assert.Single(bobQuoteMime.Attachments);

            // ForwardedEmailAttachments enforce message access and email cache confinement
            var bobAccount = new EmailAccount
            {
                Id = "acc-fwd-bob",
                OwnerEmployeeId = bob.Id,
                EmailAddress = "bob@company.test"
            };
            _db.EmailAccounts.Save(bobAccount);

            var attMeta = new EmailAttachmentMeta
            {
                Id = "att-secret-1",
                FileName = "contract.pdf",
                ContentType = "application/pdf",
                Size = 12
            };
            var bobMsg = new EmailMessage
            {
                Id = "msg-fwd-bob",
                AccountId = bobAccount.Id,
                FolderPath = "INBOX",
                ToAddresses = ["bob@company.test"],
                Attachments = [attMeta]
            };
            _db.EmailMessages.Save(bobMsg);

            var cachedAttPath = _cacheMaintenance.GetAttachmentPath(bobAccount.Id, bobMsg.Id, attMeta.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedAttPath)!);
            await File.WriteAllBytesAsync(cachedAttPath, Encoding.UTF8.GetBytes("BOB-CONTRACT"));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
                {
                    EmployeeId = alice.Id,
                    To = "ext@example.com",
                    Subject = "Fwd",
                    Body = "<p>Fwd</p>",
                    ForwardedEmailAttachments =
                    [
                        new ForwardedEmailAttachmentRef(bobAccount.Id, bobMsg.Id, attMeta.Id, "contract.pdf")
                    ]
                }, "Alice", "alice@company.test", null, alice, isAutomatedSystemSend: false));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
                {
                    EmployeeId = bob.Id,
                    To = "ext@example.com",
                    Subject = "Fwd Escape",
                    Body = "<p>Fwd</p>",
                    ForwardedEmailAttachments =
                    [
                        new ForwardedEmailAttachmentRef(bobAccount.Id, "../escape", attMeta.Id, "contract.pdf")
                    ]
                }, "Bob", "bob@company.test", null, bob, isAutomatedSystemSend: false));

            var bobFwdMime = await _deliveryService.BuildMimeMessageAsync(new SendEmailRequest
            {
                EmployeeId = bob.Id,
                To = "ext@example.com",
                Subject = "Fwd OK",
                Body = "<p>Fwd</p>",
                ForwardedEmailAttachments =
                [
                    new ForwardedEmailAttachmentRef(bobAccount.Id, bobMsg.Id, attMeta.Id, "contract.pdf")
                ]
            }, "Bob", "bob@company.test", null, bob, isAutomatedSystemSend: false);
            Assert.Single(bobFwdMime.Attachments);
        }
        finally
        {
            if (File.Exists(outsideTempFile))
            {
                try { File.Delete(outsideTempFile); } catch { }
            }
        }
    }

    // #14: Personal Virtual Inbox and its sourced identities are revoked when backing account access is lost or archived
    [Fact]
    public void EmailInboxService_RevokesVirtualInboxAndIdentityAccess_WhenBackingAccountRevokedOrArchived()
    {
        var bob = new Employee { Id = "emp-bk-bob", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var alice = new Employee { Id = "emp-bk-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _db.Employees.Save(bob);
        _db.Employees.Save(alice);

        var sharedAcc = new EmailAccount
        {
            Id = "acc-bk-shared",
            OwnerEmployeeId = bob.Id,
            IsServerManaged = false,
            EmailAddress = "shared@company.test",
            AllowedUserIds = [alice.Id]
        };
        _db.EmailAccounts.Save(sharedAcc);

        var aliceVirtual = new EmailInbox
        {
            Id = "inb-bk-alice",
            AccountId = sharedAcc.Id,
            OwnerEmployeeId = alice.Id,
            IsServerManaged = false,
            Name = "Alice Alias",
            TargetAddresses = ["alias@company.test"]
        };
        var virtualIdentity = new EmailSendingIdentity
        {
            Id = "id-bk-virtual",
            OwnerEmployeeId = alice.Id,
            SourceType = EmailIdentitySourceType.VirtualInbox,
            SourceInboxId = aliceVirtual.Id,
            SourceAccountId = sharedAcc.Id,
            Username = "alias",
            Domain = "company.test"
        };
        aliceVirtual.LinkedIdentityIds = [virtualIdentity.Id];
        aliceVirtual.DefaultIdentityId = virtualIdentity.Id;

        _db.EmailInboxes.Save(aliceVirtual);
        _db.EmailSendingIdentities.Save(virtualIdentity);

        var msg = new EmailMessage
        {
            Id = "msg-bk-1",
            AccountId = sharedAcc.Id,
            FolderPath = "INBOX",
            ToAddresses = ["alias@company.test"],
            ReceivingAddresses = ["alias@company.test"]
        };
        _db.EmailMessages.Save(msg);

        // Authorized initially
        Assert.Single(_inboxService.GetAccessibleInboxes(alice));
        Assert.Single(_inboxService.GetPermittedIdentities(alice, inbox: aliceVirtual));
        Assert.True(_inboxService.CanUserAccessEmailMessage(alice, sharedAcc.Id, msg.Id));

        // Revoke Alice's access to backing Real Inbox
        sharedAcc.AllowedUserIds = [];
        _db.EmailAccounts.Save(sharedAcc);

        Assert.Empty(_inboxService.GetAccessibleInboxes(alice));
        Assert.Empty(_inboxService.GetPermittedIdentities(alice, inbox: aliceVirtual));
        Assert.DoesNotContain(_inboxService.GetPermittedIdentities(alice), i => i.Id == virtualIdentity.Id);
        Assert.False(_inboxService.CanUserAccessEmailMessage(alice, sharedAcc.Id, msg.Id));

        // Restore access but archive backing account -> still hidden
        sharedAcc.AllowedUserIds = [alice.Id];
        sharedAcc.IsArchived = true;
        _db.EmailAccounts.Save(sharedAcc);

        Assert.Empty(_inboxService.GetAccessibleInboxes(alice));
        Assert.Empty(_inboxService.GetPermittedIdentities(alice, inbox: aliceVirtual));
    }

    // #15: GetManageableInboxesForSettings only exposes user's own personal Virtual Inboxes
    [Fact]
    public void GetManageableInboxesForSettings_ExcludesSharedTeamAndServerManagedInboxes()
    {
        var team = new Team { Id = "team-eng", Name = "Engineering" };
        _db.Teams.Save(team);

        var alice = new Employee { Id = "emp-mng-alice", FirstName = "Alice", TeamId = team.Id, IsActive = true };
        var bob = new Employee { Id = "emp-mng-bob", FirstName = "Bob", IsActive = true };
        _db.Employees.Save(alice);
        _db.Employees.Save(bob);

        var ownedPersonal = new EmailInbox
        {
            Id = "inb-owned",
            AccountId = "acc-1",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = false,
            Name = "Alice Personal Alias"
        };
        var sharedFromBob = new EmailInbox
        {
            Id = "inb-shared-bob",
            AccountId = "acc-1",
            OwnerEmployeeId = bob.Id,
            IsServerManaged = false,
            AllowedUserIds = [alice.Id],
            Name = "Bob Shared Alias"
        };
        var teamFromBob = new EmailInbox
        {
            Id = "inb-team-bob",
            AccountId = "acc-1",
            OwnerEmployeeId = bob.Id,
            IsServerManaged = false,
            AllowedTeamIds = [team.Id],
            Name = "Engineering Team Alias"
        };
        var serverManaged = new EmailInbox
        {
            Id = "inb-server",
            AccountId = "acc-2",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = true,
            AllowedUserIds = [alice.Id],
            Name = "Server Support Inbox"
        };

        _db.EmailInboxes.Save(ownedPersonal);
        _db.EmailInboxes.Save(sharedFromBob);
        _db.EmailInboxes.Save(teamFromBob);
        _db.EmailInboxes.Save(serverManaged);

        var manageable = _inboxService.GetManageableInboxesForSettings(alice.Id);
        var only = Assert.Single(manageable);
        Assert.Equal(ownedPersonal.Id, only.Id);
    }

    // #16: Archive/delete mutations reject non-owners, non-admins on server-managed entities, and suspended callers
    [Fact]
    public void EntityMutations_EnforceOwnerAndAdminAuthorizationAndBlockSuspendedCallers()
    {
        var alice = new Employee { Id = "emp-mut-alice", FirstName = "Alice", IsActive = true };
        var bob = new Employee { Id = "emp-mut-bob", FirstName = "Bob", IsActive = true };
        _db.Employees.Save(alice);
        _db.Employees.Save(bob);

        var personalAcc = new EmailAccount { Id = "acc-mut-pers", OwnerEmployeeId = alice.Id, IsServerManaged = false };
        var personalInbox = new EmailInbox { Id = "inb-mut-pers", AccountId = personalAcc.Id, OwnerEmployeeId = alice.Id, IsServerManaged = false };
        var personalIdentity = new EmailSendingIdentity { Id = "id-mut-pers", OwnerEmployeeId = alice.Id, IsServerManaged = false };

        var serverAcc = new EmailAccount { Id = "acc-mut-srv", OwnerEmployeeId = "emp-admin", IsServerManaged = true };
        var serverInbox = new EmailInbox { Id = "inb-mut-srv", AccountId = serverAcc.Id, IsServerManaged = true, AllowedUserIds = [bob.Id] };
        var serverIdentity = new EmailSendingIdentity { Id = "id-mut-srv", IsServerManaged = true, AllowedUserIds = [bob.Id] };

        _db.EmailAccounts.Save(personalAcc);
        _db.EmailInboxes.Save(personalInbox);
        _db.EmailSendingIdentities.Save(personalIdentity);
        _db.EmailAccounts.Save(serverAcc);
        _db.EmailInboxes.Save(serverInbox);
        _db.EmailSendingIdentities.Save(serverIdentity);

        // Non-owner Bob rejected on Alice's personal entities
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.SetRealInboxArchived(personalAcc.Id, true, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteRealInbox(personalAcc.Id, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteVirtualInbox(personalInbox.Id, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteSendingIdentity(personalIdentity.Id, bob.Id));

        // Non-admin Bob rejected on server-managed entities
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.SetRealInboxArchived(serverAcc.Id, true, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteRealInbox(serverAcc.Id, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteVirtualInbox(serverInbox.Id, bob.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteSendingIdentity(serverIdentity.Id, bob.Id));

        // Suspended owner Alice rejected on her own personal entities
        alice.IsSuspended = true;
        _db.Employees.Save(alice);

        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.SetRealInboxArchived(personalAcc.Id, true, alice.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteRealInbox(personalAcc.Id, alice.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteVirtualInbox(personalInbox.Id, alice.Id));
        Assert.Throws<UnauthorizedAccessException>(() => _inboxService.DeleteSendingIdentity(personalIdentity.Id, alice.Id));

        // Verify none were modified or deleted
        Assert.False(_db.EmailAccounts.GetById(personalAcc.Id)!.IsArchived);
        Assert.NotNull(_db.EmailInboxes.GetById(personalInbox.Id));
        Assert.NotNull(_db.EmailSendingIdentities.GetById(personalIdentity.Id));
        Assert.NotNull(_db.EmailAccounts.GetById(serverAcc.Id));
    }

    // #18: EmailPrintSanitizer strips scripts, event handlers, dangerous URIs, and remote subresources unless allowed
    [Fact]
    public void EmailPrintSanitizer_StripsDangerousHtmlAndGatesRemoteSubresources()
    {
        const string maliciousHtml = """
            <div>
                <p onclick="alert('xss')">Printable text</p>
                <script>window.parent.location='https://evil.example/';</script>
                <iframe src="https://evil.example/frame.html"></iframe>
                <object data="https://evil.example/obj"></object>
                <a href="javascript:alert(1)">Evil Link</a>
                <a href="https://docs.example.com/guide">Safe Docs Link</a>
                <img src="https://tracker.example.com/pixel.png" alt="tracker" />
                <img src="data:text/html,<script>alert(2)</script>" alt="bad-data" />
                <img src="data:image/png;base64,iVBORw0KGgo=" alt="inline-logo" />
                <div style="background-image: url(https://tracker.example.com/bg.png)">Styled</div>
            </div>
            """;

        var lockedDown = EmailPrintSanitizer.SanitizeForPrint(maliciousHtml, allowRemoteImages: false);
        Assert.DoesNotContain("<script", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example.com", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Printable text", lockedDown, StringComparison.Ordinal);
        Assert.Contains("https://docs.example.com/guide", lockedDown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", lockedDown, StringComparison.OrdinalIgnoreCase);

        var withRemoteImages = EmailPrintSanitizer.SanitizeForPrint(maliciousHtml, allowRemoteImages: true);
        Assert.DoesNotContain("<script", withRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", withRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", withRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", withRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:text/html", withRemoteImages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://tracker.example.com/pixel.png", withRemoteImages, StringComparison.OrdinalIgnoreCase);
    }

    // #56: Family edition and missing Email.Use permission block interactive email features while system automated sends work in standard edition
    [Fact]
    public async Task EmailCapabilities_EnforceEditionAndEmailUsePermission_WhilePreservingSystemAutomatedSend()
    {
        using var smtp = new LocalSmtpTestServer();
        var server = ConfigureLoopbackSmtpServer(smtp.Port);

        var noPermUser = new Employee { Id = "emp-no-email-perm", FirstName = "NoPerm", IsActive = true, Permissions = [] };
        var licensedUser = new Employee
        {
            Id = "emp-with-email-perm",
            FirstName = "Licensed",
            IsActive = true,
            Permissions = [AppPermissions.Email.Use]
        };
        var admin = new Employee { Id = "emp-56-admin", FirstName = "Admin", IsAdmin = true, IsActive = true };
        _db.Employees.Save(noPermUser);
        _db.Employees.Save(licensedUser);
        _db.Employees.Save(admin);

        var userAccount = new EmailAccount
        {
            Id = "acc-56-user",
            OwnerEmployeeId = licensedUser.Id,
            EmailAddress = "licensed@company.test"
        };
        var sharedIdentity = new EmailSendingIdentity
        {
            Id = "id-56-shared",
            OwnerEmployeeId = licensedUser.Id,
            AllowedUserIds = [noPermUser.Id, licensedUser.Id],
            Username = "licensed",
            Domain = "company.test",
            SendingServerConfigId = server.Id
        };
        var billAcc = new EmailAccount
        {
            Id = "acc-56-bill",
            IsServerManaged = true,
            EmailAddress = "ap@company.test"
        };
        var billIdentity = new EmailSendingIdentity
        {
            Id = "id-56-bill",
            IsServerManaged = true,
            SourceType = EmailIdentitySourceType.RealInbox,
            SourceAccountId = billAcc.Id,
            Username = "ap",
            Domain = "company.test",
            SendingServerConfigId = server.Id
        };
        _db.EmailAccounts.Save(userAccount);
        _db.EmailSendingIdentities.Save(sharedIdentity);
        _db.EmailAccounts.Save(billAcc);
        _db.EmailSendingIdentities.Save(billIdentity);

        _billInboxService.SaveSettings(new CompanyBillInboxSettings
        {
            Enabled = true,
            AccountId = billAcc.Id,
            SendingIdentityId = billIdentity.Id,
            EmailAddress = "ap@company.test"
        }, admin);

        // User missing Email.Use in Pro edition is denied identities, drafts, and interactive send
        Assert.Empty(_inboxService.GetPermittedIdentities(noPermUser));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = userAccount.Id,
            EmployeeId = noPermUser.Id,
            To = "ext@example.com",
            Subject = "No Perm Draft"
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _deliveryService.SendEmailAsync(new SendEmailRequest
        {
            AccountId = userAccount.Id,
            EmployeeId = noPermUser.Id,
            SendingIdentityId = sharedIdentity.Id,
            To = "ext@example.com",
            Subject = "No Perm Send",
            Body = "<p>Denied</p>"
        }));

        // Automated system send succeeds in non-Family edition via SendSystemBillInboxEmailAsync
        await _deliveryService.SendSystemBillInboxEmailAsync(new SendEmailRequest
        {
            AccountId = CompanyBillInboxConstants.SystemEmployeeId,
            To = "vendor@example.com",
            Subject = "Automated System Notice",
            Body = "<p>Auto</p>"
        });
        Assert.Single(smtp.ReceivedMessages);

        // Switching Edition to Family locks out even a user holding Email.Use
        var profile = _db.CompanyProfile.GetOrCreate();
        profile.Edition = "Family";
        _db.CompanyProfile.Save(profile);

        Assert.Empty(_inboxService.GetPermittedIdentities(licensedUser));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = userAccount.Id,
            EmployeeId = licensedUser.Id,
            To = "ext@example.com",
            Subject = "Family Draft"
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _deliveryService.SendEmailAsync(new SendEmailRequest
        {
            AccountId = userAccount.Id,
            EmployeeId = licensedUser.Id,
            SendingIdentityId = sharedIdentity.Id,
            To = "ext@example.com",
            Subject = "Family Send",
            Body = "<p>Denied</p>"
        }));
    }

    // #57: ResolveTargetAccountId routes Sent messages to identity's source mailbox and clears mismatched custom identities
    [Fact]
    public void ResolveTargetAccountId_UsesIdentitySourceMailbox_AndClearsMismatchedCustomIdentityTarget()
    {
        var alice = new Employee { Id = "emp-57-alice", FirstName = "Alice", IsActive = true };
        var bob = new Employee { Id = "emp-57-bob", FirstName = "Bob", IsActive = true };
        var carol = new Employee { Id = "emp-57-carol", FirstName = "Carol", IsActive = true };
        _db.Employees.Save(alice);
        _db.Employees.Save(bob);
        _db.Employees.Save(carol);

        var aliceAccount = new EmailAccount { Id = "acc-57-alice", OwnerEmployeeId = alice.Id, IsServerManaged = false };
        var bobAccount = new EmailAccount { Id = "acc-57-bob", OwnerEmployeeId = bob.Id, IsServerManaged = false };
        var backingAccount = new EmailAccount { Id = "acc-57-backing", IsServerManaged = true };
        _db.EmailAccounts.Save(aliceAccount);
        _db.EmailAccounts.Save(bobAccount);
        _db.EmailAccounts.Save(backingAccount);

        var virtualInbox = new EmailInbox { Id = "inb-57-virt", AccountId = backingAccount.Id, IsServerManaged = true };
        _db.EmailInboxes.Save(virtualInbox);

        var realSourcedIdentity = new EmailSendingIdentity
        {
            Id = "id-57-real",
            SourceType = EmailIdentitySourceType.RealInbox,
            SourceAccountId = backingAccount.Id
        };
        var virtualSourcedIdentity = new EmailSendingIdentity
        {
            Id = "id-57-virt",
            SourceType = EmailIdentitySourceType.VirtualInbox,
            SourceInboxId = virtualInbox.Id
        };
        var customIdentity = new EmailSendingIdentity
        {
            Id = "id-57-custom",
            SourceType = EmailIdentitySourceType.OutgoingServer,
            OwnerEmployeeId = alice.Id
        };
        var carolCustomIdentity = new EmailSendingIdentity
        {
            Id = "id-57-carol-custom",
            SourceType = EmailIdentitySourceType.OutgoingServer,
            OwnerEmployeeId = carol.Id
        };
        _db.EmailSendingIdentities.Save(realSourcedIdentity);
        _db.EmailSendingIdentities.Save(virtualSourcedIdentity);
        _db.EmailSendingIdentities.Save(customIdentity);
        _db.EmailSendingIdentities.Save(carolCustomIdentity);

        Assert.Equal(backingAccount.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = aliceAccount.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, realSourcedIdentity));

        Assert.Equal(backingAccount.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = aliceAccount.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, virtualSourcedIdentity));

        // Custom identity with request.AccountId pointing to another user's mailbox rejects bobAccount.Id
        // and resolves to alice's own unshared personal mailbox instead
        Assert.Equal(aliceAccount.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = bobAccount.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, customIdentity));

        // Employee with no unshared personal mailbox and a mismatched request.AccountId returns null
        Assert.Null(_deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = carol.Id,
            AccountId = bobAccount.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, carolCustomIdentity));

        // Shared personal mailbox owned by carol must NOT receive Sent mail from standalone OutgoingServer identity
        var carolSharedPersonal = new EmailAccount
        {
            Id = "acc-57-carol-shared",
            OwnerEmployeeId = carol.Id,
            IsServerManaged = false,
            AllowedUserIds = [bob.Id]
        };
        _db.EmailAccounts.Save(carolSharedPersonal);

        Assert.Null(_deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = carol.Id,
            AccountId = carolSharedPersonal.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, carolCustomIdentity));

        // Custom identity with owner's own unarchived unshared personal request.AccountId records in owner's mailbox
        Assert.Equal(aliceAccount.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = aliceAccount.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, customIdentity));
    }

    // #58: Suspended employees cannot perform lifecycle, folder, or cache-repair mutations
    [Fact]
    public async Task SuspendedEmployee_IsDeniedLifecycleFolderAndCacheRepairMutations()
    {
        var suspended = new Employee
        {
            Id = "emp-58-susp",
            FirstName = "Suspended",
            IsActive = true,
            IsSuspended = true,
            Permissions = [AppPermissions.Email.Use]
        };
        _db.Employees.Save(suspended);

        var acc = new EmailAccount
        {
            Id = "acc-58",
            OwnerEmployeeId = suspended.Id,
            EmailAddress = "susp@company.test"
        };
        _db.EmailAccounts.Save(acc);

        _db.EmailFolders.Save(new EmailFolder { Id = "f-58", AccountId = acc.Id, Path = "INBOX", IsInbox = true });
        _db.EmailMessages.Save(new EmailMessage { Id = "msg-58", AccountId = acc.Id, FolderPath = "INBOX" });

        var mimeFile = Path.Combine(_testDataPath, "EmailAccounts", acc.Id, "Mime", "msg-58.eml");
        Directory.CreateDirectory(Path.GetDirectoryName(mimeFile)!);
        await File.WriteAllTextAsync(mimeFile, "From: a@b.c\r\n\r\nhi");

        var readRes = await _lifecycleService.MarkMessageReadAsync(acc.Id, "msg-58", true, suspended.Id);
        Assert.False(readRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", readRes.Code);

        var flagRes = await _lifecycleService.MarkMessageFlaggedAsync(acc.Id, "msg-58", true, suspended.Id);
        Assert.False(flagRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", flagRes.Code);

        var moveRes = await _lifecycleService.MoveMessageAsync(acc.Id, "msg-58", "Archive", suspended.Id);
        Assert.False(moveRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", moveRes.Code);

        var delMsgRes = await _lifecycleService.DeleteMessageAsync(acc.Id, "msg-58", suspended.Id);
        Assert.False(delMsgRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", delMsgRes.Code);

        var createFldRes = await _folderService.CreateFolderAsync(acc.Id, "", "NewFld", suspended.Id);
        Assert.False(createFldRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", createFldRes.Code);

        var renameFldRes = await _folderService.RenameFolderAsync(acc.Id, "Old", "New", suspended.Id);
        Assert.False(renameFldRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", renameFldRes.Code);

        var delFldRes = await _folderService.DeleteFolderAsync(acc.Id, "Old", suspended.Id);
        Assert.False(delFldRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", delFldRes.Code);

        bool syncCalled = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cacheMaintenance.RepairEmailInstallationAsync(acc.Id, (_, _) =>
            {
                syncCalled = true;
                return Task.CompletedTask;
            }, suspended.Id));

        Assert.False(syncCalled);
        Assert.True(File.Exists(mimeFile));
        _mockImapFactory.VerifyNoOtherCalls();
    }

    // #57: Primary root INBOX confinement excludes nested Archive/INBOX and non-INBOX folders for Virtual Inbox users
    [Fact]
    public void PrimaryRootInbox_ExcludesNestedInboxAndNonInboxFolders_ForVirtualInboxAccessAndNotifications()
    {
        var owner = new Employee { Id = "emp-57root-owner", FirstName = "Owner", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var viUser = new Employee { Id = "emp-57root-vi", FirstName = "VIUser", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _db.Employees.Save(owner);
        _db.Employees.Save(viUser);

        var acc = new EmailAccount
        {
            Id = "acc-57root",
            OwnerEmployeeId = owner.Id,
            AllowedUserIds = [owner.Id],
            IsServerManaged = true,
            EmailAddress = "catchall@company.test"
        };
        _db.EmailAccounts.Save(acc);

        _db.EmailFolders.Save(new EmailFolder { Id = "f-root", AccountId = acc.Id, Name = "INBOX", Path = "INBOX", ParentPath = "", IsInbox = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-nested-inbox", AccountId = acc.Id, Name = "INBOX", Path = "Archive/INBOX", ParentPath = "Archive", IsInbox = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-sent", AccountId = acc.Id, Name = "Sent", Path = "Sent", IsSent = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-arch", AccountId = acc.Id, Name = "Archive", Path = "Archive", IsArchive = true });

        var supportInbox = new EmailInbox
        {
            Id = "inb-57root-support",
            AccountId = acc.Id,
            IsServerManaged = true,
            Name = "Support",
            TargetAddresses = ["support@company.test"],
            AllowedUserIds = [viUser.Id]
        };
        _db.EmailInboxes.Save(supportInbox);

        var msgRoot = new EmailMessage
        {
            Id = "msg-57-root",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            IsRead = false,
            ToAddresses = ["support@company.test"],
            ReceivingAddresses = ["support@company.test"]
        };
        var msgNested = new EmailMessage
        {
            Id = "msg-57-nested",
            AccountId = acc.Id,
            FolderPath = "Archive/INBOX",
            IsRead = false,
            ToAddresses = ["support@company.test"],
            ReceivingAddresses = ["support@company.test"]
        };
        var msgSent = new EmailMessage
        {
            Id = "msg-57-sent",
            AccountId = acc.Id,
            FolderPath = "Sent",
            IsRead = false,
            ToAddresses = ["support@company.test"],
            ReceivingAddresses = ["support@company.test"]
        };
        var msgArch = new EmailMessage
        {
            Id = "msg-57-arch",
            AccountId = acc.Id,
            FolderPath = "Archive",
            IsRead = false,
            ToAddresses = ["support@company.test"],
            ReceivingAddresses = ["support@company.test"]
        };
        _db.EmailMessages.Save(msgRoot);
        _db.EmailMessages.Save(msgNested);
        _db.EmailMessages.Save(msgSent);
        _db.EmailMessages.Save(msgArch);

        Assert.True(_inboxService.IsPrimaryRootInboxMessage(msgRoot));
        Assert.False(_inboxService.IsPrimaryRootInboxMessage(msgNested));
        Assert.False(_inboxService.IsPrimaryRootInboxMessage(msgSent));
        Assert.False(_inboxService.IsPrimaryRootInboxMessage(msgArch));

        Assert.True(_inboxService.CanUserAccessEmailMessage(viUser, acc.Id, msgRoot.Id));
        Assert.False(_inboxService.CanUserAccessEmailMessage(viUser, acc.Id, msgNested.Id));
        Assert.False(_inboxService.CanUserAccessEmailMessage(viUser, acc.Id, msgSent.Id));
        Assert.False(_inboxService.CanUserAccessEmailMessage(viUser, acc.Id, msgArch.Id));

        // Full owner of the Real Inbox retains access across all folders
        Assert.True(_inboxService.CanUserAccessEmailMessage(owner, acc.Id, msgNested.Id));

        var viMessages = _inboxService.GetInboxMessages(supportInbox, 50);
        Assert.Single(viMessages);
        Assert.Equal(msgRoot.Id, viMessages[0].Id);
        Assert.Equal(1, _inboxService.GetUnreadCountForInbox(supportInbox));

        Assert.Contains(viUser.Id, _inboxService.GetAuthorizedEmployeeIdsForMessage(msgRoot));
        Assert.DoesNotContain(viUser.Id, _inboxService.GetAuthorizedEmployeeIdsForMessage(msgNested));
        Assert.DoesNotContain(viUser.Id, _inboxService.GetAuthorizedEmployeeIdsForMessage(msgSent));
        Assert.DoesNotContain(viUser.Id, _inboxService.GetAuthorizedEmployeeIdsForMessage(msgArch));
    }

    // #59: Virtual Inbox user may trash/archive/junk root INBOX messages, but cannot arbitrarily MoveMessageAsync or mutate folders
    [Fact]
    public async Task VirtualInboxUser_AllowsSpecialFolderActions_AndBlocksArbitraryMoveAndFolderMutations()
    {
        var viUser = new Employee { Id = "emp-59-vi", FirstName = "VIUser", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _db.Employees.Save(viUser);

        var acc = new EmailAccount { Id = "acc-59", IsServerManaged = true, EmailAddress = "team@company.test" };
        _db.EmailAccounts.Save(acc);

        _db.EmailFolders.Save(new EmailFolder { Id = "f-59-in", AccountId = acc.Id, Name = "INBOX", Path = "INBOX", ParentPath = "", IsInbox = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-59-trash", AccountId = acc.Id, Name = "Trash", Path = "Trash", IsTrash = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-59-arch", AccountId = acc.Id, Name = "Archive", Path = "Archive", IsArchive = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-59-junk", AccountId = acc.Id, Name = "Junk", Path = "Junk", IsJunk = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-59-custom", AccountId = acc.Id, Name = "Receipts", Path = "Receipts" });

        _db.EmailInboxes.Save(new EmailInbox
        {
            Id = "inb-59",
            AccountId = acc.Id,
            IsServerManaged = true,
            TargetAddresses = ["support@company.test"],
            AllowedUserIds = [viUser.Id]
        });

        _db.EmailMessages.Save(new EmailMessage
        {
            Id = "msg-59-root",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            ToAddresses = ["support@company.test"],
            ReceivingAddresses = ["support@company.test"]
        });

        var looseImap = new Mock<IImapConnectionFactory>();
        looseImap.Setup(f => f.CreateAuthenticatedClientAsync(acc.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MailKit.Net.Imap.ImapClient?)null);

        var lifecycle = new EmailMessageLifecycleService(
            _db.CompanyProfile,
            _db.EmailFolders,
            _db.EmailMessages,
            _encryption,
            _syncState,
            _cacheMaintenance,
            _db.EmailAccounts,
            logger: null,
            imapFactory: looseImap.Object,
            employees: _db.Employees,
            inboxService: _inboxService);

        // Arbitrary MoveMessageAsync is denied at authorization BEFORE IMAP is ever called
        var moveRes = await lifecycle.MoveMessageAsync(acc.Id, "msg-59-root", "Receipts", viUser.Id);
        Assert.False(moveRes.IsSuccess);
        Assert.Equal("ACCESS_DENIED", moveRes.Code);
        looseImap.Verify(f => f.CreateAuthenticatedClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Dedicated special-folder lifecycle actions pass authorization (reaching IMAP connection step -> IMAP_ERROR, not ACCESS_DENIED)
        var trashRes = await lifecycle.MoveMessageToTrashAsync(acc.Id, "msg-59-root", viUser.Id);
        var archRes = await lifecycle.ArchiveMessageAsync(acc.Id, "msg-59-root", viUser.Id);
        var junkRes = await lifecycle.JunkMessageAsync(acc.Id, "msg-59-root", viUser.Id);
        Assert.Equal("IMAP_ERROR", trashRes.Code);
        Assert.Equal("IMAP_ERROR", archRes.Code);
        Assert.Equal("IMAP_ERROR", junkRes.Code);
        looseImap.Verify(f => f.CreateAuthenticatedClientAsync(acc.Id, It.IsAny<CancellationToken>()), Times.Exactly(3));

        // Folder mutations remain blocked for Virtual-Inbox-only callers
        var createRes = await _folderService.CreateFolderAsync(acc.Id, "", "NewFld", viUser.Id);
        var renameRes = await _folderService.RenameFolderAsync(acc.Id, "Receipts", "Moved", viUser.Id);
        var delRes = await _folderService.DeleteFolderAsync(acc.Id, "Receipts", viUser.Id);
        Assert.Equal("ACCESS_DENIED", createRes.Code);
        Assert.Equal("ACCESS_DENIED", renameRes.Code);
        Assert.Equal("ACCESS_DENIED", delRes.Code);
    }

    // #56 & #61: Private unshared OutgoingServer identity cannot bind to shared/server-managed mailboxes
    [Fact]
    public void OutgoingServerIdentity_CannotBindToSharedOrServerManagedMailbox_AndDoesNotCopySentIntoSharedMailbox()
    {
        var alice = new Employee { Id = "emp-61-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-61-bob", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _db.Employees.Save(alice);
        _db.Employees.Save(bob);

        var privateOutgoing = new EmailSendingIdentity
        {
            Id = "id-61-priv",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = false,
            SourceType = EmailIdentitySourceType.OutgoingServer,
            Username = "alice.priv",
            Domain = "personal.test"
        };
        _db.EmailSendingIdentities.Save(privateOutgoing);

        var aliceUnsharedPersonal = new EmailAccount
        {
            Id = "acc-61-alice-priv",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = false
        };
        var aliceSharedPersonal = new EmailAccount
        {
            Id = "acc-61-alice-shared",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = false,
            AllowedUserIds = [bob.Id]
        };
        var bobUnsharedPersonal = new EmailAccount
        {
            Id = "acc-61-bob-priv",
            OwnerEmployeeId = bob.Id,
            IsServerManaged = false
        };
        var serverManagedShared = new EmailAccount
        {
            Id = "acc-61-server",
            OwnerEmployeeId = alice.Id,
            IsServerManaged = true,
            AllowedUserIds = [alice.Id, bob.Id],
            LinkedIdentityIds = [privateOutgoing.Id],
            DefaultIdentityId = privateOutgoing.Id
        };
        _db.EmailAccounts.Save(aliceUnsharedPersonal);
        _db.EmailAccounts.Save(aliceSharedPersonal);
        _db.EmailAccounts.Save(bobUnsharedPersonal);
        _db.EmailAccounts.Save(serverManagedShared);

        Assert.True(EmailInboxService.CanMailboxLinkIdentity(privateOutgoing, aliceUnsharedPersonal.IsServerManaged, aliceUnsharedPersonal.OwnerEmployeeId));
        Assert.False(EmailInboxService.CanMailboxLinkIdentity(privateOutgoing, bobUnsharedPersonal.IsServerManaged, bobUnsharedPersonal.OwnerEmployeeId));
        Assert.False(EmailInboxService.CanMailboxLinkIdentity(privateOutgoing, serverManagedShared.IsServerManaged, serverManagedShared.OwnerEmployeeId));

        // Even with tampered LinkedIdentityIds/DefaultIdentityId on serverManagedShared, Bob cannot see Alice's private identity
        var bobPermitted = _inboxService.GetPermittedIdentities(bob, realInbox: serverManagedShared);
        Assert.DoesNotContain(bobPermitted, i => i.Id == privateOutgoing.Id);

        // Delivery refuses serverManagedShared & aliceSharedPersonal as Sent target for privateOutgoing and isolates Sent copy to alice's unshared personal mailbox
        Assert.Equal(aliceUnsharedPersonal.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = aliceSharedPersonal.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, privateOutgoing));
        Assert.Equal(aliceUnsharedPersonal.Id, _deliveryService.ResolveTargetAccountId(new SendEmailRequest
        {
            EmployeeId = alice.Id,
            AccountId = serverManagedShared.Id,
            To = "ext@example.com",
            Subject = "S",
            Body = "B"
        }, privateOutgoing));
    }

    // #60: Family edition & Email.Use enforced across EmailFolderService, EmailDraftService.RemoveDraftAttachmentAsync, and EmailNotificationService
    [Fact]
    public async Task EmailServices_EnforceFamilyEditionAndEmailUseAcrossFoldersDraftsAndNotifications()
    {
        var noPerm = new Employee { Id = "emp-60-noperm", FirstName = "NoPerm", IsActive = true, Permissions = [] };
        var licensed = new Employee { Id = "emp-60-lic", FirstName = "Licensed", IsActive = true, Permissions = [AppPermissions.Email.Use], EmailNotificationsEnabled = true };
        _db.Employees.Save(noPerm);
        _db.Employees.Save(licensed);

        var noPermAcc = new EmailAccount { Id = "acc-60-noperm", OwnerEmployeeId = noPerm.Id };
        var licAcc = new EmailAccount { Id = "acc-60-lic", OwnerEmployeeId = licensed.Id };
        var archivedLicAcc = new EmailAccount { Id = "acc-60-arch", OwnerEmployeeId = licensed.Id, IsArchived = true };
        _db.EmailAccounts.Save(noPermAcc);
        _db.EmailAccounts.Save(licAcc);
        _db.EmailAccounts.Save(archivedLicAcc);

        _db.EmailFolders.Save(new EmailFolder { Id = "f-60-1", AccountId = licAcc.Id, Name = "INBOX", Path = "INBOX", IsInbox = true });
        _db.EmailMessages.Save(new EmailMessage { Id = "msg-60-unseen", AccountId = licAcc.Id, FolderPath = "INBOX", IsRead = false });
        _db.EmailMessages.Save(new EmailMessage
        {
            Id = "draft-60",
            AccountId = licAcc.Id,
            FolderPath = "LocalDrafts",
            CreatedByEmployeeId = licensed.Id,
            Attachments = [new EmailAttachmentMeta { Id = "att-60", FileName = "a.txt" }]
        });

        // Missing Email.Use denies folder mutations
        Assert.Equal("ACCESS_DENIED", (await _folderService.CreateFolderAsync(noPermAcc.Id, "", "Sub", noPerm.Id)).Code);
        // Archived Real Inbox denies folder mutations even for owner
        Assert.Equal("ACCESS_DENIED", (await _folderService.CreateFolderAsync(archivedLicAcc.Id, "", "Sub", licensed.Id)).Code);

        // EmailNotificationService ignores user without Email.Use
        var mockPresenter = new Mock<IUiNotificationPresenter>();
        await using var notifNoPerm = new EmailNotificationService(
            mockPresenter.Object, _syncState, _db.EmailFolders, _db.EmailMessages, _inboxService,
            employees: _db.Employees, companyProfile: _db.CompanyProfile);
        await notifNoPerm.InitializeAsync(noPerm.Id);
        Assert.Equal(0, notifNoPerm.TotalUnreadCount);

        // Eligible user subscribes & receives unread count
        await using var notifLicensed = new EmailNotificationService(
            mockPresenter.Object, _syncState, _db.EmailFolders, _db.EmailMessages, _inboxService,
            employees: _db.Employees, companyProfile: _db.CompanyProfile);
        await notifLicensed.InitializeAsync(licensed.Id);
        Assert.Equal(1, notifLicensed.TotalUnreadCount);

        // Suspended or disabled EmailNotificationsEnabled suppresses popup alert while keeping badge count
        licensed.IsSuspended = true;
        _db.Employees.Save(licensed);
        _syncState.NotifyNewEmail(licAcc.Id, new EmailMessage { Id = "msg-60-susp", AccountId = licAcc.Id, FolderPath = "INBOX", IsRead = false, FromName = "X", Subject = "Y" });
        mockPresenter.Verify(p => p.ShowEmailNotification(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

        // Switching Edition to Family locks down RemoveDraftAttachmentAsync, FolderService, and clears EmailNotificationService badges
        licensed.IsSuspended = false;
        _db.Employees.Save(licensed);
        var profile = _db.CompanyProfile.GetOrCreate();
        profile.Edition = "Family";
        _db.CompanyProfile.Save(profile);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.RemoveDraftAttachmentAsync(licAcc.Id, "draft-60", "att-60", licensed.Id));
        Assert.Equal("ACCESS_DENIED", (await _folderService.CreateFolderAsync(licAcc.Id, "", "Sub", licensed.Id)).Code);

        notifLicensed.LoadUnreadCount();
        Assert.Equal(0, notifLicensed.TotalUnreadCount);
        _syncState.NotifyNewEmail(licAcc.Id, new EmailMessage { Id = "msg-60-fam", AccountId = licAcc.Id, FolderPath = "INBOX", IsRead = false, FromName = "X", Subject = "Y" });
        mockPresenter.Verify(p => p.ShowEmailNotification(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    // #63: EmailMessage does not persist shared remote-image preference
    [Fact]
    public void EmailMessage_DoesNotPersistAllowRemoteImages()
    {
        Assert.Null(typeof(EmailMessage).GetProperty("AllowRemoteImages"));
    }

    // #64: RepairEmailInstallationAsync enforces Real Inbox caller auth, Edition/Permission gates, and preserves draft MIME files
    [Fact]
    public async Task CacheMaintenance_RepairEmailInstallation_EnforcesAuthorizationAndPreservesLocalDraftMimeFiles()
    {
        var owner = new Employee { Id = "emp-64-owner", FirstName = "Owner", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var viOnly = new Employee { Id = "emp-64-vi", FirstName = "VIUser", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var noPerm = new Employee { Id = "emp-64-noperm", FirstName = "NoPerm", IsActive = true, Permissions = [] };
        _db.Employees.Save(owner);
        _db.Employees.Save(viOnly);
        _db.Employees.Save(noPerm);

        var acc = new EmailAccount { Id = "acc-64", OwnerEmployeeId = owner.Id, AllowedUserIds = [owner.Id], IsServerManaged = true, EmailAddress = "box@company.test" };
        var archivedAcc = new EmailAccount { Id = "acc-64-arch", OwnerEmployeeId = owner.Id, AllowedUserIds = [owner.Id], IsArchived = true, EmailAddress = "arch@company.test" };
        _db.EmailAccounts.Save(acc);
        _db.EmailAccounts.Save(archivedAcc);

        _db.EmailInboxes.Save(new EmailInbox { Id = "inb-64", AccountId = acc.Id, IsServerManaged = true, TargetAddresses = ["vi@company.test"], AllowedUserIds = [viOnly.Id] });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-64-in", AccountId = acc.Id, Path = "INBOX", IsInbox = true });
        _db.EmailFolders.Save(new EmailFolder { Id = "f-64-drafts", AccountId = acc.Id, Path = "Drafts", IsDrafts = true });

        _db.EmailMessages.Save(new EmailMessage { Id = "msg-64-cached", AccountId = acc.Id, FolderPath = "INBOX", UniqueId = 10 });
        _db.EmailMessages.Save(new EmailMessage { Id = "Draft_64_Local", AccountId = acc.Id, FolderPath = "LocalDrafts", CreatedByEmployeeId = owner.Id });
        _db.EmailMessages.Save(new EmailMessage { Id = "msg-64-imap-draft", AccountId = acc.Id, FolderPath = "Drafts", UniqueId = 20 });

        var mimeDir = Path.Combine(_testDataPath, "EmailAccounts", acc.Id, "Mime");
        var msgDir = Path.Combine(_testDataPath, "EmailAccounts", acc.Id, "Messages");
        Directory.CreateDirectory(mimeDir);
        Directory.CreateDirectory(msgDir);
        var cachedMime = Path.Combine(mimeDir, "msg-64-cached.eml");
        var localDraftMime = Path.Combine(mimeDir, "Draft_64_Local.eml");
        var imapDraftMime = Path.Combine(mimeDir, "msg-64-imap-draft.eml");
        var cachedMsgJson = Path.Combine(msgDir, "msg-64-cached.json");
        var localDraftMsgJson = Path.Combine(msgDir, "Draft_64_Local.json");
        await File.WriteAllTextAsync(cachedMime, "cached");
        await File.WriteAllTextAsync(localDraftMime, "local-draft");
        await File.WriteAllTextAsync(imapDraftMime, "imap-draft");
        await File.WriteAllTextAsync(cachedMsgJson, "{\"id\":\"msg-64-cached\"}");
        await File.WriteAllTextAsync(localDraftMsgJson, "{\"id\":\"Draft_64_Local\"}");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cacheMaintenance.RepairEmailInstallationAsync(acc.Id, (_, _) => Task.CompletedTask, viOnly.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cacheMaintenance.RepairEmailInstallationAsync(acc.Id, (_, _) => Task.CompletedTask, noPerm.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cacheMaintenance.RepairEmailInstallationAsync(archivedAcc.Id, (_, _) => Task.CompletedTask, owner.Id));

        var profile = _db.CompanyProfile.GetOrCreate();
        profile.Edition = "Family";
        _db.CompanyProfile.Save(profile);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cacheMaintenance.RepairEmailInstallationAsync(acc.Id, (_, _) => Task.CompletedTask, owner.Id));

        profile.Edition = "Business";
        _db.CompanyProfile.Save(profile);

        bool syncCalled = false;
        await _cacheMaintenance.RepairEmailInstallationAsync(acc.Id, (_, _) => { syncCalled = true; return Task.CompletedTask; }, owner.Id);

        Assert.True(syncCalled);
        Assert.False(File.Exists(cachedMime));
        Assert.False(File.Exists(cachedMsgJson));
        Assert.True(File.Exists(localDraftMime));
        Assert.True(File.Exists(imapDraftMime));
        Assert.True(File.Exists(localDraftMsgJson));
    }

    private sealed class LocalSmtpTestServer : IDisposable
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public ConcurrentQueue<string> ReceivedMessages { get; } = new();
        public int Port { get; }

        public LocalSmtpTestServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
            {
                await writer.WriteLineAsync("220 localhost ESMTP SpokesTestServer");
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("AUTH ", StringComparison.OrdinalIgnoreCase))
                    {
                        if (line.Trim().Equals("AUTH PLAIN", StringComparison.OrdinalIgnoreCase)) _ = await reader.ReadLineAsync(ct);
                        else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("334 VXNlcm5hbWU6"); _ = await reader.ReadLineAsync(ct); await writer.WriteLineAsync("334 UGFzc3dvcmQ6"); _ = await reader.ReadLineAsync(ct); }
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 End data");
                        var data = new StringBuilder();
                        while (true)
                        {
                            var dl = await reader.ReadLineAsync(ct);
                            if (dl == null || dl == ".") break;
                            data.Append(dl.StartsWith("..", StringComparison.Ordinal) ? dl[1..] : dl).Append("\r\n");
                        }
                        ReceivedMessages.Enqueue(data.ToString());
                        await writer.WriteLineAsync("250 2.0.0 Queued");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 Bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 OK");
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}
