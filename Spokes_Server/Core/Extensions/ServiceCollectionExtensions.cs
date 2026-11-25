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
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;
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
using Spokes_Server.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spokes_Server.Hubs;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers strongly-typed SpokesOptions bound from configuration section "Spokes"
    /// and environment variable fallback (SPOKES_RELAY_KEY).
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
                }

                if (string.IsNullOrEmpty(options.PushRelayKey))
                {
                    options.PushRelayKey = config?["SPOKES_RELAY_KEY"]
                        ?? Environment.GetEnvironmentVariable("SPOKES_RELAY_KEY")
                        ?? string.Empty;
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
        services.AddSingleton<TermsAndConditionsRepository>();
        services.AddSingleton<ProjectDocumentRepository>();
        services.AddSingleton<ProjectReportRepository>();
        services.AddSingleton<ExpenseReportRepository>();
        services.AddSingleton<BillRepository>();
        services.AddSingleton<ProjectNoteRepository>();
        services.AddSingleton<WeeklyPlanRepository>();
        services.AddSingleton<TeamRepository>();
        services.AddSingleton<CalendarEventRepository>();
        services.AddSingleton<OpenIdAccountRepository>();
        services.AddSingleton<EmployerContributionRepository>();
        services.AddSingleton<RecurringExpenseRepository>();
        services.AddSingleton<GlAccountRepository>();
        services.AddSingleton<JournalEntryRepository>();
        services.AddSingleton<BankStatementRepository>();
        services.AddSingleton<TaxFilingPeriodRepository>();
        services.AddSingleton<AccountingMigrationBatchRepository>();
        services.AddSingleton<FinancialReportRepository>();
        services.AddSingleton<DirectTransactionRepository>();
        services.AddSingleton<FinancialAccountRepository>();
        services.AddSingleton<SuggestedInvoicePaymentRepository>();
        services.AddSingleton<BankRuleRepository>();
        services.AddSingleton<PayRunRepository>();
        services.AddSingleton<DeviceSessionRepository>();
        services.AddSingleton<DemoConfigRepository>();

        // Chat Feature
        services.AddSingleton<ChatChannelRepository>();
        services.AddSingleton<ChatCategoryRepository>();
        services.AddSingleton<ChatMessageCacheService>();
        services.AddSingleton<ChatMessageRepository>();
        services.AddSingleton<ChatReadStateRepository>();
        services.AddSingleton<ReportedMessageRepository>();
        services.AddSingleton<AlbumRepository>();
        services.AddSingleton<ChatPollRepository>();
        services.AddSingleton<PhysicalVoiceRoomRepository>();

        // Boards Feature
        services.AddSingleton<BoardRepository>();
        services.AddSingleton<BoardCardRepository>();
        services.AddSingleton<BoardTemplateRepository>();

        // Email Feature
        services.AddSingleton<EmailAccountRepository>();
        services.AddSingleton<EmailInboxRepository>();
        services.AddSingleton<EmailSendingIdentityRepository>();
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
            sp.GetRequiredService<CoreContext>(),
            sp.GetRequiredService<SequenceService>()));
        services.AddSingleton<DatabaseRelationshipCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseRelationshipCoordinator>());

        return services;
    }

    public static IServiceCollection AddSpokesCoreServices(this IServiceCollection services)
    {
        // 0. Permission Service
        services.AddScoped<UserService>();
        services.AddSingleton<UserPrivacyService>();
        services.AddSingleton<IUserPrivacyService>(sp => sp.GetRequiredService<UserPrivacyService>());
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<AppLabelService>(sp => new AppLabelService(
            sp.GetRequiredService<CoreContext>(),
            sp.GetService<Microsoft.Extensions.Localization.IStringLocalizer<Spokes_Server.Resources.AppResources>>()));
        services.AddScoped<OvertimeService>();
        services.AddSingleton<ChatStateService>();
        services.AddSingleton<VoiceStateService>();
        services.AddSingleton<PresenceStateService>(sp => new PresenceStateService(
            sp.GetRequiredService<VoiceStateService>(),
            sp.GetService<SessionService>(),
            sp.GetService<ILogger<PresenceStateService>>()));
        services.AddSingleton<IPushDecisionService>(sp => 
            sp.GetRequiredService<PresenceStateService>());
        services.AddHostedService<PresenceWatchdogHostedService>();
        services.AddSingleton<UserClientStateService>(sp => new UserClientStateService(
            sp.GetRequiredService<CommunicationContext>(),
            sp.GetRequiredService<ChatStateService>(),
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
        services.AddSingleton<PhysicalVoiceRoomService>();
        services.AddSingleton<PhysicalRoomDirectSessionService>();
        services.AddSingleton<PhysicalRoomPipelineStage>();
        services.AddSingleton<IVoiceRoomPipelineStage>(sp => sp.GetRequiredService<PhysicalRoomPipelineStage>());
        services.AddSingleton<IVoiceBotService, VoiceBotService>();
        services.AddSingleton<MarkdownSanitizerService>();

        // Chat & Collaboration Services
        services.AddSingleton<AlbumService>();
        services.AddSingleton<PollService>();
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
            sp.GetRequiredService<ChatStateService>(),
            sp.GetRequiredService<INotificationRoutingService>(),
            sp.GetRequiredService<PresenceStateService>(),
            sp.GetRequiredService<IFileService>(),
            sp.GetRequiredService<IContentModerationService>(),
            sp.GetRequiredService<AlbumService>(),
            sp.GetRequiredService<ILogger<ChatService>>(),
            sp.GetService<IChatAuthorizationService>(),
            sp.GetRequiredService<ICryptoService>(),
            sp.GetService<IUserPrivacyService>(),
            sp.GetService<FileTokenService>(),
            sp.GetRequiredService<ChatSecurityService>(),
            sp.GetRequiredService<VoiceChannelService>()));
        services.AddSingleton<IChatService>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<ModerationReportService>();
        services.AddSingleton<IModerationReportService>(sp => sp.GetRequiredService<ModerationReportService>());
        services.AddScoped<ChatNotificationService>();
        services.AddSingleton<IGifSearchService, GifSearchService>();
        services.AddSingleton<BoardService>();
        services.AddSingleton<ImageProcessingService>();
        services.AddSingleton<IMp4FastStartService, Mp4FastStartService>();
        services.AddSingleton<CompanyBrandingService>();
        services.AddSingleton<ICompanyBrandingService>(sp => sp.GetRequiredService<CompanyBrandingService>());

        // Email Sync tracking
        services.AddSingleton<EmailSyncStateService>();
        services.AddScoped<EmailNotificationService>(); // Realtime IMAP
        services.AddSingleton<CalendarReminderStateService>();
        services.AddScoped<CalendarNotificationService>();
        services.AddHostedService<CalendarReminderBackgroundService>();
        services.AddScoped<IUiNotificationPresenter, Spokes_Server.Components.UI.UiNotificationPresenter>();
        services.AddScoped<ShareTargetStateService>();
        services.AddScoped<SearchService>();
        services.AddSingleton<TimesheetTimerService>();
        services.AddSingleton<LinkPreviewService>();
        services.TryAddSingleton<EncryptionService>();
        services.AddSingleton<IImapConnectionFactory, ImapConnectionFactory>();
        services.AddSingleton<EmailIdleService>(); // Realtime IMAP
        services.AddSingleton<IEmailInboxService, EmailInboxService>();
        services.AddScoped<IEmailCacheMaintenanceService, EmailCacheMaintenanceService>();
        services.AddScoped<IEmailDraftService, EmailDraftService>();
        services.AddScoped<IEmailDeliveryService, EmailDeliveryService>();
        services.AddScoped<IEmailFolderService, EmailFolderService>();
        services.AddScoped<IEmailMessageLifecycleService, EmailMessageLifecycleService>();
        services.AddScoped<IEmailSyncService, EmailSyncService>();
        services.AddHostedService<EmailBackgroundService>();
        services.AddSingleton<IBackupEncryptionService, BackupEncryptionService>();
        services.AddSingleton<IBackupDestinationProvider, LocalMountBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, S3BackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, WebDavBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, SftpBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, GoogleDriveBackupProvider>();
        services.AddSingleton<IBackupDestinationProvider, GitHubBackupProvider>();
        services.AddSingleton<BackupDestinationCoordinator>();
        services.AddSingleton<StorageMaintenanceService>();
        services.AddSingleton<NtpClient>();
        services.AddSingleton<SpokesClockService>();
        services.AddSingleton<ISpokesClock>(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddHostedService(sp => sp.GetRequiredService<SpokesClockService>());
        services.AddSingleton<BackupService>();
        services.AddSingleton<BackupRestoreUploadService>();
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
        services.AddSingleton<SpokesRelayClient>();
        services.AddHttpClient();
        services.AddHttpClient(SpokesRelayClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
        services.AddHttpClient(GifSearchService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
        services.AddHttpClient("LinkPreview", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SpokesBot/1.0; +https://spokes.app)");
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 512 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new SafeExternalHttpHandler());
        services.AddHttpClient("WebPush")
            .ConfigurePrimaryHttpMessageHandler(() => new SafeExternalHttpHandler(requireHttps: true, allowRedirects: false));
        services.AddHttpClient("ServerUpdate", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Spokes-Update-Checker/1.0");
        });
        services.AddHttpClient("Casdoor");
        services.AddHttpClient("WebDavBackup")
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = false
            });
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
        services.AddSingleton<ThumbnailCacheService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IFileAccessProvider, ProjectFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, ExpenseFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, BillFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, ChatFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, RecurringCostFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, BankStatementFileAccessProvider>();
        services.AddSingleton<IFileAccessProvider, DirectTransactionFileAccessProvider>();
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
        services.AddScoped<FileUrlSigner>();
        services.AddSingleton<Spokes_Server.Core.Services.Security.OAuthStateNonceService>();
        services.AddSingleton<Spokes_Server.Core.Services.Security.MobileOAuthChallengeService>();
        services.AddScoped<Spokes_Server.Core.Services.Security.UploadCompletionVerifier>();
        services.AddSingleton<GlobalKeystoreService>();
        services.AddScoped<ScopedKeystoreService>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileKeyResolver, Spokes_Server.Core.Services.Communication.Chat.ChatFileKeyResolver>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileKeyResolver, Spokes_Server.Core.Services.Communication.Chat.AlbumFileKeyResolver>();
        services.AddScoped<Spokes_Server.Core.Services.Storage.IFileAuthorizationService, Spokes_Server.Core.Services.Storage.FileAuthorizationService>();
        services.AddScoped<ITimelineEventService, TimelineEventService>();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        services.AddSingleton<JurisdictionTemplateService>(sp => (JurisdictionTemplateService)sp.GetRequiredService<IJurisdictionTemplateService>());
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<LedgerService>(sp => (LedgerService)sp.GetRequiredService<ILedgerService>());
        services.AddScoped<IDirectTransactionService, DirectTransactionService>();
        services.AddScoped<DirectTransactionService>(sp => (DirectTransactionService)sp.GetRequiredService<IDirectTransactionService>());
        services.AddScoped<IFinancialAccountService, FinancialAccountService>();
        services.AddScoped<FinancialAccountService>(sp => (FinancialAccountService)sp.GetRequiredService<IFinancialAccountService>());
        services.AddSingleton<BankStatementImportService>();
        services.AddScoped<IBankReconciliationService, BankReconciliationService>();
        services.AddScoped<BankReconciliationService>(sp => (BankReconciliationService)sp.GetRequiredService<IBankReconciliationService>());
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<TaxRemittanceService>(sp => (TaxRemittanceService)sp.GetRequiredService<ITaxRemittanceService>());
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<PayrollCalculationEngine>(sp => (PayrollCalculationEngine)sp.GetRequiredService<IPayrollCalculationEngine>());
        services.AddScoped<IAccountantExportService, AccountantExportService>();
        services.AddScoped<AccountantExportService>(sp => (AccountantExportService)sp.GetRequiredService<IAccountantExportService>());
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<AccountingService>(sp => (AccountingService)sp.GetRequiredService<IAccountingService>());
        services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();
        services.AddScoped<AccountingMigrationService>(sp => (AccountingMigrationService)sp.GetRequiredService<IAccountingMigrationService>());
        services.AddScoped<IFinancialReportService, FinancialReportService>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<BusinessOverheadService>(sp => (BusinessOverheadService)sp.GetRequiredService<IBusinessOverheadService>());
        services.AddScoped<IAccountingHealthService, AccountingHealthService>();
        services.AddScoped<AccountingHealthService>(sp => (AccountingHealthService)sp.GetRequiredService<IAccountingHealthService>());
        services.AddScoped<BillingOrchestrationService>();
        services.AddSingleton<PdfInvoiceInspectionService>();
        services.AddScoped<SmartPoMatchingEngine>();
        services.AddScoped<CompanyBillInboxService>();
        services.AddHostedService<CompanyBillInboxBackgroundService>();
        services.AddScoped<HRService>(sp => new HRService(
            sp.GetRequiredService<TimesheetRepository>(),
            sp.GetRequiredService<EmployeeRepository>(),
            sp.GetRequiredService<TeamRepository>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<HRService>>(),
            sp.GetRequiredService<ProjectRepository>(),
            sp.GetRequiredService<InvoiceRepository>()));
        services.AddScoped<Spokes_Server.Core.Services.Migrations.ClockifyMigrationService>();
        services.AddScoped<CalendarService>();
        services.AddScoped<WeeklyPlanService>();
        services.AddScoped<Spokes_Server.Core.Services.Communication.IContactService, ContactService>();
        services.AddScoped<ContactService>();
        services.AddScoped<ProjectNoteService>();
        services.AddScoped<ProjectReportService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ProjectService>();

        // 0.7 Migration Pipeline (Family & shared infrastructure one-time migrations)
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.PushDeviceSessionMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.AvatarMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LegacyOidcSubMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.EmployeeChannelOrderMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LegacyChatAttachmentRouteMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.PushSubscriptionsCaseFixMigrationStep>();
        services.AddSingleton<Spokes_Server.Core.Services.Migrations.IMigrationStep, Spokes_Server.Core.Services.Migrations.LiveKitUdpMuxMigrationStep>();

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
        services.AddScoped<Spokes_Server.Core.Services.UI.ImageRecoveryService>();
        services.AddScoped<Spokes_Server.Core.Services.UI.ISoundService, Spokes_Server.Core.Services.UI.SoundService>();
        services.AddScoped<StartupTimingService>();
        services.AddScoped<Spokes_Server.Core.Services.Reports.TimesheetReportService>();

        return services;
    }
}
