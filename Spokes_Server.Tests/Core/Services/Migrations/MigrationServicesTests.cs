using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Migrations;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Migrations;

public class MigrationServicesTests : TestDataTestBase
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly Mock<IConfiguration> _configMock;

    public MigrationServicesTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_MigrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDataDir);

        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["DataPath"]).Returns(_testDataDir);
        _configMock.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _sessionRepo = new DeviceSessionRepository(_persistence, _configMock.Object);
    }

    public override void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void PushDeviceSessionMigrationStep_MigratesSubscriptionsAndCleansUpExpired()
    {
        var session = new DeviceSession
        {
            Id = "sess-1",
            EmployeeId = "emp-1",
            DeviceId = "dev-1",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        var pushDir = Path.Combine(_testDataDir, "PushSubscriptions");
        Directory.CreateDirectory(pushDir);
        var pushFile = Path.Combine(pushDir, "sub-1.json");
        File.WriteAllText(pushFile, JsonSerializer.Serialize(new
        {
            Id = "sub-1",
            UserId = "emp-1",
            SessionId = "sess-1",
            Endpoint = "https://push.example.com/endpoint1",
            P256dh = "key1",
            Auth = "auth1",
            IsEnabled = true
        }));

        var step = new PushDeviceSessionMigrationStep(_sessionRepo, _configMock.Object);
        step.MigratePushSubscriptionsToDeviceSessions();
        _persistence.FlushAll();

        // Legacy file should be cleaned up
        Assert.False(File.Exists(pushFile));

        // DeviceSession should have push fields populated
        var updatedSession = _sessionRepo.GetById("sess-1");
        Assert.NotNull(updatedSession);
        Assert.Equal("https://push.example.com/endpoint1", updatedSession.PushEndpoint);
        Assert.True(updatedSession.PushEnabled);
    }

    [Fact]
    public void PushDeviceSessionMigrationStep_WhenFileLocked_LogsWarningAndDoesNotCrash()
    {
        var session = new DeviceSession
        {
            Id = "sess-lock-step",
            EmployeeId = "emp-lock-step",
            DeviceId = "dev-lock-step",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _sessionRepo.Save(session);

        var pushDir = Path.Combine(_testDataDir, "PushSubscriptions");
        Directory.CreateDirectory(pushDir);
        var pushFile = Path.Combine(pushDir, "sub-lock-step.json");
        File.WriteAllText(pushFile, JsonSerializer.Serialize(new
        {
            Id = "sub-lock-step",
            UserId = "emp-lock-step",
            SessionId = "sess-lock-step",
            Endpoint = "https://push.example.com/endpoint-lock-step",
            P256dh = "key-lock-step",
            Auth = "auth-lock-step",
            IsEnabled = true
        }));

        var mockLogger = new Mock<ILogger<PushDeviceSessionMigrationStep>>();
        var step = new PushDeviceSessionMigrationStep(_sessionRepo, _configMock.Object, mockLogger.Object);

        using (var lockStream = new FileStream(pushFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            step.MigratePushSubscriptionsToDeviceSessions();
        }

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to delete migrated legacy push file")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // Clean up test file
        File.Delete(pushFile);
    }

    [Fact]
    public void PushDeviceSessionMigrationStep_WhenCorruptedJson_QuarantinesToCorruptedFolderAndDoesNotCrash()
    {
        var pushDir = Path.Combine(_testDataDir, "PushSubscriptions");
        Directory.CreateDirectory(pushDir);
        var pushFile = Path.Combine(pushDir, "corrupted-sub.json");
        File.WriteAllText(pushFile, "{ corrupted invalid json text");

        var mockLogger = new Mock<ILogger<PushDeviceSessionMigrationStep>>();
        var step = new PushDeviceSessionMigrationStep(_sessionRepo, _configMock.Object, mockLogger.Object);

        step.MigratePushSubscriptionsToDeviceSessions();

        // The original corrupted file in PushSubscriptions must no longer be in the root
        Assert.False(File.Exists(pushFile));

        // The corrupted file must be quarantined in .corrupted folder
        var quarantinedFile = Path.Combine(pushDir, ".corrupted", "corrupted-sub.json");
        Assert.True(File.Exists(quarantinedFile));
        Assert.Equal("{ corrupted invalid json text", File.ReadAllText(quarantinedFile));

        // The push directory itself must NOT be deleted because it holds quarantined files
        Assert.True(Directory.Exists(pushDir));
    }

    [Fact]
    public async Task PushSubscriptionsCaseFixMigrationStep_MigratesLowercasePushSubscriptionsDirectory()
    {
        var session = new DeviceSession { Id = "sess-lowercase", EmployeeId = "emp-lc", ExpiresAt = DateTime.UtcNow.AddDays(30) };
        _sessionRepo.Save(session);
        await _persistence.FlushAllAsync();

        var lowerPushDir = Path.Combine(_testDataDir, "pushsubscriptions");
        Directory.CreateDirectory(lowerPushDir);
        var subJson = JsonSerializer.Serialize(new
        {
            Id = "sub-lc",
            UserId = "emp-lc",
            SessionId = "sess-lowercase",
            Endpoint = "https://fcm.example/lc",
            P256dh = "key-lc",
            Auth = "auth-lc",
            IsEnabled = true
        });
        await File.WriteAllTextAsync(Path.Combine(lowerPushDir, "sub-lc.json"), subJson);

        var step = new PushSubscriptionsCaseFixMigrationStep(_sessionRepo, _configMock.Object);
        await step.ExecuteAsync();
        await _persistence.FlushAllAsync();

        var updated = _sessionRepo.GetById("sess-lowercase");
        Assert.NotNull(updated);
        Assert.Equal("https://fcm.example/lc", updated!.PushEndpoint);
        Assert.False(Directory.Exists(lowerPushDir));
    }

    [Fact]
    public async Task MigrationRunnerService_ExecutesStepsInOrder_AndRecordsInLedger()
    {
        var executionOrder = new List<string>();

        var step1Mock = new Mock<IMigrationStep>();
        step1Mock.SetupGet(s => s.MigrationKey).Returns("20260101_Step1");
        step1Mock.SetupGet(s => s.Description).Returns("First Step");
        step1Mock.SetupGet(s => s.Order).Returns(10);
        step1Mock.Setup(s => s.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { executionOrder.Add("step1"); return Task.CompletedTask; });

        var step2Mock = new Mock<IMigrationStep>();
        step2Mock.SetupGet(s => s.MigrationKey).Returns("20260102_Step2");
        step2Mock.SetupGet(s => s.Description).Returns("Second Step");
        step2Mock.SetupGet(s => s.Order).Returns(20);
        step2Mock.Setup(s => s.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { executionOrder.Add("step2"); return Task.CompletedTask; });

        // Pass steps out of order
        var steps = new List<IMigrationStep> { step2Mock.Object, step1Mock.Object };
        var runner = new MigrationRunnerService(steps, _configMock.Object, _persistence);

        var summary = await runner.RunPendingMigrationsAsync();

        Assert.Equal(2, summary.TotalSteps);
        Assert.Equal(2, summary.ExecutedSteps);
        Assert.Equal(0, summary.SkippedSteps);
        Assert.Equal(new[] { "step1", "step2" }, executionOrder);

        // Verify ledger was written
        var historyFile = Path.Combine(_testDataDir, "MigrationHistory.json");
        Assert.True(File.Exists(historyFile));

        var applied = runner.GetAppliedMigrations();
        Assert.Equal(2, applied.Count);
        Assert.Equal("20260101_Step1", applied[0].Key);
        Assert.Equal("20260102_Step2", applied[1].Key);

        // Re-running skips all already applied migrations
        var reRunSummary = await runner.RunPendingMigrationsAsync();
        Assert.Equal(2, reRunSummary.TotalSteps);
        Assert.Equal(0, reRunSummary.ExecutedSteps);
        Assert.Equal(2, reRunSummary.SkippedSteps);

        step1Mock.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        step2Mock.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SupplierRepository_Save_PreservesContactsWithoutMutatingInfo()
    {
        var supplierRepo = new SupplierRepository(_persistence, _configMock.Object);
        var supplier = new Supplier
        {
            Id = "sup-sync-1",
            ContactInfo = new Spokes_Server.Core.Models.Core.ClientInfo
            {
                BusinessName = "Acme Supplies"
            },
            Contacts = new List<ContactPerson>
            {
                new ContactPerson
                {
                    Name = "Jane Smith",
                    Title = "Lead",
                    Email = "jane@smith.com",
                    IsPrimary = true
                }
            }
        };

        supplierRepo.Save(supplier);

        var saved = supplierRepo.GetById("sup-sync-1");
        Assert.NotNull(saved);
        Assert.Single(saved.Contacts);
        Assert.Equal("Jane Smith", saved.Contacts[0].Name);
        Assert.Equal("Acme Supplies", saved.ContactInfo.BusinessName);
        Assert.Equal(string.Empty, saved.ContactInfo.ContactPersonName);
    }

    [Fact]
    public async Task EmployeeChannelOrderMigrationStep_RemovesDirectChannelsFromCustomOrder()
    {
        var channelRepo = new ChatChannelRepository(_persistence, _configMock.Object);
        var directChannel = new ChatChannel { Id = "chan-direct-1", ChannelType = ChatChannelType.Direct, Name = "DM" };
        var publicChannel = new ChatChannel { Id = "chan-public-1", ChannelType = ChatChannelType.General, Name = "General" };
        channelRepo.Save(directChannel);
        channelRepo.Save(publicChannel);

        var empRepo = new EmployeeRepository(_persistence, _configMock.Object);
        var employee = new Employee
        {
            Id = "emp-sidebar-1",
            UsesCustomSidebarOrder = true,
            CustomChannelOrder = new Dictionary<string, int> { ["chan-direct-1"] = 0, ["chan-public-1"] = 1 }
        };
        empRepo.Save(employee);

        var step = new EmployeeChannelOrderMigrationStep(empRepo, channelRepo);
        Assert.Equal("20260107_EmployeeDirectChannelOrderCleanup", step.MigrationKey);
        Assert.Equal(85, step.Order);

        await step.ExecuteAsync(CancellationToken.None);

        var updated = empRepo.GetById("emp-sidebar-1");
        Assert.NotNull(updated);
        Assert.Single(updated.CustomChannelOrder);
        Assert.True(updated.CustomChannelOrder.ContainsKey("chan-public-1"));
        Assert.False(updated.CustomChannelOrder.ContainsKey("chan-direct-1"));
    }

    [Fact]
    public async Task MigrationRunnerService_WhenLedgerContainsRemovedMigrationKeys_RunsPendingStepsIdempotently()
    {
        // Simulate an existing test server whose MigrationHistory.json recorded all removed Business migration keys
        // alongside already-applied Family keys before upgrading to a build with only the 6 surviving Family steps.
        var existingLedger = new MigrationHistoryLedger
        {
            AppliedMigrations =
            [
                new MigrationRecord { Key = "20260101_EmailEmployeeCentric", Description = "Removed Email step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260104_DocumentStatusSentToFinal", Description = "Removed Status step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260929_InvoiceLegacyPaymentToPaymentRecord", Description = "Removed Payment step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260105_LegacyBase64AttachmentMigration", Description = "Removed Attachment step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260106_WorkTypeDefaultSubTask", Description = "Removed WorkType step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260107_SupplierContactsMigration", Description = "Removed SupplierContacts step", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20261112_QuoteAndSignatureFileMigration", Description = "Removed Quote/Signature step", AppliedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new MigrationRecord { Key = "20261112_LegacyInvoiceAndPoCleanup", Description = "Removed Invoice/PO cleanup step", AppliedAtUtc = DateTime.UtcNow.AddDays(-2) },
                new MigrationRecord { Key = "20260102_PushDeviceSessionSubscriptions", Description = "Push", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260103_AvatarFileCentric", Description = "Avatar", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260106_LegacyOidcSubCleanup", Description = "OIDC", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260107_EmployeeDirectChannelOrderCleanup", Description = "ChannelOrder", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) },
                new MigrationRecord { Key = "20260108_LegacyChatAttachmentRoutes", Description = "ChatAttachmentRoute", AppliedAtUtc = DateTime.UtcNow.AddDays(-10) }
            ]
        };
        File.WriteAllText(Path.Combine(_testDataDir, "MigrationHistory.json"), JsonSerializer.Serialize(existingLedger));

        var appliedFamilyMocks = new[]
        {
            "20260102_PushDeviceSessionSubscriptions",
            "20260103_AvatarFileCentric",
            "20260106_LegacyOidcSubCleanup",
            "20260107_EmployeeDirectChannelOrderCleanup",
            "20260108_LegacyChatAttachmentRoutes"
        }.Select((key, idx) =>
        {
            var m = new Mock<IMigrationStep>();
            m.SetupGet(s => s.MigrationKey).Returns(key);
            m.SetupGet(s => s.Description).Returns(key);
            m.SetupGet(s => s.Order).Returns(20 + idx * 10);
            return m;
        }).ToList();

        var newlyPendingStep = new Mock<IMigrationStep>();
        newlyPendingStep.SetupGet(s => s.MigrationKey).Returns("20261112_PushSubscriptionsCaseFix");
        newlyPendingStep.SetupGet(s => s.Description).Returns("Push case fix");
        newlyPendingStep.SetupGet(s => s.Order).Returns(115);
        newlyPendingStep.Setup(s => s.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var steps = appliedFamilyMocks.Select(m => m.Object).Append(newlyPendingStep.Object).ToList();
        var runner = new MigrationRunnerService(steps, _configMock.Object, _persistence);

        var summary = await runner.RunPendingMigrationsAsync();

        Assert.Equal(6, summary.TotalSteps);
        Assert.Equal(5, summary.SkippedSteps);
        Assert.Equal(1, summary.ExecutedSteps);

        foreach (var appliedMock in appliedFamilyMocks)
        {
            appliedMock.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
        newlyPendingStep.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);

        var appliedAfter = runner.GetAppliedMigrations();
        Assert.Equal(14, appliedAfter.Count);
        Assert.Contains(appliedAfter, r => r.Key == "20261112_PushSubscriptionsCaseFix");
        Assert.Contains(appliedAfter, r => r.Key == "20261112_LegacyInvoiceAndPoCleanup");
    }

    [Fact]
    public void MigrationAssembly_ContainsOnlyTheRetainedFamilyMigrationSteps()
    {
        var migrationStepTypes = typeof(IMigrationStep).Assembly
            .GetTypes()
            .Where(t => typeof(IMigrationStep).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(
            [
                "AvatarMigrationStep",
                "EmployeeChannelOrderMigrationStep",
                "LegacyChatAttachmentRouteMigrationStep",
                "LegacyOidcSubMigrationStep",
                "LiveKitUdpMuxMigrationStep",
                "PushDeviceSessionMigrationStep",
                "PushSubscriptionsCaseFixMigrationStep"
            ],
            migrationStepTypes);
    }
}
