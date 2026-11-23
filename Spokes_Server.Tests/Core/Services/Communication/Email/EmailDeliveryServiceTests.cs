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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailDeliveryServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailAccountRepository _accounts;
    private readonly EmailInboxRepository _inboxes;
    private readonly EmailSendingIdentityRepository _identities;
    private readonly EmailFolderRepository _folders;
    private readonly EmailMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly TeamRepository _teams;
    private readonly EncryptionService _encryption;
    private readonly EmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailInboxService _inboxService;
    private readonly EmailDraftService _draftService;
    private readonly Mock<IImapConnectionFactory> _mockImapFactory;
    private readonly EmailDeliveryService _deliveryService;

    public EmailDeliveryServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailDelivery_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfile = new CompanyProfileRepository(_persistence, _config);
        var initialProfile = _companyProfile.GetOrCreate();
        initialProfile.Edition = "Business";
        _companyProfile.Save(initialProfile);
        _accounts = new EmailAccountRepository(_persistence, _config);
        _inboxes = new EmailInboxRepository(_persistence, _config);
        _identities = new EmailSendingIdentityRepository(_persistence, _config);
        _folders = new EmailFolderRepository(_persistence, _config);
        _messages = new EmailMessageRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);
        _teams = new TeamRepository(_persistence, _config);
        _encryption = new EncryptionService(_config);

        var syncState = new EmailSyncStateService();
        _cacheMaintenance = new EmailCacheMaintenanceService(_folders, _messages, syncState, _config);

        _inboxService = new EmailInboxService(
            _accounts,
            _inboxes,
            _identities,
            _folders,
            _messages,
            _employees,
            _teams);

        _draftService = new EmailDraftService(
            _folders,
            _messages,
            _cacheMaintenance,
            _accounts,
            _identities,
            new Mock<ILogger<EmailDraftService>>().Object,
            _employees);

        _mockImapFactory = new Mock<IImapConnectionFactory>();

        _deliveryService = new EmailDeliveryService(
            _companyProfile,
            _folders,
            _encryption,
            _cacheMaintenance,
            _messages,
            _identities,
            _inboxes,
            _accounts,
            new Mock<ILogger<EmailDeliveryService>>().Object,
            _mockImapFactory.Object,
            _employees,
            _inboxService,
            _teams);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch { /* best effort */ }
        }
    }

    private SendingServerConfig ConfigureTrustedLoopbackSendingServer(int port, string id = "smtp-trusted-local")
    {
        var server = new SendingServerConfig
        {
            Id = id,
            Name = "Local Relay",
            Host = "127.0.0.1",
            Port = port,
            UseSsl = false,
            UseSharedCredentials = true,
            SharedUsername = "relay-user",
            EncryptedSharedPassword = _encryption.Encrypt("relay-secret")
        };

        var profile = _companyProfile.GetOrCreate();
        profile.EmailSettings.SendingServers = [server];
        _companyProfile.Save(profile);
        return server;
    }

    [Fact]
    public async Task SendEmailAsync_WhenEmployeeIdEqualsAccountId_StillRejectsUnauthorizedSendingIdentity()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port);

        var ceo = new Employee { Id = "emp-ceo", FirstName = "CEO", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(ceo);
        _employees.Save(alice);

        var ceoIdentity = new EmailSendingIdentity
        {
            Id = "id-ceo",
            OwnerEmployeeId = ceo.Id,
            Username = "ceo@company.com",
            DisplayName = "CEO",
            SendingServerConfigId = server.Id
        };
        _identities.Save(ceoIdentity);

        // Regression check: callers with no personal EmailAccount often pass AccountId == EmployeeId.
        // That must NOT bypass identity permission checks.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = alice.Id,
                EmployeeId = alice.Id,
                SendingIdentityId = ceoIdentity.Id,
                To = "external@example.com",
                Subject = "Spoof Attempt",
                Body = "<p>Spoofed</p>"
            }));

        Assert.Empty(smtp.ReceivedMessages);
    }

    [Fact]
    public async Task SendEmailAsync_WhenUserHasZeroPermittedIdentities_DoesNotFallbackToGlobalFirstIdentity()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port);

        var ceo = new Employee { Id = "emp-ceo-2", FirstName = "CEO", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var alice = new Employee { Id = "emp-alice-2", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(ceo);
        _employees.Save(alice);

        _identities.Save(new EmailSendingIdentity
        {
            Id = "id-ceo-only",
            OwnerEmployeeId = ceo.Id,
            Username = "ceo@company.com",
            SendingServerConfigId = server.Id
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = alice.Id,
                EmployeeId = alice.Id,
                SendingIdentityId = string.Empty,
                To = "external@example.com",
                Subject = "Fallback Probe",
                Body = "<p>Test</p>"
            }));

        Assert.Equal("IDENTITY_NOT_CONFIGURED", ex.ErrorCode);
        Assert.Empty(smtp.ReceivedMessages);
    }

    [Fact]
    public async Task SendEmailAsync_VirtualInboxTeamMember_DeliversViaPermittedIdentity_AndRecordsSentInBackingAccount()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port);

        var supportTeam = new Team { Id = "team-support", Name = "Support" };
        _teams.Save(supportTeam);

        // Agent has NO personal EmailAccount; access comes solely from Team membership on the Virtual Inbox.
        var agent = new Employee
        {
            Id = "emp-support-agent",
            FirstName = "Support",
            LastName = "Agent",
            TeamId = supportTeam.Id,
            IsActive = true,
            Permissions = [AppPermissions.Email.Use]
        };
        _employees.Save(agent);

        var backingAccount = new EmailAccount
        {
            Id = "acc-support-backing",
            IsServerManaged = true,
            EmailAddress = "support@company.com"
        };
        _accounts.Save(backingAccount);

        var supportIdentity = new EmailSendingIdentity
        {
            Id = "id-support-desk",
            IsServerManaged = true,
            Username = "support@company.com",
            DisplayName = "Support Desk",
            SendingServerConfigId = server.Id,
            SourceAccountId = backingAccount.Id
        };
        _identities.Save(supportIdentity);

        var virtualInbox = new EmailInbox
        {
            Id = "inbox-support",
            AccountId = backingAccount.Id,
            Name = "Support Queue",
            IsServerManaged = true,
            AllowedTeamIds = [supportTeam.Id],
            DefaultIdentityId = supportIdentity.Id,
            TargetAddresses = ["support@company.com"]
        };
        _inboxes.Save(virtualInbox);

        await _deliveryService.SendEmailAsync(new SendEmailRequest
        {
            AccountId = agent.Id, // Virtual-inbox-only user without personal account ID
            EmployeeId = agent.Id,
            SendingIdentityId = string.Empty, // Resolved automatically from permitted Virtual Inbox identity
            To = "customer@example.com",
            Subject = "Ticket #42 Resolved",
            Body = "<p>Your issue is fixed.</p>"
        });

        var rawMime = Assert.Single(smtp.ReceivedMessages);
        Assert.Contains("support@company.com", rawMime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ticket #42 Resolved", rawMime, StringComparison.Ordinal);

        var sentMessages = _messages.GetByFolder(backingAccount.Id, "Sent");
        var recorded = Assert.Single(sentMessages);
        Assert.Equal("support@company.com", recorded.FromAddress);
        Assert.Equal(supportIdentity.Id, recorded.SendingIdentityId);
        Assert.Equal(agent.Id, recorded.CreatedByEmployeeId);
        Assert.Contains("customer@example.com", recorded.ToAddresses);
    }

    [Fact]
    public async Task SendEmailAsync_RejectsSuspendedBannedInactiveUnknownAndSystemEmployees()
    {
        var suspended = new Employee { Id = "emp-susp", FirstName = "Susp", IsActive = true, IsSuspended = true };
        var banned = new Employee { Id = "emp-ban", FirstName = "Ban", IsActive = true, IsBanned = true };
        var inactive = new Employee { Id = "emp-inact", FirstName = "Inact", IsActive = false };
        _employees.Save(suspended);
        _employees.Save(banned);
        _employees.Save(inactive);

        foreach (var badEmployeeId in new[]
        {
            suspended.Id,
            banned.Id,
            inactive.Id,
            "unknown-employee-id",
            string.Empty,
            CompanyBillInboxConstants.SystemEmployeeId
        })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                _deliveryService.SendEmailAsync(new SendEmailRequest
                {
                    AccountId = "acc-any",
                    EmployeeId = badEmployeeId,
                    To = "recipient@example.com",
                    Subject = "Blocked",
                    Body = "<p>Blocked</p>"
                }));
        }
    }

    [Fact]
    public async Task SendEmailAsync_WhenReferencingAnotherUsersDraftAttachments_ThrowsUnauthorizedAccessException()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port);

        var alice = new Employee { Id = "emp-draft-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-draft-bob", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var sharedAccount = new EmailAccount
        {
            Id = "acc-shared-send",
            IsServerManaged = true,
            EmailAddress = "team@company.com",
            AllowedUserIds = [alice.Id, bob.Id]
        };
        _accounts.Save(sharedAccount);

        var sharedIdentity = new EmailSendingIdentity
        {
            Id = "id-shared-send",
            IsServerManaged = true,
            Username = "team@company.com",
            SendingServerConfigId = server.Id,
            SourceAccountId = sharedAccount.Id,
            AllowedUserIds = [alice.Id, bob.Id]
        };
        _identities.Save(sharedIdentity);

        var secretBytes = Encoding.UTF8.GetBytes("Confidential M&A Terms");
        var aliceDraftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = sharedAccount.Id,
            EmployeeId = alice.Id,
            To = "board@company.com",
            Subject = "Confidential Draft",
            Body = "<p>Confidential</p>",
            Attachments =
            [
                EmailAttachment.FromBytes("terms.txt", secretBytes, "text/plain")
            ]
        });

        var savedDraft = _messages.GetById(sharedAccount.Id, aliceDraftId)!;

        // Bob has legitimate access to sharedAccount and sharedIdentity,
        // but MUST NOT be able to exfiltrate Alice's draft attachment via DraftId + ExistingAttachments.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = sharedAccount.Id,
                EmployeeId = bob.Id,
                SendingIdentityId = sharedIdentity.Id,
                DraftId = aliceDraftId,
                ExistingAttachments = savedDraft.Attachments,
                To = "bob-external@example.com",
                Subject = "Exfiltration Attempt",
                Body = "<p>Stealing attachment</p>"
            }));

        Assert.Empty(smtp.ReceivedMessages);
    }

    [Fact]
    public async Task SendEmailAsync_InteractiveBillInbox_EnforcesAccountingOrBillsPermissions_AndDeliversWhenAuthorized()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port, "smtp-bill-interactive");

        var billAccount = new EmailAccount
        {
            Id = "acc-bill-inbox",
            IsServerManaged = true,
            EmailAddress = "billing@company.com",
            Name = "Accounts Payable"
        };
        _accounts.Save(billAccount);

        var billIdentity = new EmailSendingIdentity
        {
            Id = "id-bill-inbox",
            IsServerManaged = true,
            Username = "billing@company.com",
            DisplayName = "Accounts Payable",
            SendingServerConfigId = server.Id,
            SourceAccountId = billAccount.Id
        };
        _identities.Save(billIdentity);

        var profile = _companyProfile.GetOrCreate();
        profile.Accounting.BillInbox = new CompanyBillInboxSettings
        {
            Enabled = true,
            AccountId = billAccount.Id,
            SendingIdentityId = billIdentity.Id,
            EmailAddress = "billing@company.com",
            DisplayName = "Accounts Payable"
        };
        _companyProfile.Save(profile);

        var unprivilegedUser = new Employee { Id = "emp-regular", FirstName = "Regular", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var billsManager = new Employee
        {
            Id = "emp-ap",
            FirstName = "AP",
            IsActive = true,
            Permissions = [AppPermissions.Purchases.ManageBills]
        };
        _employees.Save(unprivilegedUser);
        _employees.Save(billsManager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = CompanyBillInboxConstants.SystemEmployeeId,
                EmployeeId = unprivilegedUser.Id,
                To = "vendor@example.com",
                Subject = "Unauthorized Bill Reply",
                Body = "<p>Denied</p>"
            }));

        Assert.Empty(smtp.ReceivedMessages);

        await _deliveryService.SendEmailAsync(new SendEmailRequest
        {
            AccountId = CompanyBillInboxConstants.SystemEmployeeId,
            EmployeeId = billsManager.Id,
            To = "vendor@example.com",
            Subject = "Re: Invoice #9001",
            Body = "<p>Payment scheduled.</p>",
            InReplyTo = "<inv-9001@vendor.example.com>",
            References = ["<inv-9001@vendor.example.com>"]
        });

        var rawMime = Assert.Single(smtp.ReceivedMessages);
        Assert.Contains("billing@company.com", rawMime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Re: Invoice #9001", rawMime, StringComparison.Ordinal);

        var billSent = _messages.GetByFolder(billAccount.Id, "Sent");
        var recorded = Assert.Single(billSent);
        Assert.Equal("billing@company.com", recorded.FromAddress);
        Assert.Equal(billsManager.Id, recorded.CreatedByEmployeeId);
        Assert.Equal(CompanyBillInboxConstants.BillingStatus.Approved, recorded.BillingStatus);
    }

    [Fact]
    public async Task SendSystemBillInboxEmailAsync_DeliversAutomatedSystemMailWithoutInteractiveEmployee()
    {
        using var smtp = new LoopbackSmtpTestServer();
        var server = ConfigureTrustedLoopbackSendingServer(smtp.Port, "smtp-bill-automated");

        var billAccount = new EmailAccount
        {
            Id = "acc-factures",
            IsServerManaged = true,
            EmailAddress = "factures@company.com",
            Name = "Facturation"
        };
        _accounts.Save(billAccount);

        var billIdentity = new EmailSendingIdentity
        {
            Id = "id-factures",
            IsServerManaged = true,
            Username = "factures@company.com",
            DisplayName = "Facturation",
            SendingServerConfigId = server.Id,
            SourceAccountId = billAccount.Id
        };
        _identities.Save(billIdentity);

        var profile = _companyProfile.GetOrCreate();
        profile.Accounting.BillInbox = new CompanyBillInboxSettings
        {
            Enabled = true,
            AccountId = billAccount.Id,
            SendingIdentityId = billIdentity.Id,
            EmailAddress = "factures@company.com",
            DisplayName = "Facturation"
        };
        _companyProfile.Save(profile);

        await _deliveryService.SendSystemBillInboxEmailAsync(new SendEmailRequest
        {
            AccountId = CompanyBillInboxConstants.SystemEmployeeId,
            To = "supplier@example.com",
            Subject = "Automated Remittance Notice",
            Body = "<p>Automated notice</p>"
        });

        var rawMime = Assert.Single(smtp.ReceivedMessages);
        Assert.Contains("factures@company.com", rawMime, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Automated Remittance Notice", rawMime, StringComparison.Ordinal);

        var billSent = _messages.GetByFolder(billAccount.Id, "Sent");
        Assert.Single(billSent);
    }

    [Fact]
    public async Task SendEmailAsync_UntrustedCustomSmtpIdentity_BlocksLoopbackSsrfAndPlaintextTransport()
    {
        using var smtp = new LoopbackSmtpTestServer();

        var alice = new Employee { Id = "emp-ssrf-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);

        var loopbackIdentity = new EmailSendingIdentity
        {
            Id = "id-custom-loopback",
            IsServerManaged = false,
            OwnerEmployeeId = alice.Id,
            Username = "alice@external.com",
            CustomSmtpHost = "127.0.0.1",
            CustomSmtpPort = smtp.Port,
            CustomSmtpSsl = true
        };
        _identities.Save(loopbackIdentity);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = alice.Id,
                EmployeeId = alice.Id,
                SendingIdentityId = loopbackIdentity.Id,
                To = "target@example.com",
                Subject = "SSRF Attempt",
                Body = "<p>Probe</p>"
            }));

        Assert.Empty(smtp.ReceivedMessages);

        var plaintextIdentity = new EmailSendingIdentity
        {
            Id = "id-custom-plaintext",
            IsServerManaged = false,
            OwnerEmployeeId = alice.Id,
            Username = "alice@external.com",
            CustomSmtpHost = "smtp.example.com",
            CustomSmtpPort = 587,
            CustomSmtpSsl = false
        };
        _identities.Save(plaintextIdentity);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = alice.Id,
                EmployeeId = alice.Id,
                SendingIdentityId = plaintextIdentity.Id,
                To = "target@example.com",
                Subject = "Plaintext Attempt",
                Body = "<p>Plaintext</p>"
            }));
    }

    private sealed class LoopbackSmtpTestServer : IDisposable
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public ConcurrentQueue<string> ReceivedMessages { get; } = new();
        public int Port { get; }

        public LoopbackSmtpTestServer()
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
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    break;
                }

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

                    if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("HELO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250-8BITMIME");
                        await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                        await writer.WriteLineAsync("250 OK");
                    }
                    else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        if (line.Trim().Equals("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("334 ");
                            _ = await reader.ReadLineAsync(ct);
                        }
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                        _ = await reader.ReadLineAsync(ct);
                        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
                        _ = await reader.ReadLineAsync(ct);
                        await writer.WriteLineAsync("235 2.7.0 Authentication successful");
                    }
                    else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 2.1.0 OK");
                    }
                    else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 2.1.5 OK");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var dataBuilder = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync(ct);
                            if (dataLine == null || dataLine == ".") break;
                            if (dataLine.StartsWith("..", StringComparison.Ordinal))
                            {
                                dataLine = dataLine[1..];
                            }
                            dataBuilder.Append(dataLine).Append("\r\n");
                        }
                        ReceivedMessages.Enqueue(dataBuilder.ToString());
                        await writer.WriteLineAsync("250 2.0.0 Queued");
                    }
                    else if (line.Equals("RSET", StringComparison.OrdinalIgnoreCase) ||
                             line.Equals("NOOP", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250 2.0.0 OK");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 2.0.0 Bye");
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
