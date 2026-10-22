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
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
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

    private readonly EmployeeRepository _employees;
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

        _employees = new EmployeeRepository(_persistence, _config);
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
            EmailSettings = new EmailServerSettings
            {
                ImapHost = "127.0.0.1",
                ImapPort = 993,
                SmtpHost = "127.0.0.1",
                SmtpPort = 587
            }
        });

        _cacheMaintenance = new EmailCacheMaintenanceService(_employees, _emailFolders, _emailMessages, _syncStateService, _config);
        _draftService = new EmailDraftService(_employees, _emailFolders, _emailMessages, _cacheMaintenance);
        _deliveryService = new EmailDeliveryService(_employees, _companyProfile, _emailFolders, _encryptionService, _cacheMaintenance);
        _folderService = new EmailFolderService(_employees, _companyProfile, _emailFolders, _emailMessages, _encryptionService);
        _messageLifecycleService = new EmailMessageLifecycleService(_employees, _companyProfile, _emailFolders, _emailMessages, _encryptionService, _syncStateService, _cacheMaintenance);
        _syncService = new EmailSyncService(_employees, _companyProfile, _emailFolders, _emailMessages, _encryptionService, _syncStateService, mockNotificationRouting.Object, _draftService, _cacheMaintenance);
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
    public async Task SyncEmployeeAsync_WhenEmployeeNotFound_DoesNotChangeSyncState()
    {
        await _syncService.SyncEmployeeAsync("non_existent_emp");

        Assert.Equal("Idle", _syncStateService.GetState("non_existent_emp"));
    }

    [Fact]
    public async Task SyncEmployeeAsync_WhenCredentialsMissing_SetsMissingCredentialsState()
    {
        var emp = new Employee
        {
            Id = "emp_no_creds",
            FirstName = "Test",
            LastName = "User",
            Email = "",
            EncryptedEmailPassword = ""
        };
        _employees.Save(emp);

        await _syncService.SyncEmployeeAsync(emp.Id);

        Assert.Equal("Missing Credentials", _syncStateService.GetState(emp.Id));
    }

    [Fact]
    public async Task SyncEmployeeAsync_WhenServerNotConfigured_SetsServerNotConfiguredState()
    {
        var emp = new Employee
        {
            Id = "emp_with_creds",
            FirstName = "Alice",
            LastName = "Smith",
            Email = "alice@example.com",
            EncryptedEmailPassword = _encryptionService.Encrypt("secret123")
        };
        _employees.Save(emp);

        var profile = _companyProfile.Get();
        profile.EmailSettings.ImapHost = "";
        _companyProfile.Save(profile);

        await _syncService.SyncEmployeeAsync(emp.Id);

        Assert.Equal("Server Not Configured", _syncStateService.GetState(emp.Id));
    }

    #endregion

    #region Flag & Read Status Tests

    [Fact]
    public async Task MarkMessageReadAsync_WhenMessageExists_UpdatesIsReadAndSaves()
    {
        var msg = new EmailMessage
        {
            Id = "msg_read_1",
            EmployeeId = "emp1",
            FolderPath = "INBOX",
            IsRead = false
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MarkMessageReadAsync(msg.EmployeeId, msg.Id, true);

        var updated = _emailMessages.GetById(msg.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsRead);

        await _messageLifecycleService.MarkMessageReadAsync(msg.EmployeeId, msg.Id, false);
        var unread = _emailMessages.GetById(msg.Id);
        Assert.NotNull(unread);
        Assert.False(unread.IsRead);
    }

    [Fact]
    public async Task MarkMessageReadAsync_WhenMessageDoesNotExist_CompletesWithoutError()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MarkMessageReadAsync("emp1", "unknown_msg_id", true));
        Assert.Null(ex);
    }

    [Fact]
    public async Task MarkMessageFlaggedAsync_WhenMessageExists_UpdatesIsFlaggedAndSaves()
    {
        var msg = new EmailMessage
        {
            Id = "msg_flag_1",
            EmployeeId = "emp1",
            FolderPath = "INBOX",
            IsFlagged = false
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MarkMessageFlaggedAsync(msg.EmployeeId, msg.Id, true);

        var updated = _emailMessages.GetById(msg.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsFlagged);

        await _messageLifecycleService.MarkMessageFlaggedAsync(msg.EmployeeId, msg.Id, false);
        var unflagged = _emailMessages.GetById(msg.Id);
        Assert.NotNull(unflagged);
        Assert.False(unflagged.IsFlagged);
    }

    [Fact]
    public async Task MarkMessageFlaggedAsync_WhenMessageDoesNotExist_CompletesWithoutError()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MarkMessageFlaggedAsync("emp1", "unknown_msg_id", true));
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
            EmployeeId = "emp1",
            FolderPath = "INBOX"
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("emp1", msg.Id, "INBOX");

        Assert.NotNull(_emailMessages.GetById(msg.Id));
    }

    [Fact]
    public async Task MoveMessageAsync_WhenEmployeeMismatch_DoesNothing()
    {
        var msg = new EmailMessage
        {
            Id = "msg_move_mismatch",
            EmployeeId = "emp1",
            FolderPath = "INBOX"
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("emp2", msg.Id, "Archive");

        Assert.NotNull(_emailMessages.GetById(msg.Id));
    }

    [Fact]
    public async Task MoveMessageAsync_WhenMessageNotFound_DoesNothing()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MoveMessageAsync("emp1", "not_found", "Archive"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task MoveMessageAsync_WhenServerFails_RollsBackMessageAndRestoresFolderCounts()
    {
        var emp = new Employee { Id = "emp1", Email = "emp@test.com", EncryptedEmailPassword = _encryptionService.Encrypt("pass") };
        _employees.Save(emp);

        var inbox = new EmailFolder { Id = "f_inbox", EmployeeId = "emp1", Name = "Inbox", Path = "INBOX", TotalCount = 5, UnreadCount = 2 };
        var archive = new EmailFolder { Id = "f_arch", EmployeeId = "emp1", Name = "Archive", Path = "Archive", TotalCount = 0, UnreadCount = 0 };
        _emailFolders.Save(inbox);
        _emailFolders.Save(archive);

        var msg = new EmailMessage { Id = "msg_to_move", EmployeeId = "emp1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageAsync("emp1", msg.Id, "Archive");

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
        var emp = new Employee { Id = "emp1", Email = "emp@test.com", EncryptedEmailPassword = _encryptionService.Encrypt("pass") };
        _employees.Save(emp);

        var trash = new EmailFolder { Id = "f_trash_del", EmployeeId = "emp1", Name = "Trash", Path = "Trash", IsTrash = true, TotalCount = 3, UnreadCount = 1 };
        _emailFolders.Save(trash);

        var msg = new EmailMessage { Id = "msg_trash_perm", EmployeeId = "emp1", FolderPath = "Trash", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageToTrashAsync("emp1", msg.Id);

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
        var emp = new Employee { Id = "emp1", Email = "emp@test.com", EncryptedEmailPassword = _encryptionService.Encrypt("pass") };
        _employees.Save(emp);

        var inbox = new EmailFolder { Id = "f_inbox2", EmployeeId = "emp1", Name = "Inbox", Path = "INBOX", TotalCount = 3, UnreadCount = 1 };
        var trash = new EmailFolder { Id = "f_trash2", EmployeeId = "emp1", Name = "Trash", Path = "Trash", IsTrash = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(trash);

        var msg = new EmailMessage { Id = "msg_trash_1", EmployeeId = "emp1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MoveMessageToTrashAsync("emp1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task MoveMessageToTrashAsync_WhenNoTrashFolder_HandlesGracefully()
    {
        var msg = new EmailMessage { Id = "msg_no_trash", EmployeeId = "emp1", FolderPath = "INBOX" };
        _emailMessages.Save(msg);

        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.MoveMessageToTrashAsync("emp1", msg.Id));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ArchiveMessageAsync_WhenServerFails_SafelyRollsBack()
    {
        var emp = new Employee { Id = "emp1", Email = "emp@test.com", EncryptedEmailPassword = _encryptionService.Encrypt("pass") };
        _employees.Save(emp);

        var inbox = new EmailFolder { Id = "f_inbox_arc", EmployeeId = "emp1", Name = "Inbox", Path = "INBOX", TotalCount = 2, UnreadCount = 0 };
        var archive = new EmailFolder { Id = "f_arc_folder", EmployeeId = "emp1", Name = "Archive", Path = "Archive", IsArchive = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(archive);

        var msg = new EmailMessage { Id = "msg_arc_1", EmployeeId = "emp1", FolderPath = "INBOX", IsRead = true };
        _emailMessages.Save(msg);

        await _messageLifecycleService.ArchiveMessageAsync("emp1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task ArchiveMessageAsync_WhenNoArchiveFolder_HandlesGracefully()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.ArchiveMessageAsync("emp_no_archive", "any_id"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task JunkMessageAsync_WhenServerFails_SafelyRollsBack()
    {
        var emp = new Employee { Id = "emp1", Email = "emp@test.com", EncryptedEmailPassword = _encryptionService.Encrypt("pass") };
        _employees.Save(emp);

        var inbox = new EmailFolder { Id = "f_inbox_junk", EmployeeId = "emp1", Name = "Inbox", Path = "INBOX", TotalCount = 2, UnreadCount = 0 };
        var junk = new EmailFolder { Id = "f_junk_folder", EmployeeId = "emp1", Name = "Junk", Path = "Junk", IsJunk = true };
        _emailFolders.Save(inbox);
        _emailFolders.Save(junk);

        var msg = new EmailMessage { Id = "msg_junk_1", EmployeeId = "emp1", FolderPath = "INBOX", IsRead = true };
        _emailMessages.Save(msg);

        await _messageLifecycleService.JunkMessageAsync("emp1", msg.Id);

        var restoredMsg = _emailMessages.GetById(msg.Id);
        Assert.NotNull(restoredMsg);
        Assert.Equal("INBOX", restoredMsg.FolderPath);
    }

    [Fact]
    public async Task JunkMessageAsync_WhenNoJunkFolder_HandlesGracefully()
    {
        var ex = await Record.ExceptionAsync(() => _messageLifecycleService.JunkMessageAsync("emp_no_junk", "any_id"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteMessageAsync_WhenMessageExists_DeletesFromRepoAndDecrementsFolderCounts()
    {
        var inbox = new EmailFolder { Id = "f_inbox_del", EmployeeId = "emp1", Path = "INBOX", TotalCount = 3, UnreadCount = 1 };
        _emailFolders.Save(inbox);

        var msg = new EmailMessage { Id = "msg_to_delete", EmployeeId = "emp1", FolderPath = "INBOX", IsRead = false };
        _emailMessages.Save(msg);

        await _messageLifecycleService.DeleteMessageAsync("emp1", msg.Id);

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
        var path = _cacheMaintenance.GetAttachmentPath("emp789", "msg123", "att456");

        Assert.Contains("emp789", path);
        Assert.Contains("Email", path);
        Assert.Contains("Attachments", path);
        Assert.Contains("msg123", path);
        Assert.Contains("att456", path);
    }

    [Fact]
    public void GetMimeFilePath_WhenFileDoesNotExist_ReturnsNull()
    {
        var path = _cacheMaintenance.GetMimeFilePath("emp1", "non_existent_msg");
        Assert.Null(path);
    }

    [Fact]
    public void GetMimeFilePath_WhenFileExists_ReturnsFullPath()
    {
        var mimeDir = Path.Combine(_testDataDir, "Employees", "emp1", "Email", "Mime");
        Directory.CreateDirectory(mimeDir);
        var mimeFile = Path.Combine(mimeDir, "existing_msg.eml");
        File.WriteAllText(mimeFile, "MIME dummy content");

        var path = _cacheMaintenance.GetMimeFilePath("emp1", "existing_msg");

        Assert.NotNull(path);
        Assert.Equal(mimeFile, path);
    }

    [Fact]
    public async Task GetEmailBodyAsync_WhenNeitherMimeNorLegacyHtmlExists_ReturnsEmptyString()
    {
        var body = await _cacheMaintenance.GetEmailBodyAsync("emp1", "unknown_msg");
        Assert.Equal("", body);
    }

    [Fact]
    public async Task GetEmailBodyAsync_WhenLegacyHtmlExists_ReturnsFileContent()
    {
        var bodiesDir = Path.Combine(_testDataDir, "Employees", "emp1", "Email", "Bodies");
        Directory.CreateDirectory(bodiesDir);
        var htmlPath = Path.Combine(bodiesDir, "legacy_msg.html");
        await File.WriteAllTextAsync(htmlPath, "<p>Hello Legacy Email</p>");

        var body = await _cacheMaintenance.GetEmailBodyAsync("emp1", "legacy_msg");

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

    #region Signature Management Tests

    [Fact]
    public async Task GetSignatureAsync_WhenSignatureFileExists_ReturnsFileContent()
    {
        var sigDir = Path.Combine(_testDataDir, "Employees", "emp1", "Email");
        Directory.CreateDirectory(sigDir);
        await File.WriteAllTextAsync(Path.Combine(sigDir, "Signature.html"), "<b>Best regards, Alice</b>");

        var sig = await _cacheMaintenance.GetSignatureAsync("emp1");

        Assert.Equal("<b>Best regards, Alice</b>", sig);
    }

    [Fact]
    public async Task GetSignatureAsync_WhenLegacyEmployeeSignatureExists_ReturnsLegacySignature()
    {
        var emp = new Employee
        {
            Id = "emp_legacy_sig",
            SignatureHtml = "<i>Legacy Signature</i>"
        };
        _employees.Save(emp);

        var sig = await _cacheMaintenance.GetSignatureAsync(emp.Id);

        Assert.Equal("<i>Legacy Signature</i>", sig);
    }

    [Fact]
    public async Task GetSignatureAsync_WhenNoSignatureExists_ReturnsEmptyString()
    {
        var sig = await _cacheMaintenance.GetSignatureAsync("emp_no_sig");
        Assert.Equal("", sig);
    }

    [Fact]
    public async Task SaveSignatureAsync_SavesHtmlToDiskAndClearsLegacyEmployeeSignature()
    {
        var emp = new Employee
        {
            Id = "emp_save_sig",
            SignatureHtml = "Old Legacy"
        };
        _employees.Save(emp);

        await _cacheMaintenance.SaveSignatureAsync(emp.Id, "<p>New Modern Signature</p>");

        // Legacy property should be cleared on the employee
        var reloadedEmp = _employees.GetById(emp.Id);
        Assert.NotNull(reloadedEmp);
        Assert.Equal(string.Empty, reloadedEmp.SignatureHtml);

        // File should be written and returned by GetSignatureAsync
        var savedSig = await _cacheMaintenance.GetSignatureAsync(emp.Id);
        Assert.Equal("<p>New Modern Signature</p>", savedSig);
    }

    #endregion

    #region Drafts Management Tests

    [Fact]
    public async Task SaveDraftAsync_WhenEmployeeNotFound_ReturnsEmptyString()
    {
        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = "unknown_emp",
            To = "to@test.com",
            Subject = "Subject",
            Body = "Body"
        });
        Assert.Equal("", draftId);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenNewDraft_CreatesLocalDraftsFolderAndSavesMessage()
    {
        var emp = new Employee { Id = "emp_draft_1", Email = "emp@test.com" };
        _employees.Save(emp);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp.Id,
            To = "bob@test.com",
            Subject = "Project Update",
            Body = "<p>Draft contents</p>"
        });

        Assert.False(string.IsNullOrEmpty(draftId));

        // Verifies LocalDrafts folder was created
        var folders = _emailFolders.GetByEmployee(emp.Id);
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
        var emp = new Employee { Id = "emp_draft_2", Email = "emp@test.com" };
        _employees.Save(emp);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp.Id,
            To = "initial@test.com",
            Subject = "Initial Subject",
            Body = "Initial Body"
        });
        var updatedId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp.Id,
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
    public async Task SaveDraftAsync_WhenExistingDraftBelongsToOtherEmployee_CreatesNewDraft()
    {
        var emp1 = new Employee { Id = "emp_a", Email = "a@test.com" };
        var emp2 = new Employee { Id = "emp_b", Email = "b@test.com" };
        _employees.Save(emp1);
        _employees.Save(emp2);

        var draftId1 = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp1.Id,
            To = "test@test.com",
            Subject = "Emp A Draft",
            Body = "Body A"
        });

        // Emp B attempts to save using Emp A's draft ID
        var draftId2 = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp2.Id,
            To = "test@test.com",
            Subject = "Emp B Draft",
            Body = "Body B",
            ExistingDraftId = draftId1
        });

        // Must generate a new draft ID for security
        Assert.NotEqual(draftId1, draftId2);

        var draft1 = _emailMessages.GetById(draftId1);
        Assert.NotNull(draft1);
        Assert.Equal("emp_a", draft1.EmployeeId);
        Assert.Equal("Emp A Draft", draft1.Subject);
    }

    [Fact]
    public async Task RemoveDraftAttachmentAsync_WhenAttachmentExists_RemovesFromDraftAndDeletesPhysicalFile()
    {
        var emp = new Employee { Id = "emp_draft_att", Email = "emp@test.com" };
        _employees.Save(emp);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            EmployeeId = emp.Id,
            To = "to@test.com",
            Subject = "Draft with attachment",
            Body = "Body"
        });
        var draft = _emailMessages.GetById(draftId);
        Assert.NotNull(draft);

        var attId = Guid.NewGuid().ToString();
        var attPath = _cacheMaintenance.GetAttachmentPath(emp.Id, draftId, attId);
        Directory.CreateDirectory(Path.GetDirectoryName(attPath)!);
        await File.WriteAllTextAsync(attPath, "Attachment data");

        draft.Attachments.Add(new EmailAttachmentMeta { Id = attId, FileName = "test.txt", ContentType = "text/plain", Size = 15 });
        _emailMessages.Save(draft);

        Assert.True(File.Exists(attPath));

        await _draftService.RemoveDraftAttachmentAsync(emp.Id, draftId, attId);

        var reloaded = _emailMessages.GetById(draftId);
        Assert.NotNull(reloaded);
        Assert.DoesNotContain(reloaded.Attachments, a => a.Id == attId);
        Assert.False(File.Exists(attPath));
    }

    [Fact]
    public async Task RepairLocalDraftsAsync_WhenDraftsHaveZeroUniqueId_RepairsAndIncrementsNextUid()
    {
        var emp = new Employee { Id = "emp_repair", Email = "emp@test.com" };
        _employees.Save(emp);

        var draftsFolder = new EmailFolder
        {
            Id = "f_drafts",
            EmployeeId = emp.Id,
            Path = "LocalDrafts",
            IsDrafts = true,
            NextUid = 100
        };
        _emailFolders.Save(draftsFolder);

        var msgPath = Path.Combine(_testDataDir, "Employees", emp.Id, "Email", "Messages");
        Directory.CreateDirectory(msgPath);

        var draftMsg = new EmailMessage
        {
            Id = "draft_repair_1",
            EmployeeId = emp.Id,
            FolderPath = "LocalDrafts",
            Subject = "Draft with zero UID",
            UniqueId = 0
        };
        var filePath = Path.Combine(msgPath, $"{draftMsg.Id}.json");
        await File.WriteAllTextAsync(filePath, System.Text.Json.JsonSerializer.Serialize(draftMsg));

        await _draftService.RepairLocalDraftsAsync(emp.Id);

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
    public async Task DeleteFolderAsync_WhenEmployeeNotFound_ReturnsFalse()
    {
        var result = await _folderService.DeleteFolderAsync("unknown_emp", "CustomFolder");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RenameFolderAsync_WhenEmployeeNotFound_ReturnsFalse()
    {
        var result = await _folderService.RenameFolderAsync("unknown_emp", "OldName", "NewName");
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SendEmailAsync_WhenEmployeeNotFound_ThrowsException()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                EmployeeId = "unknown_emp",
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));

        Assert.Equal("Employee not found", ex.Message);
    }

    [Fact]
    public async Task SendEmailAsync_WhenCredentialsMissing_ThrowsException()
    {
        var emp = new Employee { Id = "emp_nocreds", Email = "", EncryptedEmailPassword = "" };
        _employees.Save(emp);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                EmployeeId = emp.Id,
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));

        Assert.Contains("Missing email credentials", ex.Message);
    }

    [Fact]
    public async Task SendEmailAsync_WhenSmtpNotConfigured_ThrowsException()
    {
        var emp = new Employee
        {
            Id = "emp_nosmtp",
            Email = "emp@test.com",
            EncryptedEmailPassword = _encryptionService.Encrypt("pass")
        };
        _employees.Save(emp);

        var profile = _companyProfile.Get();
        profile.EmailSettings.SmtpHost = "";
        _companyProfile.Save(profile);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _deliveryService.SendEmailAsync(new SendEmailRequest
            {
                EmployeeId = emp.Id,
                To = "to@test.com",
                Subject = "Subject",
                Body = "Body"
            }));

        Assert.Contains("SMTP Server not configured", ex.Message);
    }

    #endregion
}
