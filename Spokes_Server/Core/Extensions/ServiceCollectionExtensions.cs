using Spokes_Server.Core.Data;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Documents;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Licensing;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Microsoft.Extensions.DependencyInjection;

public static class SpokesServiceExtensions
{
    public static IServiceCollection AddSpokesDatabase(this IServiceCollection services)
    {
        // 1. The Engine
        services.AddSingleton<DiskPersistenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<DiskPersistenceService>());

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
        services.AddSingleton<ChatMessageRepository>();
        services.AddSingleton<ChatReadStateRepository>();
        services.AddSingleton<UserClientStateRepository>();
        services.AddSingleton<ReportedMessageRepository>();
        services.AddSingleton<PushSubscriptionRepository>();
        services.AddSingleton<AlbumRepository>();

        // Boards Feature
        services.AddSingleton<BoardRepository>();
        services.AddSingleton<BoardCardRepository>();
        services.AddSingleton<BoardTemplateRepository>();

        // Email Feature
        services.AddSingleton<EmailFolderRepository>();
        services.AddSingleton<EmailMessageRepository>();

        // Services
        services.AddSingleton<ServerEscrowService>();
        services.AddSingleton<AlbumService>();
        services.AddSingleton<IChatAuthorizationService, ChatAuthorizationService>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<IChatChannelAccessService>(sp => sp.GetRequiredService<ChatService>());
        services.AddSingleton<BoardService>();
        services.AddSingleton<IWebPushService, WebPushService>();
        services.AddSingleton<IContentModerationService, ContentModerationService>();
        services.AddSingleton<ImageProcessingService>();
        services.AddScoped<CasdoorProvisioningService>();

        // Scoped services (per-user session)
        services.AddScoped<ChatNotificationService>();
        services.AddScoped<UserCircuitContext>();
        services.AddScoped<CircuitHandler, PresenceCircuitHandler>();

        // 3. The Container
        services.AddSingleton<SequenceService>();
        services.AddSingleton<Database>();

        return services;
    }

    public static IServiceCollection AddSpokesCoreServices(this IServiceCollection services)
    {
        // 0. Permission Service
        services.AddScoped<UserService>();
        services.AddScoped<AccountDeletionService>();
        services.AddScoped<AppLabelService>();
        services.AddScoped<OvertimeService>();
        services.AddSingleton<PresenceStateService>();
        services.AddSingleton<ChatStateService>();
        services.AddSingleton<UserClientStateService>();
        services.AddScoped<VoiceChannelService>();
        services.AddScoped<GlobalVoiceService>();
        services.AddHostedService<VoiceStateSyncService>();
        services.AddSingleton<LiveKitService>();
        services.AddSingleton<MarkdownSanitizerService>();
        services.AddScoped<CircuitHandler, VoiceCircuitHandler>();

        // Email Sync tracking
        services.AddSingleton<EmailSyncStateService>();
        services.AddScoped<EmailNotificationService>(); // Realtime IMAP
        services.AddSingleton<CalendarReminderStateService>();
        services.AddScoped<CalendarNotificationService>();
        services.AddHostedService<CalendarReminderBackgroundService>();
        services.AddScoped<ShareTargetStateService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<TimerService>();
        services.AddSingleton<LinkPreviewService>();
        services.AddSingleton<EncryptionService>();
        services.AddSingleton<EmailIdleService>(); // Realtime IMAP
        services.AddScoped<EmailService>();
        services.AddHostedService<EmailBackgroundService>();
        services.AddHostedService<BackupService>();
        services.AddHostedService<DemoResetService>();
        services.AddSingleton<BackupService>(sp => sp.GetServices<IHostedService>().OfType<BackupService>().First());
        services.AddSingleton<NotificationQueueService>();
        services.AddSingleton<NotificationRoutingService>();
        services.AddSingleton<AvatarGeneratorService>();
        services.AddSingleton<ShortcutIconService>();
        services.AddSingleton<VersionMetadata>();
        services.AddSingleton<LicenseValidationService>();
        services.AddHttpClient();
        services.AddHostedService<TelemetryBackgroundService>();
        services.AddHostedService<NotificationQueueBackgroundService>();
        services.AddHostedService<ServerUpdateService>();
        services.AddSingleton<ServerUpdateService>(sp => sp.GetServices<IHostedService>().OfType<ServerUpdateService>().First());

        // Document Rendering
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

        // 0.6 Cryptography & Vault
        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<FileTokenService>();
        services.AddSingleton<GlobalKeystoreService>();
        services.AddScoped<ScopedKeystoreService>();

        // 0.7 Domain Services
        services.AddScoped<FinancialCalculationEngine>();
        services.AddScoped<AccountingService>();
        services.AddScoped<HRService>();
        services.AddScoped<Spokes_Server.Core.Services.Integrations.ClockifyMigrationService>();

        return services;
    }
}
