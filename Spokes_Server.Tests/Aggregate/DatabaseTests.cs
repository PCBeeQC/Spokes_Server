using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Aggregate
{
    public class DatabaseTests : TestDataTestBase
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly Database _db;

        public DatabaseTests()
        {
            var services = new ServiceCollection();

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
            services.AddSingleton<IConfiguration>(mockConfig.Object);

            services.AddLogging(builder => builder.AddConsole());

            // Register EncryptionService (required by ServerConfigRepository, normally in AddSpokesCoreServices)
            services.AddSingleton<Spokes_Server.Core.Services.Core.EncryptionService>();

            // Register all repositories and Database
            services.AddSpokesDatabase();

            _serviceProvider = services.BuildServiceProvider();
            _db = _serviceProvider.GetRequiredService<Database>();
        }

        public override void Dispose()
        {
            _serviceProvider.Dispose();
            base.Dispose();
        }

        #region Constructors & DI Resolution

        [Fact]
        public void Constructor_Lightweight_SetsDeviceSessionsAndEmployeesProperties()
        {
            var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "DataPath", _testDataPath } }).Build();
            var sessions = new DeviceSessionRepository(persistence, config);
            var employees = new EmployeeRepository(persistence, config);

            var lightweightDb = new Database(sessions, employees);

            Assert.Same(sessions, lightweightDb.DeviceSessions);
            Assert.Same(employees, lightweightDb.Employees);
            Assert.Null(lightweightDb.Projects);
            Assert.Null(lightweightDb.Invoices);
        }

        [Fact]
        public void Resolution_Succeeds_AllRepositoryPropertiesAreNotNull()
        {
            Assert.NotNull(_db);
            Assert.NotNull(_db.Projects);
            Assert.NotNull(_db.ProjectGroups);
            Assert.NotNull(_db.Employees);
            Assert.NotNull(_db.Timesheets);
            Assert.NotNull(_db.WorkTypes);
            Assert.NotNull(_db.RateCards);
            Assert.NotNull(_db.Quotes);
            Assert.NotNull(_db.Purchases);
            Assert.NotNull(_db.Suppliers);
            Assert.NotNull(_db.Invoices);
            Assert.NotNull(_db.CommissionLedgerRecords);
            Assert.NotNull(_db.CompanyProfile);
            Assert.NotNull(_db.DocumentTemplates);
            Assert.NotNull(_db.ExpenseReports);
            Assert.NotNull(_db.Bills);
            Assert.NotNull(_db.ProjectNotes);
            Assert.NotNull(_db.WeeklyPlans);
            Assert.NotNull(_db.Teams);
            Assert.NotNull(_db.CalendarEvents);
            Assert.NotNull(_db.ChatChannels);
            Assert.NotNull(_db.ChatCategories);
            Assert.NotNull(_db.ChatMessages);
            Assert.NotNull(_db.ChatReadStates);
            Assert.NotNull(_db.ReportedMessages);
            Assert.NotNull(_db.PushSubscriptions);
            Assert.NotNull(_db.Albums);
            Assert.NotNull(_db.Boards);
            Assert.NotNull(_db.BoardCards);
            Assert.NotNull(_db.BoardTemplates);
            Assert.NotNull(_db.StandardDocuments);
            Assert.NotNull(_db.ProjectDocuments);
            Assert.NotNull(_db.OpenIdAccounts);
            Assert.NotNull(_db.ServerConfigs);
            Assert.NotNull(_db.SystemConfigs);
            Assert.NotNull(_db.DeviceSessions);
            Assert.NotNull(_db.DemoConfigs);
            Assert.NotNull(_db.EmailFolders);
            Assert.NotNull(_db.EmailMessages);
            Assert.NotNull(_db.EmployerContributions);
            Assert.NotNull(_db.RecurringExpenses);
            Assert.NotNull(_db.PublicContacts);
            Assert.NotNull(_db.PrivateContacts);
        }

        #endregion

        #region Initialize & ReloadAll

        [Fact]
        public void Initialize_CreatesDefaultGeneralChannel_AndDefaultCategories()
        {
            _db.Initialize();

            var generalChannel = _db.ChatChannels.GetAll().FirstOrDefault(c => c.Name == "General");
            Assert.NotNull(generalChannel);

            var categories = _db.ChatCategories.GetAll();
            Assert.NotEmpty(categories);
        }

        [Fact]
        public void Initialize_LinksDynamicPermissionGroups_ForEmployeesWithPermissionGroupId()
        {
            var profile = _db.CompanyProfile.Get();
            var pg = new PermissionGroup { Id = "admin-group", Name = "Administrators", Permissions = new List<string> { "all" } };
            profile.PermissionGroups = new List<PermissionGroup> { pg };
            _db.CompanyProfile.Save(profile);

            var emp = new Employee
            {
                Id = "emp-init-1",
                FirstName = "Alice",
                PermissionGroupId = "admin-group"
            };
            _db.Employees.Save(emp);

            _db.Initialize();

            var reloadedEmp = _db.Employees.GetById("emp-init-1");
            Assert.NotNull(reloadedEmp);
            Assert.NotNull(reloadedEmp.PermissionGroup);
            Assert.Equal("Administrators", reloadedEmp.PermissionGroup.Name);
        }

        [Fact]
        public void Initialize_WhenEmployeeHasNoMatchingPermissionGroupId_LeavesPermissionGroupNull()
        {
            var profile = _db.CompanyProfile.Get();
            profile.PermissionGroups = new List<PermissionGroup>
            {
                new PermissionGroup { Id = "known-group", Name = "Known Group" }
            };
            _db.CompanyProfile.Save(profile);

            var empUnknown = new Employee { Id = "emp-unknown", FirstName = "Bob", PermissionGroupId = "unknown-group" };
            var empNoGroup = new Employee { Id = "emp-nogroup", FirstName = "Charlie", PermissionGroupId = null };
            _db.Employees.Save(empUnknown);
            _db.Employees.Save(empNoGroup);

            _db.Initialize();

            Assert.Null(_db.Employees.GetById("emp-unknown")?.PermissionGroup);
            Assert.Null(_db.Employees.GetById("emp-nogroup")?.PermissionGroup);
        }

        [Fact]
        public void ReloadAll_InvokesInitialize_AndReloadsRepositoryData()
        {
            _db.Initialize();

            var proj = new Project { Id = "proj-reload", Name = "Before Reload" };
            _db.Projects.Save(proj);

            _db.ReloadAll();

            var fetched = _db.Projects.GetById("proj-reload");
            Assert.NotNull(fetched);
            Assert.Equal("Before Reload", fetched.Name);
        }

        #endregion

        #region Event Subscriptions & TriggerProjectUpdate

        [Fact]
        public void Quotes_OnSaved_TriggersProjectUpdate()
        {
            var proj = new Project { Id = "proj-q-1", Name = "Quote Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
            _db.Projects.Save(proj);
            proj.LastEdited = DateTime.UtcNow.AddDays(-2);

            var quote = new Quote { Id = "q-1", ProjectId = "proj-q-1", Title = "Estimate 1" };
            _db.Quotes.Save(quote);

            var updatedProj = _db.Projects.GetById("proj-q-1");
            Assert.NotNull(updatedProj);
            Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void Quotes_OnSaved_WithEmptyOrNonExistentProjectId_DoesNotThrow()
        {
            var quoteEmpty = new Quote { Id = "q-empty", ProjectId = string.Empty };
            var quoteMissing = new Quote { Id = "q-missing", ProjectId = "non-existent-proj" };

            _db.Quotes.Save(quoteEmpty);
            _db.Quotes.Save(quoteMissing);

            Assert.NotNull(_db.Quotes.GetById("q-empty"));
            Assert.NotNull(_db.Quotes.GetById("q-missing"));
        }

        [Fact]
        public void Invoices_OnSaved_TriggersProjectUpdate()
        {
            var proj = new Project { Id = "proj-inv-1", Name = "Invoice Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
            _db.Projects.Save(proj);
            proj.LastEdited = DateTime.UtcNow.AddDays(-2);

            var invoice = new Invoice { Id = "inv-1", ProjectId = "proj-inv-1", InvoiceNumber = "INV-001" };
            _db.Invoices.Save(invoice);

            var updatedProj = _db.Projects.GetById("proj-inv-1");
            Assert.NotNull(updatedProj);
            Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void Invoices_OnSaved_WithNullOrEmptyOrNonExistentProjectId_DoesNotThrow()
        {
            var invNull = new Invoice { Id = "inv-null", ProjectId = null! };
            var invEmpty = new Invoice { Id = "inv-empty", ProjectId = string.Empty };
            var invMissing = new Invoice { Id = "inv-missing", ProjectId = "non-existent-proj" };

            _db.Invoices.Save(invNull);
            _db.Invoices.Save(invEmpty);
            _db.Invoices.Save(invMissing);

            Assert.NotNull(_db.Invoices.GetById("inv-null"));
            Assert.NotNull(_db.Invoices.GetById("inv-empty"));
            Assert.NotNull(_db.Invoices.GetById("inv-missing"));
        }

        [Fact]
        public void ProjectNotes_OnSaved_TriggersProjectUpdate()
        {
            var proj = new Project { Id = "proj-note-1", Name = "Note Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
            _db.Projects.Save(proj);
            proj.LastEdited = DateTime.UtcNow.AddDays(-2);

            var note = new ProjectNote { Id = "note-1", ProjectId = "proj-note-1", Title = "Meeting Note" };
            _db.ProjectNotes.Save(note);

            var updatedProj = _db.Projects.GetById("proj-note-1");
            Assert.NotNull(updatedProj);
            Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void ProjectDocuments_OnSaved_TriggersProjectUpdate()
        {
            var proj = new Project { Id = "proj-doc-1", Name = "Doc Project", LastEdited = DateTime.UtcNow.AddDays(-2) };
            _db.Projects.Save(proj);
            proj.LastEdited = DateTime.UtcNow.AddDays(-2);

            var doc = new ProjectDocument { Id = "doc-1", ProjectId = "proj-doc-1", Name = "Spec Doc" };
            _db.ProjectDocuments.Save(doc);

            var updatedProj = _db.Projects.GetById("proj-doc-1");
            Assert.NotNull(updatedProj);
            Assert.True(updatedProj.LastEdited > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void Timesheets_OnSaved_TriggersProjectUpdate_ForMultipleDistinctProjects()
        {
            var proj1 = new Project { Id = "proj-ts-1", Name = "TS Project 1", LastEdited = DateTime.UtcNow.AddDays(-2) };
            var proj2 = new Project { Id = "proj-ts-2", Name = "TS Project 2", LastEdited = DateTime.UtcNow.AddDays(-2) };
            _db.Projects.Save(proj1);
            _db.Projects.Save(proj2);
            proj1.LastEdited = DateTime.UtcNow.AddDays(-2);
            proj2.LastEdited = DateTime.UtcNow.AddDays(-2);

            var ts = new Timesheet
            {
                Id = "ts-1",
                EmployeeId = "emp-ts",
                Entries = new List<TimeEntry>
                {
                    new TimeEntry { Id = "entry-1", ProjectId = "proj-ts-1", Hours = 4 },
                    new TimeEntry { Id = "entry-2", ProjectId = "proj-ts-2", Hours = 2 },
                    new TimeEntry { Id = "entry-3", ProjectId = "proj-ts-1", Hours = 1 } // duplicate project
                }
            };

            _db.Timesheets.Save(ts);

            var updated1 = _db.Projects.GetById("proj-ts-1");
            var updated2 = _db.Projects.GetById("proj-ts-2");

            Assert.NotNull(updated1);
            Assert.NotNull(updated2);
            Assert.True(updated1.LastEdited > DateTime.UtcNow.AddMinutes(-1));
            Assert.True(updated2.LastEdited > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void CompanyProfile_OnSaved_UpdatesPermissionGroupReferences_OnAllMatchingEmployees()
        {
            var emp1 = new Employee { Id = "emp-profile-1", FirstName = "Emp 1", PermissionGroupId = "group-a" };
            var emp2 = new Employee { Id = "emp-profile-2", FirstName = "Emp 2", PermissionGroupId = "group-b" };
            _db.Employees.Save(emp1);
            _db.Employees.Save(emp2);

            var profile = _db.CompanyProfile.Get();
            profile.PermissionGroups = new List<PermissionGroup>
            {
                new PermissionGroup { Id = "group-a", Name = "Group A" },
                new PermissionGroup { Id = "group-b", Name = "Group B" }
            };
            _db.CompanyProfile.Save(profile);

            Assert.Equal("Group A", _db.Employees.GetById("emp-profile-1")?.PermissionGroup?.Name);
            Assert.Equal("Group B", _db.Employees.GetById("emp-profile-2")?.PermissionGroup?.Name);
        }

        [Fact]
        public void Employees_OnSaved_WhenPermissionGroupIdMatchesProfile_LinksPermissionGroup()
        {
            var profile = _db.CompanyProfile.Get();
            profile.PermissionGroups = new List<PermissionGroup>
            {
                new PermissionGroup { Id = "group-eng", Name = "Engineering" }
            };
            _db.CompanyProfile.Save(profile);

            var emp = new Employee { Id = "emp-eng-1", FirstName = "Engineer 1", PermissionGroupId = "group-eng" };
            _db.Employees.Save(emp);

            Assert.NotNull(emp.PermissionGroup);
            Assert.Equal("Engineering", emp.PermissionGroup.Name);
        }

        [Fact]
        public void Employees_OnSaved_WhenPermissionGroupIdCleared_SetsPermissionGroupToNull()
        {
            var profile = _db.CompanyProfile.Get();
            profile.PermissionGroups = new List<PermissionGroup>
            {
                new PermissionGroup { Id = "group-mkt", Name = "Marketing" }
            };
            _db.CompanyProfile.Save(profile);

            var emp = new Employee { Id = "emp-mkt-1", FirstName = "Marketer 1", PermissionGroupId = "group-mkt" };
            _db.Employees.Save(emp);
            Assert.NotNull(emp.PermissionGroup);

            emp.PermissionGroupId = null;
            _db.Employees.Save(emp);
            Assert.Null(emp.PermissionGroup);
        }

        #endregion

        #region Migrations - Push Subscriptions

        [Fact]
        public void RunOneTimeMigrations_MigratesPushSubscriptions_WithStrictSessionIdMatch()
        {
            var session = new DeviceSession { Id = "sess-strict", EmployeeId = "emp-strict" };
            _db.DeviceSessions.Save(session);

            var sub = new PushSubscription
            {
                Id = "sub-strict",
                SessionId = "sess-strict",
                UserId = "emp-strict",
                Endpoint = "https://push.example.com/strict",
                P256dh = "p256_strict",
                Auth = "auth_strict",
                SubscriptionType = "WebPush",
                PublicKey = "pub_strict",
                IsEnabled = true,
                IsIdleDetectionEnabled = false,
                DeviceType = "Mobile",
                DeviceName = "Strict Phone",
                UserAgent = "TestUA/1.0"
            };
            _db.PushSubscriptions.Save(sub);

            _db.RunOneTimeMigrations();

            Assert.Null(_db.PushSubscriptions.GetById("sub-strict"));
            var migratedSession = _db.DeviceSessions.GetById("sess-strict");
            Assert.NotNull(migratedSession);
            Assert.Equal("https://push.example.com/strict", migratedSession.PushEndpoint);
            Assert.Equal("p256_strict", migratedSession.PushP256dh);
            Assert.Equal("auth_strict", migratedSession.PushAuth);
            Assert.Equal("WebPush", migratedSession.PushSubscriptionType);
            Assert.Equal("pub_strict", migratedSession.PushPublicKey);
            Assert.True(migratedSession.PushEnabled);
            Assert.False(migratedSession.IsIdleDetectionEnabled);
            Assert.Equal("Mobile", migratedSession.DeviceType);
            Assert.Equal("Strict Phone", migratedSession.DeviceName);
            Assert.Equal("TestUA/1.0", migratedSession.PushUserAgent);
            Assert.True(migratedSession.HasPush);
        }

        [Fact]
        public void RunOneTimeMigrations_MigratesPushSubscriptions_WithFallbackDeviceIdMatch()
        {
            var session = new DeviceSession { Id = "sess-fallback-dev", EmployeeId = "emp-dev", DeviceId = "dev-unique-1", RevokedAt = null };
            _db.DeviceSessions.Save(session);

            var sub = new PushSubscription
            {
                Id = "sub-dev",
                SessionId = null,
                UserId = "emp-dev",
                DeviceId = "dev-unique-1",
                Endpoint = "https://push.example.com/device-match"
            };
            _db.PushSubscriptions.Save(sub);

            _db.RunOneTimeMigrations();

            Assert.Null(_db.PushSubscriptions.GetById("sub-dev"));
            var updated = _db.DeviceSessions.GetById("sess-fallback-dev");
            Assert.NotNull(updated);
            Assert.Equal("https://push.example.com/device-match", updated.PushEndpoint);
        }

        [Fact]
        public void RunOneTimeMigrations_MigratesPushSubscriptions_WithFallbackUserAgentMatch()
        {
            var session = new DeviceSession
            {
                Id = "sess-fallback-ua",
                EmployeeId = "emp-ua",
                DeviceInfo = "Mozilla/5.0 Chrome/122.0 Windows",
                RevokedAt = null
            };
            _db.DeviceSessions.Save(session);

            var sub = new PushSubscription
            {
                Id = "sub-ua",
                SessionId = null,
                DeviceId = null,
                UserId = "emp-ua",
                UserAgent = "Chrome/122.0",
                Endpoint = "https://push.example.com/ua-match"
            };
            _db.PushSubscriptions.Save(sub);

            _db.RunOneTimeMigrations();

            Assert.Null(_db.PushSubscriptions.GetById("sub-ua"));
            var updated = _db.DeviceSessions.GetById("sess-fallback-ua");
            Assert.NotNull(updated);
            Assert.Equal("https://push.example.com/ua-match", updated.PushEndpoint);
        }

        [Fact]
        public void RunOneTimeMigrations_MigratesPushSubscriptions_WhenSessionAlreadyHasPush_DoesNotOverwrite()
        {
            var session = new DeviceSession
            {
                Id = "sess-existing-push",
                EmployeeId = "emp-has-push",
                PushEndpoint = "https://original-push.example.com"
            };
            _db.DeviceSessions.Save(session);

            var sub = new PushSubscription
            {
                Id = "sub-has-push",
                SessionId = "sess-existing-push",
                UserId = "emp-has-push",
                Endpoint = "https://new-push.example.com"
            };
            _db.PushSubscriptions.Save(sub);

            _db.RunOneTimeMigrations();

            Assert.Null(_db.PushSubscriptions.GetById("sub-has-push"));
            var updated = _db.DeviceSessions.GetById("sess-existing-push");
            Assert.NotNull(updated);
            Assert.Equal("https://original-push.example.com", updated.PushEndpoint);
        }

        [Fact]
        public void RunOneTimeMigrations_MigratesPushSubscriptions_WhenOrphaned_DeletesSubscription()
        {
            var sub = new PushSubscription
            {
                Id = "sub-orphan",
                SessionId = "non-existent-session",
                UserId = "non-existent-user",
                Endpoint = "https://push.orphan.com"
            };
            _db.PushSubscriptions.Save(sub);

            _db.RunOneTimeMigrations();

            Assert.Null(_db.PushSubscriptions.GetById("sub-orphan"));
        }

        [Fact]
        public void RunOneTimeMigrations_WhenNoPushSubscriptions_CompletesWithoutError()
        {
            _db.RunOneTimeMigrations();
            Assert.Empty(_db.PushSubscriptions.GetAll());
        }

        #endregion

        #region Migrations - Stale Device Sessions

        [Fact]
        public void RunOneTimeMigrations_CleansUpStaleDeviceSessions_RevokesExpiredAndClearsPush()
        {
            var expiredSession = new DeviceSession
            {
                Id = "sess-expired",
                EmployeeId = "emp-exp",
                ExpiresAt = DateTime.UtcNow.AddHours(-1),
                RevokedAt = null,
                PushEndpoint = "https://push.expired.com",
                PushEnabled = true
            };
            _db.DeviceSessions.Save(expiredSession);

            _db.RunOneTimeMigrations();

            var updated = _db.DeviceSessions.GetById("sess-expired");
            Assert.NotNull(updated);
            Assert.NotNull(updated.RevokedAt);
            Assert.Null(updated.PushEndpoint);
            Assert.False(updated.PushEnabled);
        }

        [Fact]
        public void RunOneTimeMigrations_CleansUpStaleDeviceSessions_PreservesActiveAndAlreadyRevokedSessions()
        {
            var activeSession = new DeviceSession
            {
                Id = "sess-active",
                EmployeeId = "emp-act",
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                RevokedAt = null,
                PushEndpoint = "https://push.active.com",
                PushEnabled = true
            };
            var pastRevoked = DateTime.UtcNow.AddDays(-3);
            var alreadyRevoked = new DeviceSession
            {
                Id = "sess-revoked",
                EmployeeId = "emp-rev",
                ExpiresAt = DateTime.UtcNow.AddDays(-10),
                RevokedAt = pastRevoked
            };
            _db.DeviceSessions.Save(activeSession);
            _db.DeviceSessions.Save(alreadyRevoked);

            _db.RunOneTimeMigrations();

            var updatedActive = _db.DeviceSessions.GetById("sess-active");
            var updatedRevoked = _db.DeviceSessions.GetById("sess-revoked");

            Assert.NotNull(updatedActive);
            Assert.Null(updatedActive.RevokedAt);
            Assert.Equal("https://push.active.com", updatedActive.PushEndpoint);

            Assert.NotNull(updatedRevoked);
            Assert.Equal(pastRevoked, updatedRevoked.RevokedAt);
        }

        #endregion

        #region Migrations - Email Data

        [Fact]
        public void RunOneTimeMigrations_MigratesEmailData_MovesFoldersMessagesAndBodies_AndReloadsRepositories()
        {
            var oldFoldersDir = Path.Combine(_testDataPath, "EmailFolders");
            var oldMessagesDir = Path.Combine(_testDataPath, "EmailMessages");
            var oldEmailsDir = Path.Combine(_testDataPath, "Emails");

            Directory.CreateDirectory(oldFoldersDir);
            Directory.CreateDirectory(oldMessagesDir);
            Directory.CreateDirectory(oldEmailsDir);

            var folder = new EmailFolder { Id = "fld-mig-1", EmployeeId = "emp-mig", Name = "Inbox", Path = "INBOX" };
            var message = new EmailMessage { Id = "msg-mig-1", EmployeeId = "emp-mig", UniqueId = 101, Subject = "Migrated Email", FolderPath = "INBOX" };

            File.WriteAllText(Path.Combine(oldFoldersDir, "fld-mig-1.json"), JsonSerializer.Serialize(folder));
            File.WriteAllText(Path.Combine(oldMessagesDir, "msg-mig-1.json"), JsonSerializer.Serialize(message));
            File.WriteAllText(Path.Combine(oldEmailsDir, "msg-mig-1.html"), "<p>Migrated body</p>");

            _db.RunOneTimeMigrations();

            // Verify files moved to employee-centric folders
            var expectedFolderFile = Path.Combine(_testDataPath, "Employees", "emp-mig", "Email", "Folders", "fld-mig-1.json");
            var expectedMessageFile = Path.Combine(_testDataPath, "Employees", "emp-mig", "Email", "Messages", "msg-mig-1.json");
            var expectedBodyFile = Path.Combine(_testDataPath, "Employees", "emp-mig", "Email", "Bodies", "msg-mig-1.html");

            Assert.True(File.Exists(expectedFolderFile));
            Assert.True(File.Exists(expectedMessageFile));
            Assert.True(File.Exists(expectedBodyFile));

            // Verify original files removed
            Assert.False(File.Exists(Path.Combine(oldFoldersDir, "fld-mig-1.json")));
            Assert.False(File.Exists(Path.Combine(oldMessagesDir, "msg-mig-1.json")));
            Assert.False(File.Exists(Path.Combine(oldEmailsDir, "msg-mig-1.html")));

            // Verify repositories reloaded from disk and return the migrated records
            Assert.NotNull(_db.EmailFolders.GetById("fld-mig-1"));
            Assert.NotNull(_db.EmailMessages.GetByIdOrLoad("msg-mig-1", "emp-mig"));
            Assert.True(_db.EmailMessages.Exists("emp-mig", "INBOX", 101));
        }

        [Fact]
        public void RunOneTimeMigrations_MigratesEmailData_WhenCorruptFilesEncountered_ContinuesMigration()
        {
            var oldFoldersDir = Path.Combine(_testDataPath, "EmailFolders");
            var oldMessagesDir = Path.Combine(_testDataPath, "EmailMessages");
            Directory.CreateDirectory(oldFoldersDir);
            Directory.CreateDirectory(oldMessagesDir);

            // Write corrupted json files
            File.WriteAllText(Path.Combine(oldFoldersDir, "corrupt.json"), "{ invalid json");
            File.WriteAllText(Path.Combine(oldMessagesDir, "corrupt.json"), "{ invalid json");

            // Also valid file to ensure it proceeds
            var folder = new EmailFolder { Id = "fld-valid", EmployeeId = "emp-valid", Name = "ValidFolder" };
            File.WriteAllText(Path.Combine(oldFoldersDir, "fld-valid.json"), JsonSerializer.Serialize(folder));

            _db.RunOneTimeMigrations();

            Assert.True(File.Exists(Path.Combine(_testDataPath, "Employees", "emp-valid", "Email", "Folders", "fld-valid.json")));
        }

        #endregion

        #region RestoreBackup

        [Fact]
        public void RestoreBackup_WhenFileDoesNotExist_ThrowsFileNotFoundException()
        {
            var nonExistentPath = Path.Combine(_testDataPath, "non_existent_backup.zip");
            Assert.Throws<FileNotFoundException>(() => _db.RestoreBackup(nonExistentPath));
        }

        [Fact]
        public void RestoreBackup_WhenFileIsNotValidZip_ThrowsException()
        {
            var corruptPath = Path.Combine(_testDataPath, "corrupt.zip");
            File.WriteAllText(corruptPath, "this is plain text, not a zip file");

            var ex = Assert.Throws<Exception>(() => _db.RestoreBackup(corruptPath));
            Assert.Equal("Invalid backup file format.", ex.Message);
        }

        [Fact]
        public void RestoreBackup_WhenZipContainsPathTraversal_ThrowsIOException()
        {
            var maliciousZipPath = Path.Combine(_testDataPath, "zipslip.zip");
            using (var stream = new FileStream(maliciousZipPath, FileMode.Create))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped_file.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("malicious payload");
            }

            var ex = Assert.Throws<IOException>(() => _db.RestoreBackup(maliciousZipPath));
            Assert.Contains("outside of the target directory", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RestoreBackup_HappyPath_ExtractsFiles_ClearsOldDataExceptBackupsAndTmp_AndReloads()
        {
            // 1. Initial database state
            var proj = new Project { Id = "old-proj", Name = "Old Project" };
            _db.Projects.Save(proj);
            _serviceProvider.GetRequiredService<DiskPersistenceService>().FlushAll();

            var legacyFile = Path.Combine(_testDataPath, "legacy.txt");
            File.WriteAllText(legacyFile, "legacy file");
            var tmpFile = Path.Combine(_testDataPath, "temporary.tmp");
            File.WriteAllText(tmpFile, "temporary file");

            var backupsDir = Path.Combine(_testDataPath, "Backups");
            Directory.CreateDirectory(backupsDir);
            var safeBackupFile = Path.Combine(backupsDir, "safe_backup.zip");
            File.WriteAllText(safeBackupFile, "safe backup content");

            // 2. Prepare backup zip
            var stagingDir = Path.Combine(Path.GetTempPath(), "Spokes_Restore_Staging_" + Guid.NewGuid());
            try
            {
                var restoredProjDir = Path.Combine(stagingDir, "Projects", "new-restored-proj");
                Directory.CreateDirectory(restoredProjDir);
                var newProj = new Project { Id = "new-restored-proj", Name = "Restored Project" };
                File.WriteAllText(Path.Combine(restoredProjDir, "project.json"), JsonSerializer.Serialize(newProj));

                var backupZipPath = Path.Combine(backupsDir, "restore_test.zip");
                ZipFile.CreateFromDirectory(stagingDir, backupZipPath);

                var result = _db.RestoreBackup(backupZipPath, Mock.Of<ILogger>());

                // 4. Verify
                Assert.False(result); // Casdoor not built-in, so returns false
                Assert.False(File.Exists(legacyFile));
                Assert.True(File.Exists(tmpFile));
                Assert.True(File.Exists(safeBackupFile));
                Assert.Null(_db.Projects.GetById("old-proj"));
                var reloadedProj = _db.Projects.GetById("new-restored-proj");
                Assert.NotNull(reloadedProj);
                Assert.Equal("Restored Project", reloadedProj.Name);
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
        public void RestoreBackup_WhenProviderTypeIsBuiltInCasdoor_AttemptsCasdoorRestartGracefully()
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
                File.WriteAllText(Path.Combine(settingsDir, "system_config.json"), JsonSerializer.Serialize(sysConfig));

                var backupZipPath = Path.Combine(_testDataPath, "casdoor_backup.zip");
                ZipFile.CreateFromDirectory(stagingDir, backupZipPath);

                var result = _db.RestoreBackup(backupZipPath, Mock.Of<ILogger>());

                // Supervisorctl is not present on Windows test runner; Process.Start fails and is handled gracefully
                Assert.False(result);
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

        #endregion
    }
}
