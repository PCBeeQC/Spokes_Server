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
    public void EmailDataMigrationStep_MovesLegacyFilesToEmployeeCentricPaths()
    {
        var empId = "emp-email-test";
        var legacyFolders = Path.Combine(_testDataDir, "EmailFolders");
        var legacyMessages = Path.Combine(_testDataDir, "EmailMessages");
        var legacyBodies = Path.Combine(_testDataDir, "Emails");

        Directory.CreateDirectory(legacyFolders);
        Directory.CreateDirectory(legacyMessages);
        Directory.CreateDirectory(legacyBodies);

        var folder = new EmailFolder { Id = "f-1", EmployeeId = empId, Name = "Inbox" };
        File.WriteAllText(Path.Combine(legacyFolders, "f-1.json"), JsonSerializer.Serialize(folder));

        var message = new EmailMessage { Id = "m-1", EmployeeId = empId, Subject = "Hello" };
        File.WriteAllText(Path.Combine(legacyMessages, "m-1.json"), JsonSerializer.Serialize(message));
        File.WriteAllText(Path.Combine(legacyBodies, "m-1.html"), "<p>Body</p>");

        var step = new EmailDataMigrationStep(null, _testDataDir);
        step.MigrateEmailData();

        var targetFolderFile = Path.Combine(_testDataDir, "Employees", empId, "Email", "Folders", "f-1.json");
        var targetMessageFile = Path.Combine(_testDataDir, "Employees", empId, "Email", "Messages", "m-1.json");
        var targetBodyFile = Path.Combine(_testDataDir, "Employees", empId, "Email", "Bodies", "m-1.html");

        Assert.True(File.Exists(targetFolderFile));
        Assert.True(File.Exists(targetMessageFile));
        Assert.True(File.Exists(targetBodyFile));
    }

    [Fact]
    public async Task MigrationRunnerService_CoordinatesMigrationsSuccessfully()
    {
        var step1Mock = new Mock<IMigrationStep>();
        step1Mock.SetupGet(s => s.MigrationKey).Returns("20260101_Step1");
        step1Mock.SetupGet(s => s.Description).Returns("Step 1");
        step1Mock.SetupGet(s => s.Order).Returns(10);
        step1Mock.Setup(s => s.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var step2Mock = new Mock<IMigrationStep>();
        step2Mock.SetupGet(s => s.MigrationKey).Returns("20260102_Step2");
        step2Mock.SetupGet(s => s.Description).Returns("Step 2");
        step2Mock.SetupGet(s => s.Order).Returns(20);
        step2Mock.Setup(s => s.ExecuteAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var runner = new MigrationRunnerService(new[] { step1Mock.Object, step2Mock.Object }, _configMock.Object, _persistence);
        await runner.RunPendingMigrationsAsync();

        step1Mock.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        step2Mock.Verify(s => s.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);

        var applied = runner.GetAppliedMigrations();
        Assert.Equal(2, applied.Count);
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
    public async Task AvatarMigrationStep_MigratesBase64AvatarsToDisk()
    {
        var empRepo = new EmployeeRepository(_persistence, _configMock.Object);
        var rawBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var base64 = "data:image/png;base64," + Convert.ToBase64String(rawBytes);

        var employee = new Employee
        {
            Id = "emp-step-1",
            FirstName = "Grace",
            AvatarBase64 = base64
        };
        empRepo.Save(employee);

        var step = new AvatarMigrationStep(empRepo, _configMock.Object);
        Assert.Equal("20260103_AvatarFileCentric", step.MigrationKey);
        Assert.Equal(30, step.Order);

        await step.ExecuteAsync(CancellationToken.None);

        var updated = empRepo.GetById("emp-step-1");
        Assert.NotNull(updated);
        Assert.Null(updated.AvatarBase64);
        Assert.Equal("avatar.png", updated.AvatarFile);

        var targetFile = Path.Combine(_testDataDir, "Employees", "emp-step-1", "avatar.png");
        Assert.True(File.Exists(targetFile));
        Assert.Equal(rawBytes, File.ReadAllBytes(targetFile));
    }

    [Fact]
    public async Task SupplierContactsMigrationStep_MigratesLegacyInfoToContactsAndSetsPrimary()
    {
        var supplierRepo = new SupplierRepository(_persistence, _configMock.Object);
        var supplier = new Supplier
        {
            Id = "sup-test-1",
            ContactInfo = new Spokes_Server.Core.Models.Core.ClientInfo
            {
                BusinessName = "Acme Corp",
                ContactPersonPrefix = "Mr.",
                ContactPersonName = "John Doe",
                ContactPersonTitle = "Manager",
                ContactPersonEmail = "john@acme.com",
                ContactPersonPhone = "555-1234"
            },
            Contacts = new List<ContactPerson>()
        };
        supplierRepo.Save(supplier);

        var step = new SupplierContactsMigrationStep(supplierRepo);
        Assert.Equal("20260107_SupplierContactsMigration", step.MigrationKey);
        Assert.Equal(80, step.Order);

        await step.ExecuteAsync(CancellationToken.None);

        var updated = supplierRepo.GetById("sup-test-1");
        Assert.NotNull(updated);
        Assert.Single(updated.Contacts);
        var contact = updated.Contacts[0];
        Assert.True(contact.IsPrimary);
        Assert.Equal("Mr.", contact.Prefix);
        Assert.Equal("John Doe", contact.Name);
        Assert.Equal("Manager", contact.Title);
        Assert.Equal("john@acme.com", contact.Email);
        Assert.Equal("555-1234", contact.Phone);
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
}
