using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class DecomposedEmailServicesTests : IDisposable
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

    public DecomposedEmailServicesTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_DecomposedEmail_{Guid.NewGuid()}");
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

        var mockNotificationRouting = new Mock<INotificationRoutingService>();

        _cacheMaintenance = new EmailCacheMaintenanceService(_emailFolders, _emailMessages, _syncStateService, _config);
        _draftService = new EmailDraftService(_emailFolders, _emailMessages, _cacheMaintenance, _emailAccounts, _identities);
        _deliveryService = new EmailDeliveryService(_companyProfile, _emailFolders, _encryptionService, _cacheMaintenance, _emailMessages, _identities, null, _emailAccounts);
        _folderService = new EmailFolderService(_companyProfile, _emailFolders, _emailMessages, _encryptionService, _emailAccounts);
        _messageLifecycleService = new EmailMessageLifecycleService(_companyProfile, _emailFolders, _emailMessages, _encryptionService, _syncStateService, _cacheMaintenance, _emailAccounts);
        _syncService = new EmailSyncService(
            _emailAccounts, _companyProfile, _emailFolders, _emailMessages, _encryptionService,
            _syncStateService, mockNotificationRouting.Object, _draftService, _cacheMaintenance);
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
    public async Task EmailDraftService_SaveAndRetrieveDraft()
    {
        var acc = new EmailAccount
        {
            Id = "acc_draft_test",
            EmailAddress = "alice@example.com",
            Name = "Alice Smith"
        };
        _emailAccounts.Save(acc);

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "bob@example.com",
            Subject = "Project Update",
            Body = "<p>Draft body content</p>"
        });

        Assert.False(string.IsNullOrEmpty(draftId));

        var savedDraft = _emailMessages.GetById(draftId);
        Assert.NotNull(savedDraft);
        Assert.Equal("Project Update", savedDraft.Subject);
        Assert.Equal("LocalDrafts", savedDraft.FolderPath);
    }

    [Fact]
    public void EmailDeliveryService_NormalizeQuillHtml_InjectsMargins()
    {
        var input = "<p>First line</p><p>Second line</p>";
        var normalized = EmailDeliveryService.NormalizeQuillHtml(input);

        Assert.Contains("style=\"margin:0;padding:0;\"", normalized);
    }

    [Fact]
    public async Task EmailMessageLifecycleService_GetMessageAsync_ReturnsSavedMessage()
    {
        var msg = new EmailMessage
        {
            Id = "msg_sync_test",
            AccountId = "acc1",
            FolderPath = "INBOX",
            Subject = "Test Message"
        };
        _emailMessages.Save(msg);

        var retrievedPartitioned = await _messageLifecycleService.GetMessageAsync(msg.AccountId, msg.Id);
        Assert.NotNull(retrievedPartitioned);
        Assert.Equal("Test Message", retrievedPartitioned.Subject);
    }

    [Fact]
    public async Task EmailDraftService_SaveDraftAsync_WithEmailAttachments_SavesAttachmentsOnDiskAndDraft()
    {
        var acc = new EmailAccount
        {
            Id = "acc_draft_with_att",
            EmailAddress = "carol@example.com",
            Name = "Carol Danvers"
        };
        _emailAccounts.Save(acc);

        var attachment1 = EmailAttachment.FromBytes("report.pdf", System.Text.Encoding.UTF8.GetBytes("pdf-content"), "application/pdf");
        var attachment2 = EmailAttachment.FromStream("notes.txt", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("notes-content")), "text/plain");

        var draftId = await _draftService.SaveDraftAsync(new SaveDraftRequest
        {
            AccountId = acc.Id,
            To = "dave@example.com",
            Subject = "Draft With Attachments",
            Body = "<p>Please see attachments</p>",
            Attachments = new List<EmailAttachment> { attachment1, attachment2 }
        });

        Assert.False(string.IsNullOrEmpty(draftId));
        var savedDraft = _emailMessages.GetById(draftId);
        Assert.NotNull(savedDraft);
        Assert.True(savedDraft.HasAttachments);
        Assert.Equal(2, savedDraft.Attachments.Count);

        var attMeta1 = savedDraft.Attachments.First(a => a.FileName == "report.pdf");
        var attPath1 = _cacheMaintenance.GetAttachmentPath(acc.Id, draftId, attMeta1.Id);
        Assert.True(File.Exists(attPath1));
        Assert.Equal("pdf-content", await File.ReadAllTextAsync(attPath1));

        var attMeta2 = savedDraft.Attachments.First(a => a.FileName == "notes.txt");
        var attPath2 = _cacheMaintenance.GetAttachmentPath(acc.Id, draftId, attMeta2.Id);
        Assert.True(File.Exists(attPath2));
        Assert.Equal("notes-content", await File.ReadAllTextAsync(attPath2));
    }

    [Fact]
    public async Task EmailAttachment_FromMethods_ProvideCorrectBytes()
    {
        var bytesData = System.Text.Encoding.UTF8.GetBytes("test-bytes");
        var fromBytes = EmailAttachment.FromBytes("bytes.bin", bytesData, "application/octet-stream");
        Assert.Equal(bytesData, await fromBytes.GetBytesAsync());

        using var ms = new MemoryStream(bytesData);
        var fromStream = EmailAttachment.FromStream("stream.bin", ms);
        Assert.Equal(bytesData, await fromStream.GetBytesAsync());

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, bytesData);
            var fromFile = EmailAttachment.FromFile(tempFile, "temp.bin");
            Assert.Equal("temp.bin", fromFile.FileName);
            Assert.Equal(bytesData, await fromFile.GetBytesAsync());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EmailDraftService_WithMockCacheMaintenance_DelegatesAttachmentDeletion()
    {
        var mockCache = new Mock<IEmailCacheMaintenanceService>();
        mockCache.Setup(c => c.GetAttachmentPath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Path.Combine(_testDataDir, "dummy_att.dat"));
        var draftServiceWithMock = new EmailDraftService(_emailFolders, _emailMessages, mockCache.Object, _emailAccounts, _identities);

        var acc = new EmailAccount { Id = "acc_mock_test", EmailAddress = "mock@example.com" };
        _emailAccounts.Save(acc);

        var draftMsg = new EmailMessage
        {
            Id = "draft_with_att",
            AccountId = acc.Id,
            FolderPath = "LocalDrafts",
            Attachments = [new EmailAttachmentMeta { Id = "att1", FileName = "file.txt" }]
        };
        _emailMessages.Save(draftMsg);

        await draftServiceWithMock.RemoveDraftAttachmentAsync(acc.Id, draftMsg.Id, "att1");

        mockCache.Verify(c => c.GetAttachmentPath(acc.Id, draftMsg.Id, "att1"), Times.Once);
    }

    [Fact]
    public async Task EmailMessageLifecycleService_WithMockCache_DeletesMessageFiles()
    {
        var mockCache = new Mock<IEmailCacheMaintenanceService>();

        var lifecycleServiceWithMock = new EmailMessageLifecycleService(
            _companyProfile, _emailFolders, _emailMessages,
            _encryptionService, _syncStateService, mockCache.Object, _emailAccounts);

        var acc = new EmailAccount { Id = "acc_seam_test", EmailAddress = "seam@example.com" };
        _emailAccounts.Save(acc);

        var msg = new EmailMessage
        {
            Id = "msg_to_delete",
            AccountId = acc.Id,
            FolderPath = "INBOX"
        };
        _emailMessages.Save(msg);

        await lifecycleServiceWithMock.DeleteMessageAsync(acc.Id, msg.Id);

        mockCache.Verify(c => c.DeleteLocalMessageFilesAsync(acc.Id, msg.Id), Times.Once);
    }

    [Fact]
    public async Task EmailMessageLifecycleService_MarkMessageReadAndFlagged_UpdatesRecord()
    {
        var acc = new EmailAccount { Id = "acc_lifecycle_1", EmailAddress = "life@example.com" };
        _emailAccounts.Save(acc);

        var msg = new EmailMessage
        {
            Id = "msg_lifecycle_read",
            AccountId = acc.Id,
            FolderPath = "INBOX",
            IsRead = false,
            IsFlagged = false
        };
        _emailMessages.Save(msg);

        await _messageLifecycleService.MarkMessageReadAsync(acc.Id, msg.Id, true);
        var readMsg = _emailMessages.GetById(acc.Id, msg.Id);
        Assert.NotNull(readMsg);
        Assert.True(readMsg.IsRead);

        await _messageLifecycleService.MarkMessageFlaggedAsync(acc.Id, msg.Id, true);
        var flaggedMsg = _emailMessages.GetById(acc.Id, msg.Id);
        Assert.NotNull(flaggedMsg);
        Assert.True(flaggedMsg.IsFlagged);
    }

    [Fact]
    public async Task EmailFolderService_WhenNoServerConfigured_ReturnsNullGracefully()
    {
        var acc = new EmailAccount { Id = "acc_folder_test", EmailAddress = "folder@example.com" };
        _emailAccounts.Save(acc);

        var created = await _folderService.CreateFolderAsync(acc.Id, "", "TestFolder");
        Assert.False(created.IsSuccess);
        Assert.Null(created.Value);

        var deleted = await _folderService.DeleteFolderAsync(acc.Id, "NonExistentFolder");
        Assert.False(deleted.IsSuccess);

        var renamed = await _folderService.RenameFolderAsync(acc.Id, "OldFolder", "NewFolder");
        Assert.False(renamed.IsSuccess);
    }
}
