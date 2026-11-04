using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class BackupServiceTests : IDisposable
{
        private readonly string _testDataDir;
        private readonly Mock<ILogger<BackupService>> _mockLogger;
        private readonly Database _db;
        private readonly IConfiguration _config;
        private readonly DiskPersistenceService _persistence;

        public BackupServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_Backup_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataDir);

            _mockLogger = new Mock<ILogger<BackupService>>();

            var configDict = new Dictionary<string, string?> { { "DataPath", _testDataDir } };
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            // SequenceService requires IConfiguration
            var sequenceService = new SequenceService(_config);

            // Instantiate real repositories pointed at the test directory
            var projects = new ProjectRepository(_persistence, _config);
            var projectGroups = new ProjectGroupRepository(_persistence, _config);
            var employees = new EmployeeRepository(_persistence, _config);
            var timesheets = new TimesheetRepository(_persistence, _config);
            var workTypes = new WorkTypeRepository(_persistence, _config);
            var rateCards = new RateCardRepository(_persistence, _config);

            // Repositories requiring SequenceService or specific loggers
            var quotes = new QuoteRepository(_persistence, _config, sequenceService, new Mock<ILogger<QuoteRepository>>().Object);
            var purchases = new PurchaseOrderRepository(_persistence, _config);
            var suppliers = new SupplierRepository(_persistence, _config);
            var invoices = new InvoiceRepository(_persistence, _config, sequenceService);
            var profile = new CompanyProfileRepository(_persistence, _config);
            var templates = new DocumentTemplateRepository(_persistence, _config);
            var expenses = new ExpenseReportRepository(_persistence, _config, sequenceService);
            var bills = new BillRepository(_persistence, _config);
            var notes = new ProjectNoteRepository(_persistence, _config);
            var plans = new WeeklyPlanRepository(_persistence, _config);
            var teams = new TeamRepository(_persistence, _config);
            var channels = new ChatChannelRepository(_persistence, _config);
            var categories = new ChatCategoryRepository(_persistence, _config);
            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            var messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
            var reportedMessages = new ReportedMessageRepository(_persistence, _config);
            var readStates = new ChatReadStateRepository(_persistence, _config, profile);
            var boards = new BoardRepository(_persistence, _config);
            var boardCards = new BoardCardRepository(_persistence, _config);
            var boardTemplates = new BoardTemplateRepository(_persistence, _config);
            var events = new CalendarEventRepository(_persistence, _config);
            var oidc = new OpenIdAccountRepository(_persistence, _config);
            var contrib = new EmployerContributionRepository(_persistence, _config);
            var recExpenses = new RecurringExpenseRepository(_persistence, _config);
            var mailFolders = new EmailFolderRepository(_persistence, _config);
            var mailMsgs = new EmailMessageRepository(_persistence, _config);
            var pubContacts = new PublicContactRepository(_persistence, _config);
            var privContacts = new PrivateContactRepository(_persistence, _config, new Mock<ILogger<PrivateContactRepository>>().Object);
            var standardDocs = new StandardDocumentRepository(_persistence, _config);
            var projectDocs = new ProjectDocumentRepository(_persistence, _config, new Mock<ILogger<ProjectDocumentRepository>>().Object);
            var commissionLedgers = new CommissionLedgerRecordRepository(_persistence, _config);
            var systemConfigs = new SystemConfigRepository(_persistence, _config);
            var encService = new EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config);
            var deviceSessions = new DeviceSessionRepository(_persistence, _config);
            var albums = new AlbumRepository(_persistence, _config);
            var demoConfigs = new DemoConfigRepository(_persistence, _config);
            var clientStates = new UserClientStateRepository(_persistence, _config);
            var hourBankAdjustments = new HourBankAdjustmentRepository(_persistence, _config);

            var projectsContext = new ProjectsContext(projects, projectGroups, workTypes, rateCards, quotes, notes, boards, boardCards, boardTemplates, projectDocs, profile);
            var hrContext = new HrContext(employees, timesheets, hourBankAdjustments, plans, teams, events, profile);
            var accountingContext = new AccountingContext(invoices, purchases, suppliers, commissionLedgers, expenses, bills, contrib, recExpenses, profile);
            var communicationContext = new CommunicationContext(channels, categories, messages, readStates, clientStates, reportedMessages, albums, mailFolders, mailMsgs, pubContacts, privContacts, profile);
            var coreContext = new CoreContext(profile, templates, oidc, serverConfigs, systemConfigs, deviceSessions, demoConfigs, standardDocs);

            _db = new Database(projectsContext, hrContext, accountingContext, communicationContext, coreContext);
        }

        public void Dispose()
        {
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }

        [Fact]
        public async Task RunBackupNow_CreatesZipFile()
        {
            // Setup some data
            File.WriteAllText(Path.Combine(_testDataDir, "test.json"), "{\"id\":\"1\"}");

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.ZipPath);
            var backupPath = result.ZipPath!;
            Assert.True(File.Exists(backupPath));
            Assert.Contains("Spokes_Backup_Manual_", backupPath);
            Assert.Contains(".zip", backupPath);

            using (var zip = ZipFile.OpenRead(backupPath))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "test.json");
            }
        }

        [Fact]
        public async Task RunBackupNowAsync_PrunesOldBackups()
        {
            var profile = _db.CompanyProfile.Get();
            profile.BackupRetentionCount = 2;
            profile.BackupsEnabled = true;
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            // Create 3 manual backups
            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);

            for (int i = 0; i < 3; i++)
            {
                var path = Path.Combine(backupDir, $"Spokes_Backup_old_{i}.zip");
                File.WriteAllText(path, "content");
                // Ensure creation times are different for sorting
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i));
            }

            // Running backup now should prune down to 2 auto backups
            // Plus 1 manual backup created by RunBackupNow
            var result = await service.RunBackupNowAsync();
            Assert.True(result.Success);

            var backups = service.GetBackups();
            Assert.Equal(3, backups.Count);
        }

        [Fact]
        public async Task RunBackupNow_WhenBackupEmployeeEmailsIsFalse_ExcludesEmailFiles()
        {
            var emailDir = Path.Combine(_testDataDir, "Employees", "emp1", "Email");
            Directory.CreateDirectory(emailDir);
            File.WriteAllText(Path.Combine(emailDir, "msg.eml"), "Subject: Test employee email");

            var accountRoot = Path.Combine(_testDataDir, "EmailAccounts", "acc-1");
            Directory.CreateDirectory(Path.Combine(accountRoot, "Messages"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "Bodies"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "Mime"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "Folders"));
            Directory.CreateDirectory(Path.Combine(accountRoot, "Attachments", "msg-1"));
            File.WriteAllText(Path.Combine(accountRoot, "Account.json"), "{\"id\":\"acc-1\"}");
            File.WriteAllText(Path.Combine(accountRoot, "index.json"), "{}");
            File.WriteAllText(Path.Combine(accountRoot, "Messages", "msg-1.json"), "{\"id\":\"msg-1\"}");
            File.WriteAllText(Path.Combine(accountRoot, "Bodies", "msg-1.html"), "<p>Hello</p>");
            File.WriteAllText(Path.Combine(accountRoot, "Mime", "msg-1.eml"), "Subject: Hello");
            File.WriteAllText(Path.Combine(accountRoot, "Folders", "inbox.json"), "{\"id\":\"inbox\"}");
            File.WriteAllText(Path.Combine(accountRoot, "Attachments", "msg-1", "att.pdf"), "PDF");

            var otherDir = Path.Combine(_testDataDir, "Employees", "emp1");
            File.WriteAllText(Path.Combine(otherDir, "profile.json"), "{\"name\":\"emp1\"}");

            var profile = _db.CompanyProfile.Get();
            profile.BackupEmployeeEmails = false;
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            var backupPath = result.ZipPath!;
            Assert.True(File.Exists(backupPath));
            using (var zip = ZipFile.OpenRead(backupPath))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "Employees/emp1/profile.json");
                Assert.Contains(zip.Entries, e => e.FullName == "EmailAccounts/acc-1/Account.json");
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("msg.eml", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("EmailAccounts/acc-1/Messages/", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("EmailAccounts/acc-1/Bodies/", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("EmailAccounts/acc-1/Mime/", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("EmailAccounts/acc-1/Folders/", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("EmailAccounts/acc-1/Attachments/", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(zip.Entries, e => e.FullName.Equals("EmailAccounts/acc-1/index.json", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public async Task RunBackupNow_WhenBackupEmployeeEmailsIsTrue_IncludesEmailFiles()
        {
            var emailDir = Path.Combine(_testDataDir, "Employees", "emp1", "Email");
            Directory.CreateDirectory(emailDir);
            File.WriteAllText(Path.Combine(emailDir, "msg.eml"), "Subject: Test employee email");

            var accountRoot = Path.Combine(_testDataDir, "EmailAccounts", "acc-1");
            Directory.CreateDirectory(Path.Combine(accountRoot, "Messages"));
            File.WriteAllText(Path.Combine(accountRoot, "Account.json"), "{\"id\":\"acc-1\"}");
            File.WriteAllText(Path.Combine(accountRoot, "Messages", "msg-1.json"), "{\"id\":\"msg-1\"}");

            var profile = _db.CompanyProfile.Get();
            profile.BackupEmployeeEmails = true;
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            var backupPath = result.ZipPath!;
            Assert.True(File.Exists(backupPath));
            using (var zip = ZipFile.OpenRead(backupPath))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "Employees/emp1/Email/msg.eml");
                Assert.Contains(zip.Entries, e => e.FullName == "EmailAccounts/acc-1/Account.json");
                Assert.Contains(zip.Entries, e => e.FullName == "EmailAccounts/acc-1/Messages/msg-1.json");
            }
        }

        [Fact]
        public async Task Properties_InitialAndAfterSuccessfulBackup_HaveExpectedValues()
        {
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            // Initial state assertions
            Assert.Null(service.LastBackupAt);
            Assert.Equal("Never run", service.LastBackupStatus);
            Assert.False(service.IsRunning);

            // Run backup
            File.WriteAllText(Path.Combine(_testDataDir, "test.json"), "{\"id\":\"1\"}");
            var result = await service.RunBackupNowAsync();

            // After successful backup assertions
            Assert.True(result.Success);
            var backupPath = result.ZipPath!;
            Assert.True(File.Exists(backupPath));
            Assert.NotNull(service.LastBackupAt);
            Assert.Equal("Success", service.LastBackupStatus);
            Assert.False(service.IsRunning);
        }

        [Fact]
        public void GetBackups_ReturnsList()
        {
            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, "Spokes_Backup_2023.zip"), "test");

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var list = service.GetBackups();
            Assert.Single(list);
            Assert.Equal("Spokes_Backup_2023.zip", list[0].FileName);
        }

        [Fact]
        public void GetBackups_WhenDirectoryDoesNotExist_ReturnsEmptyList()
        {
            var backupDir = Path.Combine(_testDataDir, "Backups");
            if (Directory.Exists(backupDir))
            {
                Directory.Delete(backupDir, true);
            }

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var backups = service.GetBackups();
            Assert.NotNull(backups);
            Assert.Empty(backups);
        }

        [Fact]
        public void DeleteBackup_RemovesFile()
        {
            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);
            var path = Path.Combine(backupDir, "Spokes_Backup_del.zip");
            File.WriteAllText(path, "test");

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            service.DeleteBackup(path);
            Assert.False(File.Exists(path));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void DeleteBackup_WhenPathIsNullOrEmpty_DoesNothing(string? path)
        {
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var ex = Record.Exception(() => service.DeleteBackup(path!));
            Assert.Null(ex);
        }

        [Fact]
        public async Task GetBackupBytes_ReturnsContent()
        {
            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);
            var path = Path.Combine(backupDir, "Spokes_Backup_bytes.zip");
            var content = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(path, content);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var result = await service.GetBackupBytesAsync(path);
            Assert.Equal(content, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetBackupBytesAsync_WhenPathIsNullOrEmpty_ReturnsNull(string? path)
        {
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            var result = await service.GetBackupBytesAsync(path!);
            Assert.Null(result);
        }

        [Fact]
        public async Task RestoreBackup_RestoresFilesAndReloadsDb()
        {
            // 1. Create a "Good" state and zip it
            var goodDir = Path.Combine(_testDataDir, "Good");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "data.json"), "{\"id\":\"good\"}");

            var backupPath = Path.Combine(_testDataDir, "test_restore.zip");
            ZipFile.CreateFromDirectory(goodDir, backupPath);

            // 2. Current state is "bad"
            File.WriteAllText(Path.Combine(_testDataDir, "data.json"), "{\"id\":\"bad\"}");

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            // 3. Restore
            var result = await service.RestoreBackupAsync(backupPath, _mockLogger.Object);
            Assert.True(result.Success);

            // 4. Verify
            Assert.Equal("{\"id\":\"good\"}", File.ReadAllText(Path.Combine(_testDataDir, "data.json")));
        }

        [Fact]
        public async Task RestoreBackup_WithNullLogger_DoesNotThrowArgumentNullException()
        {
            var goodDir = Path.Combine(_testDataDir, "GoodNullLogger");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "data.json"), "{\"id\":\"null_logger_test\"}");

            var backupPath = Path.Combine(_testDataDir, "test_restore_null_logger.zip");
            ZipFile.CreateFromDirectory(goodDir, backupPath);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            // Invoke RestoreBackup with null logger (e.g. from DemoSettings.razor)
            var result = await service.RestoreBackupAsync(backupPath, null);
            Assert.True(result.Success);

            Assert.Equal("{\"id\":\"null_logger_test\"}", File.ReadAllText(Path.Combine(_testDataDir, "data.json")));
        }

        [Fact]
        public async Task RestoreBackup_WithZipSlipTraversalEntry_ThrowsIOExceptionAndDoesNotExtractOutsideDataDir()
        {
            var maliciousZipPath = Path.Combine(_testDataDir, "malicious_zipslip.zip");
            var escapedFilePath = Path.Combine(Path.GetTempPath(), "spokes_zipslip_escaped_" + Guid.NewGuid() + ".txt");

            try
            {
                using (var zipStream = new FileStream(maliciousZipPath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    // Create entry with relative directory traversal
                    var entry = archive.CreateEntry("../" + Path.GetFileName(escapedFilePath));
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("malicious payload");
                }

                var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);
                var result = await service.RestoreBackupAsync(maliciousZipPath, null);
                Assert.False(result.Success);
                Assert.Contains("outside of the target directory", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(escapedFilePath), "Zip Slip entry should not have been extracted outside the data directory!");
            }
            finally
            {
                if (File.Exists(escapedFilePath))
                {
                    File.Delete(escapedFilePath);
                }
            }
        }

        [Fact]
        public async Task RestoreBackup_WhenArchiveInvalidOrMalicious_PreservesExistingDataFiles()
        {
            var existingFilePath = Path.Combine(_testDataDir, "critical_data.json");
            File.WriteAllText(existingFilePath, "{\"preserved\":true}");

            var maliciousZip = Path.Combine(_testDataDir, "malicious_preval.zip");
            using (var zipStream = new FileStream(maliciousZip, FileMode.Create))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped_preval.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("payload");
            }

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            var result = await service.RestoreBackupAsync(maliciousZip, null);
            Assert.False(result.Success);
            Assert.True(File.Exists(existingFilePath), "Existing database file was deleted despite Step 1 pre-validation failure!");
            Assert.Equal("{\"preserved\":true}", File.ReadAllText(existingFilePath));

            // Also test with corrupt/non-zip file
            var corruptFile = Path.Combine(_testDataDir, "corrupt.zip");
            File.WriteAllText(corruptFile, "NOT A REAL ZIP FILE CONTENT");

            var corruptResult = await service.RestoreBackupAsync(corruptFile, null);
            Assert.False(corruptResult.Success);
            Assert.True(File.Exists(existingFilePath), "Existing database file was deleted on corrupt archive format!");
            Assert.Equal("{\"preserved\":true}", File.ReadAllText(existingFilePath));
        }

        [Fact]
        public async Task RestoreBackup_WhenProviderTypeIsBuiltInCasdoor_AttemptsCasdoorRestartGracefully()
        {
            var stagingDir = Path.Combine(Path.GetTempPath(), "Spokes_Casdoor_Staging_" + Guid.NewGuid());
            try
            {
                var settingsDir = Path.Combine(stagingDir, "Settings");
                Directory.CreateDirectory(settingsDir);
                var sysConfig = new SystemConfig
                {
                    Id = "system_config",
                    ProviderType = IdpType.BuiltInCasdoor
                };
                File.WriteAllText(Path.Combine(settingsDir, "system_config.json"), System.Text.Json.JsonSerializer.Serialize(sysConfig));

                var backupZipPath = Path.Combine(_testDataDir, "casdoor_backup.zip");
                ZipFile.CreateFromDirectory(stagingDir, backupZipPath);

                var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);
                var result = await service.RestoreBackupAsync(backupZipPath, Mock.Of<ILogger>());

                Assert.True(result.Success);
                Assert.False(result.CasdoorRestarted);
                Assert.Equal(IdpType.BuiltInCasdoor, _db.SystemConfigs.Get().ProviderType);
            }
            finally
            {
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task RestoreBackupAsync_ValidZip_ReturnsTrueOnSuccess()
        {
            var goodDir = Path.Combine(_testDataDir, "GoodSync");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "data.json"), "{\"id\":\"sync_good\"}");

            var backupPath = Path.Combine(_testDataDir, "test_restore_sync.zip");
            ZipFile.CreateFromDirectory(goodDir, backupPath);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            var result = await service.RestoreBackupAsync(backupPath, _mockLogger.Object);

            Assert.True(result.Success);
            Assert.Equal("{\"id\":\"sync_good\"}", File.ReadAllText(Path.Combine(_testDataDir, "data.json")));
        }

        [Fact]
        public async Task RestoreBackupAsync_WhenExtractionCancelled_PreservesExistingData()
        {
            var existingFile = Path.Combine(_testDataDir, "preserve_on_cancel.json");
            File.WriteAllText(existingFile, "{\"original\":true}");

            var sourceDir = Path.Combine(_testDataDir, "CancelSource");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "hello");
            File.WriteAllText(Path.Combine(sourceDir, "file2.txt"), "world");

            var backupPath = Path.Combine(_testDataDir, "cancel_test.zip");
            ZipFile.CreateFromDirectory(sourceDir, backupPath);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancelled token

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);
            var result = await service.RestoreBackupAsync(backupPath, null, cts.Token);

            Assert.False(result.Success);
            Assert.True(File.Exists(existingFile), "Existing data file must be preserved when extraction is cancelled");
            Assert.Equal("{\"original\":true}", File.ReadAllText(existingFile));
        }

        [Fact]
        public void BackupFileInfo_FormattedSize_Works()
        {
            var b1 = new BackupFileInfo { SizeBytes = 500 };
            Assert.Equal("500 B", b1.FormattedSize);

            var b2 = new BackupFileInfo { SizeBytes = (long)(1.5 * 1024) };
            Assert.Equal("1.5 KB", b2.FormattedSize);

            var b3 = new BackupFileInfo { SizeBytes = (long)(2.5 * 1024 * 1024) };
            Assert.Equal("2.5 MB", b3.FormattedSize);
        }

        [Fact]
        public void BackupFileInfo_IsManual_Works()
        {
            var b1 = new BackupFileInfo { FileName = "Spokes_Backup_2023.zip" };
            Assert.False(b1.IsManual);
            Assert.Equal("Auto", b1.BackupType);

            var b2 = new BackupFileInfo { FileName = "Spokes_Backup_Manual_2023.zip" };
            Assert.True(b2.IsManual);
            Assert.Equal("Manual", b2.BackupType);
        }

        [Fact]
        public async Task DeleteBackup_And_GetBackupBytesAsync_WithTraversalPath_RejectsOperation()
        {
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);
            var sensitiveFile = Path.Combine(_testDataDir, "sensitive.json");
            File.WriteAllText(sensitiveFile, "{\"secret\":\"confidential\"}");

            // Craft traversal path starting with Backups prefix
            var traversalPath = Path.Combine(_testDataDir, "Backups", "..", "sensitive.json");

            // DeleteBackup should not delete the file outside Backups
            service.DeleteBackup(traversalPath);
            Assert.True(File.Exists(sensitiveFile), "Sensitive file outside Backups should not be deleted via traversal path!");

            // GetBackupBytesAsync should not read the file outside Backups
            var bytes = await service.GetBackupBytesAsync(traversalPath);
            Assert.Null(bytes);
        }

        [Fact]
        public async Task RunBackupNow_LogsToSystemLogService()
        {
            File.WriteAllText(Path.Combine(_testDataDir, "test_log.json"), "{\"id\":\"1\"}");

            var mockSystemLog = new Mock<ISystemLogService>();
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, mockSystemLog.Object);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            var backupPath = result.ZipPath!;
            Assert.True(File.Exists(backupPath));
            mockSystemLog.Verify(l => l.LogInfo(
                "Backup",
                It.Is<string>(msg => msg.Contains("Backup created successfully")),
                It.Is<string?>(details => details != null && details.Contains("Manual"))),
                Times.Once);
        }

        [Fact]
        public async Task PruneBackups_LogsToSystemLogService()
        {
            var profile = _db.CompanyProfile.Get();
            profile.BackupRetentionCount = 1;
            profile.BackupsEnabled = true;
            _db.CompanyProfile.Save(profile);

            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);

            // Create 2 old backups so 1 is pruned
            for (int i = 0; i < 2; i++)
            {
                var path = Path.Combine(backupDir, $"Spokes_Backup_prune_{i}.zip");
                File.WriteAllText(path, "content");
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-20 + i));
            }

            var mockSystemLog = new Mock<ISystemLogService>();
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, mockSystemLog.Object);

            var result = await service.RunBackupNowAsync();
            Assert.True(result.Success);

            mockSystemLog.Verify(l => l.LogInfo(
                "Backup",
                It.Is<string>(msg => msg.Contains("Pruned old backup")),
                It.IsAny<string?>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public void DeleteBackup_LogsToSystemLogService()
        {
            var backupDir = Path.Combine(_testDataDir, "Backups");
            Directory.CreateDirectory(backupDir);

            var backupPath = Path.Combine(backupDir, "Spokes_Backup_to_delete.zip");
            File.WriteAllText(backupPath, "dummy content");

            var mockSystemLog = new Mock<ISystemLogService>();
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, mockSystemLog.Object);

            service.DeleteBackup(backupPath);

            Assert.False(File.Exists(backupPath));
            mockSystemLog.Verify(l => l.LogInfo(
                "Backup",
                It.Is<string>(msg => msg.Contains("Deleted backup: Spokes_Backup_to_delete.zip")),
                It.IsAny<string?>()),
                Times.Once);
        }

        [Fact]
        public async Task RunBackupNow_WhenExceptionOccurs_ReturnsFailedResult()
        {
            // Null config will throw NullReferenceException or similar inside RunBackupInternal
            var badConfig = new Mock<IConfiguration>();
            badConfig.Setup(c => c["DataPath"]).Returns((string?)null);

            var service = new BackupService(_mockLogger.Object, null!, badConfig.Object);

            var result = await service.RunBackupNowAsync();

            Assert.False(result.Success);
            Assert.False(result.IsBusy);
            Assert.Null(result.ZipPath);
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("Failed:", service.LastBackupStatus);
        }

        [Fact]
        public async Task RunBackupNow_ConcurrentInvocations_GuaranteesMutualExclusion()
        {
            for (int i = 0; i < 50; i++)
            {
                File.WriteAllText(Path.Combine(_testDataDir, $"test_concurrent_{i}.json"), new string('x', 50000));
            }

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            using var startSignal = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 30)
                .Select(_ => Task.Run(async () =>
                {
                    startSignal.Wait();
                    return await service.RunBackupNowAsync();
                }))
                .ToList();

            startSignal.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Contains(results, r => r.Success);
            Assert.Contains(results, r => r.IsBusy);
            Assert.False(service.IsRunning);
        }

        [Fact]
        public async Task RunBackupNow_WithEncryptionEnabled_ProducesSpkBackupAndRestoresSuccessfully()
        {
            var configDict = new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "SuperSecureMasterPass123!" }
            };
            var configWithPass = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

            var profile = _db.CompanyProfile.Get();
            profile.EncryptBackupsWithMasterPassword = true;
            profile.CompanyName = "Encrypted Corp";
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, configWithPass, _persistence, _db);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            Assert.NotNull(result.ZipPath);
            Assert.EndsWith(".spkbackup", result.ZipPath);
            Assert.True(File.Exists(result.ZipPath));

            // Verify file has SPKENC01 header
            var encService = new BackupEncryptionService();
            Assert.True(encService.IsEncryptedFile(result.ZipPath));

            // Modify profile to simulate data change
            profile.CompanyName = "Tampered Corp";
            _db.CompanyProfile.Save(profile);
            await _persistence.FlushAllAsync();
            Assert.Equal("Tampered Corp", _db.CompanyProfile.Get().CompanyName);

            // Restore from the encrypted backup
            var restoreResult = await service.RestoreBackupAsync(result.ZipPath, _mockLogger.Object);
            Assert.True(restoreResult.Success);

            // Verify data was restored
            var restoredProfile = _db.CompanyProfile.Get();
            Assert.Equal("Encrypted Corp", restoredProfile.CompanyName);
        }

        [Fact]
        public async Task RestoreBackup_WithWrongMasterPassword_FailsGracefully()
        {
            var configWithCorrectPass = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "OriginalMasterPass123!" }
            }).Build();

            var profile = _db.CompanyProfile.Get();
            profile.EncryptBackupsWithMasterPassword = true;
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, configWithCorrectPass, _persistence, _db);
            var result = await service.RunBackupNowAsync();
            Assert.True(result.Success);
            Assert.NotNull(result.ZipPath);

            // Now create a service instance with a WRONG master password
            var configWithWrongPass = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "WrongMasterPass456!" }
            }).Build();

            var wrongPassService = new BackupService(_mockLogger.Object, _db.CompanyProfile, configWithWrongPass, _persistence, _db);
            var restoreResult = await wrongPassService.RestoreBackupAsync(result.ZipPath, _mockLogger.Object);

            Assert.False(restoreResult.Success);
            Assert.Contains("SPOKES_MASTER_PASSWORD", restoreResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RunBackupNowAsync_TracksIsLocalBackupInProgress_AndFiresEvent()
        {
            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            var inProgressSnapshots = new List<bool>();
            service.OnLocalBackupStateChanged += () =>
            {
                inProgressSnapshots.Add(service.IsLocalBackupInProgress);
            };

            Assert.False(service.IsLocalBackupInProgress);

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            Assert.False(service.IsLocalBackupInProgress);
            Assert.Contains(true, inProgressSnapshots);
            Assert.False(inProgressSnapshots.Last());
        }

        [Fact]
        public async Task RunBackupNowAsync_EmitsDeterminateProgressAndTransitionsStages()
        {
            var testSubdir = Path.Combine(_testDataDir, "TestData");
            Directory.CreateDirectory(testSubdir);
            var testFilePath = Path.Combine(testSubdir, "sample.bin");
            var sampleBytes = new byte[100000];
            Random.Shared.NextBytes(sampleBytes);
            await File.WriteAllBytesAsync(testFilePath, sampleBytes);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);

            var progressReports = new List<LocalBackupProgress>();
            service.OnLocalBackupProgressChanged += p =>
            {
                progressReports.Add(p);
            };

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            Assert.NotEmpty(progressReports);
            Assert.Contains(progressReports, p => p.Stage == LocalBackupStage.Preparing);
            Assert.Contains(progressReports, p => p.Stage == LocalBackupStage.Archiving);
            Assert.Contains(progressReports, p => p.Stage == LocalBackupStage.Completed);

            var completedReport = progressReports.Last();
            Assert.Equal(LocalBackupStage.Completed, completedReport.Stage);
            Assert.Equal(100.0, completedReport.Percent);
            Assert.True(completedReport.BytesProcessed > 0);
            Assert.Equal(completedReport.BytesProcessed, completedReport.TotalBytes);
            Assert.True(completedReport.FilesProcessed > 0);
            Assert.Equal(completedReport.FilesProcessed, completedReport.TotalFiles);
        }

        [Fact]
        public async Task RunBackupNowAsync_WithEncryption_ReportsEncryptingStage()
        {
            var configWithPass = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DataPath", _testDataDir },
                { "SPOKES_MASTER_PASSWORD", "MasterKey123!@#" }
            }).Build();

            var testSubdir = Path.Combine(_testDataDir, "TestDataEnc");
            Directory.CreateDirectory(testSubdir);
            var testFilePath = Path.Combine(testSubdir, "data.bin");
            var sampleBytes = new byte[150000];
            Random.Shared.NextBytes(sampleBytes);
            await File.WriteAllBytesAsync(testFilePath, sampleBytes);

            var profile = _db.CompanyProfile.Get() ?? new CompanyProfile();
            profile.Backup.EncryptBackupsWithMasterPassword = true;
            _db.CompanyProfile.Save(profile);

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, configWithPass, _persistence, _db);

            var stagesSeen = new HashSet<LocalBackupStage>();
            service.OnLocalBackupProgressChanged += p =>
            {
                stagesSeen.Add(p.Stage);
            };

            var result = await service.RunBackupNowAsync();

            Assert.True(result.Success);
            Assert.Contains(LocalBackupStage.Preparing, stagesSeen);
            Assert.Contains(LocalBackupStage.Archiving, stagesSeen);
            Assert.Contains(LocalBackupStage.Encrypting, stagesSeen);
            Assert.Contains(LocalBackupStage.Completed, stagesSeen);
        }

        [Fact]
        public async Task RestoreBackupAsync_FromPlaintextGitHubZipballWithTargetFolder_UnwrapsWrapperAndRestoresAllFiles()
        {
            const string companyJson = "{\"Id\":\"company_profile\",\"CompanyName\":\"Restored Spokes Robotics\"}";
            const string dpKeyXml = "<key id=\"dp-dr-01\"><value>RESTORED_DATA_PROTECTION_SECRET</value></key>";
            const string employeeJson = "{\"Id\":\"emp-ada\",\"FirstName\":\"Ada\",\"LastName\":\"Lovelace\"}";

            var zipballPath = Path.Combine(_testDataDir, "github_zipball_plaintext.zip");
            using (var zipStream = new FileStream(zipballPath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                static void WriteEntry(ZipArchive zip, string entryPath, ReadOnlySpan<byte> content)
                {
                    var entry = zip.CreateEntry(entryPath);
                    using var s = entry.Open();
                    s.Write(content);
                }

                WriteEntry(
                    archive,
                    "acme-spokes-backups-9f8e7d6/spokes-backups/.gitkeep",
                    ReadOnlySpan<byte>.Empty);
                WriteEntry(
                    archive,
                    "acme-spokes-backups-9f8e7d6/spokes-backups/Settings/company.json",
                    Encoding.UTF8.GetBytes(companyJson));
                WriteEntry(
                    archive,
                    "acme-spokes-backups-9f8e7d6/spokes-backups/keys/key-dr-01.xml",
                    Encoding.UTF8.GetBytes(dpKeyXml));
                WriteEntry(
                    archive,
                    "acme-spokes-backups-9f8e7d6/spokes-backups/Employees/emp-ada.json",
                    Encoding.UTF8.GetBytes(employeeJson));
            }

            var freshDrDir = Path.Combine(Path.GetTempPath(), "Spokes_FreshDR_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(freshDrDir);
            using var drPersistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            try
            {
                var drConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataPath"] = freshDrDir
                }).Build();
                var drProfiles = new CompanyProfileRepository(drPersistence, drConfig);
                var drBackupService = new BackupService(_mockLogger.Object, drProfiles, drConfig, drPersistence, db: null, new BackupEncryptionService(), destinationCoordinator: null);

                var restoreResult = await drBackupService.RestoreBackupAsync(
                    zipballPath,
                    logger: null,
                    CancellationToken.None);

                Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
                Assert.Equal(companyJson, await File.ReadAllTextAsync(Path.Combine(freshDrDir, "Settings", "company.json")));
                Assert.Equal(dpKeyXml, await File.ReadAllTextAsync(Path.Combine(freshDrDir, "keys", "key-dr-01.xml")));
                Assert.Equal(employeeJson, await File.ReadAllTextAsync(Path.Combine(freshDrDir, "Employees", "emp-ada.json")));
                Assert.False(File.Exists(Path.Combine(freshDrDir, ".gitkeep")));
                Assert.False(Directory.Exists(Path.Combine(freshDrDir, "acme-spokes-backups-9f8e7d6")));
                Assert.False(Directory.Exists(Path.Combine(freshDrDir, "Backups", ".staging")));
            }
            finally
            {
                drPersistence.Dispose();
                if (Directory.Exists(freshDrDir))
                {
                    try { Directory.Delete(freshDrDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task StagingHygiene_PreventsAndPurgesOrphanedTmpZipAndStagingDirectories()
        {
            await File.WriteAllTextAsync(Path.Combine(_testDataDir, "sample.json"), "{\"ok\":true}");

            var profile = _db.CompanyProfile.Get() ?? new CompanyProfile();
            profile.Backup.EncryptBackupsWithMasterPassword = true;
            profile.PublicChannelMasterPassword = null;
            _db.CompanyProfile.Save(profile);
            await _persistence.FlushAllAsync();

            var backupDir = Path.Combine(_testDataDir, "Backups");

            // 1. Missing SPOKES_MASTER_PASSWORD fails BEFORE creating any plaintext .tmp.zip
            var noPassService = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config, _persistence, _db);
            var noPassResult = await noPassService.RunBackupNowAsync();
            Assert.False(noPassResult.Success);
            if (Directory.Exists(backupDir))
            {
                Assert.Empty(Directory.GetFiles(backupDir, "*.tmp.zip"));
                Assert.Empty(Directory.GetFiles(backupDir, "*.spkbackup"));
            }

            // 2. Exception thrown mid-encryption deletes both plaintext .tmp.zip and partial .spkbackup in finally
            var configWithPass = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _testDataDir,
                ["SPOKES_MASTER_PASSWORD"] = "ValidMasterPass!123"
            }).Build();

            var throwingEncryption = new Mock<IBackupEncryptionService>();
            throwingEncryption
                .Setup(e => e.EncryptBackupAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<IProgress<long>?>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Stream, Stream, string, IProgress<long>?, CancellationToken>((_, outStream, _, _, _) =>
                {
                    outStream.Write(new byte[] { 1, 2, 3, 4 });
                })
                .ThrowsAsync(new IOException("Simulated disk full during encryption"));

            var failingService = new BackupService(
                _mockLogger.Object,
                _db.CompanyProfile,
                configWithPass,
                _persistence,
                _db,
                throwingEncryption.Object,
                destinationCoordinator: null);

            var failedEncResult = await failingService.RunBackupNowAsync();
            Assert.False(failedEncResult.Success);
            Assert.Empty(Directory.GetFiles(backupDir, "*.tmp.zip"));
            Assert.Empty(Directory.GetFiles(backupDir, "*.spkbackup"));

            // 3. GetBackups() excludes .tmp.zip and .staging, and BackupService constructor purges leftover crash artifacts
            var orphanStagingFile = Path.Combine(backupDir, ".staging", "crashed-run", "plaintext.json");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanStagingFile)!);
            await File.WriteAllTextAsync(orphanStagingFile, "{\"secret\":true}");

            var orphanTmpZip = Path.Combine(backupDir, "Spokes_Backup_20260101_000000.tmp.zip");
            await File.WriteAllTextAsync(orphanTmpZip, "orphan-plaintext-zip");

            var validArchive = Path.Combine(backupDir, "Spokes_Backup_20260101_010000.zip");
            await File.WriteAllTextAsync(validArchive, "valid-zip");

            var listedBeforeRestart = failingService.GetBackups();
            Assert.Single(listedBeforeRestart);
            Assert.Equal("Spokes_Backup_20260101_010000.zip", listedBeforeRestart[0].FileName);

            // Simulate server restart -> constructor purges Backups/.staging and *.tmp.zip
            _ = new BackupService(_mockLogger.Object, _db.CompanyProfile, configWithPass, _persistence, _db);
            Assert.False(Directory.Exists(Path.Combine(backupDir, ".staging")));
            Assert.False(File.Exists(orphanTmpZip));
            Assert.True(File.Exists(validArchive));
        }
    }


