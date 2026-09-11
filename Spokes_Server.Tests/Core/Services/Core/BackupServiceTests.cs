using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Core
{
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

            var configDict = new Dictionary<string, string> { { "DataPath", _testDataDir } };
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
            var push = new PushSubscriptionRepository(_persistence, _config);
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
            var encService = new Spokes_Server.Core.Services.Core.EncryptionService(_config);
            var serverConfigs = new ServerConfigRepository(_persistence, _config, encService);
            var deviceSessions = new DeviceSessionRepository(_persistence, _config);
            var albums = new AlbumRepository(_persistence, _config);
            var demoConfigs = new DemoConfigRepository(_persistence, _config);

            _db = new Database(
                _persistence, projects, projectGroups, employees, timesheets, workTypes, rateCards,
                quotes, purchases, suppliers, invoices, profile, templates,
                expenses, bills, notes, plans, teams, channels, categories, messages,
                readStates, reportedMessages, push, albums, boards, boardCards, boardTemplates, events,
                _config, oidc, contrib, recExpenses, mailFolders, mailMsgs,
                pubContacts, privContacts, standardDocs, projectDocs, commissionLedgers, systemConfigs, serverConfigs, deviceSessions, demoConfigs);
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

            var backupPath = await service.RunBackupNow();

            Assert.True(File.Exists(backupPath));
            Assert.Contains("Spokes_Backup_Manual_", backupPath);
            Assert.Contains(".zip", backupPath);

            using (var zip = ZipFile.OpenRead(backupPath))
            {
                Assert.Contains(zip.Entries, e => e.FullName == "test.json");
            }
        }

        [Fact]
        public async Task RunBackupNow_PrunesOldBackups()
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
            await service.RunBackupNow();

            var backups = service.GetBackups();
            Assert.Equal(3, backups.Count);
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

            var service = new BackupService(_mockLogger.Object, _db.CompanyProfile, _config);

            // 3. Restore
            _db.RestoreBackup(backupPath, _mockLogger.Object);

            // 4. Verify
            Assert.Equal("{\"id\":\"good\"}", File.ReadAllText(Path.Combine(_testDataDir, "data.json")));
        }

        [Fact]
        public void RestoreBackup_WithNullLogger_DoesNotThrowArgumentNullException()
        {
            var goodDir = Path.Combine(_testDataDir, "GoodNullLogger");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "data.json"), "{\"id\":\"null_logger_test\"}");

            var backupPath = Path.Combine(_testDataDir, "test_restore_null_logger.zip");
            ZipFile.CreateFromDirectory(goodDir, backupPath);

            // Invoke RestoreBackup with null logger (e.g. from DemoSettings.razor)
            _db.RestoreBackup(backupPath, null);

            Assert.Equal("{\"id\":\"null_logger_test\"}", File.ReadAllText(Path.Combine(_testDataDir, "data.json")));
        }

        [Fact]
        public void RestoreBackup_WithZipSlipTraversalEntry_ThrowsIOExceptionAndDoesNotExtractOutsideDataDir()
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

                var ex = Assert.Throws<IOException>(() => _db.RestoreBackup(maliciousZipPath, null));
                Assert.Contains("outside of the target directory", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        public void RestoreBackup_WhenArchiveInvalidOrMalicious_PreservesExistingDataFiles()
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

            Assert.Throws<IOException>(() => _db.RestoreBackup(maliciousZip, null));
            Assert.True(File.Exists(existingFilePath), "Existing database file was deleted despite Step 1 pre-validation failure!");
            Assert.Equal("{\"preserved\":true}", File.ReadAllText(existingFilePath));

            // Also test with corrupt/non-zip file
            var corruptFile = Path.Combine(_testDataDir, "corrupt.zip");
            File.WriteAllText(corruptFile, "NOT A REAL ZIP FILE CONTENT");

            Assert.Throws<Exception>(() => _db.RestoreBackup(corruptFile, null));
            Assert.True(File.Exists(existingFilePath), "Existing database file was deleted on corrupt archive format!");
            Assert.Equal("{\"preserved\":true}", File.ReadAllText(existingFilePath));
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
    }
}



