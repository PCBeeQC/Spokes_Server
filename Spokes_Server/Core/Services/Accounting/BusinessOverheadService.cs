namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;

public class BusinessOverheadService : IBusinessOverheadService
{
    private readonly ProjectsContext _projectsContext;
    private readonly HrContext _hrContext;
    private readonly AccountingContext _accountingContext;
    private readonly ILedgerService? _ledgerService;

    [ActivatorUtilitiesConstructor]
    public BusinessOverheadService(
        ProjectsContext projectsContext,
        HrContext hrContext,
        AccountingContext accountingContext,
        ILedgerService? ledgerService = null)
    {
        ArgumentNullException.ThrowIfNull(projectsContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(accountingContext);

        _projectsContext = projectsContext;
        _hrContext = hrContext;
        _accountingContext = accountingContext;
        _ledgerService = ledgerService ?? (accountingContext.JournalEntries != null ? new LedgerService(accountingContext, projectsContext) : null);
    }

    public void NormalizeLegacyEstimates(Employee? actor = null)
    {
        DomainActorGuard.RequirePermission(actor, AppPermissions.Finance.AccessAccounting, "normalize legacy overhead estimates", allowSuspended: false);
        if (actor!.IsSystem)
            throw new UnauthorizedAccessException("System service actors cannot normalize legacy overhead estimates.");
        var all = _accountingContext.RecurringExpenses.GetAll().ToList();
        var estimates = all.Where(e => e.IsEstimate).ToList();
        if (estimates.Count == 0) return;

        var actuals = all.Where(e => !e.IsEstimate).ToList();

        foreach (var est in estimates)
        {
            var match = actuals.FirstOrDefault(a =>
                string.Equals(a.Name?.Trim(), est.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Frequency, est.Frequency, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                bool changed = false;
                if (match.Amount <= 0m && est.Amount > 0m)
                {
                    match.Amount = est.Amount;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(match.Supplier) && !string.IsNullOrWhiteSpace(est.Supplier))
                {
                    match.Supplier = est.Supplier;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(match.GlAccountCode) && !string.IsNullOrWhiteSpace(est.GlAccountCode))
                {
                    match.GlAccountCode = est.GlAccountCode;
                    changed = true;
                }
                if (est.History != null && est.History.Count > 0)
                {
                    match.History ??= [];
                    var existingIds = match.History.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var record in est.History)
                    {
                        if (!existingIds.Contains(record.Id))
                        {
                            match.History.Add(record);
                            changed = true;
                        }
                    }
                }

                if (changed)
                {
                    _accountingContext.RecurringExpenses.Save(match);
                }

                _accountingContext.RecurringExpenses.Delete(est.Id);
            }
            else
            {
                est.IsEstimate = false;
                _accountingContext.RecurringExpenses.Save(est);
                actuals.Add(est);
            }
        }
    }

    public decimal GetRecurringProfileMonthlyPreTax(RecurringExpense profile, int year, int month)
    {
        if (profile == null) return 0m;
        var linkedBills = _accountingContext.Bills.GetAll()
            .Where(b => !b.IsVoid
                        && b.Status != BillStatus.Void
                        && string.Equals(b.LinkedRecurringExpenseId, profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var winStart = new DateTime(year, month, 1);
        var winEnd = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        return CalculateProfileMonthlyProratedAmount(
            profile,
            linkedBills,
            winStart,
            winEnd,
            year,
            month,
            monthDayFactor: 1m,
            allowZeroAmountEstimation: true).PreTax;
    }

    private sealed class MonthSlice
    {
        public int Year { get; init; }
        public int Month { get; init; }
        public DateTime WinStart { get; init; }
        public DateTime WinEnd { get; init; }
        public decimal DayFactor { get; init; }
        public decimal CompanyDirectHours { get; set; }
        public decimal CompanyDirectCost { get; set; }
        public decimal TargetDirectHours { get; set; }
        public decimal ShareRatio { get; set; }
        public Dictionary<string, InternalProjectOverheadSummary> InternalStats { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> InternalContributors { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public PeriodOverheadCalculationResult CalculatePeriodOverhead(
        DateTime startDate,
        DateTime endDate,
        string? targetProjectId = null,
        Employee? actor = null)
    {
        DomainActorGuard.RequireAnyPermission(
            actor,
            "calculate period overhead",
            allowSuspended: true,
            AppPermissions.Finance.AccessAccounting,
            AppPermissions.Projects.View);
        bool canManageSalaries = actor!.HasEffectivePermission(AppPermissions.Hr.ManageSalaries, allowSuspended: true);

        var startCutoff = startDate.Date;
        var endCutoff = endDate.Date;
        if (startCutoff > endCutoff)
        {
            startCutoff = endCutoff;
        }

        var calendarMonths = GetCalendarMonths(startCutoff, endCutoff);
        var allSlices = BuildMonthSlices(calendarMonths, startCutoff, endCutoff);
        var sliceByMonthKey = allSlices.ToDictionary(s => s.Year * 12 + s.Month);

        var projects = _projectsContext.Projects.GetAll()
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        Project? targetProject = null;
        if (!string.IsNullOrWhiteSpace(targetProjectId))
        {
            projects.TryGetValue(targetProjectId, out targetProject);
            if (targetProject != null && !ProjectAccessHelper.CanAccess(actor, targetProject, _hrContext.Teams, allowAccountingFinancialRead: true))
            {
                throw new UnauthorizedAccessException($"Access denied to project '{targetProjectId}'.");
            }
        }

        bool isTargetExternal = targetProject != null && !targetProject.IsInternal;

        var employees = canManageSalaries
            ? _hrContext.Employees.GetAll().ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, Employee>(StringComparer.OrdinalIgnoreCase);
        var employerContributions = canManageSalaries
            ? _accountingContext.EmployerContributions.GetAll()
            : [];
        var workTypes = _projectsContext.WorkTypes.GetAll()
            .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);

        // 1. Timesheets: populate per-month Direct Client Labor Hours & Internal Project Unbillable Labor
        PopulateTimesheetSlices(
            sliceByMonthKey,
            startCutoff,
            endCutoff,
            projects,
            employees,
            employerContributions,
            workTypes,
            isTargetExternal ? targetProject!.Id : null,
            actor,
            canManageSalaries);

        // 2. Internal Project Purchases & Expenses in [startCutoff .. endCutoff] mapped to their month slice
        PopulateInternalPurchasesAndExpensesSlices(sliceByMonthKey, startCutoff, endCutoff, projects, actor);

        // Select active months: for an external project, only charge overhead for months where the project
        // actually logged direct hours (avoiding future/post-completion months such as between last TS and AsOfDate).
        var activeSlices = isTargetExternal
            ? allSlices.Where(s => s.TargetDirectHours > 0m).ToList()
            : allSlices;

        if (activeSlices.Count == 0)
        {
            activeSlices = allSlices;
        }

        foreach (var slice in activeSlices)
        {
            slice.ShareRatio = (isTargetExternal && slice.CompanyDirectHours > 0m)
                ? slice.TargetDirectHours / slice.CompanyDirectHours
                : 0m;
        }

        var result = new PeriodOverheadCalculationResult
        {
            PeriodStartDate = startCutoff,
            PeriodEndDate = endCutoff,
            MonthCount = activeSlices.Count,
            TotalCompanyDirectLaborHours = activeSlices.Sum(s => s.CompanyDirectHours),
            TotalCompanyDirectLaborCost = activeSlices.Sum(s => s.CompanyDirectCost),
            TargetProjectDirectLaborHours = activeSlices.Sum(s => s.TargetDirectHours)
        };

        // Merge Internal Project Summaries across active slices and accumulate month-weighted allocations
        decimal internalLaborAllocSum = 0m;
        decimal internalPurchasesAllocPreTaxSum = 0m;
        decimal internalPurchasesAllocTaxSum = 0m;

        var mergedInternal = new Dictionary<string, (InternalProjectOverheadSummary Summary, HashSet<string> Contributors, decimal AllocLabor, decimal AllocPurchPreTax, decimal AllocPurchTax)>(StringComparer.OrdinalIgnoreCase);

        foreach (var slice in activeSlices)
        {
            foreach (var kvp in slice.InternalStats)
            {
                var src = kvp.Value;
                if (!mergedInternal.TryGetValue(kvp.Key, out var agg))
                {
                    agg = (
                        new InternalProjectOverheadSummary
                        {
                            ProjectId = src.ProjectId,
                            ProjectNumber = src.ProjectNumber,
                            ProjectName = src.ProjectName
                        },
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        0m,
                        0m,
                        0m);
                }

                agg.Summary.Hours += src.Hours;
                agg.Summary.LaborCost += src.LaborCost;
                agg.Summary.PurchasesPreTax += src.PurchasesPreTax;
                agg.Summary.PurchasesTax += src.PurchasesTax;
                agg.Summary.ExpensesPreTax += src.ExpensesPreTax;
                agg.Summary.ExpensesTax += src.ExpensesTax;

                if (slice.InternalContributors.TryGetValue(kvp.Key, out var names))
                {
                    foreach (var n in names) agg.Contributors.Add(n);
                }

                agg.AllocLabor += src.LaborCost * slice.ShareRatio;
                var slicePurchPreTax = src.PurchasesPreTax + src.ExpensesPreTax;
                var slicePurchTax = src.PurchasesTax + src.ExpensesTax;
                agg.AllocPurchPreTax += slicePurchPreTax * slice.ShareRatio;
                agg.AllocPurchTax += slicePurchTax * slice.ShareRatio;

                mergedInternal[kvp.Key] = agg;
            }
        }

        foreach (var agg in mergedInternal.Values)
        {
            agg.Summary.ContributorNames = agg.Contributors.OrderBy(n => n).ToList();
        }

        result.InternalProjects = mergedInternal.Values
            .Select(v => v.Summary)
            .OrderByDescending(p => p.GrandTotal)
            .ThenBy(p => p.ProjectName)
            .ToList();

        result.InternalLaborHours = result.InternalProjects.Sum(p => p.Hours);
        result.InternalLaborCost = result.InternalProjects.Sum(p => p.LaborCost);
        result.InternalPurchasesAndExpensesPreTax = result.InternalProjects.Sum(p => p.PurchasesPreTax + p.ExpensesPreTax);
        result.InternalPurchasesAndExpensesTax = result.InternalProjects.Sum(p => p.PurchasesTax + p.ExpensesTax);

        // 3. Recurring Fixed Expenses (Prorated Monthly & Allocated Month-by-Month across activeSlices)
        decimal fixedAllocPreTaxSum = 0m;
        decimal fixedAllocTaxSum = 0m;

        var allRecurring = DeduplicateRecurringProfiles(_accountingContext.RecurringExpenses.GetAll());
        var allBills = _accountingContext.Bills.GetAll()
            .Where(b => !b.IsVoid && b.Status != BillStatus.Void)
            .ToList();

        var recurringProfiles = allRecurring
            .Where(r => !string.Equals(r.Frequency, RecurringExpenseFrequency.OneTime, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name)
            .ToList();

        foreach (var profile in recurringProfiles)
        {
            decimal profilePoolPreTax = 0m;
            decimal profilePoolTax = 0m;
            decimal profileAllocPreTax = 0m;
            decimal profileAllocTax = 0m;
            int activeMonthsForProfile = 0;

            var linkedBills = allBills
                .Where(b => string.Equals(b.LinkedRecurringExpenseId, profile.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var slice in activeSlices)
            {
                var (monthPreTax, monthTax) = CalculateProfileMonthlyProratedAmount(
                    profile,
                    linkedBills,
                    slice.WinStart,
                    slice.WinEnd,
                    slice.Year,
                    slice.Month,
                    slice.DayFactor,
                    allowZeroAmountEstimation: false);

                if (monthPreTax != 0m || monthTax != 0m)
                {
                    profilePoolPreTax += monthPreTax;
                    profilePoolTax += monthTax;
                    profileAllocPreTax += monthPreTax * slice.ShareRatio;
                    profileAllocTax += monthTax * slice.ShareRatio;
                    activeMonthsForProfile++;
                }
            }

            profilePoolPreTax = Math.Round(profilePoolPreTax, 2, MidpointRounding.AwayFromZero);
            profilePoolTax = Math.Round(profilePoolTax, 2, MidpointRounding.AwayFromZero);

            if (profilePoolPreTax == 0m && profilePoolTax == 0m)
                continue;

            var roundedAllocPreTax = Math.Round(profileAllocPreTax, 2, MidpointRounding.AwayFromZero);
            var roundedAllocTax = Math.Round(profileAllocTax, 2, MidpointRounding.AwayFromZero);

            result.RecurringFixedPreTax += profilePoolPreTax;
            result.RecurringFixedTax += profilePoolTax;
            fixedAllocPreTaxSum += roundedAllocPreTax;
            fixedAllocTaxSum += roundedAllocTax;

            var avgMonthlyPreTax = activeMonthsForProfile > 0
                ? Math.Round(profilePoolPreTax / activeMonthsForProfile, 2, MidpointRounding.AwayFromZero)
                : profilePoolPreTax;

            var supplierLabel = !string.IsNullOrWhiteSpace(profile.Supplier) ? $"{profile.Supplier} · " : string.Empty;
            var details = activeMonthsForProfile > 1
                ? $"{supplierLabel}{profile.Frequency} ({avgMonthlyPreTax:C2}/mo × {activeMonthsForProfile} mos)"
                : $"{supplierLabel}{profile.Frequency} ({avgMonthlyPreTax:C2}/mo)";

            result.BreakdownRows.Add(new ReportOverheadBreakdownRow
            {
                Category = OverheadCostCategory.RecurringFixed,
                Name = string.IsNullOrWhiteSpace(profile.Name) ? "Recurring Expense" : profile.Name,
                Details = details,
                CompanyPoolPreTax = profilePoolPreTax,
                CompanyPoolTax = profilePoolTax,
                AllocatedPreTax = roundedAllocPreTax,
                AllocatedTax = roundedAllocTax
            });
        }

        // 4. One-Time Overhead Bills & Legacy One-Time Profiles (Amortized over 12 Calendar Months, Month-Weighted)
        var overheadBills = allBills
            .Where(b => b.IsBusinessOverhead
                        && string.IsNullOrWhiteSpace(b.PurchaseOrderId)
                        && string.IsNullOrWhiteSpace(b.LinkedRecurringExpenseId))
            .OrderBy(b => b.Date)
            .ToList();

        foreach (var bill in overheadBills)
        {
            var (billPreTax, billTax) = ExtractBillPreTaxAndTax(bill);
            if (billPreTax == 0m && billTax == 0m) continue;

            var (periodPreTax, periodTax, allocPreTax, allocTax, overlappingCount, monthlyPreTax) =
                CalculateAmortizedItemTotals(billPreTax, billTax, bill.Date.Year, bill.Date.Month, activeSlices);

            if (overlappingCount <= 0) continue;

            result.OneTimeAmortizedPreTax += periodPreTax;
            result.OneTimeAmortizedTax += periodTax;
            fixedAllocPreTaxSum += allocPreTax;
            fixedAllocTaxSum += allocTax;

            var billTitle = !string.IsNullOrWhiteSpace(bill.Description)
                ? bill.Description
                : (!string.IsNullOrWhiteSpace(bill.SupplierName) ? bill.SupplierName : "Business Overhead Bill");
            var supplierPrefix = !string.IsNullOrWhiteSpace(bill.SupplierName) && !billTitle.Contains(bill.SupplierName, StringComparison.OrdinalIgnoreCase)
                ? $"{bill.SupplierName} — "
                : string.Empty;

            var details = overlappingCount > 1
                ? $"12-Mo Amortized ({monthlyPreTax:C2}/mo × {overlappingCount} mos · Bill {bill.Date:yyyy-MM-dd})"
                : $"12-Mo Amortized ({monthlyPreTax:C2}/mo · Bill {bill.Date:yyyy-MM-dd})";

            result.BreakdownRows.Add(new ReportOverheadBreakdownRow
            {
                Category = OverheadCostCategory.OneTimeAmortized,
                Name = $"{supplierPrefix}{billTitle}",
                Details = details,
                CompanyPoolPreTax = periodPreTax,
                CompanyPoolTax = periodTax,
                AllocatedPreTax = allocPreTax,
                AllocatedTax = allocTax
            });
        }

        var oneTimeProfiles = allRecurring
            .Where(r => string.Equals(r.Frequency, RecurringExpenseFrequency.OneTime, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.NextDueDate)
            .ToList();

        foreach (var oneTime in oneTimeProfiles)
        {
            var latestRecord = oneTime.History?.OrderByDescending(h => h.Date).FirstOrDefault();
            var effectiveDate = latestRecord?.Date ?? (oneTime.NextDueDate != default ? oneTime.NextDueDate : oneTime.CreatedDate);
            var (totalPreTax, totalTax) = ExtractRecurringRecordOrDefaultPreTaxAndTax(oneTime, latestRecord);
            if (totalPreTax == 0m && totalTax == 0m) continue;

            var (periodPreTax, periodTax, allocPreTax, allocTax, overlappingCount, monthlyPreTax) =
                CalculateAmortizedItemTotals(totalPreTax, totalTax, effectiveDate.Year, effectiveDate.Month, activeSlices);

            if (overlappingCount <= 0) continue;

            result.OneTimeAmortizedPreTax += periodPreTax;
            result.OneTimeAmortizedTax += periodTax;
            fixedAllocPreTaxSum += allocPreTax;
            fixedAllocTaxSum += allocTax;

            var details = overlappingCount > 1
                ? $"12-Mo Amortized ({monthlyPreTax:C2}/mo × {overlappingCount} mos · {effectiveDate:yyyy-MM-dd})"
                : $"12-Mo Amortized ({monthlyPreTax:C2}/mo · {effectiveDate:yyyy-MM-dd})";

            result.BreakdownRows.Add(new ReportOverheadBreakdownRow
            {
                Category = OverheadCostCategory.OneTimeAmortized,
                Name = string.IsNullOrWhiteSpace(oneTime.Name) ? "One-Time Overhead Expense" : oneTime.Name,
                Details = details,
                CompanyPoolPreTax = periodPreTax,
                CompanyPoolTax = periodTax,
                AllocatedPreTax = allocPreTax,
                AllocatedTax = allocTax
            });
        }

        // 5. Add Internal Project Labor & Internal Purchases/Expenses Rows to BreakdownRows
        foreach (var intProj in result.InternalProjects)
        {
            mergedInternal.TryGetValue(intProj.ProjectId, out var agg);
            var projLabel = !string.IsNullOrWhiteSpace(intProj.ProjectNumber)
                ? $"{intProj.ProjectNumber} : {intProj.ProjectName}"
                : intProj.ProjectName;

            if (intProj.Hours > 0m || intProj.LaborCost > 0m)
            {
                var empCount = intProj.ContributorNames.Count;
                var allocLabor = canManageSalaries ? Math.Round(agg.AllocLabor, 2, MidpointRounding.AwayFromZero) : 0m;
                internalLaborAllocSum += allocLabor;

                result.BreakdownRows.Add(new ReportOverheadBreakdownRow
                {
                    Category = OverheadCostCategory.InternalLabor,
                    Name = $"{projLabel} (Internal Labor)",
                    Details = canManageSalaries
                        ? $"{intProj.Hours:N1} h ({empCount} {(empCount == 1 ? "employee" : "employees")})"
                        : $"{intProj.Hours:N1} h",
                    Hours = intProj.Hours,
                    CompanyPoolPreTax = canManageSalaries ? intProj.LaborCost : 0m,
                    CompanyPoolTax = 0m,
                    AllocatedPreTax = allocLabor,
                    AllocatedTax = 0m
                });
            }

            var intPurchasesPreTax = intProj.PurchasesPreTax + intProj.ExpensesPreTax;
            var intPurchasesTax = intProj.PurchasesTax + intProj.ExpensesTax;
            if (intPurchasesPreTax > 0m || intPurchasesTax > 0m)
            {
                var allocPurchPreTax = Math.Round(agg.AllocPurchPreTax, 2, MidpointRounding.AwayFromZero);
                var allocPurchTax = Math.Round(agg.AllocPurchTax, 2, MidpointRounding.AwayFromZero);
                internalPurchasesAllocPreTaxSum += allocPurchPreTax;
                internalPurchasesAllocTaxSum += allocPurchTax;

                result.BreakdownRows.Add(new ReportOverheadBreakdownRow
                {
                    Category = OverheadCostCategory.InternalPurchasesAndExpenses,
                    Name = $"{projLabel} (Internal Purchases & Expenses)",
                    Details = "Internal non-billable project materials & expenses",
                    CompanyPoolPreTax = intPurchasesPreTax,
                    CompanyPoolTax = intPurchasesTax,
                    AllocatedPreTax = allocPurchPreTax,
                    AllocatedTax = allocPurchTax
                });
            }
        }

        // 6. Final Allocated Rollups
        result.AllocatedFixedOverheadPreTax = Math.Round(fixedAllocPreTaxSum, 2, MidpointRounding.AwayFromZero);
        result.AllocatedFixedOverheadTax = Math.Round(fixedAllocTaxSum, 2, MidpointRounding.AwayFromZero);
        result.AllocatedInternalCostsPreTax = Math.Round(internalLaborAllocSum + internalPurchasesAllocPreTaxSum, 2, MidpointRounding.AwayFromZero);
        result.AllocatedInternalCostsTax = Math.Round(internalPurchasesAllocTaxSum, 2, MidpointRounding.AwayFromZero);

        result.AllocatedOverheadPreTax = result.AllocatedFixedOverheadPreTax + result.AllocatedInternalCostsPreTax;
        result.AllocatedOverheadTax = result.AllocatedFixedOverheadTax + result.AllocatedInternalCostsTax;

        decimal effectiveShareRatio = isTargetExternal
            ? (result.CompanyOverheadPoolPreTax > 0m
                ? result.AllocatedOverheadPreTax / result.CompanyOverheadPoolPreTax
                : (result.TotalCompanyDirectLaborHours > 0m ? result.TargetProjectDirectLaborHours / result.TotalCompanyDirectLaborHours : 0m))
            : 0m;
        result.AllocationShareRatio = effectiveShareRatio;
        result.AllocationSharePercent = Math.Round(effectiveShareRatio * 100m, 1, MidpointRounding.AwayFromZero);

        return result;
    }

    private static List<MonthSlice> BuildMonthSlices(
        IReadOnlyList<(int Year, int Month)> calendarMonths,
        DateTime startCutoff,
        DateTime endCutoff)
    {
        var slices = new List<MonthSlice>(calendarMonths.Count);
        foreach (var (year, month) in calendarMonths)
        {
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var monthStart = new DateTime(year, month, 1);
            var monthEnd = new DateTime(year, month, daysInMonth);
            var winStart = monthStart < startCutoff ? startCutoff : monthStart;
            var winEnd = monthEnd > endCutoff ? endCutoff : monthEnd;
            var daysInWindow = Math.Max(0, (winEnd - winStart).Days + 1);
            var dayFactor = daysInWindow >= daysInMonth ? 1m : (decimal)daysInWindow / daysInMonth;

            slices.Add(new MonthSlice
            {
                Year = year,
                Month = month,
                WinStart = winStart,
                WinEnd = winEnd,
                DayFactor = dayFactor
            });
        }
        return slices;
    }

    private void PopulateTimesheetSlices(
        Dictionary<int, MonthSlice> sliceByMonthKey,
        DateTime startCutoff,
        DateTime endCutoff,
        Dictionary<string, Project> projects,
        Dictionary<string, Employee> employees,
        IReadOnlyList<EmployerContribution> employerContributions,
        Dictionary<string, WorkType> workTypes,
        string? targetExternalProjectId,
        Employee actor,
        bool canManageSalaries)
    {
        foreach (var ts in _hrContext.Timesheets.GetAll())
        {
            if (ts?.Entries == null) continue;
            if (string.Equals(ts.Status, TimesheetStatus.Rejected, StringComparison.OrdinalIgnoreCase)) continue;

            Employee? emp = null;
            string empName = string.Empty;
            decimal empHourlyCost = 0m;
            if (canManageSalaries)
            {
                employees.TryGetValue(ts.EmployeeId ?? string.Empty, out emp);
                empName = emp != null && !string.IsNullOrWhiteSpace(emp.FullName)
                    ? emp.FullName
                    : (!string.IsNullOrWhiteSpace(ts.EmployeeName) ? ts.EmployeeName : "Unknown");
                empHourlyCost = EmployerContribution.CalculateTotalHourlyCost(emp, employerContributions);
            }

            foreach (var entry in ts.Entries)
            {
                if (entry == null || entry.Hours <= 0m || string.IsNullOrWhiteSpace(entry.ProjectId) || entry.IsLeaveOrHoliday)
                    continue;

                var entryDate = TimeEntryTypes.ResolveEntryDate(ts, entry);
                if (entryDate < startCutoff || entryDate > endCutoff)
                    continue;

                if (!sliceByMonthKey.TryGetValue(entryDate.Year * 12 + entryDate.Month, out var slice))
                    continue;

                workTypes.TryGetValue(entry.WorkTypeId ?? string.Empty, out var wt);
                var costRate = canManageSalaries ? (empHourlyCost > 0m ? empHourlyCost : (wt?.CostRate ?? 0m)) : 0m;
                var lineCost = canManageSalaries ? Math.Round(entry.Hours * costRate, 2, MidpointRounding.AwayFromZero) : 0m;

                projects.TryGetValue(entry.ProjectId, out var proj);
                if (proj?.IsInternal == true && proj != null)
                {
                    if (!ProjectAccessHelper.CanAccess(actor, proj, _hrContext.Teams, allowAccountingFinancialRead: true))
                        continue;

                    var summary = GetOrCreateInternalSummary(slice, proj);
                    summary.Hours += entry.Hours;
                    summary.LaborCost += lineCost;
                    if (canManageSalaries && !string.IsNullOrWhiteSpace(empName))
                    {
                        slice.InternalContributors[proj.Id].Add(empName);
                    }
                }
                else
                {
                    slice.CompanyDirectHours += entry.Hours;
                    slice.CompanyDirectCost += lineCost;
                    if (!string.IsNullOrWhiteSpace(targetExternalProjectId) &&
                        string.Equals(entry.ProjectId, targetExternalProjectId, StringComparison.OrdinalIgnoreCase))
                    {
                        slice.TargetDirectHours += entry.Hours;
                    }
                }
            }
        }
    }

    private void PopulateInternalPurchasesAndExpensesSlices(
        Dictionary<int, MonthSlice> sliceByMonthKey,
        DateTime startCutoff,
        DateTime endCutoff,
        Dictionary<string, Project> projects,
        Employee actor)
    {
        var allPurchaseOrders = _accountingContext.PurchaseOrders.GetAll()
            .Where(po => !string.Equals(po.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(po.Status, PurchaseOrderStatus.Draft, StringComparison.OrdinalIgnoreCase)
                         && po.Date.Date >= startCutoff
                         && po.Date.Date <= endCutoff);

        foreach (var po in allPurchaseOrders)
        {
            if (po.LineItems == null) continue;
            if (!sliceByMonthKey.TryGetValue(po.Date.Year * 12 + po.Date.Month, out var slice))
                continue;

            foreach (var item in po.LineItems)
            {
                if (string.IsNullOrWhiteSpace(item.ProjectId)) continue;
                if (!projects.TryGetValue(item.ProjectId, out var proj) || !proj.IsInternal) continue;
                if (!ProjectAccessHelper.CanAccess(actor, proj, _hrContext.Teams, allowAccountingFinancialRead: true)) continue;

                var summary = GetOrCreateInternalSummary(slice, proj);
                var net = Math.Round(item.Total, 2, MidpointRounding.AwayFromZero);
                var tax = po.ApplyTax ? Math.Round(item.Total * po.TaxRate, 2, MidpointRounding.AwayFromZero) : 0m;
                summary.PurchasesPreTax += net;
                summary.PurchasesTax += tax;
            }
        }

        foreach (var er in _accountingContext.ExpenseReports.GetAll().Where(r =>
                     !string.Equals(r.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase)
                     && r.LineItems != null && r.LineItems.Count > 0))
        {
            foreach (var item in er.LineItems)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ProjectId)) continue;
                var itemDate = item.Date.Date;
                if (itemDate < startCutoff || itemDate > endCutoff) continue;
                if (!projects.TryGetValue(item.ProjectId, out var proj) || !proj.IsInternal) continue;
                if (!ProjectAccessHelper.CanAccess(actor, proj, _hrContext.Teams, allowAccountingFinancialRead: true)) continue;
                if (!sliceByMonthKey.TryGetValue(itemDate.Year * 12 + itemDate.Month, out var slice)) continue;

                var summary = GetOrCreateInternalSummary(slice, proj);
                summary.ExpensesPreTax += Math.Round(item.EffectiveSubTotal, 2, MidpointRounding.AwayFromZero);
                summary.ExpensesTax += Math.Round(item.EffectiveTpsAmount + item.EffectiveTvqAmount, 2, MidpointRounding.AwayFromZero);
            }
        }

        // Direct Transactions linked to internal projects (MoneyOut adds to overhead pool; MoneyIn offsets it)
        foreach (var tx in (_accountingContext.DirectTransactions?.GetAll() ?? []).Where(t =>
                     !t.IsVoid
                     && !string.Equals(t.Direction, DirectTransactionDirection.Transfer, StringComparison.OrdinalIgnoreCase)
                     && t.Date.Date >= startCutoff
                     && t.Date.Date <= endCutoff))
        {
            if (!sliceByMonthKey.TryGetValue(tx.Date.Year * 12 + tx.Date.Month, out var slice)) continue;
            var fx = tx.ExchangeRate > 0m ? tx.ExchangeRate : 1.0m;
            var sign = string.Equals(tx.Direction, DirectTransactionDirection.MoneyIn, StringComparison.OrdinalIgnoreCase) ? -1m : 1m;

            if (tx.Lines is { Count: > 0 })
            {
                foreach (var line in tx.Lines)
                {
                    if (line == null) continue;
                    var effectiveProjectId = !string.IsNullOrWhiteSpace(line.ProjectId) ? line.ProjectId : tx.ProjectId;
                    if (string.IsNullOrWhiteSpace(effectiveProjectId)) continue;
                    if (!projects.TryGetValue(effectiveProjectId, out var proj) || !proj.IsInternal) continue;
                    if (!ProjectAccessHelper.CanAccess(actor, proj, _hrContext.Teams, allowAccountingFinancialRead: true)) continue;

                    var summary = GetOrCreateInternalSummary(slice, proj);
                    summary.ExpensesPreTax += Math.Round(line.SubTotal * fx * sign, 2, MidpointRounding.AwayFromZero);
                    summary.ExpensesTax += Math.Round(line.TaxAmount * fx * sign, 2, MidpointRounding.AwayFromZero);
                }
            }
            else if (!string.IsNullOrWhiteSpace(tx.ProjectId)
                     && projects.TryGetValue(tx.ProjectId, out var proj)
                     && proj.IsInternal
                     && ProjectAccessHelper.CanAccess(actor, proj, _hrContext.Teams, allowAccountingFinancialRead: true))
            {
                var summary = GetOrCreateInternalSummary(slice, proj);
                summary.ExpensesPreTax += Math.Round(tx.SubTotal * fx * sign, 2, MidpointRounding.AwayFromZero);
                summary.ExpensesTax += Math.Round(tx.TaxAmount * fx * sign, 2, MidpointRounding.AwayFromZero);
            }
        }
    }

    private static InternalProjectOverheadSummary GetOrCreateInternalSummary(MonthSlice slice, Project proj)
    {
        if (!slice.InternalStats.TryGetValue(proj.Id, out var summary))
        {
            summary = new InternalProjectOverheadSummary
            {
                ProjectId = proj.Id,
                ProjectNumber = proj.ProjectNumber ?? string.Empty,
                ProjectName = proj.Name ?? "Internal Project"
            };
            slice.InternalStats[proj.Id] = summary;
            slice.InternalContributors[proj.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return summary;
    }

    private static (decimal PeriodPreTax, decimal PeriodTax, decimal AllocPreTax, decimal AllocTax, int OverlappingCount, decimal MonthlyPreTax)
        CalculateAmortizedItemTotals(
            decimal totalPreTax,
            decimal totalTax,
            int startYear,
            int startMonth,
            IReadOnlyList<MonthSlice> activeSlices)
    {
        var windowStartIndex = startYear * 12 + (startMonth - 1);
        var windowEndIndex = windowStartIndex + 11;
        var baseMonthlyPreTax = Math.Round(totalPreTax / 12m, 2, MidpointRounding.AwayFromZero);
        var baseMonthlyTax = Math.Round(totalTax / 12m, 2, MidpointRounding.AwayFromZero);

        decimal periodPreTax = 0m;
        decimal periodTax = 0m;
        decimal allocPreTax = 0m;
        decimal allocTax = 0m;
        int count = 0;

        foreach (var slice in activeSlices)
        {
            var idx = slice.Year * 12 + (slice.Month - 1);
            if (idx < windowStartIndex || idx > windowEndIndex) continue;

            var mPreTax = slice.DayFactor == 1m
                ? baseMonthlyPreTax
                : Math.Round((totalPreTax / 12m) * slice.DayFactor, 2, MidpointRounding.AwayFromZero);
            var mTax = slice.DayFactor == 1m
                ? baseMonthlyTax
                : Math.Round((totalTax / 12m) * slice.DayFactor, 2, MidpointRounding.AwayFromZero);

            periodPreTax += mPreTax;
            periodTax += mTax;
            allocPreTax += mPreTax * slice.ShareRatio;
            allocTax += mTax * slice.ShareRatio;
            count++;
        }

        return (
            Math.Round(periodPreTax, 2, MidpointRounding.AwayFromZero),
            Math.Round(periodTax, 2, MidpointRounding.AwayFromZero),
            Math.Round(allocPreTax, 2, MidpointRounding.AwayFromZero),
            Math.Round(allocTax, 2, MidpointRounding.AwayFromZero),
            count,
            baseMonthlyPreTax);
    }

    private static List<(int Year, int Month)> GetCalendarMonths(DateTime start, DateTime end)
    {
        var list = new List<(int Year, int Month)>();
        var current = new DateTime(start.Year, start.Month, 1);
        var last = new DateTime(end.Year, end.Month, 1);

        while (current <= last)
        {
            list.Add((current.Year, current.Month));
            current = current.AddMonths(1);
        }

        if (list.Count == 0)
        {
            list.Add((start.Year, start.Month));
        }

        return list;
    }

    private static List<RecurringExpense> DeduplicateRecurringProfiles(IReadOnlyList<RecurringExpense> all)
    {
        var actuals = all.Where(e => !e.IsEstimate).ToList();
        var estimates = all.Where(e => e.IsEstimate).ToList();
        if (estimates.Count == 0) return actuals;

        var combined = new List<RecurringExpense>(actuals);
        foreach (var est in estimates)
        {
            bool exists = combined.Any(a =>
                string.Equals(a.Name?.Trim(), est.Name?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.Frequency, est.Frequency, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                combined.Add(est);
            }
        }
        return combined;
    }

    private static (decimal PreTax, decimal Tax) CalculateProfileMonthlyProratedAmount(
        RecurringExpense profile,
        IReadOnlyList<Bill> linkedBills,
        DateTime winStart,
        DateTime winEnd,
        int year,
        int month,
        decimal monthDayFactor,
        bool allowZeroAmountEstimation)
    {
        var freq = profile.Frequency ?? RecurringExpenseFrequency.Monthly;

        // Collect actual bill records strictly within [winStart .. winEnd] (merging History + linked Bills without double-counting)
        var actualRecordsInWindow = GetActualRecordsInWindow(
            profile,
            linkedBills,
            winStart,
            winEnd);

        if (string.Equals(freq, RecurringExpenseFrequency.Yearly, StringComparison.OrdinalIgnoreCase))
        {
            var yearRecords = GetActualRecordsInWindow(
                profile,
                linkedBills,
                new DateTime(year, 1, 1),
                new DateTime(year, 12, 31));

            if (yearRecords.Count > 0)
            {
                var annualPreTax = yearRecords.Sum(r => r.PreTax);
                var annualTax = yearRecords.Sum(r => r.Tax);
                return (
                    Math.Round((annualPreTax / 12m) * monthDayFactor, 2, MidpointRounding.AwayFromZero),
                    Math.Round((annualTax / 12m) * monthDayFactor, 2, MidpointRounding.AwayFromZero));
            }

            if (!allowZeroAmountEstimation && profile.Amount <= 0m)
                return (0m, 0m);

            var (basePreTax, baseTax) = GetExpectedBasePreTaxAndTax(profile);
            return (
                Math.Round((basePreTax / 12m) * monthDayFactor, 2, MidpointRounding.AwayFromZero),
                Math.Round((baseTax / 12m) * monthDayFactor, 2, MidpointRounding.AwayFromZero));
        }

        if (string.Equals(freq, RecurringExpenseFrequency.Quarterly, StringComparison.OrdinalIgnoreCase))
        {
            int qStartMonth = ((month - 1) / 3) * 3 + 1;
            var qStart = new DateTime(year, qStartMonth, 1);
            var qEnd = qStart.AddMonths(3).AddDays(-1);
            var quarterRecords = GetActualRecordsInWindow(profile, linkedBills, qStart, qEnd);

            if (quarterRecords.Count > 0)
            {
                var qPreTax = quarterRecords.Sum(r => r.PreTax);
                var qTax = quarterRecords.Sum(r => r.Tax);
                return (
                    Math.Round((qPreTax / 3m) * monthDayFactor, 2, MidpointRounding.AwayFromZero),
                    Math.Round((qTax / 3m) * monthDayFactor, 2, MidpointRounding.AwayFromZero));
            }

            if (!allowZeroAmountEstimation && profile.Amount <= 0m)
                return (0m, 0m);

            var (basePreTax, baseTax) = GetExpectedBasePreTaxAndTax(profile);
            return (
                Math.Round((basePreTax / 3m) * monthDayFactor, 2, MidpointRounding.AwayFromZero),
                Math.Round((baseTax / 3m) * monthDayFactor, 2, MidpointRounding.AwayFromZero));
        }

        // Monthly, Weekly, Bi-Weekly, As Needed:
        // If actual bills were logged in this window, use the actual billed amount
        if (actualRecordsInWindow.Count > 0)
        {
            return (
                Math.Round(actualRecordsInWindow.Sum(r => r.PreTax), 2, MidpointRounding.AwayFromZero),
                Math.Round(actualRecordsInWindow.Sum(r => r.Tax), 2, MidpointRounding.AwayFromZero));
        }

        if (string.Equals(freq, RecurringExpenseFrequency.AsNeeded, StringComparison.OrdinalIgnoreCase))
        {
            if ((profile.History == null || profile.History.Count == 0) && linkedBills.Count == 0 && profile.Amount > 0m)
            {
                var (estPreTax, estTax) = GetExpectedBasePreTaxAndTax(profile);
                return (
                    Math.Round(estPreTax * monthDayFactor, 2, MidpointRounding.AwayFromZero),
                    Math.Round(estTax * monthDayFactor, 2, MidpointRounding.AwayFromZero));
            }
            return (0m, 0m);
        }

        // Historical-only trackers (Amount <= 0) should not invent phantom bills in past months without an invoice
        if (profile.Amount <= 0m)
        {
            if (!allowZeroAmountEstimation)
                return (0m, 0m);

            var earliestMonth = GetEarliestRecordedMonth(profile, linkedBills);
            if (!earliestMonth.HasValue || (year * 12 + month) < earliestMonth.Value)
                return (0m, 0m);
        }

        var (expectedPreTax, expectedTax) = GetExpectedBasePreTaxAndTax(profile);
        decimal monthlyMultiplier = freq switch
        {
            RecurringExpenseFrequency.Weekly => 52m / 12m,
            RecurringExpenseFrequency.BiWeekly => 26m / 12m,
            RecurringExpenseFrequency.Monthly => 1m,
            _ => 1m
        };

        return (
            Math.Round(expectedPreTax * monthlyMultiplier * monthDayFactor, 2, MidpointRounding.AwayFromZero),
            Math.Round(expectedTax * monthlyMultiplier * monthDayFactor, 2, MidpointRounding.AwayFromZero));
    }

    private static int? GetEarliestRecordedMonth(RecurringExpense profile, IReadOnlyList<Bill> linkedBills)
    {
        int? minMonth = null;
        if (profile.History != null)
        {
            foreach (var h in profile.History)
            {
                if (h == null || h.Amount <= 0m || h.Date == default) continue;
                int key = h.Date.Year * 12 + h.Date.Month;
                if (!minMonth.HasValue || key < minMonth.Value) minMonth = key;
            }
        }
        foreach (var b in linkedBills)
        {
            if (b == null || b.IsVoid || b.Amount <= 0m || b.Date == default) continue;
            int key = b.Date.Year * 12 + b.Date.Month;
            if (!minMonth.HasValue || key < minMonth.Value) minMonth = key;
        }
        return minMonth;
    }

    private static List<(decimal PreTax, decimal Tax)> GetActualRecordsInWindow(
        RecurringExpense profile,
        IReadOnlyList<Bill> linkedBills,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var results = new List<(decimal PreTax, decimal Tax)>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (profile.History != null)
        {
            foreach (var h in profile.History)
            {
                if (h == null || h.Amount <= 0m) continue;
                var d = h.Date.Date;
                if (d < windowStart.Date || d > windowEnd.Date) continue;

                var (preTax, tax) = ExtractRecurringRecordOrDefaultPreTaxAndTax(profile, h);
                results.Add((preTax, tax));

                var key = !string.IsNullOrWhiteSpace(h.InvoiceNumber)
                    ? $"INV:{h.InvoiceNumber.Trim()}"
                    : $"DT:{d:yyyy-MM-dd}:{h.Amount:F2}";
                seenKeys.Add(key);
            }
        }

        foreach (var b in linkedBills)
        {
            if (b == null || b.IsVoid || b.Amount <= 0m) continue;
            var d = b.Date.Date;
            if (d < windowStart.Date || d > windowEnd.Date) continue;

            var key = !string.IsNullOrWhiteSpace(b.InvoiceNumber)
                ? $"INV:{b.InvoiceNumber.Trim()}"
                : $"DT:{d:yyyy-MM-dd}:{b.Amount:F2}";
            if (seenKeys.Contains(key)) continue;

            seenKeys.Add(key);
            results.Add(ExtractBillPreTaxAndTax(b));
        }

        return results;
    }

    private static (decimal PreTax, decimal Tax) GetExpectedBasePreTaxAndTax(RecurringExpense profile)
    {
        if (profile.Amount > 0m)
        {
            // Profile.Amount is the expected pre-tax or default amount:
            // Note: In Spokes CostEstimator & RecurringExpenseDialog, Default Amount is entered as a flat base amount.
            // To keep monthly proration predictable and exact, if IsTaxExempt is true or Default Amount is used,
            // Default Amount is treated as the Pre-Tax base (with tax added only when actual taxed bills are present).
            return (Math.Round(profile.Amount, 2, MidpointRounding.AwayFromZero), 0m);
        }

        var latest = profile.History?
            .Where(h => h != null && h.Amount > 0m)
            .OrderByDescending(h => h.Date)
            .FirstOrDefault();

        if (latest != null)
        {
            return ExtractRecurringRecordOrDefaultPreTaxAndTax(profile, latest);
        }

        return (0m, 0m);
    }

    private static (decimal PreTax, decimal Tax) ExtractRecurringRecordOrDefaultPreTaxAndTax(
        RecurringExpense profile,
        RecurringBillRecord? record)
    {
        if (record != null && record.Amount > 0m)
        {
            if (record.SubTotal > 0m)
            {
                var tax = record.TaxAmount > 0m
                    ? record.TaxAmount
                    : (record.TpsAmount + record.TvqAmount > 0m
                        ? record.TpsAmount + record.TvqAmount
                        : Math.Max(0m, record.Amount - record.SubTotal));
                return (Math.Round(record.SubTotal, 2, MidpointRounding.AwayFromZero), Math.Round(tax, 2, MidpointRounding.AwayFromZero));
            }

            return (Math.Round(record.Amount, 2, MidpointRounding.AwayFromZero), 0m);
        }

        if (profile.Amount > 0m)
        {
            return (Math.Round(profile.Amount, 2, MidpointRounding.AwayFromZero), 0m);
        }

        return (0m, 0m);
    }

    private static (decimal PreTax, decimal Tax) ExtractBillPreTaxAndTax(Bill bill)
    {
        decimal sign = bill.IsCreditNote ? -1m : 1m;
        if (bill.SubTotal > 0m)
        {
            var tax = bill.TaxAmount > 0m
                ? bill.TaxAmount
                : (bill.TpsAmount + bill.TvqAmount > 0m
                    ? bill.TpsAmount + bill.TvqAmount
                    : Math.Max(0m, bill.Amount - bill.SubTotal));
            return (
                Math.Round(bill.SubTotal * sign, 2, MidpointRounding.AwayFromZero),
                Math.Round(tax * sign, 2, MidpointRounding.AwayFromZero));
        }

        if (bill.Amount > 0m)
        {
            if (bill.IsTaxExempt)
            {
                return (Math.Round(bill.Amount * sign, 2, MidpointRounding.AwayFromZero), 0m);
            }

            var breakdown = FinancialCalculationEngine.CalculateFromGrandTotal(bill.Amount, bill.IsTaxExempt);
            return (
                Math.Round(breakdown.SubTotal * sign, 2, MidpointRounding.AwayFromZero),
                Math.Round(breakdown.TaxAmount * sign, 2, MidpointRounding.AwayFromZero));
        }

        return (0m, 0m);
    }

    public static bool IsScheduledFrequency(string? frequency) =>
        string.Equals(frequency, RecurringExpenseFrequency.Weekly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frequency, RecurringExpenseFrequency.BiWeekly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frequency, RecurringExpenseFrequency.Monthly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frequency, RecurringExpenseFrequency.Quarterly, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(frequency, RecurringExpenseFrequency.Yearly, StringComparison.OrdinalIgnoreCase);

    public static DateTime AdvanceRecurringDate(DateTime current, string? frequency)
    {
        if (string.Equals(frequency, RecurringExpenseFrequency.Weekly, StringComparison.OrdinalIgnoreCase))
            return current.AddDays(7);
        if (string.Equals(frequency, RecurringExpenseFrequency.BiWeekly, StringComparison.OrdinalIgnoreCase))
            return current.AddDays(14);
        if (string.Equals(frequency, RecurringExpenseFrequency.Quarterly, StringComparison.OrdinalIgnoreCase))
            return current.AddMonths(3);
        if (string.Equals(frequency, RecurringExpenseFrequency.Yearly, StringComparison.OrdinalIgnoreCase))
            return current.AddYears(1);
        return current.AddMonths(1);
    }

    public List<DueRecurringOccurrence> GetOverdueRecurringOccurrences(DateTime? asOfDate = null)
    {
        var cutoff = (asOfDate ?? DateTime.Today).Date;
        var results = new List<DueRecurringOccurrence>();

        foreach (var profile in _accountingContext.RecurringExpenses.GetAll())
        {
            if (!IsScheduledFrequency(profile.Frequency)) continue;
            if (profile.Amount <= 0m) continue;
            if (profile.NextDueDate == default || profile.NextDueDate.Date > cutoff) continue;

            int count = 0;
            var cursor = profile.NextDueDate.Date;
            while (cursor <= cutoff && count < 60)
            {
                count++;
                cursor = AdvanceRecurringDate(cursor, profile.Frequency);
            }

            results.Add(new DueRecurringOccurrence
            {
                RecurringExpenseId = profile.Id,
                Name = profile.Name,
                Supplier = profile.Supplier,
                Frequency = profile.Frequency,
                Amount = profile.Amount,
                NextDueDate = profile.NextDueDate.Date,
                MissedPeriodsCount = count
            });
        }

        return results.OrderBy(r => r.NextDueDate).ToList();
    }

    public int GenerateDueRecurringBills(IEnumerable<string>? expenseIds = null, DateTime? upToDate = null, Employee? actor = null)
    {
        if (actor != null)
        {
            DomainActorGuard.RequireAnyPermission(actor, "generate due recurring bills", allowSuspended: false,
                AppPermissions.Finance.AccessAccounting, AppPermissions.Purchases.ManageBills);
        }

        var cutoff = (upToDate ?? DateTime.Today).Date;
        var filterIds = expenseIds != null ? new HashSet<string>(expenseIds, StringComparer.OrdinalIgnoreCase) : null;
        int generated = 0;

        foreach (var profile in _accountingContext.RecurringExpenses.GetAll())
        {
            if (filterIds != null && !filterIds.Contains(profile.Id)) continue;
            if (!IsScheduledFrequency(profile.Frequency)) continue;
            if (profile.Amount <= 0m) continue;
            if (profile.NextDueDate == default || profile.NextDueDate.Date > cutoff) continue;

            profile.History ??= [];
            var cursor = profile.NextDueDate.Date;
            int safety = 0;
            bool modified = false;

            while (cursor <= cutoff && safety++ < 60)
            {
                // Avoid duplicating a record that already exists for the same billing date
                bool existsForDate = profile.History.Any(h => Math.Abs((h.Date.Date - cursor).TotalDays) <= 3);
                if (!existsForDate)
                {
                    var breakdown = FinancialCalculationEngine.CalculateFromGrandTotal(profile.Amount, profile.IsTaxExempt);
                    profile.History.Add(new RecurringBillRecord
                    {
                        Id = Guid.NewGuid().ToString(),
                        Date = cursor,
                        DueDate = cursor,
                        Amount = Math.Round(profile.Amount, 2, MidpointRounding.AwayFromZero),
                        SubTotal = breakdown.SubTotal,
                        TpsAmount = breakdown.TpsAmount,
                        TvqAmount = breakdown.TvqAmount,
                        TaxAmount = breakdown.TaxAmount,
                        IsTaxExempt = profile.IsTaxExempt,
                        GlAccountCode = profile.GlAccountCode,
                        TaxComponents = FinancialCalculationEngine.BuildTaxComponentsFromSubTotal(
                            breakdown.SubTotal,
                            FinancialCalculationEngine.ActiveTemplate?.SalesTaxes,
                            profile.IsTaxExempt,
                            forPurchases: true),
                        IsPaid = false
                    });
                    generated++;
                    modified = true;
                }

                cursor = AdvanceRecurringDate(cursor, profile.Frequency);
            }

            if (cursor != profile.NextDueDate.Date)
            {
                profile.NextDueDate = cursor;
                modified = true;
            }

            if (modified)
            {
                _accountingContext.RecurringExpenses.Save(profile);
                _ledgerService?.SyncRecurringExpense(profile);
            }
        }

        return generated;
    }
}
