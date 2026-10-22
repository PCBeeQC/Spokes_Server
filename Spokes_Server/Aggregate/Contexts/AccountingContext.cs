namespace Spokes_Server.Aggregate.Contexts;

using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Domain aggregate context managing financial ledgers and billing (invoices, bills, purchase orders,
/// supplier records, commission ledger records, expense reports, employer contributions, and recurring expenses).
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
        CompanyProfileRepository? companyProfile = null)
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
    }

    /// <summary>
    /// Exposes domain-owned accounting configuration decomposed from the global company profile.
    /// </summary>
    public virtual AccountingConfig GetAccountingConfig() => _companyProfile?.GetAccounting() ?? new AccountingConfig();
    public virtual AccountingConfig Config => GetAccountingConfig();

    public virtual void Initialize()
    {
        Parallel.Invoke(
            () => Invoices.LoadFromDisk(),
            () => PurchaseOrders.LoadFromDisk(),
            () => Suppliers.LoadFromDisk(),
            () => CommissionLedgerRecords.LoadFromDisk(),
            () => ExpenseReports.LoadFromDisk(),
            () => Bills.LoadFromDisk(),
            () => EmployerContributions.LoadFromDisk(),
            () => RecurringExpenses.LoadFromDisk()
        );
    }
}
