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
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.IO.Compression;

namespace Spokes_Server.Aggregate;

/// <summary>
/// Authoritative database aggregator implementing the Unit of Work and Central Bootstrapping pattern.
/// NOTE: This monolithic hub aggregates all repositories to coordinate the boot sequence (loading all repositories
/// into RAM in parallel) and simplify dependency injection across controllers, services, and Razor components,
/// preventing constructor signature bloat (injecting a single Database instance instead of 8+ repositories).
/// Under C# ASP.NET Core, compile-time strong-typing of repository properties (e.g., db.Projects) is highly preferred 
/// over resolving repositories dynamically via IServiceProvider or dynamic collections (e.g., IEnumerable<IJsonRepository>).
/// The explicit coupling ensures full type-safety, navigation ease, and compile-time verification across all Razor pages
/// and controllers, which outweighs the boilerplate of this single bootstrap coordinator file.
/// </summary>
public class Database
{
    private readonly string _dataPath;
    private readonly DiskPersistenceService _persistence;

    public virtual ProjectRepository Projects { get; }
    public virtual ProjectGroupRepository ProjectGroups { get; }
    public virtual EmployeeRepository Employees { get; }
    public virtual TimesheetRepository Timesheets { get; }
    public virtual WorkTypeRepository WorkTypes { get; }
    public virtual RateCardRepository RateCards { get; }
    public virtual QuoteRepository Quotes { get; }
    public virtual PurchaseOrderRepository Purchases { get; }
    public virtual SupplierRepository Suppliers { get; }
    public virtual InvoiceRepository Invoices { get; }
    public virtual CommissionLedgerRecordRepository CommissionLedgerRecords { get; }
    public virtual CompanyProfileRepository CompanyProfile { get; }
    public virtual DocumentTemplateRepository DocumentTemplates { get; }
    public virtual ExpenseReportRepository ExpenseReports { get; }
    public virtual BillRepository Bills { get; }
    public virtual ProjectNoteRepository ProjectNotes { get; }
    public virtual WeeklyPlanRepository WeeklyPlans { get; }
    public virtual TeamRepository Teams { get; }
    public virtual CalendarEventRepository CalendarEvents { get; }

    // Chat Feature
    public virtual ChatChannelRepository ChatChannels { get; }
    public virtual ChatCategoryRepository ChatCategories { get; }
    public virtual ChatMessageRepository ChatMessages { get; }
    public virtual ChatReadStateRepository ChatReadStates { get; }
    public virtual ReportedMessageRepository ReportedMessages { get; }
    public virtual PushSubscriptionRepository PushSubscriptions { get; }
    public virtual AlbumRepository Albums { get; }

    // Boards Feature
    public virtual BoardRepository Boards { get; }
    public virtual BoardCardRepository BoardCards { get; }
    public virtual BoardTemplateRepository BoardTemplates { get; }

    // Standard Documents Feature
    public virtual StandardDocumentRepository StandardDocuments { get; }
    public virtual ProjectDocumentRepository ProjectDocuments { get; }

    // Identity Feature
    public virtual OpenIdAccountRepository OpenIdAccounts { get; }
    public virtual ServerConfigRepository ServerConfigs { get; }
    public virtual SystemConfigRepository SystemConfigs { get; }
    public virtual DeviceSessionRepository DeviceSessions { get; }
    public virtual DemoConfigRepository DemoConfigs { get; }

    // Email Client Feature
    public virtual EmailFolderRepository EmailFolders { get; }
    public virtual EmailMessageRepository EmailMessages { get; }

    // Business Expenses Feature
    public virtual EmployerContributionRepository EmployerContributions { get; }
    public virtual RecurringExpenseRepository RecurringExpenses { get; }

    // Contacts Feature
    public virtual PublicContactRepository PublicContacts { get; }
    public virtual PrivateContactRepository PrivateContacts { get; }


    /// <summary>
    /// Lightweight constructor for unit tests focusing on identity and sessions.
    /// </summary>
    public Database(DeviceSessionRepository deviceSessions, EmployeeRepository employees)
    {
        _dataPath = "Data";
        _persistence = null!;
        DeviceSessions = deviceSessions;
        Employees = employees;
    }

