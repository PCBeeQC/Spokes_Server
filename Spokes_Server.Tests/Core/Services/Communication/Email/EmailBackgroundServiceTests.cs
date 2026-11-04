using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Email;

namespace Spokes_Server.Tests.Core.Services.Communication.Email;

public class TestEmailBackgroundService : EmailBackgroundService
{
    public int PollCount { get; private set; }
    public TaskCompletionSource<bool> FirstIterationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TestEmailBackgroundService(IServiceProvider serviceProvider, ILogger<EmailBackgroundService> logger, EmailIdleService idleService)
        : base(serviceProvider, logger, idleService)
    {
    }

    public List<EmailAccount> PublicGetSyncAccounts(EmailAccountRepository repo)
    {
        return base.GetSyncAccounts(repo);
    }

    protected override Task WaitBeforeNextPollAsync(CancellationToken stoppingToken)
    {
        PollCount++;
        FirstIterationCompleted.TrySetResult(true);
        return Task.Delay(10, stoppingToken);
    }

    protected override Task WaitOnStartupAsync(CancellationToken stoppingToken)
    {
        return Task.CompletedTask;
    }
}

public class EmailBackgroundServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly ServiceCollection _services;
    private readonly ServiceProvider _serviceProvider;

    private readonly EmailAccountRepository _accountsRepo;
    private readonly EmailFolderRepository _foldersRepo;
    private readonly EmailMessageRepository _messagesRepo;
    private readonly CompanyProfileRepository _companyProfileRepo;

    private readonly Mock<IEmailSyncService> _mockSyncService;
    private readonly EmailSyncStateService _syncStateService;
    private readonly EmailIdleService _idleService;
    private readonly Mock<ILogger<EmailBackgroundService>> _mockLogger;

    private readonly TestEmailBackgroundService _service;

    public EmailBackgroundServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_EmailBG_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

        _accountsRepo = new EmailAccountRepository(_persistence, _config);
        _foldersRepo = new EmailFolderRepository(_persistence, _config);
        _messagesRepo = new EmailMessageRepository(_persistence, _config);
        _companyProfileRepo = new CompanyProfileRepository(_persistence, _config);
        _companyProfileRepo.Save(new Spokes_Server.Core.Models.Core.CompanyProfile { Id = "GlobalProfile", Edition = "Business" });

        _mockSyncService = new Mock<IEmailSyncService>();
        _syncStateService = new EmailSyncStateService();

        _services = new ServiceCollection();
        _services.AddSingleton(_accountsRepo);
        _services.AddSingleton(_foldersRepo);
        _services.AddSingleton(_messagesRepo);
        _services.AddSingleton(_companyProfileRepo);
        _services.AddSingleton(_syncStateService);

        // For EmailIdleService dependencies
        _services.AddSingleton(new EncryptionService(_config));

        _services.AddSingleton(_mockSyncService.Object);

        _serviceProvider = _services.BuildServiceProvider();

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(s => s.ServiceProvider).Returns(_serviceProvider);
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);

        _idleService = new EmailIdleService(mockScopeFactory.Object, new Mock<ILogger<EmailIdleService>>().Object);

        _mockLogger = new Mock<ILogger<EmailBackgroundService>>();
        _service = new TestEmailBackgroundService(_serviceProvider, _mockLogger.Object, _idleService);
    }

    public void Dispose()
    {
        _service.Dispose();
        _persistence.Dispose();
        _idleService.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    [Fact]
    public void GetSyncAccounts_ReturnsEnabledAccountsWithEmailAndPassword()
    {
        // Arrange
        _accountsRepo.Save(new EmailAccount { Id = "acc1", EmailAddress = "a@b.com", EncryptedPassword = "pwd", Enabled = true });
        _accountsRepo.Save(new EmailAccount { Id = "acc2", EmailAddress = "", EncryptedPassword = "pwd", Enabled = true }); // No email
        _accountsRepo.Save(new EmailAccount { Id = "acc3", EmailAddress = "c@d.com", EncryptedPassword = "", Enabled = true }); // No pwd
        _accountsRepo.Save(new EmailAccount { Id = "acc4", EmailAddress = "e@f.com", EncryptedPassword = "pwd", Enabled = false }); // Disabled

        // Act
        var result = _service.PublicGetSyncAccounts(_accountsRepo);

        // Assert
        Assert.Single(result);
        Assert.Equal("acc1", result[0].Id);
    }

    [Fact]
    public void GetSyncAccounts_EmptyRepo_ReturnsEmptyList()
    {
        // Arrange & Act
        var result = _service.PublicGetSyncAccounts(_accountsRepo);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_SyncsAccountsAndReconcilesFolders()
    {
        // Arrange
        var accId = "acc_exec";
        _accountsRepo.Save(new EmailAccount { Id = accId, EmailAddress = "test@test.com", EncryptedPassword = "pwd", Name = "Test" });

        var folder = new EmailFolder { Id = "f1", AccountId = accId, Path = "INBOX", IsInbox = true, UnreadCount = 0 };
        _foldersRepo.Save(folder);

        // Add an unread message to the local index so reconcile fixes the count
        _messagesRepo.Save(new EmailMessage { Id = "m1", AccountId = accId, FolderPath = "INBOX", IsRead = false, UniqueId = 1 });

        _mockSyncService.Setup(s => s.SyncAccountAsync(accId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        string? notifiedAccountId = null;
        _syncStateService.OnUnreadCountChanged += id => notifiedAccountId = id;

        using var cts = new CancellationTokenSource();

        // Act
        await _service.StartAsync(cts.Token);
        await _service.FirstIterationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _service.StopAsync(CancellationToken.None);

        // Assert
        _mockSyncService.Verify(s => s.SyncAccountAsync(accId, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        var updatedFolder = _foldersRepo.GetByAccount(accId).FirstOrDefault(f => f.Path == "INBOX");
        Assert.NotNull(updatedFolder);
        Assert.Equal(1, updatedFolder.UnreadCount); // Should be reconciled
        Assert.Equal(accId, notifiedAccountId);
    }

    [Fact]
    public async Task OnIdleMessageReceived_TriggersSyncSingleFolder()
    {
        // Arrange
        var accId = "acc_idle1";
        var folderPath = "INBOX";

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockSyncService.Setup(s => s.SyncSingleFolderAsync(accId, folderPath, It.IsAny<CancellationToken>()))
            .Returns(() => { tcs.TrySetResult(true); return Task.CompletedTask; });

        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);

        // Act - Trigger event directly via internal invoker
        _idleService.TriggerMessageReceived(accId, folderPath, UniqueId.MinValue);

        // Wait deterministically for the sync to be invoked
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _service.StopAsync(CancellationToken.None);

        // Assert
        _mockSyncService.Verify(s => s.SyncSingleFolderAsync(accId, folderPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnIdleFlagsChanged_TriggersSyncSingleFolder()
    {
        // Arrange
        var accId = "acc_idle2";
        var folderPath = "Sent";

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockSyncService.Setup(s => s.SyncSingleFolderAsync(accId, folderPath, It.IsAny<CancellationToken>()))
            .Returns(() => { tcs.TrySetResult(true); return Task.CompletedTask; });

        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);

        // Act - Trigger event directly via internal invoker
        _idleService.TriggerMessageFlagsChanged(accId, folderPath, UniqueId.MinValue);

        // Wait deterministically for the sync to be invoked
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _service.StopAsync(CancellationToken.None);

        // Assert
        _mockSyncService.Verify(s => s.SyncSingleFolderAsync(accId, folderPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnIdle_MultipleRapidEventsForSameFolder_DeduplicatesAndSyncsControlled()
    {
        // Arrange
        var accId = "acc_rapid";
        var folderPath = "INBOX";

        var syncCallCount = 0;
        var syncCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockSyncService.Setup(s => s.SyncSingleFolderAsync(accId, folderPath, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref syncCallCount);
                syncCompleted.TrySetResult(true);
                return Task.CompletedTask;
            });

        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);

        // Act - Trigger 20 rapid events for the exact same folder
        for (int i = 0; i < 20; i++)
        {
            _idleService.TriggerMessageReceived(accId, folderPath, new UniqueId((uint)(i + 1)));
        }

        // Wait deterministically for the worker queue to dispatch
        await syncCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _service.StopAsync(CancellationToken.None);

        // Assert - deduplicated rather than spawning 20 separate syncs
        Assert.InRange(syncCallCount, 1, 3);
    }

    [Fact]
    public async Task StartAndStop_DoesNotThrow()
    {
        var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);
        await _service.StopAsync(CancellationToken.None);
        Assert.True(true);
    }
}
