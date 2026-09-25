using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Tests.Core.Services.Core;

public class AccountDeletionServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly PresenceStateService _presenceState;
    private readonly AccountDeletionService _service;
    private readonly List<string> _loggedOutSessionIds = [];

    public AccountDeletionServiceTests()
    {
        _mockConfig = new Mock<IConfiguration>();
        _mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_mockConfig.Object);
        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _presenceState = new PresenceStateService(new ChatStateService(), _db);
        _presenceState.OnSessionRevoked += sessionId => _loggedOutSessionIds.Add(sessionId);

        _service = new AccountDeletionService(_db, _mockConfig.Object, _presenceState);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    #region Constructor

    [Fact]
    public void Constructor_DefaultDataPath_WhenConfigDataPathNull()
    {
        var emptyConfig = new Mock<IConfiguration>();
        emptyConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var service = new AccountDeletionService(_db, emptyConfig.Object, _presenceState);

        Assert.NotNull(service);
    }

    #endregion

    #region 1. System Deleted User

    [Fact]
    public async Task DeleteAccountAsync_WhenSystemDeletedUserDoesNotExist_CreatesSystemDeletedUser()
    {
        var emp = new Employee { Id = "emp-del-1", FirstName = "Alice", LastName = "Smith" };
        _db.Employees.Save(emp);

        Assert.Null(_db.Employees.GetById("system-deleted-user"));

        await _service.DeleteAccountAsync("emp-del-1");

        var sysUser = _db.Employees.GetById("system-deleted-user");
        Assert.NotNull(sysUser);
        Assert.Equal("system-deleted-user", sysUser.Id);
        Assert.Equal("Deleted", sysUser.FirstName);
        Assert.Equal("User", sysUser.LastName);
        Assert.Equal("deleted@system.local", sysUser.Email);
        Assert.False(sysUser.IsActive);
        Assert.True(sysUser.IsSystem);
        Assert.False(sysUser.IsSelectable);
    }

    [Fact]
    public async Task DeleteAccountAsync_WhenSystemDeletedUserAlreadyExists_PreservesExistingUser()
    {
        var existingSysUser = new Employee
        {
            Id = "system-deleted-user",
            FirstName = "PreExisting",
            LastName = "SystemAccount",
            Email = "custom-sys@domain.com",
            IsActive = false
        };
        _db.Employees.Save(existingSysUser);

        var emp = new Employee { Id = "emp-del-2", FirstName = "Bob", LastName = "Jones" };
        _db.Employees.Save(emp);

        await _service.DeleteAccountAsync("emp-del-2");

        var sysUser = _db.Employees.GetById("system-deleted-user");
        Assert.NotNull(sysUser);
        Assert.Equal("PreExisting", sysUser.FirstName);
        Assert.Equal("SystemAccount", sysUser.LastName);
        Assert.Equal("custom-sys@domain.com", sysUser.Email);
        Assert.True(sysUser.IsSystem);
        Assert.False(sysUser.IsActive);
        Assert.False(sysUser.IsSelectable);
    }

    [Fact]
    public async Task DeleteAccountAsync_CleansUpChannelParticipantsAndAllowedPostUsers()
    {
        var empId = "emp-channel-member";
        var otherId = "emp-channel-admin";
        _db.Employees.Save(new Employee { Id = empId, FirstName = "Channel", LastName = "User" });
        _db.Employees.Save(new Employee { Id = otherId, FirstName = "Admin", LastName = "User" });

        var channel = new ChatChannel
        {
            Id = "ch-test-cleanup",
            Name = "Announcement Channel",
            ParticipantIds = new List<string> { empId, otherId },
            AllowedPostUserIds = new List<string> { empId, otherId }
        };
        _db.ChatChannels.Save(channel);

        await _service.DeleteAccountAsync(empId);

        var reloadedChannel = _db.ChatChannels.GetById("ch-test-cleanup");
        Assert.NotNull(reloadedChannel);
        Assert.DoesNotContain(empId, reloadedChannel.ParticipantIds);
        Assert.Contains(otherId, reloadedChannel.ParticipantIds);
        Assert.DoesNotContain(empId, reloadedChannel.AllowedPostUserIds);
        Assert.Contains(otherId, reloadedChannel.AllowedPostUserIds);
    }

    #endregion

    #region 2. Device Sessions & Remote Logout

    [Fact]
    public async Task DeleteAccountAsync_DeletesActiveSessions_AndTriggersRemoteLogout()
    {
        var empId = "emp-sessions";
        _db.Employees.Save(new Employee { Id = empId, FirstName = "User", LastName = "Sessions" });

        var s1 = new DeviceSession { Id = "sess-1", EmployeeId = empId };
        var s2 = new DeviceSession { Id = "sess-2", EmployeeId = empId };
        var sOther = new DeviceSession { Id = "sess-other", EmployeeId = "emp-other" };

        _db.DeviceSessions.Save(s1);
        _db.DeviceSessions.Save(s2);
        _db.DeviceSessions.Save(sOther);

        await _service.DeleteAccountAsync(empId);

        Assert.Null(_db.DeviceSessions.GetById("sess-1"));
        Assert.Null(_db.DeviceSessions.GetById("sess-2"));
        Assert.NotNull(_db.DeviceSessions.GetById("sess-other"));

        Assert.Contains("sess-1", _loggedOutSessionIds);
        Assert.Contains("sess-2", _loggedOutSessionIds);
        Assert.DoesNotContain("sess-other", _loggedOutSessionIds);
    }

    [Fact]
    public async Task DeleteAccountAsync_WithExcludeSessionId_AnonymizesAndRevokesExcludedSession_AndDeletesOthers()
    {
        var empId = "emp-exclude";
        _db.Employees.Save(new Employee { Id = empId, FirstName = "Exclude", LastName = "Tester" });

        var sCurrent = new DeviceSession { Id = "sess-current", EmployeeId = empId };
        var sOld = new DeviceSession { Id = "sess-old", EmployeeId = empId };

        _db.DeviceSessions.Save(sCurrent);
        _db.DeviceSessions.Save(sOld);

        var beforeTime = DateTime.UtcNow.AddSeconds(-1);
        await _service.DeleteAccountAsync(empId, excludeSessionId: "sess-current");
        var afterTime = DateTime.UtcNow.AddSeconds(1);

        // Excluded session should NOT be deleted; it should be re-parented and revoked
        var sCurrentUpdated = _db.DeviceSessions.GetById("sess-current");
        Assert.NotNull(sCurrentUpdated);
        Assert.Equal("system-deleted-user", sCurrentUpdated.EmployeeId);
        Assert.NotNull(sCurrentUpdated.RevokedAt);
        Assert.InRange(sCurrentUpdated.RevokedAt.Value, beforeTime, afterTime);

        // Other session for the same user should be deleted
        Assert.Null(_db.DeviceSessions.GetById("sess-old"));

        // Remote logout should only be triggered for the non-excluded session
        Assert.DoesNotContain("sess-current", _loggedOutSessionIds);
        Assert.Contains("sess-old", _loggedOutSessionIds);
    }

    [Fact]
    public void DeviceSession_ClearPushFields_ClearsAllPushRelatedProperties()
    {
        var session = new DeviceSession
        {
            Id = "sess-push-test",
            EmployeeId = "emp-push",
            PushEndpoint = "https://push.example.com/sub/123",
            PushP256dh = "p256dh-key-sample",
            PushAuth = "auth-secret-sample",
            PushPublicKey = "rsa-pub-sample",
            PushSubscriptionType = "NativeRelay",
            PushEnabled = true,
            PushSubscribedAt = DateTime.UtcNow,
            PushUserAgent = "Mozilla/5.0 TestBrowser"
        };
        _db.DeviceSessions.Save(session);
        Assert.True(session.HasPush);

        _db.DeviceSessions.ClearPushFields(session);

        var updated = _db.DeviceSessions.GetById("sess-push-test");
        Assert.NotNull(updated);
        Assert.Null(updated.PushEndpoint);
        Assert.Equal(string.Empty, updated.PushP256dh);
        Assert.Equal(string.Empty, updated.PushAuth);
        Assert.Equal(string.Empty, updated.PushPublicKey);
        Assert.Equal("WebPush", updated.PushSubscriptionType);
        Assert.False(updated.PushEnabled);
        Assert.Null(updated.PushSubscribedAt);
        Assert.Equal(string.Empty, updated.PushUserAgent);
        Assert.False(updated.HasPush);
    }

    #endregion

    #region 3. Chat Messages & Attachments

    [Fact]
    public async Task DeleteAccountAsync_ReparentsChatMessages_ClearsAttachments_DeletesAttachmentFiles_AndSetsDeletedText()
    {
        var empId = "emp-chat";
        _db.Employees.Save(new Employee { Id = empId, FirstName = "Chat", LastName = "User" });

        // Create attachment files on disk
        var attachDir = Path.Combine(_testDataPath, "attachments");
        Directory.CreateDirectory(attachDir);

        var file1Rel = Path.Combine("attachments", "photo1.png");
        var file2Rel = Path.Combine("attachments", "doc2.pdf");
        var file1Full = Path.Combine(_testDataPath, file1Rel);
        var file2Full = Path.Combine(_testDataPath, file2Rel);

        await File.WriteAllTextAsync(file1Full, "dummy photo content");
        await File.WriteAllTextAsync(file2Full, "dummy doc content");
        Assert.True(File.Exists(file1Full));
        Assert.True(File.Exists(file2Full));

        // Message 1: Empty text with attachment -> should become "[Deleted Image]"
        var msg1 = new ChatMessage
        {
            Id = "msg-1",
            SenderId = empId,
            Content = "   ",
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment { Id = "att-1", FilePath = file1Rel }
            }
        };

        // Message 2: Non-empty text with attachment -> text preserved, attachments cleared
        var msg2 = new ChatMessage
        {
            Id = "msg-2",
            SenderId = empId,
            Content = "Check out this document!",
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment { Id = "att-2", FilePath = file2Rel }
            }
        };

        // Message 3: Text only, no attachments -> text preserved, re-parented
        var msg3 = new ChatMessage
        {
            Id = "msg-3",
            SenderId = empId,
            Content = "Plain message without attachment",
            Attachments = new List<ChatAttachment>()
        };

        // Message 4: From another user -> untouched
        var msgOther = new ChatMessage
        {
            Id = "msg-other",
            SenderId = "emp-other",
            Content = "Hello from other user"
        };

        _db.ChatMessages.Save(msg1);
        _db.ChatMessages.Save(msg2);
        _db.ChatMessages.Save(msg3);
        _db.ChatMessages.Save(msgOther);

        await _service.DeleteAccountAsync(empId);

        // Physical attachment files should be deleted from disk
        Assert.False(File.Exists(file1Full));
        Assert.False(File.Exists(file2Full));

        // Message 1: re-parented, attachments wiped, empty content replaced with "[Deleted Image]"
        var updatedMsg1 = _db.ChatMessages.GetById("msg-1");
        Assert.NotNull(updatedMsg1);
        Assert.Equal("system-deleted-user", updatedMsg1.SenderId);
        Assert.Empty(updatedMsg1.Attachments);
        Assert.Equal("[Deleted Image]", updatedMsg1.Content);

        // Message 2: re-parented, attachments wiped, original content preserved
        var updatedMsg2 = _db.ChatMessages.GetById("msg-2");
        Assert.NotNull(updatedMsg2);
        Assert.Equal("system-deleted-user", updatedMsg2.SenderId);
        Assert.Empty(updatedMsg2.Attachments);
        Assert.Equal("Check out this document!", updatedMsg2.Content);

        // Message 3: re-parented, original content preserved
        var updatedMsg3 = _db.ChatMessages.GetById("msg-3");
        Assert.NotNull(updatedMsg3);
        Assert.Equal("system-deleted-user", updatedMsg3.SenderId);
        Assert.Equal("Plain message without attachment", updatedMsg3.Content);

        // Message 4: completely untouched
        var updatedOther = _db.ChatMessages.GetById("msg-other");
        Assert.NotNull(updatedOther);
        Assert.Equal("emp-other", updatedOther.SenderId);
        Assert.Equal("Hello from other user", updatedOther.Content);
    }

    [Fact]
    public async Task DeleteAccountAsync_WhenAttachmentFileDoesNotExistOnDisk_CleansUpWithoutThrowing()
    {
        var empId = "emp-missing-file";
        _db.Employees.Save(new Employee { Id = empId });

        var msg = new ChatMessage
        {
            Id = "msg-missing-file",
            SenderId = empId,
            Content = null!,
            Attachments = new List<ChatAttachment>
            {
                new ChatAttachment { Id = "att-missing", FilePath = "attachments/non-existent-file.bin" }
            }
        };
        _db.ChatMessages.Save(msg);

        await _service.DeleteAccountAsync(empId);

        var updated = _db.ChatMessages.GetById("msg-missing-file");
        Assert.NotNull(updated);
        Assert.Equal("system-deleted-user", updated.SenderId);
        Assert.Empty(updated.Attachments);
        Assert.Equal("[Deleted Image]", updated.Content);
    }

    #endregion

    #region 4. Shared Data Re-parenting (ProjectNotes & ReportedMessages)

    [Fact]
    public async Task DeleteAccountAsync_ReparentsProjectNotes_ToSystemDeletedUser()
    {
        var empId = "emp-notes";
        _db.Employees.Save(new Employee { Id = empId });

        var note1 = new ProjectNote { Id = "note-1", CreatedBy = empId, Title = "Note 1" };
        var note2 = new ProjectNote { Id = "note-2", CreatedBy = "emp-other", Title = "Other Note" };

        _db.ProjectNotes.Save(note1);
        _db.ProjectNotes.Save(note2);

        await _service.DeleteAccountAsync(empId);

        var updated1 = _db.ProjectNotes.GetById("note-1");
        Assert.NotNull(updated1);
        Assert.Equal("system-deleted-user", updated1.CreatedBy);

        var updated2 = _db.ProjectNotes.GetById("note-2");
        Assert.NotNull(updated2);
        Assert.Equal("emp-other", updated2.CreatedBy);
    }

    [Fact]
    public async Task DeleteAccountAsync_ReparentsReportedMessages_ToSystemDeletedUser()
    {
        var empId = "emp-reporter";
        _db.Employees.Save(new Employee { Id = empId });

        var report1 = new ReportedMessage { Id = "rep-1", ReporterId = empId, ReportedUserId = "bad-actor" };
        var report2 = new ReportedMessage { Id = "rep-2", ReporterId = "other-reporter", ReportedUserId = "bad-actor" };

        _db.ReportedMessages.Save(report1);
        _db.ReportedMessages.Save(report2);

        await _service.DeleteAccountAsync(empId);

        var updated1 = _db.ReportedMessages.GetById("rep-1");
        Assert.NotNull(updated1);
        Assert.Equal("system-deleted-user", updated1.ReporterId);

        var updated2 = _db.ReportedMessages.GetById("rep-2");
        Assert.NotNull(updated2);
        Assert.Equal("other-reporter", updated2.ReporterId);
    }

    #endregion

    #region 5. Private Data Hard-Deletion (EmailFolders, EmailMessages, PrivateContacts)

    [Fact]
    public async Task DeleteAccountAsync_DeletesEmailFolders_EmailMessages_AndPrivateContacts()
    {
        var empId = "emp-private";
        var otherId = "emp-keep";

        _db.Employees.Save(new Employee { Id = empId });
        _db.Employees.Save(new Employee { Id = otherId });

        // Email folders
        var f1 = new EmailFolder { Id = "f-1", EmployeeId = empId, Name = "Inbox", Path = "INBOX" };
        var fKeep = new EmailFolder { Id = "f-keep", EmployeeId = otherId, Name = "Inbox", Path = "INBOX" };
        _db.EmailFolders.Save(f1);
        _db.EmailFolders.Save(fKeep);

        // Email messages
        var em1 = new EmailMessage { Id = "em-1", EmployeeId = empId, FolderPath = "INBOX", UniqueId = 1 };
        var emKeep = new EmailMessage { Id = "em-keep", EmployeeId = otherId, FolderPath = "INBOX", UniqueId = 2 };
        _db.EmailMessages.Save(em1);
        _db.EmailMessages.Save(emKeep);

        // Private contacts
        var c1 = new ContactPerson { Id = "c-1", Name = "Private Guy" };
        var cKeep = new ContactPerson { Id = "c-keep", Name = "Other Guy" };
        _db.PrivateContacts.Save(empId, c1);
        _db.PrivateContacts.Save(otherId, cKeep);

        await _service.DeleteAccountAsync(empId);

        // Email Folders: deleted for empId, preserved for otherId
        Assert.Null(_db.EmailFolders.GetById("f-1"));
        Assert.NotNull(_db.EmailFolders.GetById("f-keep"));

        // Email Messages: deleted for empId, preserved for otherId
        Assert.Null(_db.EmailMessages.GetById("em-1"));
        Assert.NotNull(_db.EmailMessages.GetById("em-keep"));

        // Private Contacts: deleted for empId, preserved for otherId
        Assert.Empty(_db.PrivateContacts.GetAllForEmployee(empId));
        var otherContacts = _db.PrivateContacts.GetAllForEmployee(otherId);
        Assert.Single(otherContacts);
        Assert.Equal("c-keep", otherContacts[0].Id);
    }

    #endregion

    #region 6. User Records & Linked OpenId Accounts

    [Fact]
    public async Task DeleteAccountAsync_DeletesLinkedOpenIdAccounts_AndEmployeeRecord()
    {
        var empId = "emp-oid";
        var otherId = "emp-oid-other";

        _db.Employees.Save(new Employee { Id = empId, FirstName = "Oid", LastName = "User" });
        _db.Employees.Save(new Employee { Id = otherId, FirstName = "Oid", LastName = "Keep" });

        var acc1 = new OpenIdAccount { Id = "oid-1", LinkedEmployeeId = empId, Sub = "sub-1" };
        var acc2 = new OpenIdAccount { Id = "oid-2", LinkedEmployeeId = empId, Sub = "sub-2" };
        var accKeep = new OpenIdAccount { Id = "oid-keep", LinkedEmployeeId = otherId, Sub = "sub-keep" };

        _db.OpenIdAccounts.Save(acc1);
        _db.OpenIdAccounts.Save(acc2);
        _db.OpenIdAccounts.Save(accKeep);

        await _service.DeleteAccountAsync(empId);

        // OpenID accounts for empId deleted
        Assert.Null(_db.OpenIdAccounts.GetById("oid-1"));
        Assert.Null(_db.OpenIdAccounts.GetById("oid-2"));
        // OpenID accounts for other user preserved
        Assert.NotNull(_db.OpenIdAccounts.GetById("oid-keep"));

        // Employee record deleted
        Assert.Null(_db.Employees.GetById(empId));
        Assert.NotNull(_db.Employees.GetById(otherId));
    }

    #endregion

    #region 7. Physical Directory Wipe

    [Fact]
    public async Task DeleteAccountAsync_DeletesPhysicalEmployeeDirectory()
    {
        var empId = "emp-disk";
        var otherId = "emp-disk-keep";

        _db.Employees.Save(new Employee { Id = empId });
        _db.Employees.Save(new Employee { Id = otherId });

        var empDir = Path.Combine(_testDataPath, "Employees", empId);
        var subDir = Path.Combine(empDir, "Avatar");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "avatar.png"), "avatar data");

        var otherDir = Path.Combine(_testDataPath, "Employees", otherId);
        Directory.CreateDirectory(otherDir);
        await File.WriteAllTextAsync(Path.Combine(otherDir, "file.txt"), "other data");

        Assert.True(Directory.Exists(empDir));
        Assert.True(Directory.Exists(otherDir));

        await _service.DeleteAccountAsync(empId);

        Assert.False(Directory.Exists(empDir));
        Assert.True(Directory.Exists(otherDir));
    }

    [Fact]
    public async Task DeleteAccountAsync_WhenEmployeeDirectoryDoesNotExist_CompletesSuccessfully()
    {
        var empId = "emp-no-dir";
        _db.Employees.Save(new Employee { Id = empId });

        var empDir = Path.Combine(_testDataPath, "Employees", empId);
        Assert.False(Directory.Exists(empDir));

        await _service.DeleteAccountAsync(empId);

        Assert.Null(_db.Employees.GetById(empId));
    }

    #endregion

    #region End-to-End Integration

    [Fact]
    public async Task DeleteAccountAsync_FullAccountDeletion_CleansAllUserAssetsAndPreservesOthers()
    {
        var empId = "emp-full-test";
        var otherId = "emp-untouched";

        _db.Employees.Save(new Employee { Id = empId, FirstName = "Full", LastName = "Deletion" });
        _db.Employees.Save(new Employee { Id = otherId, FirstName = "Untouched", LastName = "User" });

        // Sessions
        var sCurrent = new DeviceSession { Id = "s-cur", EmployeeId = empId };
        var sOtherSess = new DeviceSession { Id = "s-oth", EmployeeId = empId };
        var sUntouched = new DeviceSession { Id = "s-unt", EmployeeId = otherId };
        _db.DeviceSessions.Save(sCurrent);
        _db.DeviceSessions.Save(sOtherSess);
        _db.DeviceSessions.Save(sUntouched);

        // Chat with attachment
        var attachDir = Path.Combine(_testDataPath, "attachments");
        Directory.CreateDirectory(attachDir);
        var attachRel = Path.Combine("attachments", "full_test.png");
        var attachFull = Path.Combine(_testDataPath, attachRel);
        await File.WriteAllTextAsync(attachFull, "image payload");

        _db.ChatMessages.Save(new ChatMessage
        {
            Id = "m-full",
            SenderId = empId,
            Content = "",
            Attachments = new List<ChatAttachment> { new ChatAttachment { FilePath = attachRel } }
        });

        // Project Notes & Reported Messages
        _db.ProjectNotes.Save(new ProjectNote { Id = "n-full", CreatedBy = empId, Title = "Note" });
        _db.ReportedMessages.Save(new ReportedMessage { Id = "r-full", ReporterId = empId, ReportedUserId = "other" });

        // Email Folders, Messages, Private Contacts
        _db.EmailFolders.Save(new EmailFolder { Id = "ef-full", EmployeeId = empId, Name = "Trash" });
        _db.EmailMessages.Save(new EmailMessage { Id = "em-full", EmployeeId = empId, FolderPath = "Trash", UniqueId = 99 });
        _db.PrivateContacts.Save(empId, new ContactPerson { Id = "pc-full", Name = "Secret Contact" });

        // Linked OpenID
        _db.OpenIdAccounts.Save(new OpenIdAccount { Id = "oid-full", LinkedEmployeeId = empId, Sub = "sub-full" });

        // Employee Directory
        var empDir = Path.Combine(_testDataPath, "Employees", empId);
        Directory.CreateDirectory(empDir);
        await File.WriteAllTextAsync(Path.Combine(empDir, "data.bin"), "some bytes");

        // Act: Delete account while keeping s-cur as the excluded session
        await _service.DeleteAccountAsync(empId, excludeSessionId: "s-cur");

        // Verify System Deleted User created
        var sysUser = _db.Employees.GetById("system-deleted-user");
        Assert.NotNull(sysUser);

        // Verify Employee deleted
        Assert.Null(_db.Employees.GetById(empId));
        Assert.NotNull(_db.Employees.GetById(otherId));

        // Verify Sessions
        var curSess = _db.DeviceSessions.GetById("s-cur");
        Assert.NotNull(curSess);
        Assert.Equal("system-deleted-user", curSess.EmployeeId);
        Assert.NotNull(curSess.RevokedAt);
        Assert.Null(_db.DeviceSessions.GetById("s-oth"));
        Assert.NotNull(_db.DeviceSessions.GetById("s-unt"));

        // Verify Chat
        var chatMsg = _db.ChatMessages.GetById("m-full");
        Assert.NotNull(chatMsg);
        Assert.Equal("system-deleted-user", chatMsg.SenderId);
        Assert.Equal("[Deleted Image]", chatMsg.Content);
        Assert.Empty(chatMsg.Attachments);
        Assert.False(File.Exists(attachFull));

        // Verify Project Notes & Reports
        Assert.Equal("system-deleted-user", _db.ProjectNotes.GetById("n-full")!.CreatedBy);
        Assert.Equal("system-deleted-user", _db.ReportedMessages.GetById("r-full")!.ReporterId);

        // Verify Private Data
        Assert.Null(_db.EmailFolders.GetById("ef-full"));
        Assert.Null(_db.EmailMessages.GetById("em-full"));
        Assert.Empty(_db.PrivateContacts.GetAllForEmployee(empId));

        // Verify OpenId
        Assert.Null(_db.OpenIdAccounts.GetById("oid-full"));

        // Verify Physical Directory
        Assert.False(Directory.Exists(empDir));
    }

    #endregion
}
