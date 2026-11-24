namespace Spokes_Server.Core.Services.Accounting.Export;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;
using Spokes_Server.Core.Services.Storage;

public class AccountantAdjustingImportResult
{
    public int ImportedEntryCount { get; set; }
    public int ImportedLineCount { get; set; }
    public List<JournalEntry> ImportedEntries { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public interface IAccountantExportService
{
    string GetSuggestedPackageFileName(DateTime startDate, DateTime endDate);
    byte[] GenerateAccountantPackageZip(DateTime startDate, DateTime endDate, bool includeVouchers = true, Employee? actor = null);
    string GenerateCpaReadmeTxt(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateTrialBalanceCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateGeneralLedgerCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateIncomeStatementCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateBalanceSheetCsv(DateTime asOfDate, Employee? actor = null);
    string GenerateAgedReceivablesAndPayablesCsv(DateTime asOfDate, Employee? actor = null);
    string GenerateSalesTaxSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GeneratePayrollAndDasSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateBusinessOverheadAndProjectAllocationCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateGifiScheduleCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateGifiTaxImportGff(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateFixedAssetsAndCapExCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateBankReconciliationSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateProjectAndRnDLaborScheduleCsv(DateTime startDate, DateTime endDate, Employee? actor = null);
    string GenerateSage50ImportImp(DateTime startDate, DateTime endDate, Employee? actor = null);
    AccountantAdjustingImportResult ImportAdjustingEntriesCsv(string csvContent, string? importedBy = null, Employee? actor = null);
}

public class AccountantExportService : IAccountantExportService
{
    private readonly AccountingContext _accountingContext;
    private readonly CoreContext _coreContext;
    private readonly ILedgerService _ledgerService;
    private readonly ITaxRemittanceService _taxRemittanceService;
    private readonly IPayrollCalculationEngine _payrollEngine;
    private readonly IBusinessOverheadService? _overheadService;
    private readonly IFileService? _fileService;
    private readonly IFileAuthorizationService? _fileAuthorization;
    private readonly ProjectsContext? _projectsContext;
    private readonly HrContext? _hrContext;

    public AccountantExportService(
        AccountingContext accountingContext,
        CoreContext coreContext,
        ILedgerService ledgerService,
        ITaxRemittanceService taxRemittanceService,
        IPayrollCalculationEngine payrollEngine,
        IBusinessOverheadService? overheadService = null,
        IFileService? fileService = null,
        ProjectsContext? projectsContext = null,
        HrContext? hrContext = null,
        IFileAuthorizationService? fileAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(accountingContext);
        ArgumentNullException.ThrowIfNull(coreContext);
        ArgumentNullException.ThrowIfNull(ledgerService);
        ArgumentNullException.ThrowIfNull(taxRemittanceService);
        ArgumentNullException.ThrowIfNull(payrollEngine);

        _accountingContext = accountingContext;
        _coreContext = coreContext;
        _ledgerService = ledgerService;
        _taxRemittanceService = taxRemittanceService;
        _payrollEngine = payrollEngine;
        _overheadService = overheadService;
        _fileService = fileService;
        _projectsContext = projectsContext;
        _hrContext = hrContext;
        _fileAuthorization = fileAuthorization;
    }

    public string GetSuggestedPackageFileName(DateTime startDate, DateTime endDate)
    {
        var companyName = _coreContext.CompanyProfile.Get()?.CompanyName;
        var safeCompany = string.IsNullOrWhiteSpace(companyName)
            ? "Company"
            : new string(companyName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        if (string.IsNullOrWhiteSpace(safeCompany))
            safeCompany = "Company";

        return $"Spokes_Accountant_Package_{safeCompany}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}.zip";
    }

    public byte[] GenerateAccountantPackageZip(DateTime startDate, DateTime endDate, bool includeVouchers = true, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export accountant ZIP package", allowSuspended: true);
        bool canManageSalaries = actor!.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true);

        var start = startDate.Date;
        var end = endDate.Date;
        if (start > end)
        {
            (start, end) = (end, start);
        }

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteUtf8Entry(archive, "00_CPA_README.txt", GenerateCpaReadmeTxt(start, end, actor));
            WriteUtf8Entry(archive, "01_Trial_Balance.csv", GenerateTrialBalanceCsv(start, end, actor));
            WriteUtf8Entry(archive, "02_General_Ledger_Transactions.csv", GenerateGeneralLedgerCsv(start, end, actor));
            WriteUtf8Entry(archive, "03_Income_Statement_PL.csv", GenerateIncomeStatementCsv(start, end, actor));
            WriteUtf8Entry(archive, "04_Balance_Sheet.csv", GenerateBalanceSheetCsv(end, actor));
            WriteUtf8Entry(archive, "05_Aged_Receivables_And_Payables.csv", GenerateAgedReceivablesAndPayablesCsv(end, actor));
            WriteUtf8Entry(archive, "06_Sales_Tax_Summary.csv", GenerateSalesTaxSummaryCsv(start, end, actor));
            if (canManageSalaries)
            {
                WriteUtf8Entry(archive, "07_Payroll_And_DAS_Summary.csv", GeneratePayrollAndDasSummaryCsv(start, end, actor));
            }
            WriteUtf8Entry(archive, "08_Business_Overhead_And_Project_Allocation.csv", GenerateBusinessOverheadAndProjectAllocationCsv(start, end, actor));
            WriteUtf8Entry(archive, "09_GIFI_Schedule_100_125.csv", GenerateGifiScheduleCsv(start, end, actor));
            WriteUtf8Entry(archive, "09_GIFI_Tax_Import.gff", GenerateGifiTaxImportGff(start, end, actor));
            WriteUtf8Entry(archive, "10_Fixed_Assets_And_CapEx_Schedule.csv", GenerateFixedAssetsAndCapExCsv(start, end, actor));
            WriteUtf8Entry(archive, "11_Bank_Reconciliation_Summary.csv", GenerateBankReconciliationSummaryCsv(start, end, actor));
            if (canManageSalaries)
            {
                WriteUtf8Entry(archive, "12_Project_And_RnD_Labor_Schedule.csv", GenerateProjectAndRnDLaborScheduleCsv(start, end, actor));
            }
            WriteUtf8Entry(archive, "13_Sage50_Import_Ready.imp", GenerateSage50ImportImp(start, end, actor));

            if (includeVouchers && _fileService != null)
            {
                AppendVouchersToArchive(archive, start, end, actor);
            }
        }

        return memoryStream.ToArray();
    }

    public string GenerateTrialBalanceCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export trial balance", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("AccountCode,AccountName,AccountType,AccountSubType,StandardTaxCode,OpeningBalance,PeriodDebits,PeriodCredits,EndingBalance,TrialBalanceDebit,TrialBalanceCredit");

        var report = _ledgerService.CalculateTrialBalance(startDate, endDate);
        foreach (var row in report.Rows)
        {
            sb.AppendLine(string.Join(",",
                CsvSanitizer.EscapeCsv(row.AccountCode),
                CsvSanitizer.EscapeCsv(row.AccountName),
                CsvSanitizer.EscapeCsv(row.AccountType),
                CsvSanitizer.EscapeCsv(row.SubType),
                CsvSanitizer.EscapeCsv(row.StandardTaxCode),
                FormatDecimal(row.OpeningBalance),
                FormatDecimal(row.PeriodDebits),
                FormatDecimal(row.PeriodCredits),
                FormatDecimal(row.EndingBalance),
                FormatDecimal(row.TrialBalanceDebitColumn),
                FormatDecimal(row.TrialBalanceCreditColumn)));
        }

        return sb.ToString();
    }

    public string GenerateGeneralLedgerCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export general ledger", allowSuspended: true);
        var start = startDate.Date;
        var end = endDate.Date;

        var accountsByCode = (_accountingContext.GlAccounts?.GetAll() ?? [])
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

        var entries = (_accountingContext.JournalEntries?.GetByDateRange(start, end) ?? [])
            .OrderBy(e => e.Date)
            .ThenBy(e => e.EntryNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("EntryNumber,Date,SourceType,Reference,AccountCode,AccountName,StandardTaxCode,Description,PartyName,ProjectId,TaxCode,Debit,Credit,IsReconciled");

        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines)
            {
                accountsByCode.TryGetValue(line.GlAccountCode, out var acct);
                var accountName = !string.IsNullOrWhiteSpace(line.GlAccountName)
                    ? line.GlAccountName
                    : (acct?.Name ?? line.GlAccountCode);
                var standardTaxCode = acct?.StandardTaxCode ?? string.Empty;
                var desc = !string.IsNullOrWhiteSpace(line.Memo) ? line.Memo : entry.Description;

                sb.AppendLine(string.Join(",",
                    CsvSanitizer.EscapeCsv(entry.EntryNumber),
                    entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CsvSanitizer.EscapeCsv(entry.SourceType),
                    CsvSanitizer.EscapeCsv(entry.Reference),
                    CsvSanitizer.EscapeCsv(line.GlAccountCode),
                    CsvSanitizer.EscapeCsv(accountName),
                    CsvSanitizer.EscapeCsv(standardTaxCode),
                    CsvSanitizer.EscapeCsv(desc),
                    CsvSanitizer.EscapeCsv(entry.PartyName),
                    CsvSanitizer.EscapeCsv(line.ProjectId ?? entry.ProjectId),
                    CsvSanitizer.EscapeCsv(line.TaxCode),
                    FormatDecimal(line.Debit),
                    FormatDecimal(line.Credit),
                    entry.IsReconciled ? "TRUE" : "FALSE"));
            }
        }

        return sb.ToString();
    }