    public Database(
        DiskPersistenceService persistence,
        ProjectRepository projects,
        ProjectGroupRepository projectGroups,
        EmployeeRepository employees,
        TimesheetRepository timesheets,
        WorkTypeRepository workTypes,
        RateCardRepository rateCards,
        QuoteRepository quotes,
        PurchaseOrderRepository purchases,
        SupplierRepository suppliers,
        InvoiceRepository invoices,
        CompanyProfileRepository companyProfile,
        DocumentTemplateRepository documentTemplates,
        ExpenseReportRepository expenseReports,
        BillRepository bills,
        ProjectNoteRepository projectNotes,
        WeeklyPlanRepository weeklyPlans,
        TeamRepository teams,
        ChatChannelRepository chatChannels,
        ChatCategoryRepository chatCategories,
        ChatMessageRepository chatMessages,
        ChatReadStateRepository chatReadStates,
        ReportedMessageRepository reportedMessages,
        PushSubscriptionRepository pushSubscriptions,
        AlbumRepository albums,
        BoardRepository boards,
        BoardCardRepository boardCards,
        BoardTemplateRepository boardTemplates,
        CalendarEventRepository calendarEvents,
        IConfiguration config,
        OpenIdAccountRepository openIdAccounts,
        EmployerContributionRepository employerContributions,
        RecurringExpenseRepository recurringExpenses,
        EmailFolderRepository emailFolders,
        EmailMessageRepository emailMessages,
        PublicContactRepository publicContacts,
        PrivateContactRepository privateContacts,
        StandardDocumentRepository standardDocuments,
        ProjectDocumentRepository projectDocuments,
        CommissionLedgerRecordRepository commissionLedgerRecords,
        SystemConfigRepository systemConfigs,
        ServerConfigRepository serverConfigs,
        DeviceSessionRepository deviceSessions,
        DemoConfigRepository demoConfigs)
    {
        _persistence = persistence;
        _dataPath = config["DataPath"] ?? "Data";
        DeviceSessions = deviceSessions;
        SystemConfigs = systemConfigs;
        ServerConfigs = serverConfigs;
        DemoConfigs = demoConfigs;
        OpenIdAccounts = openIdAccounts;
        EmployerContributions = employerContributions;
        RecurringExpenses = recurringExpenses;
        EmailFolders = emailFolders;
        EmailMessages = emailMessages;
        PublicContacts = publicContacts;
        PrivateContacts = privateContacts;
        StandardDocuments = standardDocuments;
        ProjectDocuments = projectDocuments;
        Projects = projects;
        ProjectGroups = projectGroups;
        Employees = employees;
        Timesheets = timesheets;
        WorkTypes = workTypes;
        RateCards = rateCards;
        Quotes = quotes;
        Purchases = purchases;
        Suppliers = suppliers;
        Invoices = invoices;
        CommissionLedgerRecords = commissionLedgerRecords;
        CompanyProfile = companyProfile;
        DocumentTemplates = documentTemplates;
        ExpenseReports = expenseReports;
        Bills = bills;
        ProjectNotes = projectNotes;
        WeeklyPlans = weeklyPlans;
        Teams = teams;
        ChatChannels = chatChannels;
        ChatCategories = chatCategories;
        ChatMessages = chatMessages;
        ChatReadStates = chatReadStates;
        ReportedMessages = reportedMessages;
        PushSubscriptions = pushSubscriptions;
        Albums = albums;
        Boards = boards;
        BoardCards = boardCards;
        BoardTemplates = boardTemplates;
        CalendarEvents = calendarEvents;

        // Auto-update Project LastEdited
        void TriggerProjectUpdate(string? projectId)
        {
            if (!string.IsNullOrEmpty(projectId))
            {
                var project = Projects.GetById(projectId);
                if (project != null) Projects.Save(project);
            }
        }

        Quotes.OnSaved += q => TriggerProjectUpdate(q.ProjectId);
        Invoices.OnSaved += i => TriggerProjectUpdate(i.ProjectId);
        ProjectNotes.OnSaved += n => TriggerProjectUpdate(n.ProjectId);
        ProjectDocuments.OnSaved += d => TriggerProjectUpdate(d.ProjectId);
        Timesheets.OnSaved += t => 
        {
            var projectIds = t.Entries.Select(e => e.ProjectId).Distinct();
            foreach (var pid in projectIds)
            {
                TriggerProjectUpdate(pid);
            }
        };

        // Maintain Dynamic Permission Group References
        CompanyProfile.OnSaved += profile => 
        {
            foreach (var employee in Employees.GetAll())
            {
                if (!string.IsNullOrEmpty(employee.PermissionGroupId))
                {
                    employee.PermissionGroup = profile.PermissionGroups.FirstOrDefault(g => g.Id == employee.PermissionGroupId);
                }
            }
        };

        Employees.OnSaved += employee => 
        {
            if (!string.IsNullOrEmpty(employee.PermissionGroupId))
            {
                var profile = CompanyProfile.Get();
                if (profile != null)
                {
                    employee.PermissionGroup = profile.PermissionGroups.FirstOrDefault(g => g.Id == employee.PermissionGroupId);
                }
            }
            else
            {
                employee.PermissionGroup = null;
            }
        };
    }

