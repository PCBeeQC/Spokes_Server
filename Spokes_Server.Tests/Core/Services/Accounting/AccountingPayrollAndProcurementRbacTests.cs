namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.Accounting.Export;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;
using Xunit;

[Collection("JurisdictionStaticState")]
public class AccountingPayrollAndProcurementRbacTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ILedgerService _ledger;
    private readonly IPayrollCalculationEngine _payroll;
    private readonly IAccountantExportService _exportWithVouchers;
    private readonly IFinancialReportService _financialReports;
    private readonly IAccountingMigrationService _migration;
    private readonly IBusinessOverheadService _overhead;
    private readonly IAccountingService _accountingService;
    private readonly SmartPoMatchingEngine _poMatchingEngine;
    private readonly CompanyBillInboxService _inboxService;
    private readonly Mock<IFileService> _mockFileService;

    private static readonly Employee AdminActor = new()
    {
        Id = "rbac-admin",
        FirstName = "Global",
        LastName = "Admin",
        IsActive = true,
        IsAdmin = true
    };

    private static Employee MakeEmployee(string id, bool isAdmin = false, bool isSuspended = false, params string[] permissions) => new()
    {
        Id = id,
        FirstName = id,
        LastName = "User",
        IsActive = true,
        IsAdmin = isAdmin,
        IsSuspended = isSuspended,
        Permissions = [.. permissions]
    };

    public AccountingPayrollAndProcurementRbacTests()
    {
        var services = new ServiceCollection();
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddSingleton<IJurisdictionTemplateService, JurisdictionTemplateService>();
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<ITaxRemittanceService, TaxRemittanceService>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();
        services.AddScoped<IAccountingService, AccountingService>();
        services.AddScoped<IAccountingMigrationService, AccountingMigrationService>();
        services.AddScoped<IFinancialReportService, FinancialReportService>();

        _mockFileService = new Mock<IFileService>();
        var mockCacheMaintenance = new Mock<IEmailCacheMaintenanceService>();
        mockCacheMaintenance.Setup(c => c.EmailBodyPath).Returns(_testDataPath);
        var mockDelivery = new Mock<IEmailDeliveryService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _db.Initialize();
        _serviceProvider.GetRequiredService<IJurisdictionTemplateService>().EnsureChartOfAccountsSeeded();

        _ledger = _serviceProvider.GetRequiredService<ILedgerService>();
        _payroll = _serviceProvider.GetRequiredService<IPayrollCalculationEngine>();
        _financialReports = _serviceProvider.GetRequiredService<IFinancialReportService>();
        _migration = _serviceProvider.GetRequiredService<IAccountingMigrationService>();
        _overhead = _serviceProvider.GetRequiredService<IBusinessOverheadService>();
        _accountingService = _serviceProvider.GetRequiredService<IAccountingService>();
        _poMatchingEngine = new SmartPoMatchingEngine(_db);

        var fileAuth = new FileAuthorizationService([
            new ExpenseFileAccessProvider(_db.ExpenseReports),
            new BillFileAccessProvider(),
            new BankStatementFileAccessProvider()
        ]);

        _exportWithVouchers = new AccountantExportService(
            _db.AccountingContext,
            _db.CoreContext,
            _ledger,
            _serviceProvider.GetRequiredService<ITaxRemittanceService>(),
            _payroll,
            _overhead,
            _mockFileService.Object,
            _db.ProjectsContext,
            _db.HrContext,
            fileAuth);

        _inboxService = new CompanyBillInboxService(
            _db,
            _mockFileService.Object,
            new PdfInvoiceInspectionService(),
            _poMatchingEngine,
            _serviceProvider.GetRequiredService<EncryptionService>(),
            mockCacheMaintenance.Object,
            mockDelivery.Object,
            accountingService: _accountingService);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void PayrollIsolation_AccountingWithoutHrManageSalaries_BlocksPayrollOperationsAndPayrollCsv_ButAllowsNonPayrollZipExport()
    {
        var accountantOnly = MakeEmployee("acct-only", isAdmin: false, isSuspended: false, AppPermissions.Finance.AccessAccounting);
        var suspendedHr = MakeEmployee("hr-susp", isAdmin: false, isSuspended: true, AppPermissions.Finance.AccessAccounting, AppPermissions.Hr.ManageSalaries);

        _db.Employees.Save(new Employee
        {
            Id = "salaried-emp",
            FirstName = "Marc",
            LastName = "Gagnon",
            IsActive = true,
            HourlyRate = 55m
        });

        var adminRun = _payroll.CreateDraftPayRun(new DateTime(2026, 3, 1), new DateTime(2026, 3, 14), new DateTime(2026, 3, 19), actor: AdminActor);
        _payroll.SavePayRun(adminRun, actor: AdminActor);

        Assert.Throws<UnauthorizedAccessException>(() =>
            _payroll.CreateDraftPayRun(new DateTime(2026, 3, 15), new DateTime(2026, 3, 28), new DateTime(2026, 4, 2), actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.SavePayRun(adminRun, actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.FinalizePayRun(adminRun, actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.MarkPayRunPaid(adminRun, new DateTime(2026, 3, 19), actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.RecordDasRemittance(adminRun, new DateTime(2026, 4, 15), actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.CalculateDasRemittanceSummary(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.CalculateYearEndSlipsSummary(2026, actor: accountantOnly));
        Assert.Throws<UnauthorizedAccessException>(() => _payroll.SavePayRun(adminRun, actor: suspendedHr));

        Assert.Throws<UnauthorizedAccessException>(() =>
            _exportWithVouchers.GeneratePayrollAndDasSummaryCsv(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), actor: accountantOnly));

        var zipBytes = _exportWithVouchers.GenerateAccountantPackageZip(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), includeVouchers: false, actor: accountantOnly);
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("00_CPA_README.txt", entryNames);
        Assert.DoesNotContain("07_Payroll_And_DAS_Summary.csv", entryNames);
        Assert.DoesNotContain("12_Project_And_RnD_Labor_Schedule.csv", entryNames);
    }

    [Fact]
    public void FinancialReportsAndMigration_EnforceRbacSuspendedReadOnlyAndGlobalAdmin()
    {
        var projectViewer = MakeEmployee("proj-only", isAdmin: false, isSuspended: false, AppPermissions.Projects.View);
        var accountant = MakeEmployee("acct-user", isAdmin: false, isSuspended: false, AppPermissions.Finance.AccessAccounting);
        var suspendedAcct = MakeEmployee("acct-susp", isAdmin: false, isSuspended: true, AppPermissions.Finance.AccessAccounting);
        var suspendedAdmin = MakeEmployee("admin-susp", isAdmin: true, isSuspended: true);

        var draft = _financialReports.CreateDraft(
            "Q1 Statements",
            "",
            FinancialReportType.Quarterly,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 3, 31),
            new FinancialReportOptions(),
            "Summary",
            "Notes",
            "Admin",
            AdminActor.Id,
            AdminActor.FullName,
            actor: AdminActor);

        Assert.Throws<UnauthorizedAccessException>(() => _financialReports.GetReports(actor: projectViewer));
        Assert.Throws<UnauthorizedAccessException>(() => _financialReports.CreateDraft(
            "Unauth", "", FinancialReportType.Monthly, new DateTime(2026, 1, 1), new DateTime(2026, 1, 31),
            new FinancialReportOptions(), "", "", "", projectViewer.Id, projectViewer.FullName, actor: projectViewer));
        Assert.Throws<UnauthorizedAccessException>(() => _financialReports.UpdateReport(draft, false, suspendedAcct.Id, suspendedAcct.FullName, actor: suspendedAcct));
        Assert.Throws<UnauthorizedAccessException>(() => _financialReports.ChangeStatus(draft.Id, FinancialReportStatus.Final, suspendedAcct.Id, suspendedAcct.FullName, actor: suspendedAcct));
        Assert.Throws<UnauthorizedAccessException>(() => _financialReports.DeleteReport(draft.Id, actor: suspendedAcct));

        Assert.Single(_financialReports.GetReports(actor: accountant));

        var tbCsv = "Account Number,Account Name,Account Type,Debit,Credit\n1020,Operating Bank,Asset,1000.00,0.00\n3600,Retained Earnings,Equity,0.00,1000.00\n";
        var migReq = new AccountingMigrationPreviewRequest
        {
            Mode = AccountingMigrationMode.FreshCutover,
            AsOfDate = new DateTime(2026, 3, 31),
            Files = [new MigrationUploadedFile { FileName = "tb.csv", Content = Encoding.UTF8.GetBytes(tbCsv), ExplicitKind = MigrationFileKind.TrialBalance }]
        };

        Assert.Throws<UnauthorizedAccessException>(() => _migration.BuildPreview(migReq, accountant));
        Assert.Throws<UnauthorizedAccessException>(() => _migration.BuildPreview(migReq, suspendedAdmin));

        var preview = _migration.BuildPreview(migReq, AdminActor);
        Assert.Throws<UnauthorizedAccessException>(() => _migration.ExecuteMigration(preview, accountant.Id, accountant.FullName, accountant));
        Assert.Throws<UnauthorizedAccessException>(() => _migration.ExecuteMigration(preview, suspendedAdmin.Id, suspendedAdmin.FullName, suspendedAdmin));

        var result = _migration.ExecuteMigration(preview, AdminActor.Id, AdminActor.FullName, AdminActor);
        Assert.Throws<UnauthorizedAccessException>(() => _migration.RollbackMigrationBatch(result.Batch.Id, accountant.FullName, accountant));
        Assert.Throws<UnauthorizedAccessException>(() => _migration.RollbackMigrationBatch(result.Batch.Id, suspendedAdmin.FullName, suspendedAdmin));

        _migration.RollbackMigrationBatch(result.Batch.Id, AdminActor.FullName, AdminActor);
    }

    [Fact]
    public void CpaExportAndBusinessOverhead_ExcludesDraftExpenses_AndEnforcesFileAuthorizationOnVouchers()
    {
        var accountant = MakeEmployee("cpa-acct", isAdmin: false, isSuspended: false, AppPermissions.Finance.AccessAccounting);

        var internalProj = new Project
        {
            Id = "internal-oh-proj",
            ProjectNumber = "INT-01",
            Name = "Internal R&D",
            IsInternal = true,
            AccessPolicy = ProjectAccessPolicy.Restricted
        };
        _db.Projects.Save(internalProj);

        var draftExpense = new ExpenseReport
        {
            Id = "exp-draft-1",
            ReportNumber = "EXP-DRAFT-01",
            EmployeeId = "other-emp",
            EmployeeName = "Other",
            DateSubmitted = null,
            DatePaid = null,
            LineItems =
            [
                new ExpenseItem
                {
                    ProjectId = internalProj.Id,
                    Date = new DateTime(2026, 3, 10),
                    Description = "Draft Taxi Receipt",
                    Amount = 100m,
                    IsTaxExempt = true,
                    ReceiptPath = "/spokesapi/files/expenses/exp-draft-1/draft.pdf"
                }
            ]
        };
        var submittedExpense = new ExpenseReport
        {
            Id = "exp-sub-1",
            ReportNumber = "EXP-SUB-01",
            EmployeeId = "other-emp",
            EmployeeName = "Other",
            DateSubmitted = new DateTime(2026, 3, 12),
            DatePaid = null,
            LineItems =
            [
                new ExpenseItem
                {
                    ProjectId = internalProj.Id,
                    Date = new DateTime(2026, 3, 11),
                    Description = "Submitted Taxi Receipt",
                    Amount = 250m,
                    IsTaxExempt = true,
                    ReceiptPath = "/spokesapi/files/expenses/exp-sub-1/submitted.pdf"
                }
            ]
        };
        _db.ExpenseReports.Save(draftExpense);
        _db.ExpenseReports.Save(submittedExpense);

        var unauthorizedUser = MakeEmployee("no-acct-no-proj", isAdmin: false, isSuspended: false, AppPermissions.Chat.Use);
        var projectViewerOnly = MakeEmployee("proj-viewer-only", isAdmin: false, isSuspended: false, AppPermissions.Projects.View);
        Assert.Throws<UnauthorizedAccessException>(() =>
            _overhead.CalculatePeriodOverhead(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), actor: unauthorizedUser));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _overhead.CalculatePeriodOverhead(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), targetProjectId: internalProj.Id, actor: projectViewerOnly));

        var ohResult = _overhead.CalculatePeriodOverhead(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), actor: accountant);
        Assert.Equal(250m, ohResult.InternalPurchasesAndExpensesPreTax);

        _mockFileService.Setup(f => f.OpenRead("expenses", "exp-sub-1", "submitted.pdf", null))
            .Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("PDF-SUBMITTED")));

        var zipBytes = _exportWithVouchers.GenerateAccountantPackageZip(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), includeVouchers: true, actor: accountant);
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entries = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains(entries, e => e.Contains("submitted.pdf"));
        Assert.DoesNotContain(entries, e => e.Contains("draft.pdf"));
        _mockFileService.Verify(f => f.OpenRead("expenses", "exp-draft-1", It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void AccountingServiceAndSmartPoMatching_EnforceRbacApproveBillsAndRestrictedPoIsolation()
    {
        var billClerk = MakeEmployee("bill-clerk", isAdmin: false, isSuspended: false, AppPermissions.Purchases.ManageBills);
        var poViewerOnly = MakeEmployee("po-viewer", isAdmin: false, isSuspended: false, AppPermissions.Purchases.ViewPOs);
        var projectsOnly = MakeEmployee("proj-user", isAdmin: false, isSuspended: false, AppPermissions.Projects.View, AppPermissions.Projects.Edit);
        var suspendedAdmin = MakeEmployee("admin-susp", isAdmin: true, isSuspended: true);

        var draftBill = new Bill
        {
            Id = "bill-awaiting",
            InvoiceNumber = "INV-AP-1",
            SupplierName = "Acme Supply",
            Status = BillStatus.AwaitingApproval,
            Amount = 500m
        };
        _db.Bills.Save(draftBill);

        // Cannot promote AwaitingApproval -> Unpaid via SaveBill or ApproveBill without Purchases.ApproveBills
        var promotedCopy = new Bill
        {
            Id = draftBill.Id,
            InvoiceNumber = draftBill.InvoiceNumber,
            SupplierName = draftBill.SupplierName,
            Status = BillStatus.Unpaid,
            Amount = 500m
        };
        Assert.Throws<UnauthorizedAccessException>(() => _accountingService.SaveBill(promotedCopy, billClerk.FullName, actor: billClerk));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingService.ApproveBill(draftBill, billClerk.FullName, actor: billClerk));
        Assert.Throws<UnauthorizedAccessException>(() => _accountingService.ApproveBill(draftBill, suspendedAdmin.FullName, actor: suspendedAdmin));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _accountingService.RecordBillPayment(draftBill, new PaymentRecord { Amount = 100m, Method = "EFT" }, billClerk.FullName, actor: billClerk));

        var rec = new RecurringExpense { Id = "rec-1", Name = "Cloud", Amount = 100m, Frequency = RecurringExpenseFrequency.Monthly };
        Assert.Throws<UnauthorizedAccessException>(() => _accountingService.SaveRecurringExpense(rec, actor: projectsOnly));

        var inv = new Invoice { Id = "inv-rbac", InvoiceNumber = "INV-1", Status = InvoiceStatus.Draft };
        Assert.Throws<UnauthorizedAccessException>(() =>
            _accountingService.ProcessInvoiceStatusChange(inv, InvoiceStatus.Draft, InvoiceStatus.Sent, projectsOnly.FullName, actor: projectsOnly));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _accountingService.RecordInvoicePayment(inv, new PaymentRecord { Amount = 50m, Method = "Wire" }, projectsOnly.FullName, actor: projectsOnly));

        // SmartPoMatchingEngine isolation
        var restrictedProj = new Project { Id = "rest-po-proj", ProjectNumber = "RP-1", Name = "Secret Project", AccessPolicy = ProjectAccessPolicy.Restricted };
        _db.Projects.Save(restrictedProj);

        var restrictedPo = new PurchaseOrder
        {
            Id = "po-rest",
            PoNumber = "PO-SECRET-1",
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "Secret Vendor" },
            LineItems = [new PoItem { Description = "Sensor", Quantity = 1, UnitPrice = 250m, ProjectId = restrictedProj.Id }]
        };
        var draftPo = new PurchaseOrder
        {
            Id = "po-draft",
            PoNumber = "PO-DRAFT-1",
            Status = PurchaseOrderStatus.Draft,
            Supplier = new PartyContactInfo { BusinessName = "Open Vendor" },
            LineItems = [new PoItem { Description = "Bolt", Quantity = 1, UnitPrice = 25m }]
        };
        _db.PurchaseOrders.Save(restrictedPo);
        _db.PurchaseOrders.Save(draftPo);

        var noPoPermResult = _poMatchingEngine.FindMatch(new ExtractedInvoiceData { PurchaseOrderReference = "PO-SECRET-1", TotalAmount = 250m }, actor: projectsOnly);
        Assert.Null(noPoPermResult.MatchedPo);

        var restrictedResult = _poMatchingEngine.FindMatch(new ExtractedInvoiceData { PurchaseOrderReference = "PO-SECRET-1", TotalAmount = 250m }, actor: poViewerOnly);
        Assert.Null(restrictedResult.MatchedPo);

        var draftResult = _poMatchingEngine.FindMatch(new ExtractedInvoiceData { PurchaseOrderReference = "PO-DRAFT-1", TotalAmount = 25m }, actor: AdminActor);
        Assert.Null(draftResult.MatchedPo);
    }

    [Fact]
    public async Task CompanyBillInboxService_EnforcesPermissionsSuspensionRestrictedInvoicesAndDecisionLogRedaction()
    {
        var accountantNoBills = MakeEmployee("acct-nobills", isAdmin: false, isSuspended: false, AppPermissions.Finance.AccessAccounting);
        var approveInvoicesOnly = MakeEmployee("inv-approver", isAdmin: false, isSuspended: false, AppPermissions.Finance.ApproveInvoices);
        var billClerk = MakeEmployee("inbox-clerk", isAdmin: false, isSuspended: false, AppPermissions.Purchases.ManageBills, AppPermissions.Finance.AccessAccounting);
        var suspendedClerk = MakeEmployee("inbox-susp", isAdmin: false, isSuspended: true, AppPermissions.Purchases.ManageBills, AppPermissions.Finance.AccessAccounting);

        var restrictedProj = new Project { Id = "inbox-rest-proj", ProjectNumber = "IRP-1", Name = "Classified", AccessPolicy = ProjectAccessPolicy.Restricted };
        _db.Projects.Save(restrictedProj);

        var restrictedPo = new PurchaseOrder
        {
            Id = "inbox-rest-po",
            PoNumber = "PO-CLASS-99",
            Status = PurchaseOrderStatus.Final,
            Supplier = new PartyContactInfo { BusinessName = "Classified Parts" },
            LineItems = [new PoItem { Description = "Part", Quantity = 1, UnitPrice = 999m, ProjectId = restrictedProj.Id }]
        };
        _db.PurchaseOrders.Save(restrictedPo);

        var existingBillOnRestrictedPo = new Bill
        {
            Id = "bill-rest-1",
            InvoiceNumber = "EXT-999",
            SupplierName = "Classified Parts",
            PurchaseOrderId = restrictedPo.Id,
            Amount = 999m,
            Status = BillStatus.Unpaid,
            Date = new DateTime(2026, 3, 15)
        };
        _db.Bills.Save(existingBillOnRestrictedPo);

        var restrictedInvoice = new Invoice
        {
            Id = "inv-rest-1",
            InvoiceNumber = "CINV-99",
            ProjectId = restrictedProj.Id,
            Status = InvoiceStatus.Sent,
            LineItems = [new InvoiceLine { Description = "Work", Quantity = 1, UnitPrice = 400m }]
        };
        _db.Invoices.Save(restrictedInvoice);

        var email = new EmailMessage
        {
            Id = "email-inbox-rbac",
            AccountId = CompanyBillInboxConstants.SystemEmployeeId,
            Subject = "Invoice EXT-999 for 999.00",
            Snippet = "Amount $999.00",
            FromAddress = "billing@classified.example",
            FromName = "Classified Parts",
            Date = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero),
            BillingStatus = CompanyBillInboxConstants.BillingStatus.AutoDrafted,
            LinkedBillId = existingBillOnRestrictedPo.Id,
            LinkedBillIds = [existingBillOnRestrictedPo.Id],
            DetectedAmount = 999m
        };
        _db.EmailMessages.Save(email);

        // Unauthorized / suspended inbox mutations
        Assert.Throws<UnauthorizedAccessException>(() =>
            _inboxService.SaveSettings(new CompanyBillInboxSettings(), actor: billClerk));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _inboxService.BlockSenderDomainAsync("spam@example.com", email.Id, actor: billClerk));
        Assert.Throws<UnauthorizedAccessException>(() =>
            _inboxService.ApproveBillFromEmail(email.Id, existingBillOnRestrictedPo.Id, billClerk.Id, actor: billClerk));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _inboxService.DeleteEmailAsync(email.Id, actor: suspendedClerk));

        // Restricted-project invoice receipt linking blocked for Finance.ApproveInvoices without Finance.AccessAccounting or project access
        Assert.Throws<UnauthorizedAccessException>(() =>
            _inboxService.LinkReceiptToInvoice(email.Id, restrictedInvoice.Id, approveInvoicesOnly.Id, actor: approveInvoicesOnly));

        // Decision log redacts restricted PO/bill details when actor lacks Purchases.ViewBills or project access
        var preExtracted = new List<(ExtractedInvoiceData, string, string)>
        {
            (new ExtractedInvoiceData
            {
                InvoiceNumber = "EXT-999",
                PurchaseOrderReference = "PO-CLASS-99",
                SupplierName = "Classified Parts",
                TotalAmount = 999m,
                InvoiceDate = new DateTime(2026, 3, 15)
            }, string.Empty, "inv.pdf")
        };

        var redactedLog = await _inboxService.GenerateDecisionLogAsync(email, preExtracted, actor: accountantNoBills);
        Assert.Empty(redactedLog.DuplicateMatches);
        Assert.DoesNotContain(redactedLog.Steps, s => s.StepName == "Purchase Order Matching" && s.Status == DecisionStepStatus.Success);

        var unauthorizedPoBillLog = await _inboxService.GenerateDecisionLogAsync(email, preExtracted, actor: billClerk);
        Assert.Empty(unauthorizedPoBillLog.DuplicateMatches);
        Assert.DoesNotContain(unauthorizedPoBillLog.Steps, s => s.StepName == "Purchase Order Matching" && s.Status == DecisionStepStatus.Success);

        var adminLog = await _inboxService.GenerateDecisionLogAsync(email, preExtracted, actor: AdminActor);
        Assert.Single(adminLog.DuplicateMatches);
    }

    [Fact]
    public void AccountantExport_RestrictedProjectFinancialsVisibleToFinance_DeniedWithoutAccountingPermission()
    {
        var accountant = MakeEmployee("exp-acct", isAdmin: false, isSuspended: false, AppPermissions.Finance.AccessAccounting);
        var projMemberWithoutFinance = MakeEmployee("exp-proj-user", isAdmin: false, isSuspended: false, AppPermissions.Projects.View);

        var restrictedProject = new Project
        {
            Id = "rest-acct-vis",
            ProjectNumber = "RAV-01",
            Name = "Confidential Accounting Project",
            IsInternal = true,
            AccessPolicy = ProjectAccessPolicy.Restricted
        };
        _db.Projects.Save(restrictedProject);

        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-rest-sub",
            ReportNumber = "EXP-REST-1",
            EmployeeId = "worker-1",
            EmployeeName = "Worker",
            DateSubmitted = new DateTime(2026, 3, 15),
            LineItems =
            [
                new ExpenseItem
                {
                    ProjectId = restrictedProject.Id,
                    Date = new DateTime(2026, 3, 15),
                    Description = "Lab Equipment",
                    Amount = 420m,
                    IsTaxExempt = true
                }
            ]
        });

        Assert.Throws<UnauthorizedAccessException>(() =>
            _exportWithVouchers.GenerateBusinessOverheadAndProjectAllocationCsv(
                new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), actor: projMemberWithoutFinance));

        var allocationCsv = _exportWithVouchers.GenerateBusinessOverheadAndProjectAllocationCsv(
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 31), actor: accountant);

        Assert.Contains("Confidential Accounting Project", allocationCsv);
    }
}
