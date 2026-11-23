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
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
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
        var pushStep = new Spokes_Server.Core.Services.Migrations.PushDeviceSessionMigrationStep(_db.DeviceSessions, config);
        var runner = new Spokes_Server.Core.Services.Migrations.MigrationRunnerService(new Spokes_Server.Core.Services.Migrations.IMigrationStep[] { pushStep }, config);
        runner.RunPendingMigrationsAsync().GetAwaiter().GetResult();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    #region Constructors & DI Resolution

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
        Assert.NotNull(_db.TermsAndConditions);
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
        Assert.Same(_db.TermsAndConditions, _db.CoreContext.TermsAndConditions);
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
        Assert.Single(reloadedEmp.PermissionGroups);
        Assert.Equal("Administrators", reloadedEmp.PermissionGroups[0].Name);
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

        Assert.Empty(_db.Employees.GetById("emp-unknown")!.PermissionGroups);
        Assert.Empty(_db.Employees.GetById("emp-nogroup")!.PermissionGroups);
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

}