    public void Initialize()
    {
        // Boot Sequence: Load everything into RAM
        Parallel.Invoke(
            () => Projects.LoadFromDisk(),
            () => ProjectGroups.LoadFromDisk(),
            () => Employees.LoadFromDisk(),
            () => Timesheets.LoadFromDisk(),
            () => WorkTypes.LoadFromDisk(),
            () => RateCards.LoadFromDisk(),
            () => Quotes.LoadFromDisk(),
            () => Purchases.LoadFromDisk(),
            () => Suppliers.LoadFromDisk(),
            () => Invoices.LoadFromDisk(),
            () => CompanyProfile.LoadFromDisk(),
            () => DocumentTemplates.LoadFromDisk(),
            () => ExpenseReports.LoadFromDisk(),
            () => Bills.LoadFromDisk(),
            () => ProjectNotes.LoadFromDisk(),
            () => WeeklyPlans.LoadFromDisk(),
            () => Teams.LoadFromDisk(),
            () => ChatChannels.LoadFromDisk(),
            () => ChatCategories.LoadFromDisk(),
            () => ChatMessages.LoadFromDisk(),
            () => ChatReadStates.LoadFromDisk(),
            () => ReportedMessages.LoadFromDisk(),
            () => PushSubscriptions.LoadFromDisk(),
            () => Albums.LoadFromDisk(),
            () => Boards.LoadFromDisk(),
            () => BoardCards.LoadFromDisk(),
            () => BoardTemplates.LoadFromDisk(),
            () => CalendarEvents.LoadFromDisk(),
            () => OpenIdAccounts.LoadFromDisk(),
            () => EmployerContributions.LoadFromDisk(),
            () => RecurringExpenses.LoadFromDisk(),
            () => EmailFolders.LoadFromDisk(),
            () => EmailMessages.LoadFromDisk(),
            () => PublicContacts.LoadFromDisk(),
            () => PrivateContacts.LoadFromDisk(),
            () => StandardDocuments.LoadFromDisk(),
            () => ProjectDocuments.LoadFromDisk(),
            () => CommissionLedgerRecords.LoadFromDisk(),
            () => SystemConfigs.LoadFromDisk(),
            () => ServerConfigs.LoadFromDisk(),
            () => DeviceSessions.LoadFromDisk(),
            () => DemoConfigs.LoadFromDisk()
        );

        // Ensure default General channel exists
        ChatChannels.GetOrCreateDefaultGeneral();

        // Ensure default Categories exist
        ChatCategories.EnsureDefaultCategories();

        // Bootstrapping: Link dynamic permission groups for employees
        var profile = CompanyProfile.Get();
        if (profile != null)
        {
            foreach (var employee in Employees.GetAll())
            {
                if (!string.IsNullOrEmpty(employee.PermissionGroupId))
                {
                    employee.PermissionGroup = profile.PermissionGroups.FirstOrDefault(g => g.Id == employee.PermissionGroupId);
                }
            }
        }
    }

    public void RunOneTimeMigrations()
    {
        // Migrate Email Data to Employee-Centric structure
        MigrateEmailData();

        // RELOAD Email Caches after migration (so they pick up the moved files immediately)
        EmailFolders.LoadFromDisk();
        EmailMessages.LoadFromDisk();

        // Migrate PushSubscription data into DeviceSession (unified device model)
        MigratePushSubscriptionsToDeviceSessions();

        // Auto-revoke expired device sessions and clean dead push endpoints
        CleanupStaleDeviceSessions();
    }