    public string GenerateIncomeStatementCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export income statement", allowSuspended: true);
        bool canManageSalaries = actor!.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true);

        var report = _ledgerService.CalculateIncomeStatement(startDate, endDate);
        var sb = new StringBuilder();
        sb.AppendLine("Section,AccountCode,AccountName,StandardTaxCode,Amount");

        foreach (var line in report.RevenueLines)
        {
            sb.AppendLine(string.Join(",",
                "Revenue",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "TotalRevenue", "", "Total Revenue", "", FormatDecimal(report.TotalRevenue)));

        foreach (var line in report.DirectCostLines)
        {
            sb.AppendLine(string.Join(",",
                "CostOfServices",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "GrossProfit", "", "Gross Profit", "", FormatDecimal(report.GrossProfit)));

        if (canManageSalaries)
        {
            foreach (var line in report.PayrollExpenseLines)
            {
                sb.AppendLine(string.Join(",",
                    "OperatingExpense",
                    CsvSanitizer.EscapeCsv(line.AccountCode),
                    CsvSanitizer.EscapeCsv(line.AccountName),
                    CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                    FormatDecimal(line.Amount)));
            }
        }
        else if (report.PayrollExpenseLines.Count > 0 || report.TotalPayrollExpenses != 0m)
        {
            sb.AppendLine(string.Join(",",
                "OperatingExpense",
                "5000",
                "Payroll & Employee Compensation",
                "",
                FormatDecimal(report.TotalPayrollExpenses)));
        }

        foreach (var line in report.OverheadExpenseLines)
        {
            sb.AppendLine(string.Join(",",
                "OperatingExpense",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "TotalOperatingExpenses", "", "Total Operating Expenses", "", FormatDecimal(report.TotalOperatingExpenses)));
        sb.AppendLine(string.Join(",", "NetIncome", "", "Net Income", "", FormatDecimal(report.NetIncome)));

        return sb.ToString();
    }

    public string GenerateBalanceSheetCsv(DateTime asOfDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export balance sheet", allowSuspended: true);
        var report = _ledgerService.CalculateBalanceSheet(asOfDate);
        var sb = new StringBuilder();
        sb.AppendLine("Section,AccountCode,AccountName,StandardTaxCode,Balance");

        foreach (var line in report.AssetLines)
        {
            sb.AppendLine(string.Join(",",
                "Asset",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "TotalAssets", "", "Total Assets", "", FormatDecimal(report.TotalAssets)));

        foreach (var line in report.LiabilityLines)
        {
            sb.AppendLine(string.Join(",",
                "Liability",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "TotalLiabilities", "", "Total Liabilities", "", FormatDecimal(report.TotalLiabilities)));

        foreach (var line in report.EquityLines)
        {
            sb.AppendLine(string.Join(",",
                "Equity",
                CsvSanitizer.EscapeCsv(line.AccountCode),
                CsvSanitizer.EscapeCsv(line.AccountName),
                CsvSanitizer.EscapeCsv(line.StandardTaxCode),
                FormatDecimal(line.Amount)));
        }

        sb.AppendLine(string.Join(",", "Equity", "3200-CUR", "Current Period Net Income", "3680", FormatDecimal(report.CurrentYearNetIncome)));
        sb.AppendLine(string.Join(",", "TotalEquity", "", "Total Equity", "", FormatDecimal(report.TotalEquity)));
        sb.AppendLine(string.Join(",", "TotalLiabilitiesAndEquity", "", "Total Liabilities & Equity", "", FormatDecimal(report.TotalLiabilitiesAndEquity)));

        return sb.ToString();
    }

    public string GenerateAgedReceivablesAndPayablesCsv(DateTime asOfDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export aged receivables and payables", allowSuspended: true);
        var cutoff = asOfDate.Date;
        var sb = new StringBuilder();
        sb.AppendLine("SubledgerType,DocumentNumber,PartyName,DocumentDate,DueDate,DaysPastDue,AgingBucket,TotalAmount,PaidAmount,BalanceRemaining");

        var openInvoices = _accountingContext.Invoices.GetAll()
            .Where(i => InvoiceStatus.IsPosted(i.Status)
                        && i.Date.Date <= cutoff
                        && i.BalanceDue > 0.009m)
            .OrderBy(i => i.Date)
            .ToList();

        foreach (var inv in openInvoices)
        {
            var dueDate = inv.DueDate.Date;
            var daysPastDue = Math.Max(0, (cutoff - dueDate).Days);
            var proj = !string.IsNullOrWhiteSpace(inv.ProjectId)
                ? _projectsContext?.Projects.GetById(inv.ProjectId)
                : null;
            var clientName = proj?.Client?.BusinessName;
            if (string.IsNullOrWhiteSpace(clientName))
                clientName = proj?.Name;
            if (string.IsNullOrWhiteSpace(clientName))
                clientName = inv.Note;

            sb.AppendLine(string.Join(",",
                "AccountsReceivable",
                CsvSanitizer.EscapeCsv(inv.InvoiceNumber),
                CsvSanitizer.EscapeCsv(clientName),
                inv.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                daysPastDue.ToString(CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(GetAgingBucket(daysPastDue)),
                FormatDecimal(inv.GrandTotal),
                FormatDecimal(inv.AmountPaid),
                FormatDecimal(inv.BalanceDue)));
        }

        var openBills = _accountingContext.Bills.GetAll()
            .Where(b => !b.IsVoid
                        && b.Status != BillStatus.Void
                        && b.Date.Date <= cutoff
                        && b.BalanceDue > 0.009m)
            .OrderBy(b => b.Date)
            .ToList();

        foreach (var bill in openBills)
        {
            var dueDate = bill.DueDate.Date;
            var daysPastDue = Math.Max(0, (cutoff - dueDate).Days);
            sb.AppendLine(string.Join(",",
                "AccountsPayable",
                CsvSanitizer.EscapeCsv(bill.InvoiceNumber ?? bill.Description),
                CsvSanitizer.EscapeCsv(bill.SupplierName),
                bill.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                daysPastDue.ToString(CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(GetAgingBucket(daysPastDue)),
                FormatDecimal(bill.Amount),
                FormatDecimal(bill.AmountPaid),
                FormatDecimal(bill.BalanceDue)));
        }

        return sb.ToString();
    }

    public string GenerateSalesTaxSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export sales tax summary", allowSuspended: true);
        var period = _taxRemittanceService.CalculateTaxPeriodSummary(startDate, endDate);
        var sb = new StringBuilder();
        sb.AppendLine("PeriodStartDate,PeriodEndDate,TaxCode,AuthorityName,TotalSalesBeforeTax,TaxCollectedOnSales,TotalPurchasesBeforeTax,InputTaxCreditsOnPurchases,NetTaxOwing");

        foreach (var line in period.AuthorityLines)
        {
            sb.AppendLine(string.Join(",",
                period.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                period.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(line.TaxCode),
                CsvSanitizer.EscapeCsv(line.AuthorityName),
                FormatDecimal(period.TotalSalesBeforeTax),
                FormatDecimal(line.TaxCollectedOnSales),
                FormatDecimal(period.TotalPurchasesBeforeTax),
                FormatDecimal(line.InputTaxCreditsOnPurchases),
                FormatDecimal(line.NetTaxOwing)));
        }

        return sb.ToString();
    }

    public string GeneratePayrollAndDasSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export payroll and DAS summary", allowSuspended: true);
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "export employee payroll tax slips", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("RecordType,Year,EmployeeName,JurisdictionId,GrossEarnings,IncomeTaxWithheld,EmployeeContributions,EmployerContributions,TotalNetPay");

        var years = Enumerable.Range(startDate.Year, Math.Max(1, endDate.Year - startDate.Year + 1));
        foreach (var year in years)
        {
            var slips = _payrollEngine.CalculateYearEndSlipsSummary(year, actor);
            foreach (var slip in slips)
            {
                var totalTax = slip.FederalIncomeTax + slip.RegionalIncomeTax;
                var totalEmpContrib = slip.QppOrCppEmployee + slip.Qpp2OrCpp2Employee + slip.EiEmployee + slip.QpipEmployee;

                sb.AppendLine(string.Join(",",
                    "AnnualSlipSummary",
                    year.ToString(CultureInfo.InvariantCulture),
                    CsvSanitizer.EscapeCsv(slip.EmployeeName),
                    CsvSanitizer.EscapeCsv(slip.JurisdictionId),
                    FormatDecimal(slip.GrossEmploymentIncome),
                    FormatDecimal(totalTax),
                    FormatDecimal(totalEmpContrib),
                    FormatDecimal(slip.TotalEmployerTaxes),
                    FormatDecimal(slip.TotalNetPay)));
            }
        }

        return sb.ToString();
    }

    public string GenerateBusinessOverheadAndProjectAllocationCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export business overhead schedule", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("Category,Name,Details,CompanyPoolPreTax,CompanyPoolTax,CompanyPoolTotal");

        if (_overheadService == null)
            return sb.ToString();

        var result = _overheadService.CalculatePeriodOverhead(startDate, endDate, actor: actor);
        foreach (var row in result.BreakdownRows)
        {
            sb.AppendLine(string.Join(",",
                CsvSanitizer.EscapeCsv(row.Category),
                CsvSanitizer.EscapeCsv(row.Name),
                CsvSanitizer.EscapeCsv(row.Details),
                FormatDecimal(row.CompanyPoolPreTax),
                FormatDecimal(row.CompanyPoolTax),
                FormatDecimal(row.CompanyPoolTotal)));
        }

        sb.AppendLine(string.Join(",",
            "Summary",
            "Total Company Overhead Pool",
            $"Direct Client Labor Hours: {result.TotalCompanyDirectLaborHours:F2}h | Internal Unbillable Hours: {result.InternalLaborHours:F2}h",
            FormatDecimal(result.CompanyOverheadPoolPreTax),
            FormatDecimal(result.CompanyOverheadPoolTax),
            FormatDecimal(result.CompanyOverheadPoolTotal)));

        return sb.ToString();
    }

    public string GenerateCpaReadmeTxt(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export CPA README", allowSuspended: true);
        bool canManageSalaries = actor!.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true);

        var companyName = _coreContext.CompanyProfile.Get()?.CompanyName ?? "Company";
        var config = _accountingContext.GetAccountingConfig();
        var currency = !string.IsNullOrWhiteSpace(config.DefaultCurrency) ? config.DefaultCurrency : "CAD";
        var tb = _ledgerService.CalculateTrialBalance(startDate, endDate);

        var sb = new StringBuilder();
        sb.AppendLine("========================================================================");
        sb.AppendLine($"SPOKES CPA YEAR-END & TAX WORKING PAPERS PACKAGE — {companyName}");
        sb.AppendLine("========================================================================");
        sb.AppendLine($"Period Covered : {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Currency       : {currency}");
        sb.AppendLine($"Basis          : Accrual Double-Entry General Ledger");
        sb.AppendLine($"TB Equilibrium : Debits {tb.TotalEndingDebitColumn:N2} vs Credits {tb.TotalEndingCreditColumn:N2} ({(tb.IsBalanced ? "BALANCED" : "OUT OF BALANCE")})");
        sb.AppendLine();
        sb.AppendLine("PACKAGE CONTENTS:");
        sb.AppendLine("  01_Trial_Balance.csv                       - Opening, Period Movement, and Ending Trial Balance with GIFI codes");
        sb.AppendLine("  02_General_Ledger_Transactions.csv         - Full chronological double-entry journal lines & project/party tags");
        sb.AppendLine("  03_Income_Statement_PL.csv                 - Profit & Loss (Revenue, COGS, Payroll, Overhead, Net Income)");
        sb.AppendLine("  04_Balance_Sheet.csv                       - Statement of Financial Position as of Period End");
        sb.AppendLine("  05_Aged_Receivables_And_Payables.csv       - Open AR Invoices (with Client Name) and Open AP Bills + aging buckets");
        sb.AppendLine("  06_Sales_Tax_Summary.csv                   - GST/HST & QST/PST collected, Input Tax Credits (ITCs), Net Owing");
        if (canManageSalaries)
        {
            sb.AppendLine("  07_Payroll_And_DAS_Summary.csv             - T4 / RL-1 Annual Payroll & Source Deductions (QPP/RRQ, EI, QPIP)");
        }
        else
        {
            sb.AppendLine("  07_Payroll_And_DAS_Summary.csv             - [OMITTED — Requires Hr.ManageSalaries permission]");
        }
        sb.AppendLine("  08_Business_Overhead_And_Project_Allocation.csv - Shared company overhead pool & direct/internal labor hours");
        sb.AppendLine("  09_GIFI_Schedule_100_125.csv               - Aggregated CRA GIFI Schedule 100 (Balance Sheet) & 125 (Income Statement)");
        sb.AppendLine("  09_GIFI_Tax_Import.gff                     - Direct CRA T2 GIFI import file (TaxCycle / Profile / DT Max / Cantax)");
        sb.AppendLine("  10_Fixed_Assets_And_CapEx_Schedule.csv     - Capital Assets summary + CapEx transactions for CCA depreciation scheduling");
        sb.AppendLine("  11_Bank_Reconciliation_Summary.csv         - Bank & Credit Card GL vs Reconciled Statements & Uncleared items");
        if (canManageSalaries)
        {
            sb.AppendLine("  12_Project_And_RnD_Labor_Schedule.csv      - Project & SR&ED / RS&DE labor hours and estimated salary cost breakdown");
        }
        else
        {
            sb.AppendLine("  12_Project_And_RnD_Labor_Schedule.csv      - [OMITTED — Requires Hr.ManageSalaries permission]");
        }
        sb.AppendLine("  13_Sage50_Import_Ready.imp                 - Sage 50 Accounting Chart of Accounts & Journal Entry import file");
        sb.AppendLine("  Vouchers/                                  - Supporting Bill receipts, Expense receipts, Direct Transaction slips, and Bank Statement PDFs/CSVs");
        sb.AppendLine();
        sb.AppendLine("RETURNING ADJUSTING JOURNAL ENTRIES TO SPOKES:");
        sb.AppendLine("  You may provide year-end adjusting journal entries in CSV format with columns:");
        sb.AppendLine("    EntryNumber,Date,AccountCode,AccountName,Description,Debit,Credit");
        sb.AppendLine("  Management can import that CSV in one click via Accounting -> Import CPA Adjusting CSV.");
        return sb.ToString();
    }

    public string GenerateGifiScheduleCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export GIFI schedule", allowSuspended: true);
        bool canManageSalaries = actor!.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true);

        var bs = _ledgerService.CalculateBalanceSheet(endDate);
        var pl = _ledgerService.CalculateIncomeStatement(startDate, endDate);
        var accountsByCode = (_accountingContext.GlAccounts?.GetAll() ?? [])
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

        string ResolveGifi(string code, string fallbackGifi) =>
            accountsByCode.TryGetValue(code, out var acct) && !string.IsNullOrWhiteSpace(acct.StandardTaxCode)
                ? acct.StandardTaxCode.Trim()
                : fallbackGifi;

        var gifiRows = new Dictionary<(string Schedule, string GifiCode), (string AccountNames, decimal Total)>();

        void Accumulate(string schedule, string gifiCode, string accountName, decimal amount)
        {
            if (amount == 0m) return;
            var key = (schedule, gifiCode);
            if (!gifiRows.TryGetValue(key, out var existing))
            {
                gifiRows[key] = (accountName, amount);
            }
            else
            {
                var names = existing.AccountNames.Contains(accountName, StringComparison.OrdinalIgnoreCase)
                    ? existing.AccountNames
                    : $"{existing.AccountNames}; {accountName}";
                gifiRows[key] = (names, existing.Total + amount);
            }
        }

        foreach (var a in bs.AssetLines)
            Accumulate("Schedule 100 - Balance Sheet (Assets)", ResolveGifi(a.AccountCode, "1000"), a.AccountName, a.Amount);
        foreach (var l in bs.LiabilityLines)
            Accumulate("Schedule 100 - Balance Sheet (Liabilities)", ResolveGifi(l.AccountCode, "2620"), l.AccountName, l.Amount);
        foreach (var e in bs.EquityLines)
            Accumulate("Schedule 100 - Balance Sheet (Equity)", ResolveGifi(e.AccountCode, "3600"), e.AccountName, e.Amount);
        if (bs.CurrentYearNetIncome != 0m)
            Accumulate("Schedule 100 - Balance Sheet (Equity)", "3680", "Current Year Net Income / (Loss)", bs.CurrentYearNetIncome);

        foreach (var r in pl.RevenueLines)
            Accumulate("Schedule 125 - Income Statement (Revenue)", ResolveGifi(r.AccountCode, "8000"), r.AccountName, r.Amount);
        foreach (var c in pl.DirectCostLines)
            Accumulate("Schedule 125 - Income Statement (COGS)", ResolveGifi(c.AccountCode, "8300"), c.AccountName, c.Amount);
        if (canManageSalaries)
        {
            foreach (var op in pl.PayrollExpenseLines)
                Accumulate("Schedule 125 - Income Statement (Expenses)", ResolveGifi(op.AccountCode, "8810"), op.AccountName, op.Amount);
        }
        else if (pl.TotalPayrollExpenses != 0m)
        {
            Accumulate("Schedule 125 - Income Statement (Expenses)", ResolveGifi("5000", "8810"), "Payroll & Employee Compensation", pl.TotalPayrollExpenses);
        }
        foreach (var op in pl.OverheadExpenseLines)
            Accumulate("Schedule 125 - Income Statement (Expenses)", ResolveGifi(op.AccountCode, "8810"), op.AccountName, op.Amount);

        var sb = new StringBuilder();
        sb.AppendLine("Schedule,GIFI_Code,ContributingSpokesAccounts,AmountExact,RoundedWholeDollar");
        foreach (var kvp in gifiRows.OrderBy(k => k.Key.Schedule).ThenBy(k => k.Key.GifiCode, StringComparer.Ordinal))
        {
            var rounded = Math.Round(kvp.Value.Total, 0, MidpointRounding.AwayFromZero);
            sb.AppendLine(string.Join(",",
                CsvSanitizer.EscapeCsv(kvp.Key.Schedule),
                CsvSanitizer.EscapeCsv(kvp.Key.GifiCode),
                CsvSanitizer.EscapeCsv(kvp.Value.AccountNames),
                FormatDecimal(kvp.Value.Total),
                rounded.ToString("0", CultureInfo.InvariantCulture)));
        }

        return sb.ToString();
    }

    public string GenerateGifiTaxImportGff(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export GIFI tax import file", allowSuspended: true);
        var csv = GenerateGifiScheduleCsv(startDate, endDate, actor);
        var rows = CsvReaderHelper.Parse(csv).Skip(1).ToList();
        var sb = new StringBuilder();
        var totalsByGifi = new SortedDictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var r in rows)
        {
            if (r.Count < 5) continue;
            var code = r[1].Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;
            var rounded = ParseAmount(r[4]);
            totalsByGifi[code] = totalsByGifi.GetValueOrDefault(code) + rounded;
        }

        foreach (var (code, amt) in totalsByGifi)
        {
            if (amt == 0m) continue;
            sb.AppendLine($"{code},{amt.ToString("0", CultureInfo.InvariantCulture)}");
        }

        return sb.ToString();
    }

    public string GenerateFixedAssetsAndCapExCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export fixed assets and CapEx schedule", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("RowType,Date,AccountCode,AccountName,ReferenceOrEntry,Description,OpeningBalance,AdditionsDebits,DisposalsOrAmortCredits,EndingBalance");

        var tb = _ledgerService.CalculateTrialBalance(startDate, endDate, includeZeroAccounts: true);
        var fixedAssetAccounts = tb.Rows
            .Where(r => string.Equals(r.SubType, GlAccountSubType.FixedAsset, StringComparison.OrdinalIgnoreCase) ||
                        r.AccountName.Contains("Depreciation", StringComparison.OrdinalIgnoreCase) ||
                        r.AccountName.Contains("Amortization", StringComparison.OrdinalIgnoreCase) ||
                        r.AccountName.Contains("Amortissement", StringComparison.OrdinalIgnoreCase) ||
                        r.AccountCode.StartsWith("15", StringComparison.Ordinal) ||
                        r.AccountCode.StartsWith("16", StringComparison.Ordinal))
            .OrderBy(r => r.AccountCode)
            .ToList();

        var assetCodes = fixedAssetAccounts.Select(a => a.AccountCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in fixedAssetAccounts)
        {
            sb.AppendLine(string.Join(",",
                "AssetAccountSummary",
                endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CsvSanitizer.EscapeCsv(row.AccountCode),
                CsvSanitizer.EscapeCsv(row.AccountName),
                "",
                CsvSanitizer.EscapeCsv($"GL Class Balance Summary ({row.StandardTaxCode})"),
                FormatDecimal(row.OpeningBalance),
                FormatDecimal(row.PeriodDebits),
                FormatDecimal(row.PeriodCredits),
                FormatDecimal(row.EndingBalance)));
        }

        var entries = (_accountingContext.JournalEntries?.GetByDateRange(startDate, endDate) ?? [])
            .OrderBy(e => e.Date)
            .ToList();

        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines)
            {
                bool isAssetAccount = assetCodes.Contains(line.GlAccountCode);
                bool isPotentialCapExExpense = !isAssetAccount &&
                                               Math.Max(line.Debit, line.Credit) >= 500m &&
                                               line.GlAccountCode.StartsWith("5", StringComparison.Ordinal);
                if (!isAssetAccount && !isPotentialCapExExpense) continue;

                sb.AppendLine(string.Join(",",
                    isAssetAccount ? "CapExTransaction" : "PotentialCapExOver500",
                    entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CsvSanitizer.EscapeCsv(line.GlAccountCode),
                    CsvSanitizer.EscapeCsv(line.GlAccountName),
                    CsvSanitizer.EscapeCsv($"{entry.EntryNumber} {entry.Reference}".Trim()),
                    CsvSanitizer.EscapeCsv(string.IsNullOrWhiteSpace(line.Memo) ? entry.Description : line.Memo),
                    "0.00",
                    FormatDecimal(line.Debit),
                    FormatDecimal(line.Credit),
                    FormatDecimal(line.Debit - line.Credit)));
            }
        }

        return sb.ToString();
    }

    public string GenerateBankReconciliationSummaryCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export bank reconciliation summary", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("RecordType,AccountCode,AccountName,StatementOrStatus,PeriodEndDate,StatementClosingBalance,GlEndingBalance,ReconciledMovement,UnreconciledMovement,Difference");

        var tb = _ledgerService.CalculateTrialBalance(startDate, endDate, includeZeroAccounts: true);
        var bankAccounts = tb.Rows
            .Where(r => GlAccountSubType.IsCashOrCardPaymentAccount(r.SubType))
            .OrderBy(r => r.AccountCode)
            .ToList();

        var periodEntries = _accountingContext.JournalEntries?.GetByDateRange(startDate, endDate) ?? [];
        var statements = (_accountingContext.BankStatements?.GetAll() ?? [])
            .Where(s => s.EndDate.Date <= endDate.Date)
            .ToList();

        foreach (var acct in bankAccounts)
        {
            var latestStmt = statements
                .Where(s => string.Equals(s.GlAccountCode, acct.AccountCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.EndDate)
                .FirstOrDefault();

            var lines = periodEntries
                .SelectMany(e => e.Lines.Select(l => (Entry: e, Line: l)))
                .Where(x => string.Equals(x.Line.GlAccountCode, acct.AccountCode, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var reconciledNet = lines.Where(x => x.Entry.IsReconciled).Sum(x => x.Line.Debit - x.Line.Credit);
            var unreconciledNet = lines.Where(x => !x.Entry.IsReconciled).Sum(x => x.Line.Debit - x.Line.Credit);
            var stmtClosing = latestStmt?.ClosingBalance ?? 0m;
            var diff = latestStmt != null ? Math.Round(stmtClosing - acct.EndingBalance, 2) : 0m;

            sb.AppendLine(string.Join(",",
                acct.SubType,
                CsvSanitizer.EscapeCsv(acct.AccountCode),
                CsvSanitizer.EscapeCsv(acct.AccountName),
                CsvSanitizer.EscapeCsv(latestStmt != null ? $"{latestStmt.StatementName} ({latestStmt.Status})" : "No Uploaded Statement"),
                (latestStmt?.EndDate ?? endDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FormatDecimal(stmtClosing),
                FormatDecimal(acct.EndingBalance),
                FormatDecimal(reconciledNet),
                FormatDecimal(unreconciledNet),
                FormatDecimal(diff)));
        }

        return sb.ToString();
    }

    public string GenerateProjectAndRnDLaborScheduleCsv(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export project and R&D labor schedule", allowSuspended: true);
        DomainActorGuard.RequirePermission(actor, AppPermissions.Hr.ManageSalaries, "export employee labor rates and salary costs", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("ProjectNumber,ProjectName,ClientName,EmployeeName,Status,TotalHours,BillableHours,NonBillableHours,HourlyCostRate,EstimatedLaborCost");

        if (_hrContext?.Timesheets == null)
            return sb.ToString();

        var projectsById = (_projectsContext?.Projects.GetAll() ?? [])
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var employeesById = (_hrContext.Employees?.GetAll() ?? [])
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var rows = new Dictionary<(string ProjectId, string EmployeeId), (string EmpName, string Status, decimal TotalHours, decimal BillableHours)>();

        foreach (var ts in _hrContext.Timesheets.GetAll())
        {
            if (string.Equals(ts.Status, TimesheetStatus.Rejected, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var entry in ts.Entries.Where(e => e.Date.Date >= startDate.Date && e.Date.Date <= endDate.Date && e.Hours > 0m))
            {
                var projId = string.IsNullOrWhiteSpace(entry.ProjectId) ? "(General / Internal)" : entry.ProjectId;
                var key = (projId, ts.EmployeeId);
                rows.TryGetValue(key, out var existing);
                rows[key] = (
                    ts.EmployeeName,
                    ts.Status,
                    existing.TotalHours + entry.Hours,
                    existing.BillableHours + (entry.IsBillable ? entry.Hours : 0m));
            }
        }

        foreach (var kvp in rows.OrderBy(k => k.Key.ProjectId).ThenBy(k => k.Value.EmpName))
        {
            projectsById.TryGetValue(kvp.Key.ProjectId, out var proj);
            employeesById.TryGetValue(kvp.Key.EmployeeId, out var emp);

            decimal hourlyRate = emp?.HourlyRate ?? 0m;
            if (hourlyRate <= 0m && (emp?.Payroll?.AnnualSalary ?? 0m) > 0m)
                hourlyRate = Math.Round(emp!.Payroll.AnnualSalary / 2080m, 2);

            var nonBillable = Math.Max(0m, kvp.Value.TotalHours - kvp.Value.BillableHours);
            var estCost = Math.Round(kvp.Value.TotalHours * hourlyRate, 2);

            sb.AppendLine(string.Join(",",
                CsvSanitizer.EscapeCsv(proj?.ProjectNumber ?? ""),
                CsvSanitizer.EscapeCsv(proj?.Name ?? kvp.Key.ProjectId),
                CsvSanitizer.EscapeCsv(proj?.Client?.BusinessName ?? ""),
                CsvSanitizer.EscapeCsv(string.IsNullOrWhiteSpace(emp?.FullName) ? kvp.Value.EmpName : emp!.FullName),
                CsvSanitizer.EscapeCsv(kvp.Value.Status),
                FormatDecimal(kvp.Value.TotalHours),
                FormatDecimal(kvp.Value.BillableHours),
                FormatDecimal(nonBillable),
                FormatDecimal(hourlyRate),
                FormatDecimal(estCost)));
        }

        return sb.ToString();
    }

    public string GenerateSage50ImportImp(DateTime startDate, DateTime endDate, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "export Sage 50 import file", allowSuspended: true);
        var sb = new StringBuilder();
        sb.AppendLine("<Version>");
        sb.AppendLine("\"12001\",\"1\"");
        sb.AppendLine("</Version>");

        var accounts = (_accountingContext.GlAccounts?.GetAll() ?? [])
            .Where(a => a.IsActive)
            .OrderBy(a => a.Code)
            .ToList();

        if (accounts.Count > 0)
        {
            sb.AppendLine("<COA>");
            foreach (var acct in accounts)
            {
                var sageType = acct.Type switch
                {
                    GlAccountType.Asset => "A",
                    GlAccountType.Liability => "L",
                    GlAccountType.Equity => "Q",
                    GlAccountType.Revenue => "R",
                    GlAccountType.Expense => "E",
                    _ => "X"
                };
                sb.AppendLine($"{CsvSanitizer.EscapeCsv(acct.Code)},{CsvSanitizer.EscapeCsv(acct.Name)},{CsvSanitizer.EscapeCsv(sageType)}");
            }
            sb.AppendLine("</COA>");
        }

        var entries = (_accountingContext.JournalEntries?.GetByDateRange(startDate, endDate) ?? [])
            .OrderBy(e => e.Date)
            .ThenBy(e => e.EntryNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count > 0)
        {
            sb.AppendLine("<Journal>");
            foreach (var entry in entries)
            {
                sb.AppendLine($"{CsvSanitizer.EscapeCsv(entry.Date.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture))},{CsvSanitizer.EscapeCsv(entry.EntryNumber)},{CsvSanitizer.EscapeCsv(entry.Description)}");
                foreach (var line in entry.Lines)
                {
                    var net = line.Debit - line.Credit;
                    sb.AppendLine($"{CsvSanitizer.EscapeCsv(line.GlAccountCode)},{FormatDecimal(net)}");
                }
            }
            sb.AppendLine("</Journal>");
        }

        return sb.ToString();
    }

    public AccountantAdjustingImportResult ImportAdjustingEntriesCsv(string csvContent, string? importedBy = null, Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "import adjusting journal entries", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot import adjusting journal entries.");

        var result = new AccountantAdjustingImportResult();
        if (string.IsNullOrWhiteSpace(csvContent))
            return result;

        var rows = CsvReaderHelper.Parse(csvContent)
            .Where(r => r.Count > 0 && r.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();
        if (rows.Count < 2)
            return result;

        var headers = rows[0].Select(h => h.Trim()).ToList();

        int idxRef = FindHeader(headers, "EntryNumber", "Reference", "Ref", "EntryId", "JournalNumber");
        int idxDate = FindHeader(headers, "Date", "EntryDate", "TransactionDate");
        int idxAccount = FindHeader(headers, "AccountCode", "GlAccountCode", "Account", "GLCode");
        int idxAccountName = FindHeader(headers, "AccountName", "GlAccountName");
        int idxDesc = FindHeader(headers, "Description", "Memo", "Comment");
        int idxDebit = FindHeader(headers, "Debit", "DebitAmount");
        int idxCredit = FindHeader(headers, "Credit", "CreditAmount");

        if (idxAccount < 0 || (idxDebit < 0 && idxCredit < 0))
            throw new InvalidOperationException("Adjusting entries CSV must contain AccountCode and Debit/Credit columns.");

        var groupedLines = new Dictionary<string, (DateTime Date, string Description, List<JournalLine> Lines)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i < rows.Count; i++)
        {
            var fields = rows[i];
            string accountCode = GetField(fields, idxAccount);
            if (string.IsNullOrWhiteSpace(accountCode)) continue;

            string refKey = GetField(fields, idxRef);
            string dateRaw = GetField(fields, idxDate);
            string desc = GetField(fields, idxDesc);
            string accountName = GetField(fields, idxAccountName);

            DateTime entryDate = DateTime.UtcNow.Date;
            if (!string.IsNullOrWhiteSpace(dateRaw) && DateTime.TryParse(dateRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                entryDate = parsedDate.Date;
            }

            if (string.IsNullOrWhiteSpace(refKey))
            {
                refKey = $"ADJ-{entryDate:yyyyMMdd}";
            }

            decimal debit = ParseAmount(GetField(fields, idxDebit));
            decimal credit = ParseAmount(GetField(fields, idxCredit));
            if (debit == 0m && credit == 0m) continue;

            if (!groupedLines.TryGetValue(refKey, out var group))
            {
                group = (entryDate, string.IsNullOrWhiteSpace(desc) ? $"CPA Adjusting Entry ({refKey})" : desc, []);
                groupedLines[refKey] = group;
            }

            group.Lines.Add(new JournalLine
            {
                GlAccountCode = accountCode.Trim(),
                GlAccountName = accountName.Trim(),
                Memo = desc.Trim(),
                Debit = Math.Max(0m, debit),
                Credit = Math.Max(0m, credit)
            });
        }

        foreach (var (refKey, group) in groupedLines)
        {
            var entry = new JournalEntry
            {
                Date = group.Date,
                SourceType = JournalSourceType.AccountantAdjustment,
                Reference = refKey,
                Description = group.Description,
                CreatedBy = !string.IsNullOrWhiteSpace(importedBy) ? importedBy : actor.FullName,
                Lines = group.Lines
            };

            if (!entry.IsBalanced)
            {
                result.Warnings.Add($"Skipped unbalanced entry '{refKey}' (Debits: {entry.TotalDebits:F2}, Credits: {entry.TotalCredits:F2}).");
                continue;
            }

            var posted = _ledgerService.SaveManualJournalEntry(entry);
            result.ImportedEntries.Add(posted);
            result.ImportedEntryCount++;
            result.ImportedLineCount += posted.Lines.Count;
        }

        return result;
    }

    private void AppendVouchersToArchive(ZipArchive archive, DateTime start, DateTime end, Employee? actor)
    {
        if (_fileService == null || _fileAuthorization == null || actor == null) return;

        var bills = _accountingContext.Bills.GetAll()
            .Where(b => !b.IsVoid && b.Date.Date >= start && b.Date.Date <= end)
            .ToList();

        foreach (var bill in bills)
        {
            foreach (var fileUrl in bill.GetAssociatedFilePaths())
            {
                TryCopyFileUrlToZip(archive, fileUrl, $"Vouchers/Bills/{SanitizeFileSegment(bill.InvoiceNumber ?? bill.Id)}_{GetFileNameFromUrl(fileUrl)}", actor);
            }
        }

        var expenseReports = _accountingContext.ExpenseReports.GetAll()
            .Where(er => (er.DateSubmitted.HasValue || er.DatePaid.HasValue)
                         && !string.Equals(er.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase)
                         && er.LineItems.Any(li => li != null && li.Date.Date >= start && li.Date.Date <= end))
            .ToList();

        foreach (var report in expenseReports)
        {
            foreach (var li in report.LineItems.Where(li => li != null && li.Date.Date >= start && li.Date.Date <= end))
            {
                foreach (var fileUrl in li.GetAssociatedFilePaths())
                {
                    TryCopyFileUrlToZip(archive, fileUrl, $"Vouchers/Expenses/{SanitizeFileSegment(report.ReportNumber)}_{GetFileNameFromUrl(fileUrl)}", actor);
                }
            }
        }

        var statements = (_accountingContext.BankStatements?.GetAll() ?? [])
            .Where(s => s.EndDate.Date >= start && s.StartDate.Date <= end)
            .ToList();

        foreach (var stmt in statements)
        {
            foreach (var fileUrl in stmt.GetAssociatedFilePaths())
            {
                TryCopyFileUrlToZip(archive, fileUrl, $"Vouchers/BankStatements/{SanitizeFileSegment(stmt.StatementName)}_{GetFileNameFromUrl(fileUrl)}", actor);
            }
        }

        var directTransactions = (_accountingContext.DirectTransactions?.GetAll() ?? [])
            .Where(t => !t.IsVoid && t.Date.Date >= start && t.Date.Date <= end)
            .ToList();

        foreach (var tx in directTransactions)
        {
            foreach (var fileUrl in tx.GetAssociatedFilePaths())
            {
                TryCopyFileUrlToZip(archive, fileUrl, $"Vouchers/DirectTransactions/{SanitizeFileSegment(tx.TransactionNumber ?? tx.Id)}_{GetFileNameFromUrl(fileUrl)}", actor);
            }
        }
    }

    private void TryCopyFileUrlToZip(ZipArchive archive, string? fileUrl, string zipEntryPath, Employee? actor)
    {
        if (_fileService == null || _fileAuthorization == null || actor == null || string.IsNullOrWhiteSpace(fileUrl)) return;

        var clean = fileUrl.Trim();
        var qIdx = clean.IndexOf('?');
        if (qIdx >= 0) clean = clean[..qIdx];

        const string prefix = "/spokesapi/files/";
        if (!clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;

        var segments = clean[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 4 && segments[0].Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments[1..];
        }
        if (segments.Length != 3) return;

        if (!_fileAuthorization.CanAccess(segments[0], segments[1], actor)) return;

        try
        {
            using var sourceStream = _fileService.OpenRead(segments[0], segments[1], segments[2]);
            if (sourceStream == null) return;

            var entry = archive.CreateEntry(zipEntryPath, CompressionLevel.Fastest);
            using var targetStream = entry.Open();
            sourceStream.CopyTo(targetStream);
        }
        catch
        {
            // Ignore unreadable or missing voucher files so export still succeeds
        }
    }

    private static void WriteUtf8Entry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(content);
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string GetAgingBucket(int daysPastDue) => daysPastDue switch
    {
        <= 0 => "Current",
        <= 30 => "1-30 Days",
        <= 60 => "31-60 Days",
        <= 90 => "61-90 Days",
        _ => "90+ Days"
    };

    private static int FindHeader(List<string> headers, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            int idx = headers.FindIndex(h => string.Equals(h, candidate, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static string GetField(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index].Trim() : string.Empty;

    private static decimal ParseAmount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        var clean = raw.Replace("$", "").Replace(" ", "").Replace(",", "");
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : 0m;
    }

    private static string SanitizeFileSegment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "DOC";
        return new string(input.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
    }

    private static string GetFileNameFromUrl(string fileUrl)
    {
        var lastSlash = fileUrl.LastIndexOf('/');
        return lastSlash >= 0 ? fileUrl[(lastSlash + 1)..] : "attachment.bin";
    }
}
