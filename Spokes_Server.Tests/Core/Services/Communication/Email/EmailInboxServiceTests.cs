using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailInboxServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    private readonly EmailAccountRepository _accounts;
    private readonly EmailInboxRepository _inboxes;
    private readonly EmailSendingIdentityRepository _identities;
    private readonly EmailFolderRepository _folders;
    private readonly EmailMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly EmailInboxService _service;

    public EmailInboxServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailInbox_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _accounts = new EmailAccountRepository(_persistence, _config);
        _inboxes = new EmailInboxRepository(_persistence, _config);
        _identities = new EmailSendingIdentityRepository(_persistence, _config);
        _folders = new EmailFolderRepository(_persistence, _config);
        _messages = new EmailMessageRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);

        _service = new EmailInboxService(
            _accounts,
            _inboxes,
            _identities,
            _folders,
            _messages,
            _employees,
            _teams);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void AddressAndDomainRouting_FiltersVirtualInboxesAndPreservesFullRealInbox()
    {
        var acc = new EmailAccount
        {
            Id = "acc-main",
            OwnerEmployeeId = "emp-owner",
            Name = "Work Mailbox",
            EmailAddress = "owner@spokes.sh"
        };
        _accounts.Save(acc);

        var inboxFolder = new EmailFolder
        {
            Id = "fld-inbox",
            AccountId = acc.Id,
            Name = "INBOX",
            Path = "INBOX",
            IsInbox = true
        };
        _folders.Save(inboxFolder);

        var supportInbox = new EmailInbox
        {
            Id = "inb-support",
            AccountId = acc.Id,
            OwnerEmployeeId = "emp-owner",
            Name = "Support",
            TargetAddresses = ["support@spokes.sh", "help@spokes.sh"]
        };
        var domainInbox = new EmailInbox
        {
            Id = "inb-domain",
            AccountId = acc.Id,
            OwnerEmployeeId = "emp-owner",
            Name = "Partner Domain",
            TargetAddresses = ["@partner.org"]
        };
        _inboxes.Save(supportInbox);
        _inboxes.Save(domainInbox);

        var msgSupport = new EmailMessage
        {
            Id = "m-support",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 1,
            Subject = "Support question",
            ToAddresses = ["support@spokes.sh"],
            Date = DateTimeOffset.UtcNow.AddMinutes(-3)
        };
        var msgPartner = new EmailMessage
        {
            Id = "m-partner",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 2,
            Subject = "Partner inquiry",
            ToAddresses = ["contact@partner.org"],
            Date = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        var msgDirect = new EmailMessage
        {
            Id = "m-direct",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 3,
            Subject = "Direct email",
            ToAddresses = ["owner@spokes.sh"],
            Date = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        _messages.Save(msgSupport);
        _messages.Save(msgPartner);
        _messages.Save(msgDirect);

        var supportMsgs = _service.GetInboxMessages(supportInbox, 50);
        Assert.Single(supportMsgs);
        Assert.Equal("m-support", supportMsgs[0].Id);

        var domainMsgs = _service.GetInboxMessages(domainInbox, 50);
        Assert.Single(domainMsgs);
        Assert.Equal("m-partner", domainMsgs[0].Id);

        // Real Inbox returns all 3 messages in INBOX
        var realInboxMsgs = _service.GetRealInboxMessages(acc.Id, 50);
        Assert.Equal(3, realInboxMsgs.Count);
    }

    [Fact]
    public void EnsureRealInboxSendingIdentity_AutomaticallyCreatesAndLinksIdentity()
    {
        var acc = new EmailAccount
        {
            Id = "acc-work",
            OwnerEmployeeId = "emp-alice",
            Name = "Alice Work",
            EmailAddress = "alice@spokes.sh",
            OutgoingServerConfigId = "smtp-main",
            AllowedUserIds = ["emp-bob"]
        };
        _accounts.Save(acc);

        var identity = _service.EnsureRealInboxSendingIdentity(acc, "Alice Smith");
        Assert.NotNull(identity);
        Assert.Equal(EmailIdentitySourceType.RealInbox, identity.SourceType);
        Assert.Equal(acc.Id, identity.SourceAccountId);
        Assert.Equal("alice@spokes.sh", identity.EmailAddress);
        Assert.Equal("Alice Smith", identity.DisplayName);
        Assert.Equal("smtp-main", identity.SendingServerConfigId);
        Assert.Contains("emp-bob", identity.AllowedUserIds);
        Assert.Equal(identity.Id, acc.AutoCreatedIdentityId);
        Assert.Equal(identity.Id, acc.DefaultIdentityId);
    }

    [Fact]
    public void RealInboxSharing_AllowsMultipleUsersToAccessSameRealInboxWithoutDuplication()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", TeamId = "team-ops", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);
        _teams.Save(new Team { Id = "team-ops", Name = "Operations" });

        var sharedAccount = new EmailAccount
        {
            Id = "acc-shared",
            OwnerEmployeeId = alice.Id,
            Name = "Operations Mailbox",
            EmailAddress = "ops@spokes.sh",
            AllowedTeamIds = ["team-ops"]
        };
        _accounts.Save(sharedAccount);

        var bobRealInboxes = _service.GetAccessibleRealInboxes(bob);
        Assert.Single(bobRealInboxes);
        Assert.Equal("acc-shared", bobRealInboxes[0].Id);
    }

    [Fact]
    public void GetUnmatchedAddressesForMessage_ReturnsOnlyAddressesNotClaimedByAnyInbox()
    {
        var accId = "acc-1";
        _inboxes.Save(new EmailInbox
        {
            Id = "inb-1",
            AccountId = accId,
            Name = "Support",
            TargetAddresses = ["support@spokes.sh"]
        });

        var msg = new EmailMessage
        {
            Id = "msg-multi",
            AccountId = accId,
            FolderPath = "INBOX",
            ToAddresses = ["support@spokes.sh", "sales@spokes.sh"]
        };

        var unmatched = _service.GetUnmatchedAddressesForMessage(msg);
        Assert.Single(unmatched);
        Assert.Equal("sales@spokes.sh", unmatched[0]);
    }

    [Fact]
    public void GetPermittedIdentities_RespectsLinkedIdentitiesAndDomainMatchedToggle()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", Email = "bob@otherdomain.com", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var sharedSupportIdentity = new EmailSendingIdentity
        {
            Id = "id-support",
            OwnerEmployeeId = "emp-admin",
            IsServerManaged = true,
            DisplayName = "Spokes Support",
            Username = "support",
            Domain = "spokes.sh"
        };
        var alicePersonalIdentity = new EmailSendingIdentity
        {
            Id = "id-alice",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Alice",
            Username = "alice",
            Domain = "spokes.sh"
        };
        var bobPersonalIdentity = new EmailSendingIdentity
        {
            Id = "id-bob",
            OwnerEmployeeId = bob.Id,
            DisplayName = "Bob",
            Username = "bob",
            Domain = "otherdomain.com"
        };
        _identities.Save(sharedSupportIdentity);
        _identities.Save(alicePersonalIdentity);
        _identities.Save(bobPersonalIdentity);

        _accounts.Save(new EmailAccount
        {
            Id = "acc-1",
            IsServerManaged = true,
            EmailAddress = "support@spokes.sh"
        });

        var supportInbox = new EmailInbox
        {
            Id = "inb-support",
            AccountId = "acc-1",
            IsServerManaged = true,
            Name = "Support",
            TargetAddresses = ["support@spokes.sh"],
            AllowedUserIds = [alice.Id, bob.Id],
            LinkedIdentityIds = [sharedSupportIdentity.Id],
            DefaultIdentityId = sharedSupportIdentity.Id,
            AllowDomainMatchedPersonalIdentity = true
        };
        _inboxes.Save(supportInbox);

        // Alice can reply as support@spokes.sh AND alice@spokes.sh (domain matches spokes.sh)
        var aliceIdentities = _service.GetPermittedIdentities(alice, supportInbox);
        Assert.Equal(2, aliceIdentities.Count);
        Assert.Contains(aliceIdentities, i => i.Id == sharedSupportIdentity.Id);
        Assert.Contains(aliceIdentities, i => i.Id == alicePersonalIdentity.Id);

        // Bob can reply as support@spokes.sh, NOT bob@otherdomain.com (domain mismatch)
        var bobIdentities = _service.GetPermittedIdentities(bob, supportInbox);
        Assert.Single(bobIdentities);
        Assert.Equal(sharedSupportIdentity.Id, bobIdentities[0].Id);
    }

    [Fact]
    public void SelectDefaultIdentity_MatchesReplyToReceivingAddressFirst()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        _accounts.Save(new EmailAccount
        {
            Id = "acc-1",
            OwnerEmployeeId = alice.Id,
            EmailAddress = "alice@spokes.sh"
        });

        var idSupport = new EmailSendingIdentity
        {
            Id = "id-support",
            OwnerEmployeeId = alice.Id,
            Username = "support",
            Domain = "spokes.sh"
        };
        var idSales = new EmailSendingIdentity
        {
            Id = "id-sales",
            OwnerEmployeeId = alice.Id,
            Username = "sales",
            Domain = "spokes.sh"
        };
        _identities.Save(idSupport);
        _identities.Save(idSales);

        var inbox = new EmailInbox
        {
            Id = "inb-1",
            AccountId = "acc-1",
            OwnerEmployeeId = alice.Id,
            TargetAddresses = ["support@spokes.sh", "sales@spokes.sh"],
            LinkedIdentityIds = [idSupport.Id, idSales.Id],
            DefaultIdentityId = idSupport.Id
        };
        _inboxes.Save(inbox);

        var incomingToSales = new EmailMessage
        {
            Id = "m-sales",
            AccountId = "acc-1",
            ToAddresses = ["sales@spokes.sh"]
        };

        var selected = _service.SelectDefaultIdentity(alice, inbox, incomingToSales.ToAddresses);
        Assert.NotNull(selected);
        Assert.Equal(idSales.Id, selected.Id);
    }

    [Fact]
    public void UnifiedViews_AggregateAcrossAccessibleMailboxesAndInboxes()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var acc1 = new EmailAccount { Id = "acc-1", OwnerEmployeeId = alice.Id, Name = "Work" };
        var acc2 = new EmailAccount { Id = "acc-2", OwnerEmployeeId = "emp-other", Name = "Shared" };
        _accounts.Save(acc1);
        _accounts.Save(acc2);

        _folders.Save(new EmailFolder { Id = "f-in-1", AccountId = acc1.Id, Path = "INBOX", IsInbox = true });
        _folders.Save(new EmailFolder { Id = "f-sent-1", AccountId = acc1.Id, Path = "Sent", IsSent = true });
        _folders.Save(new EmailFolder { Id = "f-in-2", AccountId = acc2.Id, Path = "INBOX", IsInbox = true });

        var sharedInbox = new EmailInbox
        {
            Id = "inb-shared",
            AccountId = acc2.Id,
            OwnerEmployeeId = "emp-other",
            Name = "Info",
            TargetAddresses = ["info@spokes.sh"],
            AllowedUserIds = [alice.Id]
        };
        _inboxes.Save(sharedInbox);

        _messages.Save(new EmailMessage
        {
            Id = "m-1",
            AccountId = acc1.Id,
            FolderPath = "INBOX",
            Subject = "Personal",
            ToAddresses = ["alice@spokes.sh"],
            Date = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        _messages.Save(new EmailMessage
        {
            Id = "m-2",
            AccountId = acc2.Id,
            FolderPath = "INBOX",
            Subject = "Shared Info",
            ToAddresses = ["info@spokes.sh"],
            Date = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        _messages.Save(new EmailMessage
        {
            Id = "m-sent",
            AccountId = acc1.Id,
            FolderPath = "Sent",
            Subject = "Sent Reply",
            Date = DateTimeOffset.UtcNow
        });

        var allInboxMsgs = _service.GetAllInboxesMessages(alice, 50);
        Assert.Equal(2, allInboxMsgs.Count);
        Assert.Equal("m-2", allInboxMsgs[0].Id);
        Assert.Equal("m-1", allInboxMsgs[1].Id);

        Assert.Equal(2, _service.GetTotalUnreadCountForUser(alice.Id));
    }

    [Fact]
    public void ServerManagedMailboxes_RequireExplicitSharingAndIsolateFromPersonalMailboxes()
    {
        var admin = new Employee { Id = "emp-admin", FirstName = "Admin", IsAdmin = true };
        var user = new Employee { Id = "emp-user", FirstName = "User", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(admin);
        _employees.Save(user);

        // Unassigned Server-Managed Real Inbox (0 users, 0 groups)
        var unassignedServerAccount = new EmailAccount
        {
            Id = "acc-server-unassigned",
            OwnerEmployeeId = admin.Id,
            IsServerManaged = true,
            Name = "Unassigned Server Mailbox",
            EmailAddress = "info@acme.test"
        };

        // Shared Server-Managed Real Inbox (shared with admin)
        var sharedServerAccount = new EmailAccount
        {
            Id = "acc-server-shared",
            OwnerEmployeeId = admin.Id,
            IsServerManaged = true,
            Name = "Shared Server Mailbox",
            EmailAddress = "sales@acme.test",
            AllowedUserIds = [admin.Id]
        };

        // Personal Real Inbox owned by regular user (0 users, 0 groups)
        var personalUserAccount = new EmailAccount
        {
            Id = "acc-user-personal",
            OwnerEmployeeId = user.Id,
            IsServerManaged = false,
            Name = "User Personal Mailbox",
            EmailAddress = "user@example.test"
        };

        _accounts.Save(unassignedServerAccount);
        _accounts.Save(sharedServerAccount);
        _accounts.Save(personalUserAccount);

        // Admin on Email page only sees the shared server mailbox, NOT the unassigned server mailbox or user's personal mailbox
        var adminRealInboxes = _service.GetAccessibleRealInboxes(admin);
        Assert.Single(adminRealInboxes);
        Assert.Equal("acc-server-shared", adminRealInboxes[0].Id);

        var adminAccounts = _service.GetAccessibleAccounts(admin);
        Assert.Single(adminAccounts);
        Assert.Equal("acc-server-shared", adminAccounts[0].Id);

        // Regular user sees their own personal mailbox even with 0 shared users/groups
        var userRealInboxes = _service.GetAccessibleRealInboxes(user);
        Assert.Single(userRealInboxes);
        Assert.Equal("acc-user-personal", userRealInboxes[0].Id);
    }

    [Fact]
    public void CloudflareForwardedEmail_ExtractsAndDetectsForwardedAliasWithoutSentOrCoRecipientPollution()
    {
        var acc = new EmailAccount
        {
            Id = "acc-personal-mail",
            OwnerEmployeeId = "emp-alex",
            Name = "Personal Mail",
            EmailAddress = "owner@example.test"
        };
        _accounts.Save(acc);

        _folders.Save(new EmailFolder { Id = "f-inbox", AccountId = acc.Id, Name = "INBOX", Path = "INBOX", IsInbox = true });
        _folders.Save(new EmailFolder { Id = "f-sent", AccountId = acc.Id, Name = "Sent", Path = "[Gmail]/Sent Mail", IsSent = true });
        _folders.Save(new EmailFolder { Id = "f-all", AccountId = acc.Id, Name = "All Mail", Path = "[Gmail]/All Mail" });

        // 1. Verify MimeMessage parsing with Cloudflare headers (X-Forwarded-For and Received: for <...>)
        var mime = new MimeKit.MimeMessage();
        mime.From.Add(new MimeKit.MailboxAddress("Sender", "sender@external.test"));
        mime.To.Add(new MimeKit.MailboxAddress("Acme Support", "support@acme.test"));
        mime.Headers.Add("Delivered-To", "owner@example.test");
        mime.Headers.Add("X-Forwarded-To", "owner@example.test");
        mime.Headers.Add("X-Forwarded-For", "support@acme.test owner@example.test");
        mime.Headers.Add("Received", "from mail.external.test by cloudflare-email.net (cloudflare) id EvzFMEGdkqKi for <support@acme.test>; Wed, 7 Oct 2026 09:59:15 +0000");

        var extractedReceiving = EmailSyncService.ExtractReceivingAddresses(mime);
        Assert.Contains("support@acme.test", extractedReceiving);
        Assert.Contains("owner@example.test", extractedReceiving);

        // Save forwarded message in INBOX
        _messages.Save(new EmailMessage
        {
            Id = "msg-cf-forwarded",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 1,
            FromAddress = "sender@external.test",
            ToAddresses = ["support@acme.test"],
            ReceivingAddresses = extractedReceiving
        });

        // Save a direct email to owner@example.test with co-recipients in To/Cc (should NOT be detected as Virtual Inbox targets)
        _messages.Save(new EmailMessage
        {
            Id = "msg-direct-cc",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 2,
            FromAddress = "colleague@company.com",
            ToAddresses = ["owner@example.test", "otherperson@company.com"],
            CcAddresses = ["boss@company.com"],
            ReceivingAddresses = ["owner@example.test", "otherperson@company.com", "boss@company.com"]
        });

        // Save an email sent BY owner@example.test in INBOX and All Mail (recipients should NEVER be detected)
        _messages.Save(new EmailMessage
        {
            Id = "msg-sent-by-owner",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 3,
            FromAddress = "owner@example.test",
            ToAddresses = ["recipient-i-emailed@external.com"],
            ReceivingAddresses = ["recipient-i-emailed@external.com"]
        });
        _messages.Save(new EmailMessage
        {
            Id = "msg-all-mail",
            AccountId = acc.Id,
            FolderPath = "[Gmail]/All Mail",
            UniqueId = 4,
            FromAddress = "owner@example.test",
            ToAddresses = ["another-sent-recipient@external.com"],
            ReceivingAddresses = ["another-sent-recipient@external.com"]
        });

        var detected = _service.GetDetectedAddressesForAccount(acc.Id);
        Assert.Single(detected);
        Assert.Equal("support@acme.test", detected[0]);
    }

    [Fact]
    public void EnsureRealInboxSendingIdentity_UsesSenderDisplayNameOrEmployeeFullName_NotUiInboxName()
    {
        _employees.Save(new Employee
        {
            Id = "emp-alex",
            FirstName = "Alex",
            LastName = "Tremblay",
            IsActive = true
        });

        var acc = new EmailAccount
        {
            Id = "acc-work",
            OwnerEmployeeId = "emp-alex",
            Name = "My Work Mailbox (UI Label)",
            EmailAddress = "alex@acme.test"
        };
        _accounts.Save(acc);

        // 1. Without explicit displayName, falls back to Employee.FullName ("Alex Tremblay") instead of UI Inbox Name
        var identity = _service.EnsureRealInboxSendingIdentity(acc);
        Assert.Equal("Alex Tremblay", identity.DisplayName);
        Assert.NotEqual(acc.Name, identity.DisplayName);

        // 2. With explicit displayName (e.g. detected from IMAP or customized), updates DisplayName without changing UI Inbox Name
        var updatedIdentity = _service.EnsureRealInboxSendingIdentity(acc, "Alex T. (Acme Demo)");
        Assert.Equal("Alex T. (Acme Demo)", updatedIdentity.DisplayName);
        Assert.Equal("My Work Mailbox (UI Label)", acc.Name);
    }

    [Fact]
    public async Task SetRealInboxArchived_HidesFromActiveViewsAndPreservesDiskFiles_AndUnarchiveRestores()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var acc = new EmailAccount
        {
            Id = "acc-former-employee",
            OwnerEmployeeId = alice.Id,
            Name = "Former Employee Mailbox",
            EmailAddress = "former@spokes.sh",
            OutgoingServerConfigId = "smtp-1"
        };
        _accounts.Save(acc);

        var identity = _service.EnsureRealInboxSendingIdentity(acc, "Former Employee");
        var virtualInbox = new EmailInbox
        {
            Id = "inb-former-alias",
            AccountId = acc.Id,
            OwnerEmployeeId = alice.Id,
            Name = "Former Alias",
            TargetAddresses = ["alias@spokes.sh"]
        };
        _inboxes.Save(virtualInbox);

        _folders.Save(new EmailFolder { Id = "f-inbox", AccountId = acc.Id, Name = "INBOX", Path = "INBOX", IsInbox = true });

        var msg = new EmailMessage
        {
            Id = "msg-important",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 10,
            Subject = "Historical Contract",
            ToAddresses = ["former@spokes.sh"],
            IsRead = false,
            Date = DateTimeOffset.UtcNow
        };
        _messages.Save(msg);
        var bodyPath = Path.Combine(_testDataDir, "EmailAccounts", acc.Id, "Bodies", $"{msg.Id}.html");
        Directory.CreateDirectory(Path.GetDirectoryName(bodyPath)!);
        await File.WriteAllTextAsync(bodyPath, "<p>Contract body</p>");
        await _persistence.FlushAllAsync();

        // Active before archiving
        Assert.Single(_service.GetAccessibleRealInboxes(alice));
        Assert.Empty(_service.GetAccessibleArchivedRealInboxes(alice));
        Assert.Single(_service.GetAccessibleInboxes(alice));
        Assert.Single(_service.GetPermittedIdentities(alice, realInbox: acc));
        Assert.Equal(1, _service.GetTotalUnreadCountForUser(alice.Id));

        // Archive the Real Inbox
        _service.SetRealInboxArchived(acc.Id, isArchived: true, alice.Id);

        // Hidden from all active views, unread counts, virtual inboxes, and sending identities
        Assert.Empty(_service.GetAccessibleRealInboxes(alice));
        Assert.Empty(_service.GetAccessibleInboxes(alice));
        Assert.Empty(_service.GetPermittedIdentities(alice, realInbox: acc));
        Assert.Equal(0, _service.GetTotalUnreadCountForUser(alice.Id));
        Assert.Empty(_service.GetAllInboxesMessages(alice));

        // Visible in Archived Real Inboxes and accessible for browsing/attachments when includeArchived: true
        var archivedList = _service.GetAccessibleArchivedRealInboxes(alice);
        Assert.Single(archivedList);
        Assert.Equal(acc.Id, archivedList[0].Id);
        Assert.True(archivedList[0].IsArchived);
        Assert.NotNull(archivedList[0].ArchivedAt);

        Assert.Single(_service.GetAccessibleAccounts(alice, includeArchived: true));
        Assert.Single(_service.GetRealInboxMessages(acc.Id, 50));
        Assert.Equal("<p>Contract body</p>", await File.ReadAllTextAsync(bodyPath));

        // Unarchive restores active state
        _service.SetRealInboxArchived(acc.Id, isArchived: false, alice.Id);
        Assert.Single(_service.GetAccessibleRealInboxes(alice));
        Assert.Empty(_service.GetAccessibleArchivedRealInboxes(alice));
        Assert.Single(_service.GetAccessibleInboxes(alice));
        Assert.Single(_service.GetPermittedIdentities(alice, realInbox: acc));
        Assert.Equal(1, _service.GetTotalUnreadCountForUser(alice.Id));
    }

    [Fact]
    public async Task DeleteRealInbox_WipesAllDiskFilesEvictsCachesAndCascadesVirtualInboxesAndIdentities()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var acc = new EmailAccount
        {
            Id = "acc-to-wipe",
            OwnerEmployeeId = alice.Id,
            Name = "Wipe Me",
            EmailAddress = "wipe@spokes.sh",
            OutgoingServerConfigId = "smtp-1"
        };
        _accounts.Save(acc);

        var realIdentity = _service.EnsureRealInboxSendingIdentity(acc, "Wipe Identity");

        var virtualInbox = new EmailInbox
        {
            Id = "inb-attached",
            AccountId = acc.Id,
            OwnerEmployeeId = alice.Id,
            Name = "Attached Virtual Inbox",
            TargetAddresses = ["attached@spokes.sh"]
        };
        _inboxes.Save(virtualInbox);
        var virtualIdentity = new EmailSendingIdentity
        {
            OwnerEmployeeId = alice.Id,
            SourceType = EmailIdentitySourceType.VirtualInbox,
            SourceInboxId = virtualInbox.Id,
            SourceAccountId = acc.Id,
            SendingServerConfigId = "smtp-1",
            DisplayName = "Attached Identity",
            Username = "attached",
            Domain = "spokes.sh"
        };
        _identities.Save(virtualIdentity);
        virtualInbox.LinkedIdentityIds.Add(virtualIdentity.Id);
        virtualInbox.DefaultIdentityId = virtualIdentity.Id;
        _inboxes.Save(virtualInbox);

        var otherInbox = new EmailInbox
        {
            Id = "inb-other",
            AccountId = "acc-other",
            OwnerEmployeeId = alice.Id,
            Name = "Other Inbox",
            LinkedIdentityIds = [realIdentity.Id],
            DefaultIdentityId = realIdentity.Id
        };
        _inboxes.Save(otherInbox);

        _folders.Save(new EmailFolder { Id = "f-inbox", AccountId = acc.Id, Name = "INBOX", Path = "INBOX", IsInbox = true });

        var msg = new EmailMessage
        {
            Id = "msg-wipe-1",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 42,
            Subject = "Secret Email"
        };
        _messages.Save(msg);

        var accountDir = Path.Combine(_testDataDir, "EmailAccounts", acc.Id);
        var bodyPath = Path.Combine(accountDir, "Bodies", $"{msg.Id}.html");
        var mimePath = Path.Combine(accountDir, "Mime", $"{msg.Id}.eml");
        var attPath = Path.Combine(accountDir, "Attachments", msg.Id, "secret.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(bodyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(mimePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(attPath)!);
        await File.WriteAllTextAsync(bodyPath, "<p>Secret HTML</p>");
        await File.WriteAllBytesAsync(mimePath, new byte[] { 1, 2, 3, 4 });
        await File.WriteAllBytesAsync(attPath, new byte[] { 9, 8, 7 });

        await _persistence.FlushAllAsync();

        Assert.True(Directory.Exists(accountDir));
        Assert.True(File.Exists(attPath));

        // Perform permanent delete
        _service.DeleteRealInbox(acc.Id, alice.Id);
        await _persistence.FlushAllAsync();

        // Verify entire account directory (Account.json, Folders, Messages, index.json, Bodies, Mime, Attachments) is completely gone from disk
        Assert.False(Directory.Exists(accountDir));
        Assert.Null(_accounts.GetById(acc.Id));
        Assert.Empty(_folders.GetByAccount(acc.Id));
        Assert.Empty(_messages.GetByFolder(acc.Id, "INBOX"));
        Assert.Null(_messages.GetById(acc.Id, msg.Id));

        // Verify cascading cleanup of attached Virtual Inboxes and Sourced Sending Identities
        Assert.Null(_inboxes.GetById(virtualInbox.Id));
        Assert.Null(_identities.GetById(realIdentity.Id));
        Assert.Null(_identities.GetById(virtualIdentity!.Id));

        // Verify other inboxes unlinked the deleted identity
        var updatedOtherInbox = _inboxes.GetById(otherInbox.Id);
        Assert.NotNull(updatedOtherInbox);
        Assert.DoesNotContain(realIdentity.Id, updatedOtherInbox.LinkedIdentityIds);
        Assert.Null(updatedOtherInbox.DefaultIdentityId);
    }

    [Fact]
    public void SelectDefaultIdentity_PrefersVirtualInboxDefault_OverBackingRealInboxAutoIdentity()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var realIdentity = new EmailSendingIdentity
        {
            Id = "id-real",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Alice Real",
            Username = "alice",
            Domain = "company.com"
        };
        var virtualIdentity = new EmailSendingIdentity
        {
            Id = "id-support",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Support",
            Username = "support",
            Domain = "company.com"
        };
        _identities.Save(realIdentity);
        _identities.Save(virtualIdentity);

        var realAccount = new EmailAccount
        {
            Id = "acc-1",
            OwnerEmployeeId = alice.Id,
            Name = "Company Main",
            EmailAddress = "alice@company.com",
            AutoCreatedIdentityId = realIdentity.Id,
            DefaultIdentityId = realIdentity.Id
        };
        _accounts.Save(realAccount);

        var virtualInbox = new EmailInbox
        {
            Id = "inb-support",
            AccountId = realAccount.Id,
            OwnerEmployeeId = alice.Id,
            Name = "Support",
            TargetAddresses = ["support@company.com"],
            LinkedIdentityIds = [virtualIdentity.Id],
            DefaultIdentityId = virtualIdentity.Id
        };
        _inboxes.Save(virtualInbox);

        var permitted = _service.GetPermittedIdentities(alice, virtualInbox, realInbox: realAccount);

        // Permitted identities must include the virtual inbox identity
        Assert.Contains(permitted, i => i.Id == virtualIdentity.Id);

        // When composing in virtual inbox with real account backing, virtual inbox default MUST take precedence
        var selected = _service.SelectDefaultIdentity(permitted, alice.Id, inbox: virtualInbox, realInbox: realAccount);
        Assert.NotNull(selected);
        Assert.Equal(virtualIdentity.Id, selected.Id);
    }

    [Fact]
    public void GetPermittedIdentities_WhenBothInboxAndRealInboxProvided_IncludesBoth()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var realIdentity = new EmailSendingIdentity
        {
            Id = "id-real-acc",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Alice Work",
            Username = "alice",
            Domain = "work.com"
        };
        var virtualIdentity = new EmailSendingIdentity
        {
            Id = "id-billing",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Billing Dept",
            Username = "billing",
            Domain = "work.com"
        };
        _identities.Save(realIdentity);
        _identities.Save(virtualIdentity);

        var realAccount = new EmailAccount
        {
            Id = "acc-work",
            OwnerEmployeeId = alice.Id,
            AutoCreatedIdentityId = realIdentity.Id,
            DefaultIdentityId = realIdentity.Id
        };
        _accounts.Save(realAccount);

        var virtualInbox = new EmailInbox
        {
            Id = "inb-billing",
            AccountId = realAccount.Id,
            OwnerEmployeeId = alice.Id,
            LinkedIdentityIds = [virtualIdentity.Id],
            DefaultIdentityId = virtualIdentity.Id
        };
        _inboxes.Save(virtualInbox);

        var permitted = _service.GetPermittedIdentities(alice, virtualInbox, realInbox: realAccount);

        Assert.Contains(permitted, i => i.Id == virtualIdentity.Id);
        Assert.Contains(permitted, i => i.Id == realIdentity.Id);
    }

    [Fact]
    public void GetPermittedIdentities_WhenNeitherInboxNorRealInboxProvided_ReturnsAllAccessibleIdentities()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var id1 = new EmailSendingIdentity { Id = "id-1", OwnerEmployeeId = alice.Id, DisplayName = "Ident 1", Username = "user1", Domain = "a.com" };
        var id2 = new EmailSendingIdentity { Id = "id-2", OwnerEmployeeId = alice.Id, DisplayName = "Ident 2", Username = "user2", Domain = "b.com" };
        var id3 = new EmailSendingIdentity { Id = "id-3", OwnerEmployeeId = alice.Id, DisplayName = "Ident 3", Username = "user3", Domain = "c.com" };
        _identities.Save(id1);
        _identities.Save(id2);
        _identities.Save(id3);

        // When in All Inboxes (null inbox, null realInbox), all 3 accessible identities must be returned
        var permitted = _service.GetPermittedIdentities(alice, inbox: null, realInbox: null);

        Assert.Equal(3, permitted.Count);
        Assert.Contains(permitted, i => i.Id == id1.Id);
        Assert.Contains(permitted, i => i.Id == id2.Id);
        Assert.Contains(permitted, i => i.Id == id3.Id);
    }

    [Fact]
    public void SelectDefaultIdentity_AllInboxesFallback_SelectsUserOwnedIdentity()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", Email = "bob@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var bobSharedId = new EmailSendingIdentity
        {
            Id = "id-bob-shared",
            OwnerEmployeeId = bob.Id,
            AllowedUserIds = [alice.Id],
            DisplayName = "Bob Shared",
            Username = "bob",
            Domain = "shared.com"
        };
        var aliceOwnedId = new EmailSendingIdentity
        {
            Id = "id-alice-owned",
            OwnerEmployeeId = alice.Id,
            DisplayName = "Alice Main",
            Username = "alice",
            Domain = "spokes.sh"
        };
        _identities.Save(bobSharedId);
        _identities.Save(aliceOwnedId);

        var permitted = _service.GetPermittedIdentities(alice, inbox: null, realInbox: null);
        Assert.Equal(2, permitted.Count);

        // When in All Inboxes, SelectDefaultIdentity must pick Alice's own identity over shared identities
        var selected = _service.SelectDefaultIdentity(permitted, alice.Id, inbox: null, realInbox: null);
        Assert.NotNull(selected);
        Assert.Equal(aliceOwnedId.Id, selected.Id);
    }

    [Fact]
    public void SelectDefaultIdentity_WhenRealInboxProvided_SelectsRealInboxDefaultIdentity()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", Email = "alice@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var idAccountA = new EmailSendingIdentity { Id = "id-acc-a", OwnerEmployeeId = alice.Id, Username = "a", Domain = "a.com" };
        var idAccountB = new EmailSendingIdentity { Id = "id-acc-b", OwnerEmployeeId = alice.Id, Username = "b", Domain = "b.com" };
        _identities.Save(idAccountA);
        _identities.Save(idAccountB);

        var accountB = new EmailAccount
        {
            Id = "acc-b",
            OwnerEmployeeId = alice.Id,
            DefaultIdentityId = idAccountB.Id,
            AutoCreatedIdentityId = idAccountB.Id
        };
        _accounts.Save(accountB);

        var permitted = _service.GetPermittedIdentities(alice, inbox: null, realInbox: accountB);
        var selected = _service.SelectDefaultIdentity(permitted, alice.Id, inbox: null, realInbox: accountB);

        Assert.NotNull(selected);
        Assert.Equal(idAccountB.Id, selected.Id);
    }

    [Fact]
    public void CanUserAccessEmailMessage_EnforcesPersonalMailboxPrivacyAndVirtualInboxIsolation()
    {
        var admin = new Employee { Id = "emp-admin", FirstName = "Admin", IsAdmin = true, IsActive = true };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Email.Use] };
        var carol = new Employee { Id = "emp-carol", FirstName = "Carol", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Email.Use] };
        _employees.Save(admin);
        _employees.Save(bob);
        _employees.Save(carol);

        // 1. Personal mailbox owned by Bob (IsServerManaged = false)
        var bobPersonalAccount = new EmailAccount
        {
            Id = "acc-bob-priv",
            OwnerEmployeeId = bob.Id,
            IsServerManaged = false,
            EmailAddress = "bob@personal.com"
        };
        _accounts.Save(bobPersonalAccount);
        _messages.Save(new EmailMessage
        {
            Id = "msg-bob-priv",
            AccountId = bobPersonalAccount.Id,
            FolderPath = "INBOX",
            ToAddresses = ["bob@personal.com"]
        });

        // Bob can access his own personal message, but Admin and Carol cannot (preserving personal mailbox privacy)
        Assert.True(_service.CanUserAccessEmailMessage(bob, bobPersonalAccount.Id, "msg-bob-priv"));
        Assert.False(_service.CanUserAccessEmailMessage(admin, bobPersonalAccount.Id, "msg-bob-priv"));
        Assert.False(_service.CanUserAccessEmailMessage(carol, bobPersonalAccount.Id, "msg-bob-priv"));

        // 2. Server-managed catch-all account with two Virtual Inboxes (support@ vs hr@)
        var catchAllAccount = new EmailAccount
        {
            Id = "acc-catchall",
            OwnerEmployeeId = admin.Id,
            IsServerManaged = true,
            EmailAddress = "catchall@spokes.sh"
        };
        _accounts.Save(catchAllAccount);

        var supportInbox = new EmailInbox
        {
            Id = "inb-support",
            AccountId = catchAllAccount.Id,
            IsServerManaged = true,
            TargetAddresses = ["support@spokes.sh"],
            AllowedUserIds = [bob.Id]
        };
        var hrInbox = new EmailInbox
        {
            Id = "inb-hr",
            AccountId = catchAllAccount.Id,
            IsServerManaged = true,
            TargetAddresses = ["hr@spokes.sh"],
            AllowedUserIds = [carol.Id]
        };
        _inboxes.Save(supportInbox);
        _inboxes.Save(hrInbox);

        _messages.Save(new EmailMessage
        {
            Id = "msg-support",
            AccountId = catchAllAccount.Id,
            FolderPath = "INBOX",
            ToAddresses = ["support@spokes.sh"],
            ReceivingAddresses = ["support@spokes.sh"]
        });
        _messages.Save(new EmailMessage
        {
            Id = "msg-hr",
            AccountId = catchAllAccount.Id,
            FolderPath = "INBOX",
            ToAddresses = ["hr@spokes.sh"],
            ReceivingAddresses = ["hr@spokes.sh"]
        });
        _messages.Save(new EmailMessage
        {
            Id = "draft-bob",
            AccountId = catchAllAccount.Id,
            FolderPath = "LocalDrafts",
            CreatedByEmployeeId = bob.Id
        });

        // Bob can access support@ message and his own draft, but NOT hr@ message
        Assert.True(_service.CanUserAccessEmailMessage(bob, catchAllAccount.Id, "msg-support"));
        Assert.True(_service.CanUserAccessEmailMessage(bob, catchAllAccount.Id, "draft-bob"));
        Assert.False(_service.CanUserAccessEmailMessage(bob, catchAllAccount.Id, "msg-hr"));

        // Carol can access hr@ message, but NOT support@ message or Bob's draft
        Assert.True(_service.CanUserAccessEmailMessage(carol, catchAllAccount.Id, "msg-hr"));
        Assert.False(_service.CanUserAccessEmailMessage(carol, catchAllAccount.Id, "msg-support"));
        Assert.False(_service.CanUserAccessEmailMessage(carol, catchAllAccount.Id, "draft-bob"));
    }

    [Fact]
    public void MarkEmailPageVisited_ClearsUnseenUnreadCount_WhilePreservingTotalAndInboxUnreadCounts()
    {
        var alice = new Employee { Id = "emp-visit-1", FirstName = "Alice", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var acc = new EmailAccount
        {
            Id = "acc-visit-1",
            OwnerEmployeeId = alice.Id,
            Name = "Alice Mailbox",
            EmailAddress = "alice@spokes.sh"
        };
        _accounts.Save(acc);

        _folders.Save(new EmailFolder
        {
            Id = "fld-visit-inbox",
            AccountId = acc.Id,
            Name = "Inbox",
            Path = "INBOX",
            IsInbox = true
        });

        // Two existing unread messages (one in the past, one slightly skewed into the future)
        var baseTime = DateTimeOffset.UtcNow;
        _messages.Save(new EmailMessage
        {
            Id = "msg-old-1",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 1,
            IsRead = false,
            Date = baseTime.AddHours(-2)
        });
        _messages.Save(new EmailMessage
        {
            Id = "msg-skewed-2",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 2,
            IsRead = false,
            Date = baseTime.AddMinutes(1)
        });

        Assert.Equal(2, _service.GetTotalUnreadCountForUser(alice.Id));
        Assert.Equal(2, _service.GetUnseenUnreadCountForUser(alice.Id));

        // Visiting the email page clears unseen count to 0 while keeping actual inbox unread counts at 2
        _service.MarkEmailPageVisited(alice.Id);

        Assert.Equal(0, _service.GetUnseenUnreadCountForUser(alice.Id));
        Assert.Equal(2, _service.GetTotalUnreadCountForUser(alice.Id));
        Assert.Equal(2, _service.GetUnreadCountForRealInbox(acc.Id));

        var savedAlice = _employees.GetById(alice.Id);
        Assert.NotNull(savedAlice?.LastEmailVisitedAt);

        // A new unread message arriving after the visit increments unseen count to 1 (while total unread becomes 3)
        _messages.Save(new EmailMessage
        {
            Id = "msg-new-3",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 3,
            IsRead = false,
            Date = savedAlice!.LastEmailVisitedAt!.Value.AddSeconds(10)
        });

        Assert.Equal(1, _service.GetUnseenUnreadCountForUser(alice.Id));
        Assert.Equal(3, _service.GetTotalUnreadCountForUser(alice.Id));
        Assert.Equal(3, _service.GetUnreadCountForRealInbox(acc.Id));
    }

    [Fact]
    public void GetPermittedIdentities_WhenInboxIsNullAndUserLacksRealInboxAccess_DoesNotLeakRealInboxIdentities()
    {
        var bob = new Employee { Id = "emp-bob-vi", FirstName = "Bob", Email = "bob@spokes.sh", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(bob);

        var adminIdentity = new EmailSendingIdentity
        {
            Id = "id-catchall-root",
            IsServerManaged = true,
            DisplayName = "Company Root",
            Username = "root",
            Domain = "company.com"
        };
        var supportIdentity = new EmailSendingIdentity
        {
            Id = "id-support-vi",
            IsServerManaged = true,
            DisplayName = "Support Desk",
            Username = "support",
            Domain = "company.com"
        };
        _identities.Save(adminIdentity);
        _identities.Save(supportIdentity);

        var catchAllAccount = new EmailAccount
        {
            Id = "acc-catchall-main",
            IsServerManaged = true,
            Name = "Catch-All",
            EmailAddress = "root@company.com",
            DefaultIdentityId = adminIdentity.Id,
            AutoCreatedIdentityId = adminIdentity.Id,
            LinkedIdentityIds = [adminIdentity.Id]
        };
        _accounts.Save(catchAllAccount);

        var supportInbox = new EmailInbox
        {
            Id = "inb-support-vi",
            AccountId = catchAllAccount.Id,
            IsServerManaged = true,
            Name = "Support",
            TargetAddresses = ["support@company.com"],
            AllowedUserIds = [bob.Id],
            LinkedIdentityIds = [supportIdentity.Id],
            DefaultIdentityId = supportIdentity.Id
        };
        _inboxes.Save(supportInbox);

        // Even when inbox is omitted and realInbox is supplied (e.g. composing from a backing folder),
        // Bob lacks realInbox.CanUserAccess so catchAllAccount's identities MUST NOT leak.
        var permittedWithRealOnly = _service.GetPermittedIdentities(bob, inbox: null, realInbox: catchAllAccount);
        Assert.DoesNotContain(permittedWithRealOnly, i => i.Id == adminIdentity.Id);

        // In Virtual Inbox context (or global All Inboxes context), Bob gets his permitted Support identity only.
        var permittedGlobal = _service.GetPermittedIdentities(bob, inbox: null, realInbox: null);
        Assert.Single(permittedGlobal);
        Assert.Equal(supportIdentity.Id, permittedGlobal[0].Id);
    }

    [Fact]
    public void IsIdentityPermittedForUser_EnforcesTeamMembershipOnVirtualInboxesAndBlocksUnauthorizedUsers()
    {
        var supportTeam = new Team { Id = "team-support", Name = "Support Team" };
        _teams.Save(supportTeam);

        var teamMember = new Employee { Id = "emp-team-member", FirstName = "Dana", TeamId = supportTeam.Id, Permissions = [AppPermissions.Email.Use] };
        var outsider = new Employee { Id = "emp-outsider", FirstName = "Eve", Permissions = [AppPermissions.Email.Use] };
        _employees.Save(teamMember);
        _employees.Save(outsider);

        var teamIdentity = new EmailSendingIdentity
        {
            Id = "id-team-support",
            IsServerManaged = true,
            DisplayName = "Support",
            Username = "support",
            Domain = "company.com"
        };
        _identities.Save(teamIdentity);

        var backingAccount = new EmailAccount
        {
            Id = "acc-team-backing",
            IsServerManaged = true,
            Name = "Main Catch-All",
            EmailAddress = "catchall@company.com"
        };
        _accounts.Save(backingAccount);

        var teamVirtualInbox = new EmailInbox
        {
            Id = "inb-team-support",
            AccountId = backingAccount.Id,
            IsServerManaged = true,
            Name = "Support Team Inbox",
            AllowedTeamIds = [supportTeam.Id],
            DefaultIdentityId = teamIdentity.Id
        };
        _inboxes.Save(teamVirtualInbox);

        Assert.True(_service.IsIdentityPermittedForUser(teamMember.Id, teamIdentity.Id));
        Assert.True(_service.IsIdentityPermittedForUser(teamMember.Id, teamIdentity.Id, teamVirtualInbox.Id));
        Assert.False(_service.IsIdentityPermittedForUser(outsider.Id, teamIdentity.Id));
        Assert.False(_service.IsIdentityPermittedForUser(outsider.Id, teamIdentity.Id, teamVirtualInbox.Id));
    }

    [Fact]
    public void CanUserAccessEmailMessage_EnforcesLocalDraftCreatorIsolation_EvenOnSharedRealInboxAndForServerAdmins()
    {
        var alice = new Employee { Id = "emp-shared-alice", FirstName = "Alice", Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-shared-bob", FirstName = "Bob", Permissions = [AppPermissions.Email.Use] };
        var admin = new Employee { Id = "emp-shared-admin", FirstName = "Admin", IsAdmin = true };
        _employees.Save(alice);
        _employees.Save(bob);
        _employees.Save(admin);

        var sharedServerAccount = new EmailAccount
        {
            Id = "acc-shared-real",
            IsServerManaged = true,
            Name = "Shared Sales Mailbox",
            EmailAddress = "sales@company.com",
            AllowedUserIds = [alice.Id, bob.Id]
        };
        _accounts.Save(sharedServerAccount);

        var inboxMsg = new EmailMessage
        {
            Id = "msg-sales-inbox",
            AccountId = sharedServerAccount.Id,
            FolderPath = "INBOX",
            Subject = "Customer Lead"
        };
        var aliceDraft = new EmailMessage
        {
            Id = "draft-sales-alice",
            AccountId = sharedServerAccount.Id,
            FolderPath = "LocalDrafts",
            CreatedByEmployeeId = alice.Id,
            Subject = "Alice Unsent Draft"
        };
        var legacyUntaggedSharedDraft = new EmailMessage
        {
            Id = "draft-sales-untagged",
            AccountId = sharedServerAccount.Id,
            FolderPath = "LocalDrafts",
            CreatedByEmployeeId = null,
            Subject = "Legacy Untagged Draft on Shared Account"
        };
        _messages.Save(inboxMsg);
        _messages.Save(aliceDraft);
        _messages.Save(legacyUntaggedSharedDraft);

        // All three have full Real Inbox access to normal INBOX messages on the shared server-managed account
        Assert.True(_service.CanUserAccessEmailMessage(alice, sharedServerAccount.Id, inboxMsg.Id));
        Assert.True(_service.CanUserAccessEmailMessage(bob, sharedServerAccount.Id, inboxMsg.Id));
        Assert.True(_service.CanUserAccessEmailMessage(admin, sharedServerAccount.Id, inboxMsg.Id));

        // ONLY Alice (the draft creator) can access her LocalDrafts message or its attachments;
        // co-worker Bob and server Admin are strictly blocked.
        Assert.True(_service.CanUserAccessEmailMessage(alice, sharedServerAccount.Id, aliceDraft.Id));
        Assert.False(_service.CanUserAccessEmailMessage(bob, sharedServerAccount.Id, aliceDraft.Id));
        Assert.False(_service.CanUserAccessEmailMessage(admin, sharedServerAccount.Id, aliceDraft.Id));

        // Legacy untagged drafts on a server-managed shared account fail closed for everyone
        Assert.False(_service.CanUserAccessEmailMessage(alice, sharedServerAccount.Id, legacyUntaggedSharedDraft.Id));
        Assert.False(_service.CanUserAccessEmailMessage(admin, sharedServerAccount.Id, legacyUntaggedSharedDraft.Id));

        // Realtime/notification recipients for Alice's LocalDrafts message must ONLY include Alice
        var authorizedForDraft = _service.GetAuthorizedEmployeeIdsForMessage(aliceDraft);
        Assert.Single(authorizedForDraft);
        Assert.Equal(alice.Id, authorizedForDraft[0]);
    }
}