    /// <summary>
    /// One-time migration: Merge all PushSubscription records into their corresponding DeviceSession.
    /// After migration, the PushSubscription store is empty and all push data lives on DeviceSession.
    /// Idempotent: if no PushSubscriptions exist, this is a no-op.
    /// </summary>
    private void MigratePushSubscriptionsToDeviceSessions()
    {
        var allPushSubs = PushSubscriptions.GetAll();
        if (!allPushSubs.Any()) return;

        Console.WriteLine($"[Migration] Merging {allPushSubs.Count} PushSubscriptions into DeviceSessions...");
        int merged = 0, orphaned = 0;

        foreach (var sub in allPushSubs)
        {
            DeviceSession? session = null;

            // Try strict match: SessionId
            if (!string.IsNullOrEmpty(sub.SessionId))
                session = DeviceSessions.GetById(sub.SessionId);

            // Fallback: DeviceId + UserId
            if (session == null && !string.IsNullOrEmpty(sub.DeviceId))
                session = DeviceSessions.GetByEmployeeId(sub.UserId)
                    .FirstOrDefault(s => s.DeviceId == sub.DeviceId && s.RevokedAt == null);

            // Fallback: Fuzzy match on UserAgent (for legacy subscriptions without SessionId/DeviceId)
            if (session == null && !string.IsNullOrEmpty(sub.UserAgent))
            {
                var userSessions = DeviceSessions.GetAll()
                    .Where(s => s.EmployeeId == sub.UserId && s.RevokedAt == null && !s.HasPush)
                    .ToList();
                var ua = sub.UserAgent;
                session = userSessions.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.DeviceInfo) &&
                    (s.DeviceInfo.Contains(ua, StringComparison.OrdinalIgnoreCase) ||
                     ua.Contains(s.DeviceInfo, StringComparison.OrdinalIgnoreCase)));
            }

            if (session != null)
            {
                // Only stamp push data if this session doesn't already have push data
                // (prevents overwriting if migration ran partially before)
                if (!session.HasPush)
                {
                    session.PushEndpoint = sub.Endpoint;
                    session.PushP256dh = sub.P256dh;
                    session.PushAuth = sub.Auth;
                    session.PushSubscriptionType = sub.SubscriptionType;
                    session.PushPublicKey = sub.PublicKey;
                    session.PushEnabled = sub.IsEnabled;
                    session.IsIdleDetectionEnabled = sub.IsIdleDetectionEnabled;
                    session.PushSubscribedAt = sub.CreatedAt;
                    session.PushUserAgent = sub.UserAgent;
                    session.DeviceType = sub.DeviceType;
                    if (!string.IsNullOrEmpty(sub.DeviceName))
                        session.DeviceName = sub.DeviceName;
                    DeviceSessions.Save(session);
                }
                merged++;
            }
            else
            {
                var endpointPreview = sub.Endpoint.Length > 30 ? sub.Endpoint[..30] + "..." : sub.Endpoint;
                Console.WriteLine($"[Migration] Orphaned PushSubscription {sub.Id} (user={sub.UserId}, endpoint={endpointPreview})");
                orphaned++;
            }

