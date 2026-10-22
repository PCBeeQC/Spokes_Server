namespace Spokes_Server.Core.Services.Accounting;

using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Storage;

public class ExpenseFileAccessProvider : IFileAccessProvider
{
    private readonly ExpenseReportRepository _expenseReports;
    public string Category => "expenses";

    public ExpenseFileAccessProvider(ExpenseReportRepository expenseReports)
    {
        _expenseReports = expenseReports;
    }

    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;

        // Finance admins can see all expenses
        if (user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting)) return true;

        var report = _expenseReports.GetById(contextId);
        if (report == null) return false;

        return report.EmployeeId == user.Id;
    }
}

public class BillFileAccessProvider : IFileAccessProvider
{
    public string Category => "bills";

    // Bills are accessible to finance/admin and purchase managers/reviewers
    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;

        return user.IsAdmin
            || user.HasPermission(AppPermissions.Finance.AccessAccounting)
            || user.HasPermission(AppPermissions.Purchases.ViewBills)
            || user.HasPermission(AppPermissions.Purchases.ManageBills)
            || user.HasPermission(AppPermissions.Purchases.ApproveBills)
            || user.HasPermission(AppPermissions.Purchases.ViewPOs);
    }
}

public class RecurringCostFileAccessProvider : IFileAccessProvider
{
    public string Category => "recurringcosts";

    // Recurring costs/one-time expenses are finance-only
    public bool CanAccess(Employee user, string contextId)
    {
        if (user == null || !user.IsActive || user.IsSuspended || user.IsBanned) return false;

        return user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting);
    }
}
