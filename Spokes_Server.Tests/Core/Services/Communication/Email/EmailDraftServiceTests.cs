using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

public class EmailDraftServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly EmailAccountRepository _accounts;
    private readonly EmailSendingIdentityRepository _identities;
    private readonly EmailFolderRepository _folders;
    private readonly EmailMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly EmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailDraftService _draftService;

    public EmailDraftServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailDrafts_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _accounts = new EmailAccountRepository(_persistence, _config);
        _identities = new EmailSendingIdentityRepository(_persistence, _config);
        _folders = new EmailFolderRepository(_persistence, _config);
        _messages = new EmailMessageRepository(_persistence, _config);
        _employees = new EmployeeRepository(_persistence, _config);

        var syncState = new EmailSyncStateService();
        _cacheMaintenance = new EmailCacheMaintenanceService(_folders, _messages, syncState, _config);

        _draftService = new EmailDraftService(
            _folders,
            _messages,
            _cacheMaintenance,
            _accounts,
            _identities,
            new Mock<ILogger<EmailDraftService>>().Object,
            _employees);
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

    [Fact]
    public async Task SaveDraftAsync_SetsCreatedByEmployeeId_AndBlocksSharedAccountCoworkerFromOverwritingDraft()
    {
        var alice = new Employee { Id = "emp-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-bob", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var sharedAccount = new EmailAccount
        {
            Id = "acc-shared",
            IsServerManaged = true,
            EmailAddress = "team@company.com",
            AllowedUserIds = [alice.Id, bob.Id]
        };
        _accounts.Save(sharedAccount);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = sharedAccount.Id,
            EmployeeId = alice.Id,
            To = "client@example.com",
            Subject = "Alice Confidential Proposal",
            Body = "<p>Initial draft</p>"
        });

        var saved = _messages.GetById(sharedAccount.Id, draftId);
        Assert.NotNull(saved);
        Assert.Equal(alice.Id, saved!.CreatedByEmployeeId);
        Assert.Equal("LocalDrafts", saved.FolderPath);
        Assert.True(saved.IsLocalOnly);

        // Co-worker Bob has full access to sharedAccount, but MUST NOT be able to overwrite Alice's draft
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.SaveDraftAsync(new SaveDraftRequest
            {
                AccountId = sharedAccount.Id,
                EmployeeId = bob.Id,
                ExistingDraftId = draftId,
                To = "attacker@example.com",
                Subject = "Tampered",
                Body = "<p>Overwritten by Bob</p>"
            }));

        // Verify Alice's draft remains untouched
        var afterAttempt = _messages.GetById(sharedAccount.Id, draftId);
        Assert.NotNull(afterAttempt);
        Assert.Equal("Alice Confidential Proposal", afterAttempt!.Subject);

        // Alice can update her own draft
        var updatedId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = sharedAccount.Id,
            EmployeeId = alice.Id,
            ExistingDraftId = draftId,
            To = "client@example.com",
            Subject = "Alice Updated Proposal",
            Body = "<p>Updated by Alice</p>"
        });
        Assert.Equal(draftId, updatedId);
        Assert.Equal("Alice Updated Proposal", _messages.GetById(sharedAccount.Id, draftId)!.Subject);
    }

    [Fact]
    public async Task RemoveDraftAttachmentAsync_And_DeleteDraftAsync_EnforceCreatorIsolation()
    {
        var alice = new Employee { Id = "emp-att-alice", FirstName = "Alice", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var bob = new Employee { Id = "emp-att-bob", FirstName = "Bob", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        _employees.Save(alice);
        _employees.Save(bob);

        var sharedAccount = new EmailAccount
        {
            Id = "acc-shared-att",
            IsServerManaged = true,
            EmailAddress = "shared@company.com",
            AllowedUserIds = [alice.Id, bob.Id]
        };
        _accounts.Save(sharedAccount);

        var payload = Encoding.UTF8.GetBytes("Top secret attachment");
        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = sharedAccount.Id,
            EmployeeId = alice.Id,
            To = "partner@example.com",
            Subject = "With Attachment",
            Body = "<p>See attached</p>",
            Attachments =
            [
                EmailAttachment.FromBytes("secret.txt", payload, "text/plain")
            ]
        });

        var draft = _messages.GetById(sharedAccount.Id, draftId);
        Assert.NotNull(draft);
        var attId = Assert.Single(draft!.Attachments).Id;

        // Bob cannot remove Alice's draft attachment or delete Alice's draft
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.RemoveDraftAttachmentAsync(sharedAccount.Id, draftId, attId, bob.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.DeleteDraftAsync(sharedAccount.Id, draftId, bob.Id));

        Assert.Single(_messages.GetById(sharedAccount.Id, draftId)!.Attachments);

        // Alice can remove her attachment and delete her draft
        await _draftService.RemoveDraftAttachmentAsync(sharedAccount.Id, draftId, attId, alice.Id);
        Assert.Empty(_messages.GetById(sharedAccount.Id, draftId)!.Attachments);

        await _draftService.DeleteDraftAsync(sharedAccount.Id, draftId, alice.Id);
        Assert.Null(_messages.GetById(sharedAccount.Id, draftId));
    }

    [Fact]
    public async Task DraftOperations_RejectSuspendedBannedAndUnknownEmployees()
    {
        var activeCreator = new Employee { Id = "emp-active", FirstName = "Active", IsActive = true, Permissions = [AppPermissions.Email.Use] };
        var suspendedUser = new Employee { Id = "emp-suspended", FirstName = "Suspended", IsActive = true, IsSuspended = true };
        var bannedUser = new Employee { Id = "emp-banned", FirstName = "Banned", IsActive = true, IsBanned = true };
        _employees.Save(activeCreator);
        _employees.Save(suspendedUser);
        _employees.Save(bannedUser);

        var account = new EmailAccount { Id = "acc-mod", OwnerEmployeeId = activeCreator.Id, EmailAddress = "user@company.com" };
        _accounts.Save(account);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = account.Id,
            EmployeeId = activeCreator.Id,
            Subject = "Valid Draft",
            Body = "Body"
        });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.SaveDraftAsync(new SaveDraftRequest
            {
                AccountId = account.Id,
                EmployeeId = suspendedUser.Id,
                Subject = "Blocked",
                Body = "Body"
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.SaveDraftAsync(new SaveDraftRequest
            {
                AccountId = account.Id,
                EmployeeId = bannedUser.Id,
                Subject = "Blocked",
                Body = "Body"
            }));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.SaveDraftAsync(new SaveDraftRequest
            {
                AccountId = account.Id,
                EmployeeId = "non-existent-employee",
                Subject = "Blocked",
                Body = "Body"
            }));

        // If activeCreator is subsequently suspended, deleting or modifying their existing draft is also blocked
        activeCreator.IsSuspended = true;
        _employees.Save(activeCreator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _draftService.DeleteDraftAsync(account.Id, draftId, activeCreator.Id));
    }
}