            // Delete the old PushSubscription file
            PushSubscriptions.Delete(sub.Id);
        }

        Console.WriteLine($"[Migration] Complete: {merged} merged, {orphaned} orphaned.");
    }

    /// <summary>
    /// Auto-revoke device sessions that have passed their ExpiresAt date.
    /// Also clears any push data on those expired sessions.
    /// </summary>
    private void CleanupStaleDeviceSessions()
    {
        var expiredSessions = DeviceSessions.GetAll()
            .Where(s => s.RevokedAt == null && s.ExpiresAt < DateTime.UtcNow)
            .ToList();

        if (expiredSessions.Any())
        {
            Console.WriteLine($"[Cleanup] Revoking {expiredSessions.Count} expired device sessions...");
            foreach (var session in expiredSessions)
            {
                session.RevokedAt = DateTime.UtcNow;
                if (session.HasPush)
                {
                    session.PushEndpoint = null;
                    session.PushEnabled = false;
                }
                DeviceSessions.Save(session);
            }
        }
    }

    private void MigrateEmailData()
    {
        var dataPath = _dataPath;

        var oldFoldersPath = Path.Combine(dataPath, "EmailFolders");
        var oldMessagesPath = Path.Combine(dataPath, "EmailMessages");
        var oldBodiesPath = Path.Combine(dataPath, "Emails");

        // 1. Migrate Folders
        if (Directory.Exists(oldFoldersPath))
        {
            foreach (var file in Directory.GetFiles(oldFoldersPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = System.Text.Json.JsonSerializer.Deserialize<EmailFolder>(json);
                    if (item != null)
                    {
                        var newDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Folders");
                        if (!Directory.Exists(newDir)) Directory.CreateDirectory(newDir);

                        var newPath = Path.Combine(newDir, Path.GetFileName(file));
                        if (!File.Exists(newPath)) File.Move(file, newPath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Migration] Failed to migrate folder file {file}: {ex.Message}");
                }
            }
        }

        // 2. Migrate Messages & Bodies
        if (Directory.Exists(oldMessagesPath))
        {
            foreach (var file in Directory.GetFiles(oldMessagesPath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var item = System.Text.Json.JsonSerializer.Deserialize<EmailMessage>(json);
                    if (item != null)
                    {
                        // Move Message JSON
                        var newMsgDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Messages");
                        if (!Directory.Exists(newMsgDir)) Directory.CreateDirectory(newMsgDir);

                        var newMsgPath = Path.Combine(newMsgDir, Path.GetFileName(file));
                        if (!File.Exists(newMsgPath)) File.Move(file, newMsgPath);

                        // Move Body HTML
                        var oldBodyFile = Path.Combine(oldBodiesPath, $"{item.Id}.html");
                        if (File.Exists(oldBodyFile))
                        {
                            var newBodyDir = Path.Combine(dataPath, "Employees", item.EmployeeId, "Email", "Bodies");
                            if (!Directory.Exists(newBodyDir)) Directory.CreateDirectory(newBodyDir);

                            var newBodyPath = Path.Combine(newBodyDir, $"{item.Id}.html");
                            if (!File.Exists(newBodyPath)) File.Move(oldBodyFile, newBodyPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Migration] Failed to migrate message file {file}: {ex.Message}");
                }
            }
        }
    }


    public virtual void ReloadAll()
    {
        // Cache clearing is now handled atomically inside each repository's LoadFromDisk() method
        // to minimize the window where active HTTP requests would query empty collections.
        Initialize();
    }

    public bool RestoreBackup(string filePath, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Backup file not found", filePath);

        var fullFilePath = Path.GetFullPath(filePath);
        var fullDataPath = Path.GetFullPath(_dataPath);
        if (!fullDataPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDataPath += Path.DirectorySeparatorChar;
        }

        // 1. Validate it's a valid zip and pre-validate all entries against Zip Slip path traversal
        // BEFORE deleting any existing database files to prevent data loss on invalid or malicious archives.
        try
        {
            using var zip = ZipFile.OpenRead(fullFilePath);
            foreach (var entry in zip.Entries)
            {
                var safeEntryName = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(fullDataPath, safeEntryName));

                if (!destinationPath.StartsWith(fullDataPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Zip entry is outside of the target directory.");
            }
        }
        catch (InvalidDataException)
        {
            throw new Exception("Invalid backup file format.");
        }

        // Flush all pending writes to disk to prevent sharing violations during restore
        _persistence.FlushAll();

        // 2. Clear current Data directory (except Backups)
        var dataDir = new DirectoryInfo(_dataPath);
        foreach (var file in dataDir.GetFiles())
        {
            if (file.FullName.Equals(fullFilePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Extension == ".tmp") continue;

            try { file.Delete(); } catch (Exception ex) { logger?.LogWarning(ex, "Failed to delete file: {FileName}", file.Name); }
        }

        foreach (var dir in dataDir.GetDirectories())
        {
            if (dir.Name.Equals("Backups", StringComparison.OrdinalIgnoreCase)) continue;
            try { dir.Delete(recursive: true); } catch (Exception ex) { logger?.LogWarning(ex, "Failed to delete directory: {DirName}", dir.Name); }
        }

        // 3. Extract Backup
        using (var zip = ZipFile.OpenRead(fullFilePath))
        {
            foreach (var entry in zip.Entries)
            {
                var safeEntryName = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var destinationPath = Path.GetFullPath(Path.Combine(fullDataPath, safeEntryName));

                if (string.IsNullOrEmpty(entry.Name)) // It's a directory
                {
                    Directory.CreateDirectory(destinationPath);
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        File.SetUnixFileMode(destinationPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                    }
                }
                else
                {
                    var dir = Path.GetDirectoryName(destinationPath)!;
                    Directory.CreateDirectory(dir);
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        File.SetUnixFileMode(dir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                    }
                    entry.ExtractToFile(destinationPath, overwrite: true);
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        File.SetUnixFileMode(destinationPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                    }
                }
            }
        }

        // 4. Reload Database
        ReloadAll();

        logger?.LogInformation("Database restored from backup: {Path}", filePath);

        bool casdoorRestarted = false;
        var sysConfig = SystemConfigs.Get();
        if (sysConfig != null && sysConfig.ProviderType == IdpType.BuiltInCasdoor)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("supervisorctl", "restart casdoor")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi);
                logger?.LogInformation("Restarted builtin Casdoor after backup restore.");
                casdoorRestarted = true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to restart builtin Casdoor after backup restore.");
            }
        }

        return casdoorRestarted;
    }
}

