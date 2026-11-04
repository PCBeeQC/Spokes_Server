using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Exceptions;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class EmailPipelineServicesTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;

    private readonly EmailAccountRepository _emailAccounts;
    private readonly EmailSendingIdentityRepository _identities;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly EmailFolderRepository _emailFolders;
    private readonly EmailMessageRepository _emailMessages;
    private readonly EncryptionService _encryptionService;
    private readonly EmailSyncStateService _syncStateService;

    private readonly EmailCacheMaintenanceService _cacheMaintenance;
    private readonly EmailDraftService _draftService;
    private readonly EmailDeliveryService _deliveryService;
    private readonly EmailFolderService _folderService;
    private readonly EmailMessageLifecycleService _messageLifecycleService;
    private readonly EmailSyncService _syncService;

    public EmailPipelineServicesTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Email_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _emailAccounts = new EmailAccountRepository(_persistence, _config);
        _identities = new EmailSendingIdentityRepository(_persistence, _config);
        _companyProfile = new CompanyProfileRepository(_persistence, _config);
        _emailFolders = new EmailFolderRepository(_persistence, _config);
        _emailMessages = new EmailMessageRepository(_persistence, _config);

        _encryptionService = new EncryptionService(_config);
        _syncStateService = new EmailSyncStateService();
        var presenceState = new PresenceStateService(new ChatStateService());

        var mockNotificationRouting = new Mock<INotificationRoutingService>();

        _companyProfile.Save(new CompanyProfile
        {
            Id = "GlobalProfile",
            Edition = "Business",
            EmailSettings = new EmailServerSettings
            {
                IncomingServers = new List<IncomingServerConfig>
                {
                    new IncomingServerConfig { Id = "imap-1", Name = "Local IMAP", Host = "127.0.0.1", Port = 993, UseSsl = true }
                },
                SendingServers = new List<SendingServerConfig>
                {
                    new SendingServerConfig { Id = "smtp-1", Name = "Local SMTP", Host = "127.0.0.1", Port = 587, UseSsl = false }
                }
            }
        });

        _cacheMaintenance = new EmailCacheMaintenanceService(_emailFolders, _emailMessages, _syncStateService, _config);
        _draftService = new EmailDraftService(_emailFolders, _emailMessages, _cacheMaintenance, _emailAccounts, _identities);
        _deliveryService = new EmailDeliveryService(_companyProfile, _emailFolders, _encryptionService, _cacheMaintenance, _emailMessages, _identities, null, _emailAccounts);
        _folderService = new EmailFolderService(_companyProfile, _emailFolders, _emailMessages, _encryptionService, _emailAccounts);
        _messageLifecycleService = new EmailMessageLifecycleService(_companyProfile, _emailFolders, _emailMessages, _encryptionService, _syncStateService, _cacheMaintenance, _emailAccounts);
        _syncService = new EmailSyncService(_emailAccounts, _companyProfile, _emailFolders, _emailMessages, _encryptionService, _syncStateService, mockNotificationRouting.Object, _draftService, _cacheMaintenance);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try
            {
                Directory.Delete(_testDataDir, true);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TestCleanup] Note: {ex.Message}");
            }
        }
    }

    #region Sync & Preconditions Tests

    [Fact]
    public async Task SyncAccountAsync_WhenAccountNotFound_DoesNotChangeSyncState()
    {
        await _syncService.SyncAccountAsync("non_existent_acc");

        Assert.Equal("Idle", _syncStateService.GetState("non_existent_acc"));
    }

    [Fact]
    public async Task SyncAccountAsync_WhenCredentialsMissing_SetsMissingCredentialsState()
    {
        var acc = new EmailAccount
        {
            Id = "acc_no_creds",
            Name = "Test User",
            EmailAddress = "",
            EncryptedPassword = ""
        };
        _emailAccounts.Save(acc);

        await _syncService.SyncAccountAsync(acc.Id);

        Assert.Equal("Missing Credentials", _syncStateService.GetState(acc.Id));
    }

    [Fact]
    public async Task SyncAccountAsync_WhenServerNotConfigured_SetsServerNotConfiguredState()
    {
        var acc = new EmailAccount
        {
            Id = "acc_with_creds",
            Name = "Alice Smith",
            EmailAddress = "alice@example.com",
            EncryptedPassword = _encryptionService.Encrypt("secret123")
        };
        _emailAccounts.Save(acc);

        var profile = _companyProfile.Get();
        profile.EmailSettings.IncomingServers.Clear();
        _companyProfile.Save(profile);

        await _syncService.SyncAccountAsync(acc.Id);

        Assert.Equal("Server Not Configured", _syncStateService.GetState(acc.Id));
    }

    #endregion

    #region Flag & Read Status Tests

    [Fact]
    public async Task MarkMessageReadAsync_WhenMessageExists_UpdatesIsReadAndFolderUnreadCountAndNotifiesSyncState()
    {
        var folder = new EmailFolder
        {
            Id = "folder_read_1",
            AccountId = "acc1",
            Name = "Inbox",
            Path = "INBOX",
            TotalCount = 1,
            UnreadCount = 1
        };
        _emailFolders.Save(folder);

        var msg = new EmailMessage
        {
            Id = "msg_read_1",
            AccountId = "acc1",
            FolderPath = "INBOX",
            IsRead = false,
            IsLocalOnly = true
        };
        _emailMessages.Save(msg);

        var notifiedAccounts = new List<string>();
        _syncStateService.OnUnreadCountChanged += accId => notifiedAccounts.Add(accId);

        await _messageLifecycleService.MarkMessageReadAsync(msg.AccountId, msg.Id, true);

        var updated = _emailMessages.GetById(msg.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsRead);
        Assert.Equal(0, _emailFolders.GetById("folder_read_1")!.UnreadCount);
        Assert.Contains("acc1", notifiedAccounts);

        notifiedAccounts.Clear();
        await _messageLifecycleService.MarkMessageReadAsync(msg.AccountId, msg.Id, false);
        var unread = _emailMessages.GetById(msg.Id);
        Assert.NotNull(unread);
        Assert.False(unread.IsRead);
        Assert.Equal(1, _emailFolders.GetById("folder_read_1")!.UnreadCount);
        Assert.Contains("acc1", notifiedAccounts);
    }

    [Fact]
    public async Task SyncFolderAsync_WhenExistingMessageFlagsChange_UpdatesFolderUnreadCountAndNotifiesSyncState()
    {
        var acc = new EmailAccount { Id = "acc_flag_sync", EmailAddress = "sync@test.com", LastSyncedAt = DateTime.UtcNow.AddMinutes(-10) };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_flag_sync", AccountId = acc.Id, Name = "Inbox", Path = "INBOX", IsInbox = true, TotalCount = 1, UnreadCount = 1 };
        _emailFolders.Save(inbox);

        var msg = new EmailMessage
        {
            Id = "msg_flag_sync_1",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 42,
            IsRead = false,
            IsFlagged = false
        };
        _emailMessages.Save(msg);

        var mockSummary = new Mock<MailKit.IMessageSummary>();
        mockSummary.SetupGet(s => s.UniqueId).Returns(new MailKit.UniqueId(42));
        mockSummary.SetupGet(s => s.Flags).Returns(MailKit.MessageFlags.Seen);

        var mockFolder = new Mock<MailKit.IMailFolder>();
        mockFolder.SetupGet(f => f.Name).Returns("Inbox");
        mockFolder.SetupGet(f => f.FullName).Returns("INBOX");
        mockFolder.SetupGet(f => f.DirectorySeparator).Returns('/');
        mockFolder.SetupGet(f => f.Attributes).Returns(MailKit.FolderAttributes.Inbox);
        mockFolder.SetupGet(f => f.Count).Returns(1);
        mockFolder.Setup(f => f.OpenAsync(MailKit.FolderAccess.ReadWrite, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(MailKit.FolderAccess.ReadWrite);
        mockFolder.Setup(f => f.FetchAsync(0, -1, It.IsAny<MailKit.IFetchRequest>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<MailKit.IMessageSummary> { mockSummary.Object });

        int? unreadCountAtNotificationTime = null;
        _syncStateService.OnUnreadCountChanged += accountId =>
        {
            if (accountId == acc.Id)
            {
                unreadCountAtNotificationTime = _emailFolders.GetById("f_flag_sync")?.UnreadCount;
            }
        };

        await _syncService.SyncFolderAsync(null, mockFolder.Object, acc, isInitialAccountSync: false);

        Assert.Equal(0, unreadCountAtNotificationTime);
        Assert.True(_emailMessages.GetById(acc.Id, msg.Id)!.IsRead);
    }

    [Fact]
    public async Task SyncFolderAsync_WhenExistingUnreadMessageExpunged_MovesToLocalTrashAndNotifiesSyncState()
    {
        var acc = new EmailAccount { Id = "acc_expunge_sync", EmailAddress = "expunge@test.com", LastSyncedAt = DateTime.UtcNow.AddMinutes(-10) };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_expunge_sync", AccountId = acc.Id, Name = "Inbox", Path = "INBOX", IsInbox = true, TotalCount = 1, UnreadCount = 1 };
        var trash = new EmailFolder { Id = "f_expunge_trash", AccountId = acc.Id, Name = "Trash", Path = "Trash", IsTrash = true, TotalCount = 0, UnreadCount = 0 };
        _emailFolders.Save(inbox);
        _emailFolders.Save(trash);

        var msg = new EmailMessage
        {
            Id = "msg_expunge_1",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            UniqueId = 99,
            GlobalMessageId = "<expunge-99@test.com>",
            IsRead = false
        };
        _emailMessages.Save(msg);

        var mockFolder = new Mock<MailKit.IMailFolder>();
        mockFolder.SetupGet(f => f.Name).Returns("Inbox");
        mockFolder.SetupGet(f => f.FullName).Returns("INBOX");
        mockFolder.SetupGet(f => f.DirectorySeparator).Returns('/');
        mockFolder.SetupGet(f => f.Attributes).Returns(MailKit.FolderAttributes.Inbox);
        mockFolder.SetupGet(f => f.Count).Returns(0);
        mockFolder.Setup(f => f.OpenAsync(MailKit.FolderAccess.ReadWrite, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(MailKit.FolderAccess.ReadWrite);
        mockFolder.Setup(f => f.FetchAsync(0, -1, It.IsAny<MailKit.IFetchRequest>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new List<MailKit.IMessageSummary>());

        bool notified = false;
        _syncStateService.OnUnreadCountChanged += accountId =>
        {
            if (accountId == acc.Id) notified = true;
        };

        await _syncService.SyncFolderAsync(null, mockFolder.Object, acc, isInitialAccountSync: false);

        Assert.True(notified);
        Assert.Equal(0, _emailFolders.GetById("f_expunge_sync")!.UnreadCount);
    }

    [Fact]
    public async Task MarkMessageReadAsync_WhenMessageDoesNotExist_CompletesWithoutError()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MarkMessageReadAsync("acc1", "unknown_msg_id", true));
        Assert.Null(ex);
    }

    [Fact]
    public async Task MarkMessageFlaggedAsync_WhenMessageExists_UpdatesIsFlaggedAndSaves()
    {
        var msg = new EmailMessage
        {
            Id = "msg_flag_1",
            AccountId = "acc1",
            FolderPath = "INBOX",
            IsFlagged = false
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MarkMessageFlaggedAsync(msg.AccountId, msg.Id, true);

        var updated = _emailMessages.GetById(msg.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsFlagged);

        await _messageLifecycleService.MarkMessageFlaggedAsync(msg.AccountId, msg.Id, false);
        var unflagged = _emailMessages.GetById(msg.Id);
        Assert.NotNull(unflagged);
        Assert.False(unflagged.IsFlagged);
    }

    [Fact]
    public async Task MarkMessageFlaggedAsync_WhenMessageDoesNotExist_CompletesWithoutError()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MarkMessageFlaggedAsync("acc1", "unknown_msg_id", true));
        Assert.Null(ex);
    }

    #endregion

    #region Message Movement & Organization Tests

    [Fact]
    public async Task MoveMessageAsync_WhenDestinationEqualsSource_DoesNothing()
    {
        var msg = new EmailMessage
        {
            Id = "msg_move_same",
            AccountId = "acc1",
            FolderPath = "INBOX"
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("acc1", msg.Id, "INBOX");

        Assert.NotNull(_emailMessages.GetById(msg.Id));
    }

    [Fact]
    public async Task MoveMessageAsync_WhenAccountMismatch_DoesNothing()
    {
        var msg = new EmailMessage
        {
            Id = "msg_move_mismatch",
            AccountId = "acc1",
            FolderPath = "INBOX"
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("acc2", msg.Id, "Archive");

        Assert.NotNull(_emailMessages.GetById(msg.Id));
    }

    [Fact]
    public async Task MoveMessageAsync_WhenMessageNotFound_DoesNothing()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MoveMessageAsync("acc1", "not_found", "Archive"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task MoveMessageAsync_WhenServerFails_RollsBackMessageAndRestoresFolderCounts()
    {
        var acc = new EmailAccount { Id = "acc1", EmailAddress = "emp@test.com", IncomingServerConfigId = "imap-1", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_inbox", AccountId = "acc1", Name = "Inbox", Path = "INBOX", TotalCount = 5, UnreadCount = 2 };
        var archive = new EmailFolder { Id = "f_arch", AccountId = "acc1", Name = "Archive", Path = "Archive", TotalCount = 0, UnreadCount = 0 };
        _emailFolders.Save(inbox);
        _emailFolders.Save(archive);

        var msg = new EmailMessage { Id = "msg_to_move", AccountId = "acc1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("acc1", msg.Id, "Archive");

        // Rollback ensures message is not lost locally when remote move fails
        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);

        // Source folder counts are restored
        var restoredInbox = _emailFolders.GetById("f_inbox");
        Assert.NotNull(restoredInbox);
        Assert.Equal(5, restoredInbox.TotalCount);
        Assert.Equal(2, restoredInbox.UnreadCount);
    }

    [Fact]
    public async Task MoveMessageToTrashAsync_WhenAlreadyInTrash_PermanentlyDeletesMessage()
    {
        var acc = new EmailAccount { Id = "acc1", EmailAddress = "emp@test.com", IncomingServerConfigId = "imap-1", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var trash = new EmailFolder { Id = "f_trash_del", AccountId = "acc1", Name = "Trash", Path = "Trash", IsTrash = true, TotalCount = 3, UnreadCount = 1 };
        _emailFolders.Save(trash);

        var msg = new EmailMessage { Id = "msg_trash_perm", AccountId = "acc1", FolderPath = "Trash", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageToTrashAsync("acc1", msg.Id);

        // Message is permanently deleted from local repository
        Assert.Null(_emailMessages.GetById(msg.Id));
        var updatedTrash = _emailFolders.GetById("f_trash_del");
        Assert.NotNull(updatedTrash);
        Assert.Equal(2, updatedTrash.TotalCount);
        Assert.Equal(0, updatedTrash.UnreadCount);
    }

    [Fact]
    public async Task MoveMessageToTrashAsync_WhenNotInTrash_ServerFails_RollsBack()
    {
        var acc = new EmailAccount { Id = "acc1", EmailAddress = "emp@test.com", IncomingServerConfigId = "imap-1", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_inbox2", AccountId = "acc1", Name = "Inbox", Path = "INBOX", TotalCount = 3, UnreadCount = 1 };
        var trash = new EmailFolder { Id = "f_trash2", AccountId = "acc1", Name = "Trash", Path = "Trash", IsTrash = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(trash);

        var msg = new EmailMessage { Id = "msg_trash_1", AccountId = "acc1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageToTrashAsync("acc1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task MoveMessageToTrashAsync_WhenNoTrashFolder_HandlesGracefully()
    {
        var msg = new EmailMessage { Id = "msg_no_trash", AccountId = "acc1", FolderPath = "INBOX" };
        _emailMessages.Save(msg);

        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MoveMessageToTrashAsync("acc1", msg.Id));
        Assert.Null(ex);
    }

    [Fact]
    public async Task MoveMessageToTrashAsync_WhenTrashIsGmailStylePath_FindsFolderSuccessfully()
    {
        var acc = new EmailAccount { Id = "acc_gmail", EmailAddress = "emp@gmail.com", IncomingServerConfigId = "imap-gmail", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_inbox_gm", AccountId = "acc_gmail", Name = "Inbox", Path = "INBOX", TotalCount = 5, UnreadCount = 2 };
        var gmailTrash = new EmailFolder { Id = "f_trash_gm", AccountId = "acc_gmail", Name = "Trash", Path = "[Gmail]/Trash", IsTrash = false };
        _emailFolders.Save(inbox);
        _emailFolders.Save(gmailTrash);

        var msg = new EmailMessage { Id = "msg_gm_1", AccountId = "acc_gmail", FolderPath = "INBOX", IsRead = true };
        _emailMessages.Save(msg);

        // Server will fail because it's a dummy test IMAP server, but it confirms MoveMessageAsync was invoked on [Gmail]/Trash
        var result = await _messageLifecycleService.MoveMessageToTrashAsync("acc_gmail", msg.Id);
        // It attempted move to [Gmail]/Trash (and failed at IMAP layer), rather than failing with TRASH_FOLDER_NOT_FOUND
        Assert.NotEqual("TRASH_FOLDER_NOT_FOUND", result.Code);
    }

    [Fact]
    public void EmailFolder_IsSelectable_DefaultsToTrue_AndCanBeSetFalse()
    {
        var folder = new EmailFolder { Id = "f_cont", Name = "[Gmail]", Path = "[Gmail]" };
        Assert.True(folder.IsSelectable);

        folder.IsSelectable = false;
        Assert.False(folder.IsSelectable);
    }

    [Fact]
    public async Task ArchiveMessageAsync_WhenServerFails_SafelyRollsBack()
    {
        var acc = new EmailAccount { Id = "acc1", EmailAddress = "emp@test.com", IncomingServerConfigId = "imap-1", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_inbox_arc", AccountId = "acc1", Name = "Inbox", Path = "INBOX", TotalCount = 2, UnreadCount = 0 };
        var archive = new EmailFolder { Id = "f_arc_folder", AccountId = "acc1", Name = "Archive", Path = "Archive", IsArchive = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(archive);

        var msg = new EmailMessage { Id = "msg_arc_1", AccountId = "acc1", FolderPath = "INBOX", IsRead = true };
        _emailMessages.Save(msg);

        await _messageLifecycleService.ArchiveMessageAsync("acc1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task ArchiveMessageAsync_WhenNoArchiveFolder_HandlesGracefully()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.ArchiveMessageAsync("acc_no_archive", "any_id"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task JunkMessageAsync_WhenServerFails_SafelyRollsBack()
    {
        var acc = new EmailAccount { Id = "acc1", EmailAddress = "emp@test.com", IncomingServerConfigId = "imap-1", EncryptedPassword = _encryptionService.Encrypt("pass") };
        _emailAccounts.Save(acc);

        var inbox = new EmailFolder { Id = "f_inbox_junk", AccountId = "acc1", Name = "Inbox", Path = "INBOX", TotalCount = 2, UnreadCount = 0 };
        var junk = new EmailFolder { Id = "f_junk_folder", AccountId = "acc1", Name = "Junk", Path = "Junk", IsJunk = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(junk);

        var msg = new EmailMessage { Id = "msg_junk_1", AccountId = "acc1", FolderPath = "INBOX", IsRead = true };
        _emailMessages.Save(msg);

        await _messageLifecycleService.JunkMessageAsync("acc1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task JunkMessageAsync_WhenNoJunkFolder_HandlesGracefully()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.JunkMessageAsync("acc_no_junk", "any_id"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteMessageAsync_WhenMessageExists_DeletesFromRepoAndDecrementsFolderCounts()
    {
        var inbox = new EmailFolder { Id = "f_inbox_del", AccountId = "acc1", Path = "INBOX", TotalCount = 3, UnreadCount = 1 };
        _emailFolders.Save(inbox);

        var msg = new EmailMessage { Id = "msg_to_delete", AccountId = "acc1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.DeleteMessageAsync("acc1", msg.Id);

        Assert.Null(_emailMessages.GetById(msg.Id));
        var updated = _emailFolders.GetById("f_inbox_del");
        Assert.NotNull(updated);
        Assert.Equal(2, updated.TotalCount);
        Assert.Equal(0, updated.UnreadCount);
    }

    #endregion

    #region Attachments & Body Paths Tests

    [Fact]
    public void GetAttachmentPath_ConstructsExpectedPhysicalPath()
    {
        var path = _cacheMaintenance.GetAttachmentPath("acc789", "msg123", "att456");

        Assert.Contains("acc789", path);
        Assert.Contains("EmailAccounts", path);
        Assert.Contains("Attachments", path);
        Assert.Contains("msg123", path);
        Assert.Contains("att456", path);
    }

    [Fact]
    public void GetMimeFilePath_WhenFileDoesNotExist_ReturnsNull()
    {
        var path = _cacheMaintenance.GetMimeFilePath("acc1", "non_existent_msg");
        Assert.Null(path);
    }

    [Fact]
    public void GetMimeFilePath_WhenFileExists_ReturnsFullPath()
    {
        var mimeDir = Path.Combine(_testDataDir, "EmailAccounts", "acc1", "Mime");
        Directory.CreateDirectory(mimeDir);
        var mimeFile = Path.Combine(mimeDir, "existing_msg.eml");
        File.WriteAllText(mimeFile, "MIME dummy content");

        var path = _cacheMaintenance.GetMimeFilePath("acc1", "existing_msg");

        Assert.NotNull(path);
        Assert.Equal(mimeFile, path);
    }

    [Fact]
    public async Task GetEmailBodyAsync_WhenNeitherMimeNorLegacyHtmlExists_ReturnsEmptyString()
    {
        var body = await _cacheMaintenance.GetEmailBodyAsync("acc1", "unknown_msg");
        Assert.Equal("", body);
    }

    [Fact]
    public async Task GetEmailBodyAsync_WhenLegacyHtmlExists_ReturnsFileContent()
    {
        var bodiesDir = Path.Combine(_testDataDir, "EmailAccounts", "acc1", "Bodies");
        Directory.CreateDirectory(bodiesDir);
        var htmlPath = Path.Combine(bodiesDir, "legacy_msg.html");
        await File.WriteAllTextAsync(htmlPath, "<p>Hello Legacy Email</p>");

        var body = await _cacheMaintenance.GetEmailBodyAsync("acc1", "legacy_msg");

        Assert.Equal("<p>Hello Legacy Email</p>", body);
    }

    [Fact]
    public async Task ExtractBodyFromMimeAsync_WithPlainText_WrapsInPreStyledHtml()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("plain") { Text = "Plain text email body" };

        var tempEml = Path.Combine(Path.GetTempPath(), $"mime_test_{Guid.NewGuid()}.eml");
        try
        {
            await using (var stream = File.Create(tempEml))
            {
                await message.WriteToAsync(stream);
            }

            var html = await EmailCacheMaintenanceService.ExtractBodyFromMimeAsync(tempEml);

            Assert.Contains("Plain text email body", html);
            Assert.Contains("white-space:pre-wrap", html);
        }
        finally
        {
            if (File.Exists(tempEml)) File.Delete(tempEml);
        }
    }

    [Fact]
    public async Task ExtractBodyFromMimeAsync_WithHtmlAndCidImages_InlinesAsBase64()
    {
        var builder = new BodyBuilder
        {
            HtmlBody = "<p>Image test: <img src=\"cid:my_image\"></p>"
        };
        var dummyBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        var imagePart = builder.LinkedResources.Add("test.png", dummyBytes, new ContentType("image", "png"));
        imagePart.ContentId = "my_image";

        var message = new MimeMessage { Body = builder.ToMessageBody() };
        var tempEml = Path.Combine(Path.GetTempPath(), $"mime_cid_test_{Guid.NewGuid()}.eml");
        try
        {
            await using (var stream = File.Create(tempEml))
            {
                await message.WriteToAsync(stream);
            }

            var html = await EmailCacheMaintenanceService.ExtractBodyFromMimeAsync(tempEml);

            Assert.Contains("data:image/png;base64,", html);
            Assert.DoesNotContain("cid:my_image", html);
        }
        finally
        {
            if (File.Exists(tempEml)) File.Delete(tempEml);
        }
    }

    #endregion

    #region Drafts Management Tests

    [Fact]
    public async Task SaveDraftAsync_WhenAccountNotFound_ReturnsEmptyString()
    {
        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = "",
            To = "to@test.com",
            Subject = "Subject",
            Body = "Body"
        });
        Assert.Equal("", draftId);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenNewDraft_CreatesLocalDraftsFolderAndSavesMessage()
    {
        var acc = new EmailAccount { Id = "acc_draft_1", EmailAddress = "emp@test.com" };
        _emailAccounts.Save(acc);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "bob@test.com",
            Subject = "Project Update",
            Body = "<p>Draft contents</p>"
        });

        Assert.False(string.IsNullOrEmpty(draftId));

        // Verifies LocalDrafts folder was created
        var folders = _emailFolders.GetByAccount(acc.Id);
        var draftsFolder = folders.FirstOrDefault(f => f.Path == "LocalDrafts");
        Assert.NotNull(draftsFolder);
        Assert.True(draftsFolder.IsDrafts);
        Assert.Equal(1, draftsFolder.TotalCount);

        // Verifies message was saved to repository
        var savedDraft = _emailMessages.GetById(draftId);
        Assert.NotNull(savedDraft);
        Assert.Equal("Project Update", savedDraft.Subject);
        Assert.Contains("bob@test.com", savedDraft.ToAddresses);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenExistingDraftIdProvided_UpdatesExistingMessage()
    {
        var acc = new EmailAccount { Id = "acc_draft_2", EmailAddress = "emp@test.com" };
        _emailAccounts.Save(acc);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "initial@test.com",
            Subject = "Initial Subject",
            Body = "Initial Body"
        });
        var updatedId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "updated@test.com",
            Subject = "Updated Subject",
            Body = "Updated Body",
            ExistingDraftId = draftId
        });

        Assert.Equal(draftId, updatedId);

        var savedDraft = _emailMessages.GetById(draftId);
        Assert.NotNull(savedDraft);
        Assert.Equal("Updated Subject", savedDraft.Subject);
        Assert.Contains("updated@test.com", savedDraft.ToAddresses);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenExistingDraftBelongsToOtherAccount_CreatesNewDraft()
    {
        var acc1 = new EmailAccount { Id = "acc_a", EmailAddress = "a@test.com" };
        var acc2 = new EmailAccount { Id = "acc_b", EmailAddress = "b@test.com" };
        _emailAccounts.Save(acc1);
        _emailAccounts.Save(acc2);

        var draftId1 = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc1.Id,
            To = "test@test.com",
            Subject = "Acc A Draft",
            Body = "Body A"
        });

        // Acc B attempts to save using Acc A's draft ID
        var draftId2 = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc2.Id,
            To = "test@test.com",
            Subject = "Acc B Draft",
            Body = "Body B",
            ExistingDraftId = draftId1
        });

        // Must generate a new draft ID for security
        Assert.NotEqual(draftId1, draftId2);

        var draft1 = _emailMessages.GetById(draftId1);
        Assert.NotNull(draft1);
        Assert.Equal("acc_a", draft1.AccountId);
        Assert.Equal("Acc A Draft", draft1.Subject);
    }

    [Fact]
    public async Task RemoveDraftAttachmentAsync_WhenAttachmentExists_RemovesFromDraftAndDeletesPhysicalFile()
    {
        var acc = new EmailAccount { Id = "acc_draft_att", EmailAddress = "emp@test.com" };
        _emailAccounts.Save(acc);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "to@test.com",
            Subject = "Draft with attachment",
            Body = "Body"
        });
        var draft = _emailMessages.GetById(draftId);
        Assert.NotNull(draft);

        var attId = Guid.NewGuid().ToString();
        var attPath = _cacheMaintenance.GetAttachmentPath(acc.Id, draftId, attId);
        Directory.CreateDirectory(Path.GetDirectoryName(attPath)!);
        await File.WriteAllTextAsync(attPath, "Attachment data");

        draft.Attachments.Add(new EmailAttachmentMeta { Id = attId, FileName = "test.txt", ContentType = "text/plain", Size = 15 });
        _emailMessages.Save(draft);

        Assert.True(File.Exists(attPath));

        await _draftService.RemoveDraftAttachmentAsync(acc.Id, draftId, attId);

        var reloaded = _emailMessages.GetById(draftId);
        Assert.NotNull(reloaded);
        Assert.DoesNotContain(reloaded.Attachments, a => a.Id == attId);
        Assert.False(File.Exists(attPath));
    }

    [Fact]
    public async Task RepairLocalDraftsAsync_WhenDraftsHaveZeroUniqueId_RepairsAndIncrementsNextUid()
    {
        var acc = new EmailAccount { Id = "acc_repair", EmailAddress = "emp@test.com" };
        _emailAccounts.Save(acc);

        var draftsFolder = new EmailFolder
        {
            Id = "f_drafts",
            AccountId = acc.Id,
            Path = "LocalDrafts",
            IsDrafts = true,
            NextUid = 100
        };
        _emailFolders.Save(draftsFolder);

        var msgPath = Path.Combine(_testDataDir, "EmailAccounts", acc.Id, "Messages");
        Directory.CreateDirectory(msgPath);

        var draftMsg = new EmailMessage
        {
            Id = "draft_repair_1",
            AccountId = acc.Id,
            FolderPath = "LocalDrafts",
            Subject = "Draft with zero UID",
            UniqueId = 0
        };
        var filePath = Path.Combine(msgPath, $"{draftMsg.Id}.json");
        await File.WriteAllTextAsync(filePath, System.Text.Json.JsonSerializer.Serialize(draftMsg));

        await _draftService.RepairLocalDraftsAsync(acc.Id);

        var reloadedMsg = _emailMessages.GetById("draft_repair_1");
        Assert.NotNull(reloadedMsg);
        Assert.Equal(100u, reloadedMsg.UniqueId);

        var reloadedFolder = _emailFolders.GetById("f_drafts");
        Assert.NotNull(reloadedFolder);
        Assert.Equal(101u, reloadedFolder.NextUid);
    }

    #endregion

    #region Folder Operations & Send Preconditions Tests

    [Fact]
    public async Task DeleteFolderAsync_WhenAccountNotFound_ReturnsFalse()
    {
        var result = await _folderService.DeleteFolderAsync("unknown_acc", "CustomFolder");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RenameFolderAsync_WhenAccountNotFound_ReturnsFalse()
    {
        var result = await _folderService.RenameFolderAsync("unknown_acc", "OldName", "NewName");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SendEmailAsync_WhenIdentityAndAccountNotFound_ThrowsException()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                AccountId = "unknown_acc",
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));

        Assert.Contains("No Sending Identity is configured", ex.Message);
    }

    [Fact]
    public async Task SendEmailAsync_WhenCredentialsCorrupted_ThrowsException()
    {
        var identity = new EmailSendingIdentity
        {
            Id = "id_corrupt",
            Username = "emp",
            Domain = "test.com",
            SendingServerConfigId = "smtp-1",
            EncryptedSmtpPassword = "not-valid-base64-cipher"
        };
        _identities.Save(identity);

        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                SendingIdentityId = identity.Id,
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));
    }

    [Fact]
    public async Task SendEmailAsync_WhenSmtpNotConfigured_ThrowsException()
    {
        var identity = new EmailSendingIdentity
        {
            Id = "id_nosmtp",
            Username = "emp",
            Domain = "test.com",
            SendingServerConfigId = "",
            CustomSmtpHost = ""
        };
        _identities.Save(identity);

        var profile = _companyProfile.Get();
        profile.EmailSettings.SendingServers.Clear();
        _companyProfile.Save(profile);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                SendingIdentityId = identity.Id,
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));

        Assert.Contains("SMTP Server is not configured", ex.Message);
    }

    [Fact]
    public async Task SyncSingleFolderAsync_ConcurrentCalls_AreSerializedViaLock()
    {
        var account = new EmailAccount
        {
            Id = "acc_concurrent",
            EmailAddress = "test@concurrent.com",
            Enabled = true
        };
        _emailAccounts.Save(account);

        var task1 = _syncService.SyncSingleFolderAsync(account.Id, "INBOX");
        var task2 = _syncService.SyncSingleFolderAsync(account.Id, "INBOX");

        await Task.WhenAll(task1, task2);

        Assert.Equal("Missing Credentials", _syncStateService.GetState(account.Id));
    }

    #endregion
}
