namespace Spokes_Server.Aggregate;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Documents;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

/// <summary>
/// Authoritative database aggregate façade coordinating domain aggregate contexts (ProjectsContext, HrContext,
/// AccountingContext, CommunicationContext, CoreContext) and their bootstrap lifecycle.
/// Provides a unified aggregate accessor for Blazor UI components and system orchestration,
/// while focused domain services and background workers inject specific domain contexts directly.
/// </summary>
public class Database
{
    public virtual ProjectsContext ProjectsContext { get; }
    public virtual HrContext HrContext { get; }
    public virtual AccountingContext AccountingContext { get; }
    public virtual CommunicationContext CommunicationContext { get; }
    public virtual CoreContext CoreContext { get; }

    // Projects Bounded Context
    public virtual ProjectRepository Projects => ProjectsContext?.Projects!;
    public virtual ProjectGroupRepository ProjectGroups => ProjectsContext?.ProjectGroups!;
    public virtual WorkTypeRepository WorkTypes => ProjectsContext?.WorkTypes!;
    public virtual RateCardRepository RateCards => ProjectsContext?.RateCards!;
    public virtual QuoteRepository Quotes => ProjectsContext?.Quotes!;
    public virtual ProjectNoteRepository ProjectNotes => ProjectsContext?.ProjectNotes!;
    public virtual BoardRepository Boards => ProjectsContext?.Boards!;
    public virtual BoardCardRepository BoardCards => ProjectsContext?.BoardCards!;
    public virtual BoardTemplateRepository BoardTemplates => ProjectsContext?.BoardTemplates!;
    public virtual ProjectDocumentRepository ProjectDocuments => ProjectsContext?.ProjectDocuments!;

    // HR Bounded Context
    public virtual EmployeeRepository Employees => HrContext?.Employees!;
    public virtual TimesheetRepository Timesheets => HrContext?.Timesheets!;
    public virtual HourBankAdjustmentRepository HourBankAdjustments => HrContext?.HourBankAdjustments!;
    public virtual WeeklyPlanRepository WeeklyPlans => HrContext?.WeeklyPlans!;
    public virtual TeamRepository Teams => HrContext?.Teams!;
    public virtual CalendarEventRepository CalendarEvents => HrContext?.CalendarEvents!;

    // Accounting Bounded Context
    public virtual InvoiceRepository Invoices => AccountingContext?.Invoices!;
    public virtual PurchaseOrderRepository PurchaseOrders => AccountingContext?.PurchaseOrders!;
    public virtual SupplierRepository Suppliers => AccountingContext?.Suppliers!;
    public virtual CommissionLedgerRecordRepository CommissionLedgerRecords => AccountingContext?.CommissionLedgerRecords!;
    public virtual ExpenseReportRepository ExpenseReports => AccountingContext?.ExpenseReports!;
    public virtual BillRepository Bills => AccountingContext?.Bills!;
    public virtual EmployerContributionRepository EmployerContributions => AccountingContext?.EmployerContributions!;
    public virtual RecurringExpenseRepository RecurringExpenses => AccountingContext?.RecurringExpenses!;

    // Communication Bounded Context
    public virtual ChatChannelRepository ChatChannels => CommunicationContext?.ChatChannels!;
    public virtual ChatCategoryRepository ChatCategories => CommunicationContext?.ChatCategories!;
    public virtual ChatMessageRepository ChatMessages => CommunicationContext?.ChatMessages!;
    public virtual ChatReadStateRepository ChatReadStates => CommunicationContext?.ChatReadStates!;
    public virtual UserClientStateRepository ClientStates => CommunicationContext?.ClientStates!;
    public virtual ReportedMessageRepository ReportedMessages => CommunicationContext?.ReportedMessages!;
    public virtual AlbumRepository Albums => CommunicationContext?.Albums!;
    public virtual EmailFolderRepository EmailFolders => CommunicationContext?.EmailFolders!;
    public virtual EmailMessageRepository EmailMessages => CommunicationContext?.EmailMessages!;
    public virtual PublicContactRepository PublicContacts => CommunicationContext?.PublicContacts!;
    public virtual PrivateContactRepository PrivateContacts => CommunicationContext?.PrivateContacts!;

    // Core Bounded Context
    public virtual CompanyProfileRepository CompanyProfile => CoreContext?.CompanyProfile!;
    public virtual DocumentTemplateRepository DocumentTemplates => CoreContext?.DocumentTemplates!;
    public virtual StandardDocumentRepository StandardDocuments => CoreContext?.StandardDocuments!;
    public virtual OpenIdAccountRepository OpenIdAccounts => CoreContext?.OpenIdAccounts!;
    public virtual ServerConfigRepository ServerConfigs => CoreContext?.ServerConfigs!;
    public virtual SystemConfigRepository SystemConfigs => CoreContext?.SystemConfigs!;
    public virtual DeviceSessionRepository DeviceSessions => CoreContext?.DeviceSessions!;
    public virtual DemoConfigRepository DemoConfigs => CoreContext?.DemoConfigs!;

    // Decomposed Module Configurations
    public virtual AccountingConfig AccountingConfig => AccountingContext?.GetAccountingConfig() ?? new AccountingConfig();
    public virtual HRSettings HRSettings => HrContext?.GetHRSettings() ?? new HRSettings();
    public virtual TimesheetSettings TimesheetSettings => HrContext?.GetTimesheetSettings() ?? new TimesheetSettings();
    public virtual ChatConfig ChatConfig => CommunicationContext?.GetChatConfig() ?? new ChatConfig();
    public virtual PushConfig PushConfig => CommunicationContext?.GetPushConfig() ?? new PushConfig();
    public virtual EmailServerSettings EmailSettings => CommunicationContext?.GetEmailSettings() ?? new EmailServerSettings();
    public virtual DocumentNumberingConfig DocumentNumbering => ProjectsContext?.GetDocumentNumbering() ?? new DocumentNumberingConfig();
    public virtual BrandingConfig Branding => CoreContext?.GetBranding() ?? new BrandingConfig();
    public virtual ContactConfig Contact => CoreContext?.GetContact() ?? new ContactConfig();

    /// <summary>
    /// Protected parameterless constructor strictly reserved for mock proxy frameworks (e.g. Moq).
    /// </summary>
    protected Database()
    {
        ProjectsContext = null!;
        HrContext = null!;
        AccountingContext = null!;
        CommunicationContext = null!;
        CoreContext = null!;
    }

    [ActivatorUtilitiesConstructor]
    public Database(
        ProjectsContext projectsContext,
        HrContext hrContext,
        AccountingContext accountingContext,
        CommunicationContext communicationContext,
        CoreContext coreContext)
    {
        ProjectsContext = projectsContext;
        HrContext = hrContext;
        AccountingContext = accountingContext;
        CommunicationContext = communicationContext;
        CoreContext = coreContext;
    }

    private bool _isInitialized;
    private readonly object _initLock = new();

    public event Action? OnInitialized;

    public virtual void Initialize()
    {
        lock (_initLock)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            Parallel.Invoke(
                () => ProjectsContext?.Initialize(),
                () => HrContext?.Initialize(),
                () => AccountingContext?.Initialize(),
                () => CommunicationContext?.Initialize(),
                () => CoreContext?.Initialize()
            );

            OnInitialized?.Invoke();
        }
    }

    public virtual void ReloadAll()
    {
        lock (_initLock)
        {
            _isInitialized = false;
            Initialize();
        }
    }
}
