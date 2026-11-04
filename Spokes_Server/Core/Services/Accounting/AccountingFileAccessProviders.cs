namespace Spokes_Server.Core.Services.Accounting;

using System;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Models.Accounting;
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
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;

        // Finance admins can see all expenses
        if (user.IsAdmin || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Finance.AccessAccounting))
            return true;

        var report = _expenseReports.GetById(contextId);
        if (report == null) return false;

        return report.EmployeeId == user.Id;
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user) || string.IsNullOrWhiteSpace(contextId))
            return false;

        var report = _expenseReports.GetById(contextId);
        if (report == null)
            return false;

        if (user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting))
            return true;

        return user.HasPermission(AppPermissions.Finance.AccessExpenses)
            && report.EmployeeId == user.Id
            && string.Equals(report.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase);
    }
}

public class BillFileAccessProvider : IFileAccessProvider
{
    public string Category => "bills";

    // Bills are accessible to finance/admin and purchase managers/reviewers
    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;

        return user.IsAdmin
            || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Finance.AccessAccounting)
            || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ViewBills)
            || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ManageBills)
            || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ApproveBills)
            || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Purchases.ViewPOs);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;

        return user.IsAdmin
            || user.HasPermission(AppPermissions.Finance.AccessAccounting)
            || user.HasPermission(AppPermissions.Purchases.ManageBills);
    }
}

public class RecurringCostFileAccessProvider : IFileAccessProvider
{
    public string Category => "recurringcosts";

    // Recurring costs/one-time expenses are finance-only
    public bool CanAccess(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsReadableUser(user)) return false;

        return user.IsAdmin || FileAccessPermissionHelper.HasAssignedPermission(user, AppPermissions.Finance.AccessAccounting);
    }

    public bool CanUpload(Employee user, string contextId)
    {
        if (!FileAccessPermissionHelper.IsWritableUser(user)) return false;

        return user.IsAdmin || user.HasPermission(AppPermissions.Finance.AccessAccounting);
    }
}
