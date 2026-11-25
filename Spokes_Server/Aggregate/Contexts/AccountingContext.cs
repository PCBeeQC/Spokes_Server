namespace Spokes_Server.Aggregate.Contexts;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Core;

/// <summary>
/// Domain aggregate context managing financial ledgers and billing (invoices, bills, purchase orders,
/// supplier records, commission ledger records, expense reports, employer contributions, recurring expenses,
/// Chart of Accounts, General Ledger journal entries, bank statements, and tax filing periods).
/// Note: Quotes and RateCards reside in ProjectsContext as project estimation and scoping artifacts.
/// </summary>
public class AccountingContext
{
    private readonly CompanyProfileRepository? _companyProfile;

    public virtual InvoiceRepository Invoices { get; }
    public virtual PurchaseOrderRepository PurchaseOrders { get; }

    public virtual SupplierRepository Suppliers { get; }
    public virtual CommissionLedgerRecordRepository CommissionLedgerRecords { get; }
    public virtual ExpenseReportRepository ExpenseReports { get; }
    public virtual BillRepository Bills { get; }
    public virtual EmployerContributionRepository EmployerContributions { get; }
    public virtual RecurringExpenseRepository RecurringExpenses { get; }
    public virtual GlAccountRepository GlAccounts { get; }
    public virtual JournalEntryRepository JournalEntries { get; }
    public virtual BankStatementRepository BankStatements { get; }
    public virtual TaxFilingPeriodRepository TaxFilingPeriods { get; }
    public virtual AccountingMigrationBatchRepository MigrationBatches { get; }
    public virtual FinancialReportRepository FinancialReports { get; }
    public virtual DirectTransactionRepository DirectTransactions { get; }
    public virtual FinancialAccountRepository FinancialAccounts { get; }
    public virtual SuggestedInvoicePaymentRepository SuggestedInvoicePayments { get; }
    public virtual BankRuleRepository BankRules { get; }

    protected AccountingContext()
    {
        Invoices = null!;
        PurchaseOrders = null!;
        Suppliers = null!;
        CommissionLedgerRecords = null!;
        ExpenseReports = null!;
        Bills = null!;
        EmployerContributions = null!;
        RecurringExpenses = null!;
        GlAccounts = null!;
        JournalEntries = null!;
        BankStatements = null!;
        TaxFilingPeriods = null!;
        MigrationBatches = null!;
        FinancialReports = null!;
        DirectTransactions = null!;
        FinancialAccounts = null!;
        SuggestedInvoicePayments = null!;
        BankRules = null!;
    }

    [ActivatorUtilitiesConstructor]
    public AccountingContext(
        InvoiceRepository invoices,
        PurchaseOrderRepository purchases,
        SupplierRepository suppliers,
        CommissionLedgerRecordRepository commissionLedgerRecords,
        ExpenseReportRepository expenseReports,
        BillRepository bills,
        EmployerContributionRepository employerContributions,
        RecurringExpenseRepository recurringExpenses,
        CompanyProfileRepository? companyProfile = null,
        GlAccountRepository? glAccounts = null,
        JournalEntryRepository? journalEntries = null,
        BankStatementRepository? bankStatements = null,
        TaxFilingPeriodRepository? taxFilingPeriods = null,
        AccountingMigrationBatchRepository? migrationBatches = null,
        FinancialReportRepository? financialReports = null,
        DirectTransactionRepository? directTransactions = null,
        FinancialAccountRepository? financialAccounts = null,
        SuggestedInvoicePaymentRepository? suggestedInvoicePayments = null,
        BankRuleRepository? bankRules = null)
    {
        Invoices = invoices;
        PurchaseOrders = purchases;
        Suppliers = suppliers;
        CommissionLedgerRecords = commissionLedgerRecords;
        ExpenseReports = expenseReports;
        Bills = bills;
        EmployerContributions = employerContributions;
        RecurringExpenses = recurringExpenses;
        _companyProfile = companyProfile;
        GlAccounts = glAccounts!;
        JournalEntries = journalEntries!;
        BankStatements = bankStatements!;
        TaxFilingPeriods = taxFilingPeriods!;
        MigrationBatches = migrationBatches!;
        FinancialReports = financialReports!;
        DirectTransactions = directTransactions!;
        FinancialAccounts = financialAccounts!;
        SuggestedInvoicePayments = suggestedInvoicePayments!;
        BankRules = bankRules!;
    }

    /// <summary>
    /// Exposes domain-owned accounting configuration decomposed from the global company profile.
    /// </summary>
    public virtual AccountingConfig GetAccountingConfig() => _companyProfile?.GetAccounting() ?? new AccountingConfig();
    public virtual AccountingConfig Config => GetAccountingConfig();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => Invoices?.LoadFromDisk(),
            () => PurchaseOrders?.LoadFromDisk(),
            () => Suppliers?.LoadFromDisk(),
            () => CommissionLedgerRecords?.LoadFromDisk(),
            () => ExpenseReports?.LoadFromDisk(),
            () => Bills?.LoadFromDisk(),
            () => EmployerContributions?.LoadFromDisk(),
            () => RecurringExpenses?.LoadFromDisk(),
            () => GlAccounts?.LoadFromDisk(),
            () => JournalEntries?.LoadFromDisk(),
            () => BankStatements?.LoadFromDisk(),
            () => TaxFilingPeriods?.LoadFromDisk(),
            () => MigrationBatches?.LoadFromDisk(),
            () => FinancialReports?.LoadFromDisk(),
            () => DirectTransactions?.LoadFromDisk(),
            () => FinancialAccounts?.LoadFromDisk(),
            () => SuggestedInvoicePayments?.LoadFromDisk(),
            () => BankRules?.LoadFromDisk()
        );
    }
}
