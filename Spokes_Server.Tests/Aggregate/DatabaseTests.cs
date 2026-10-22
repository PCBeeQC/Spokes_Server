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

namespace Spokes_Server.Tests.Aggregate;

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
        services.AddSingleton<EncryptionService>();

        // Register all repositories and Database
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        var coordinator = _serviceProvider.GetRequiredService<DatabaseRelationshipCoordinator>();
        coordinator.RegisterSubscriptions();
    }

    private void RunMigrations()
    {
        var config = _serviceProvider.GetRequiredService<IConfiguration>();
        var emailStep = new Spokes_Server.Core.Services.Migrations.EmailDataMigrationStep(_db.CommunicationContext, config);
        var pushStep = new Spokes_Server.Core.Services.Migrations.PushDeviceSessionMigrationStep(_db.DeviceSessions, config);
        var runner = new Spokes_Server.Core.Services.Migrations.MigrationRunnerService(new Spokes_Server.Core.Services.Migrations.IMigrationStep[] { emailStep, pushStep }, config);
        runner.RunPendingMigrationsAsync().GetAwaiter().GetResult();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    #region Constructors & DI Resolution

    [Fact]
    public void TestDatabaseFactory_CreatesMockWithRequestedRepositories()
    {
        var persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "DataPath", _testDataPath } }).Build();
        var sessions = new DeviceSessionRepository(persistence, config);
        var employees = new EmployeeRepository(persistence, config);

        var lightweightDb = Spokes_Server.Tests.Helpers.TestDatabaseFactory.Create(sessions, employees);

        Assert.Same(sessions, lightweightDb.DeviceSessions);
        Assert.Same(employees, lightweightDb.Employees);
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
        Assert.NotNull(_db.PurchaseOrders);
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

        Assert.NotNull(_db.ProjectsContext);
        Assert.NotNull(_db.HrContext);
        Assert.NotNull(_db.AccountingContext);
        Assert.NotNull(_db.CommunicationContext);
        Assert.NotNull(_db.CoreContext);
    }

    [Fact]
    public void Resolution_Succeeds_DomainContextsAreDelegatedProperly()
    {
        Assert.Same(_db.Projects, _db.ProjectsContext.Projects);
        Assert.Same(_db.Employees, _db.HrContext.Employees);
        Assert.Same(_db.Invoices, _db.AccountingContext.Invoices);
        Assert.Same(_db.PurchaseOrders, _db.AccountingContext.PurchaseOrders);
        Assert.Same(_db.ChatChannels, _db.CommunicationContext.ChatChannels);
        Assert.Same(_db.CompanyProfile, _db.CoreContext.CompanyProfile);
        Assert.Same(_db.StandardDocuments, _db.CoreContext.StandardDocuments);
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
    public void Timesheets_OnSaved_DoesNotTriggerProjectUpdate()
    {
        var originalTime1 = DateTime.UtcNow.AddDays(-5);
        var originalTime2 = DateTime.UtcNow.AddDays(-4);
        var proj1 = new Project { Id = "proj-ts-1", Name = "TS Project 1", LastEdited = originalTime1 };
        var proj2 = new Project { Id = "proj-ts-2", Name = "TS Project 2", LastEdited = originalTime2 };
        _db.Projects.Save(proj1);
        _db.Projects.Save(proj2);
        proj1.LastEdited = originalTime1;
        proj2.LastEdited = originalTime2;

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
        Assert.Equal(originalTime1, updated1.LastEdited);
        Assert.Equal(originalTime2, updated2.LastEdited);
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

    private void WriteLegacyPushSubscription(
        string id,
        string? sessionId,
        string userId,
        string endpoint,
        string p256dh = "p256",
        string auth = "auth",
        string? deviceId = null,
        string? deviceType = null,
        string? deviceName = null,
        string? userAgent = null,
        string? publicKey = null,
        bool isEnabled = true,
        bool isIdleDetectionEnabled = false,
        string subscriptionType = "WebPush")
    {
        var pushDir = Path.Combine(_testDataPath, "PushSubscriptions");
        Directory.CreateDirectory(pushDir);
        var file = Path.Combine(pushDir, $"{id}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            Id = id,
            SessionId = sessionId,
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            DeviceId = deviceId,
            DeviceType = deviceType,
            DeviceName = deviceName,
            UserAgent = userAgent,
            PublicKey = publicKey,
            IsEnabled = isEnabled,
            IsIdleDetectionEnabled = isIdleDetectionEnabled,
            SubscriptionType = subscriptionType,
            CreatedAt = DateTime.UtcNow
        }));
    }

    [Fact]
    public void RunOneTimeMigrations_MigratesPushSubscriptions_WithStrictSessionIdMatch()
    {
        var session = new DeviceSession { Id = "sess-strict", EmployeeId = "emp-strict" };
        _db.DeviceSessions.Save(session);

        WriteLegacyPushSubscription(
            id: "sub-strict",
            sessionId: "sess-strict",
            userId: "emp-strict",
            endpoint: "https://push.example.com/strict",
            p256dh: "p256_strict",
            auth: "auth_strict",
            subscriptionType: "WebPush",
            publicKey: "pub_strict",
            isEnabled: true,
            isIdleDetectionEnabled: false,
            deviceType: "Mobile",
            deviceName: "Strict Phone",
            userAgent: "TestUA/1.0");

        RunMigrations();

        Assert.False(File.Exists(Path.Combine(_testDataPath, "PushSubscriptions", "sub-strict.json")));
        var migratedSession = _db.DeviceSessions.GetById("sess-strict");
        Assert.NotNull(migratedSession);
        Assert.Equal("https://push.example.com/strict", migratedSession.PushEndpoint);
        Assert.Equal("p256_strict", migratedSession.PushP256dh);
        Assert.Equal("auth_strict", migratedSession.PushAuth);
        Assert.Equal(PushSubscriptionType.WebPush, migratedSession.PushSubscriptionType);
        Assert.Equal("pub_strict", migratedSession.PushPublicKey);
        Assert.True(migratedSession.PushEnabled);
        Assert.False(migratedSession.IsIdleDetectionEnabled);
        Assert.Equal(DeviceType.Mobile, migratedSession.DeviceType);
        Assert.Equal("Strict Phone", migratedSession.DeviceName);
        Assert.Equal("TestUA/1.0", migratedSession.PushUserAgent);
        Assert.True(migratedSession.HasPush);
    }

    [Fact]
    public void RunOneTimeMigrations_MigratesPushSubscriptions_WithFallbackDeviceIdMatch()
    {
        var session = new DeviceSession { Id = "sess-fallback-dev", EmployeeId = "emp-dev", DeviceId = "dev-unique-1", RevokedAt = null };
        _db.DeviceSessions.Save(session);

        WriteLegacyPushSubscription(
            id: "sub-dev",
            sessionId: null,
            userId: "emp-dev",
            endpoint: "https://push.example.com/device-match",
            deviceId: "dev-unique-1");

        RunMigrations();

        Assert.False(File.Exists(Path.Combine(_testDataPath, "PushSubscriptions", "sub-dev.json")));
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

        WriteLegacyPushSubscription(
            id: "sub-ua",
            sessionId: null,
            userId: "emp-ua",
            endpoint: "https://push.example.com/ua-match",
            userAgent: "Chrome/122.0");

        RunMigrations();

        Assert.False(File.Exists(Path.Combine(_testDataPath, "PushSubscriptions", "sub-ua.json")));
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

        WriteLegacyPushSubscription(
            id: "sub-has-push",
            sessionId: "sess-existing-push",
            userId: "emp-has-push",
            endpoint: "https://new-push.example.com");

        RunMigrations();

        Assert.False(File.Exists(Path.Combine(_testDataPath, "PushSubscriptions", "sub-has-push.json")));
        var updated = _db.DeviceSessions.GetById("sess-existing-push");
        Assert.NotNull(updated);
        Assert.Equal("https://original-push.example.com", updated.PushEndpoint);
    }

    [Fact]
    public void RunOneTimeMigrations_MigratesPushSubscriptions_WhenOrphaned_DeletesSubscription()
    {
        WriteLegacyPushSubscription(
            id: "sub-orphan",
            sessionId: "non-existent-session",
            userId: "non-existent-user",
            endpoint: "https://push.orphan.com");

        RunMigrations();

        Assert.False(File.Exists(Path.Combine(_testDataPath, "PushSubscriptions", "sub-orphan.json")));
    }

    [Fact]
    public void RunOneTimeMigrations_WhenNoPushSubscriptions_CompletesWithoutError()
    {
        RunMigrations();
        Assert.False(Directory.Exists(Path.Combine(_testDataPath, "PushSubscriptions")));
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

        RunMigrations();

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

        RunMigrations();

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

        RunMigrations();

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
        Assert.NotNull(_db.EmailMessages.GetByIdOrLoad("emp-mig", "msg-mig-1"));
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

        RunMigrations();

        Assert.True(File.Exists(Path.Combine(_testDataPath, "Employees", "emp-valid", "Email", "Folders", "fld-valid.json")));
    }

    #endregion

    #region Bounded Context Direct Consumer Injection Tests

    [Fact]
    public void BoundedContexts_CanBeInjectedDirectlyIntoDomainServices()
    {
        // 1. HRService directly injects HrContext
        var hrService = new Spokes_Server.Core.Services.HR.HRService(_db.HrContext);
        Assert.NotNull(hrService);

        // 2. TimesheetReportService directly injects HrContext, ProjectsContext, CoreContext
        var reportService = new Spokes_Server.Core.Services.Reports.TimesheetReportService(_db.HrContext, _db.ProjectsContext, _db.CoreContext);
        Assert.NotNull(reportService);

        // 3. SessionService directly injects CoreContext and HrContext
        var sessionService = new Spokes_Server.Core.Services.Security.SessionService(_db.CoreContext, _db.HrContext);
        Assert.NotNull(sessionService);

        // 4. AppLabelService directly injects CoreContext
        var labelService = new Spokes_Server.Core.Services.UI.AppLabelService(_db.CoreContext);
        Assert.NotNull(labelService.Team);

        // 5. PermissionAuthorizationHandler directly injects HrContext
        var authHandler = new Spokes_Server.Core.Security.PermissionAuthorizationHandler(_db.HrContext);
        Assert.NotNull(authHandler);

        // 6. InvoicePaymentMigrationStep directly injects AccountingContext
        var paymentStep = new Spokes_Server.Core.Services.Migrations.InvoicePaymentMigrationStep(_db.AccountingContext);
        Assert.Equal(45, paymentStep.Order);

        // 7. TelemetryBackgroundService directly injects CoreContext and HrContext
        var telemetry = new Spokes_Server.Core.Services.Licensing.TelemetryBackgroundService(
            _db.CoreContext,
            _db.HrContext,
            Mock.Of<System.Net.Http.IHttpClientFactory>(),
            Mock.Of<ILogger<Spokes_Server.Core.Services.Licensing.TelemetryBackgroundService>>());
        Assert.NotNull(telemetry);
    }

    #endregion
}
