using Spokes_Server.Core.Data;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Maintenance;
using Spokes_Server.Core.Services.Maintenance.Destinations;
using Spokes_Server.Core.Services.Maintenance.Time;
using Spokes_Server.Core.Services.Identity;
using Spokes_Server.Core.Services.Storage;
using Spokes_Server.Core.Services.Content;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Documents;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.HR.Calendar;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spokes_Server.Hubs;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers strongly-typed SpokesOptions bound from configuration section "Spokes"
    /// and environment variable fallbacks (SPOKES_RELAY_KEY, SPOKES_KLIPY_KEY).
    /// </summary>
    public static IServiceCollection AddSpokesConfiguration(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddOptions<Spokes_Server.Core.Configuration.SpokesOptions>()
            .Configure<IServiceProvider>((options, sp) =>
            {
                var config = configuration ?? sp.GetService<IConfiguration>();
                if (config != null)
                {
                    var section = config.GetSection(Spokes_Server.Core.Configuration.SpokesOptions.SectionName);
                    if (section.Exists())
                    {
                        section.Bind(options);
                    }
                    if (string.IsNullOrEmpty(options.PushRelayKey))
                    {
                        options.PushRelayKey = config["SPOKES_RELAY_KEY"]
                            ?? Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY")
                            ?? string.Empty;
                    }
#pragma warning disable CS0618
                    if (string.IsNullOrEmpty(options.DefaultKlipyApiKey))
                    {
                        options.DefaultKlipyApiKey = config["SPOKES_KLIPY_KEY"]
                            ?? Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY")
                            ?? string.Empty;
                    }
#pragma warning restore CS0618
                }
                else
                {
                    // If no IConfiguration was registered, still fall back to process environment variables
                    if (string.IsNullOrEmpty(options.PushRelayKey))
                    {
                        options.PushRelayKey = Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY") ?? string.Empty;
                    }
#pragma warning disable CS0618
                    if (string.IsNullOrEmpty(options.DefaultKlipyApiKey))
                    {
                        options.DefaultKlipyApiKey = Environment.GetEnvironmentVariable("SPOKES_KLIPY_KEY") ?? string.Empty;
                    }
#pragma warning restore CS0618
                }
            });

        return services;
    }

    public static IServiceCollection AddSpokesDatabase(this IServiceCollection services)
    {
        // 1. The Engine
        services.AddSingleton<DiskPersistenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiskPersistenceService>());
        services.TryAddSingleton<EncryptionService>();

        // 2. The Repositories (Add new ones here in the future)
        services.AddSingleton<SystemConfigRepository>();
        services.AddSingleton<ServerConfigRepository>();
        services.AddSingleton<ProjectRepository>();
        services.AddSingleton<ProjectGroupRepository>();
        services.AddSingleton<EmployeeRepository>();
        services.AddSingleton<TimesheetRepository>();
        services.AddSingleton<HourBankAdjustmentRepository>();
        services.AddSingleton<WorkTypeRepository>();
        services.AddSingleton<RateCardRepository>();
        services.AddSingleton<QuoteRepository>();
        services.AddSingleton<PurchaseOrderRepository>();
        services.AddSingleton<SupplierRepository>();
        services.AddSingleton<InvoiceRepository>();
        services.AddSingleton<CommissionLedgerRecordRepository>();
        // Contacts
        services.AddSingleton<PublicContactRepository>();
        services.AddSingleton<PrivateContactRepository>();
        services.AddSingleton<CompanyProfileRepository>();
        services.AddSingleton<DocumentTemplateRepository>();
        services.AddSingleton<StandardDocumentRepository>();
        services.AddSingleton<ProjectDocumentRepository>();
        services.AddSingleton<ExpenseReportRepository>();
        services.AddSingleton<BillRepository>();
        services.AddSingleton<ProjectNoteRepository>();
        services.AddSingleton<WeeklyPlanRepository>();
        services.AddSingleton<TeamRepository>();
        services.AddSingleton<CalendarEventRepository>();
        services.AddSingleton<OpenIdAccountRepository>();
        services.AddSingleton<EmployerContributionRepository>();
        services.AddSingleton<RecurringExpenseRepository>();
        services.AddSingleton<DeviceSessionRepository>();
        services.AddSingleton<DemoConfigRepository>();

        // Chat Feature
        services.AddSingleton<ChatChannelRepository>();
        services.AddSingleton<ChatCategoryRepository>();
        services.AddSingleton<ChatMessageCacheService>();
        services.AddSingleton<ChatMessageRepository>();
        services.AddSingleton<ChatReadStateRepository>();
        services.AddSingleton<UserClientStateRepository>();
        services.AddSingleton<ReportedMessageRepository>();
        services.AddSingleton<AlbumRepository>();

        // Boards Feature
        services.AddSingleton<BoardRepository>();
        services.AddSingleton<BoardCardRepository>();
        services.AddSingleton<BoardTemplateRepository>();

        // Email Feature
        services.AddSingleton<EmailFolderRepository>();
        services.AddSingleton<EmailMessageRepository>();

        // 3. Domain Aggregate Contexts & Container
        services.AddSingleton<ProjectsContext>();
        services.AddSingleton<HrContext>();
        services.AddSingleton<AccountingContext>();
        services.AddSingleton<CommunicationContext>();
        services.AddSingleton<CoreContext>();

        services.AddSingleton<SequenceService>();
        services.AddSingleton<Database>(sp => new Database(
            sp.GetRequiredService<ProjectsContext>(),
            sp.GetRequiredService<HrContext>(),
            sp.GetRequiredService<AccountingContext>(),
            sp.GetRequiredService<CommunicationContext>(),
            sp.GetRequiredService<CoreContext>()));
        services.AddSingleton<DatabaseRelationshipCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseRelationshipCoordinator>());

        return services;
    }

    public static IServiceCollection AddSpokesCoreServices(this IServiceCollection services)
    {
        // 0. Permission Service
        services.AddScoped<UserService>();
        services.AddSingleton<IUserPrivacyService, UserPrivacyService>();
        services.AddSingleton<UserPrivacyService>();
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<AppLabelService>(sp => new AppLabelService(
            sp.GetRequiredService<CoreContext>(),
            sp.GetService<Microsoft.Extensions.Localization.IStringLocalizer<Spokes_Server.Resources.AppResources>>()));
        services.AddScoped<OvertimeService>();
        services.AddSingleton<PresenceStateService>(sp => new PresenceStateService(
            sp.GetRequiredService<ChatStateService>(),
            sp.GetService<SessionService>(),
            sp.GetService<ILogger<PresenceStateService>>()));
        services.AddSingleton<IPushDecisionService>(sp => 
            sp.GetRequiredService<PresenceStateService>());
        services.AddHostedService<PresenceWatchdogHostedService>();
        services.AddSingleton<ChatStateService>();
        services.AddSingleton<IChatRealtimeGateway, ChatRealtimeGateway>();
        services.AddSingleton<UserClientStateService>(sp => new UserClientStateService(
            sp.GetRequiredService<CommunicationContext>(),
            sp.GetRequiredService<ChatStateService>(),
            sp.GetRequiredService<IChatRealtimeGateway>(),
            sp.GetRequiredService<ILogger<UserClientStateService>>()));
        services.AddSingleton<IUserClientStateService>(sp => sp.GetRequiredService<UserClientStateService>());
        services.AddSingleton<VoiceChannelService>();
        services.AddScoped<Spokes_Server.Core.Services.Communication.Voice.IVoiceJsInteropService, Spokes_Server.Core.Services.Communication.Voice.VoiceJsInteropService>();
        services.AddScoped<Spokes_Server.Core.Services.Communication.Voice.IVoiceDeviceService, Spokes_Server.Core.Services.Communication.Voice.VoiceDeviceService>();
        services.AddScoped<Spokes_Server.Core.Services.Communication.Voice.IDemoVoiceAdapter, Spokes_Server.Core.Services.Communication.Voice.DemoVoiceAdapter>();
        services.AddScoped<GlobalVoiceService>();
        services.AddScoped<Spokes_Server.Components.UI.IVoiceUiStateService, Spokes_Server.Components.UI.VoiceUiStateService>();
        services.AddHostedService<VoiceStateSyncService>();
        services.AddSingleton<LiveKitService>();
        services.AddSingleton<IVoiceBotService, VoiceBotService>();
        services.AddSingleton<MarkdownSanitizerService>();

        // Chat & Collaboration Services
        services.AddSingleton<AlbumService>();
        services.AddSingleton<ISlideshowSessionService, SlideshowSessionService>();
        services.AddScoped<ISlideshowCastService, SlideshowCastService>();
        services.AddSingleton<ChatAuthorizationService>();
        services.AddSingleton<IChatAuthorizationService>(sp => sp.GetRequiredService<ChatAuthorizationService>());
        services.AddSingleton<IChatChannelAccessService>(sp => sp.GetRequiredService<ChatAuthorizationService>());
        services.AddSingleton<ChatSecurityService>();
        services.AddSingleton<IContentModerationService, ContentModerationService>();
        services.AddSingleton<ChatService>(sp => new ChatService(
            sp.GetRequiredService<CommunicationContext>(),
            sp.GetRequiredService<HrContext>(),
            sp.GetRequiredService<ProjectsContext>(),
            sp.GetRequiredService<IChatRealtimeGateway>(),
            sp.GetRequiredService<ChatStateService>(),
            sp.GetRequiredService<INotificationRoutingService>(),
            sp.GetRequiredService<PresenceStateService>(),
            sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IContentModerationService>(),
            sp.GetRequiredService<AlbumService>(),
            sp.GetRequiredService<MarkdownSanitizerService>(),
            sp.GetRequiredService<ILogger<ChatService>>(),
            sp.GetService<IChatAuthorizationService>(),
            sp.GetRequiredService<ICryptoService>(),
            sp.GetService<IUserPrivacyService>()));
        services.AddSingleton<IChatService>(sp => sp.GetRequiredService<ChatService>());
        services.AddScoped<ChatNotificationService>();
        services.AddScoped<IGifSearchService, GifSearchService>();
        services.AddSingleton<BoardService>();
        services.AddSingleton<ImageProcessingService>();
        services.AddSingleton<IMp4FastStartService, Mp4FastStartService>();
        services.AddSingleton<ICompanyBrandingService, CompanyBrandingService>();
        services.AddSingleton<CompanyBrandingService>();

        // Email Sync tracking
        services.AddSingleton<EmailSyncStateService>();
        services.AddScoped<EmailNotificationService>(); // Realtime IMAP
        services.AddSingleton<CalendarReminderStateService>();
        services.AddScoped<CalendarNotificationService>();
        services.AddHostedService<CalendarReminderBackgroundService>();
        services.AddScoped<IUiNotificationPresenter, Spokes_Server.Components.UI.UiNotificationPresenter>();
        services.AddScoped<Spokes_Server.Core.Services.Client.ISpokesClientBridge, Spokes_Server.Core.Services.Client.SpokesClientBridge>();
        services.AddScoped<ShareTargetStateService>();
        services.AddScoped<SearchService>();
        services.AddSingleton<TimesheetTimerService>();
        services.AddSingleton<LinkPreviewService>();
        services.TryAddSingleton<EncryptionService>();
        services.AddSingleton<IImapConnectionFactory, ImapConnectionFactory>();
        services.AddSingleton<EmailIdleService>(); // Realtime IMAP
        services.AddScoped<IEmailCacheMaintenanceService, EmailCacheMaintenanceService>();
        services.AddScoped<IEmailDraftService, EmailDraftService>();
        services.AddScoped<IEmailDeliveryService, EmailDeliveryService>();
        services.AddScoped<IEmailFolderService, EmailFolderService>();
        services.AddScoped<IEmailMessageLifecycleService, EmailMessageLifecycleService>();
        services.AddScoped<IEmailSyncService, EmailSyncService>();
        services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
        services.AddSingleton<IBackupDestinationProvider, LocalMountBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, S3BackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, WebDavBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, SftpBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, GoogleDriveBackupProvider>();
        services.AddSingleton<BackupDestinationCoordinator>();
        services.AddSingleton<StorageMaintenanceService>();
        services.AddSingleton<NtpClient>();
        services.AddSingleton<SpokesClockService>();
        services.AddSingleton<ISpokesClock>(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddHostedService(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddSingleton<BackupService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
        services.AddHostedService<DemoResetService>();
        services.AddSingleton<NotificationQueueService>();
        services.AddSingleton<NotificationRecipientFilter>();
        services.AddSingleton<NotificationContentDecryptor>();
        services.AddSingleton<NotificationRateLimiter>();
        services.AddSingleton<IInAppNotificationDispatcher, InAppNotificationDispatcher>();
        services.AddSingleton<NotificationRoutingService>();
        services.AddSingleton<INotificationRoutingService>(sp => sp.GetRequiredService<NotificationRoutingService>());

        // Push Notifications & Options
        services.AddSingleton<PushTokenService>();
        services.AddSingleton<PushPayloadBuilder>();
        services.AddSingleton<NativeRelayDispatcher>();
        services.AddSingleton<IWebPushService, WebPushService>();
        services.AddSingleton<WebPushService>(sp => (WebPushService)sp.GetRequiredService<IWebPushService>());
        services.AddSingleton<IPushLicenseEligibilityService, PushLicenseEligibilityService>();
        services.AddSingleton<AvatarGeneratorService>();
        services.AddSingleton<ShortcutIconService>();
        services.AddSingleton<VersionMetadata>();
        services.AddSingleton<LicenseValidationService>();
        services.AddSingleton<RelayTrialTicketService>();
        services.AddHttpClient();
        services.AddHttpClient("LinkPreview", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SpokesBot/1.0; +https://spokes.app)");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 512 * 1024;
        });
        services.AddHttpClient("ServerUpdate", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Spokes-Update-Checker/1.0");
        });
        services.AddHttpClient("Casdoor");
        services.AddHostedService<TelemetryBackgroundService>();
        services.AddHostedService<NotificationQueueBackgroundService>();
        services.AddHostedService<ServerUpdateService>();
        services.AddHostedService<TempFileCleanupBackgroundService>();
        services.AddHostedService<DatabaseBootstrapperHostedService>();
        services.AddSingleton<ServerUpdateService>(sp => sp.GetServices<IHostedService>().OfType<ServerUpdateService>().First());

        // Document Rendering
        services.AddSingleton<Spokes_Server.Core.Services.Documents.IBrowserManager, Spokes_Server.Core.Services.Documents.BrowserManager>();
        services.AddSingleton<RenderTokenService>();
        services.AddScoped<DocumentPdfService>();

        // 0.5 File Services
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IFileAccessProvider, ProjectFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, ExpenseFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, BillFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, ChatFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, RecurringCostFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, ProjectNoteFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, AvatarFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, SignatureFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, AlbumFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, TempFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, EmailFileAccessProvider>();

        // 0.6 Cryptography & Vault
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<ServerEscrowService>(sp => new ServerEscrowService(
            sp.GetRequiredService<CompanyProfileRepository>(),
            sp.GetRequiredService<ServerConfigRepository>(),
            sp.GetRequiredService<ICryptoService>(),
            sp.GetService<ILogger<ServerEscrowService>>(),
            sp.GetService<IConfiguration>()));
        services.AddSingleton<FileTokenService>();
        services.AddSingleton<GlobalKeystoreService>();
        services.AddScoped<ScopedKeystoreService>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileKeyResolver, Spokes_Server.Core.Services.Communication.Chat.ChatFileKeyResolver>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileKeyResolver, Spokes_Server.Core.Services.Communication.Chat.AlbumFileKeyResolver>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileAuthorizationService, Spokes_Server.Core.Services.Storage.FileAuthorizationService>();
        services.AddScoped<ITimelineEventService, TimelineEventService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<AccountingService>(sp => (AccountingService)sp.GetRequiredService<IAccountingService>());
        services.AddScoped<BillingOrchestrationService>();
        services.AddSingleton<PdfInvoiceInspectionService>();
        services.AddScoped<SmartPoMatchingEngine>();
        services.AddScoped<CompanyBillInboxService>();
        services.AddHostedService<CompanyBillInboxBackgroundService>();
        services.AddScoped<ISage50ExportService, Sage50ExportService>();
        services.AddScoped<Sage50ExportService>(sp => (Sage50ExportService)sp.GetRequiredService<ISage50ExportService>());
        services.AddScoped<HRService>(sp => new HRService(
            sp.GetRequiredService<TimesheetRepository>(),
            sp.GetRequiredService<EmployeeRepository>(),
            sp.GetRequiredService<TeamRepository>()));
        services.AddScoped<Spokes_Server.Core.Services.Migrations.ClockifyMigrationService>();
        services.AddScoped<CalendarService>();
        services.AddScoped<Spokes_Server.Core.Services.Communication.IContactService, ContactService>();
        services.AddScoped<ContactService>();
        services.AddScoped<ProjectNoteService>();

        // 0.7 Migration Pipeline
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.EmailDataMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.PushDeviceSessionMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.AvatarMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.DocumentStatusMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.InvoicePaymentMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LegacyAttachmentMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.WorkTypeSubTaskMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LegacyOidcSubMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.SupplierContactsMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.EmployeeChannelOrderMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LegacyChatAttachmentRouteMigrationStep>();

        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationRunner>(sp => 
            new Spokes_Server.Core.Services.Migrations.MigrationRunnerService(
                sp.GetServices<Spokes_Server.Core.Services.Migrations.IMigrationStep>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetService<Spokes_Server.Core.Data.DiskPersistenceService>(),
                sp.GetService<ILogger<Spokes_Server.Core.Services.Migrations.MigrationRunnerService>>()));
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.MigrationRunnerService>(sp => 
            (Spokes_Server.Core.Services.Migrations.MigrationRunnerService)sp.GetRequiredService<Spokes_Server.Core.Services.Migrations.IMigrationRunner>());

        // 0.8 Background Provisioning & Boot Hosted Services
        services.AddHostedService<Spokes_Server.Core.Services.Documents.ChromiumProvisioningService>();
        services.AddSingleton<CasdoorConfigTemplateGenerator>();
        services.AddSingleton<CasdoorApiClient>();
        services.AddSingleton<CasdoorDatabaseMigrator>();
        services.AddScoped<CasdoorProvisioningService>();
        services.AddHostedService<CasdoorBrandingBackgroundService>();

        // 0.9 Core Diagnostics & Logging
        services.AddSingleton<Spokes_Server.Core.Services.Logging.ISystemLogService>(sp =>
            new Spokes_Server.Core.Services.Logging.SystemLogService(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetService<ILogger<Spokes_Server.Core.Services.Logging.SystemLogService>>(),
                TimeProvider.System));
        services.AddSingleton<IStoragePersistenceLogger>(sp => sp.GetRequiredService<Spokes_Server.Core.Services.Logging.ISystemLogService>());
        services.AddSingleton<IStorageHealthService, StorageHealthService>();
        services.AddSingleton<IStorageHealthNotifier>(sp => sp.GetRequiredService<IStorageHealthService>());
        services.AddScoped<Spokes_Server.Core.Services.UI.SpokesDomInteropService>();
        services.AddScoped<Spokes_Server.Core.Services.UI.ImageRecoveryService>();
        services.AddScoped<Spokes_Server.Core.Services.UI.ISoundService, Spokes_Server.Core.Services.UI.SoundService>();
        services.AddScoped<StartupTimingService>();
        services.AddScoped<Spokes_Server.Core.Services.Reports.TimesheetReportService>();

        return services;
    }
}
