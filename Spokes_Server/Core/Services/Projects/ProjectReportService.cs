namespace Spokes_Server.Core.Services.Projects;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Documents;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Security;

public class PeriodicLaborMatrixChunk
{
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public List<PeriodicLaborMatrixColumn> Columns { get; set; } = [];
    public List<PeriodicLaborMatrixRow> Rows { get; set; } = [];
    public decimal GrandTotalHours { get; set; }
    public decimal GrandTotalInternalCost { get; set; }
    public decimal GrandTotalBillableValue { get; set; }

    public decimal GetColumnTotalHours(string employeeId) =>
        Rows.Sum(r => r.GetHours(employeeId));

    public decimal GetColumnTotalInternalCost(string employeeId) =>
        Rows.Sum(r => r.GetInternalCost(employeeId));

    public decimal GetColumnTotalBillableValue(string employeeId) =>
        Rows.Sum(r => r.GetBillableValue(employeeId));
}

public class PeriodicLaborMatrixColumn
{
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
}

public class PeriodicLaborMatrixRow
{
    public string PeriodKey { get; set; } = string.Empty;
    public long SortKey { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string SubLabel { get; set; } = string.Empty;
    public Dictionary<string, decimal> HoursByEmployee { get; set; } = [];
    public Dictionary<string, decimal> InternalCostByEmployee { get; set; } = [];
    public Dictionary<string, decimal> BillableValueByEmployee { get; set; } = [];
    public decimal PeriodTotalHours { get; set; }
    public decimal RunningTotalHours { get; set; }
    public decimal PeriodTotalInternalCost { get; set; }
    public decimal PeriodTotalBillableValue { get; set; }

    public decimal GetHours(string employeeId) =>
        HoursByEmployee.TryGetValue(employeeId, out var h) ? h : 0m;

    public decimal GetInternalCost(string employeeId) =>
        InternalCostByEmployee.TryGetValue(employeeId, out var c) ? c : 0m;

    public decimal GetBillableValue(string employeeId) =>
        BillableValueByEmployee.TryGetValue(employeeId, out var b) ? b : 0m;
}

public class EmployeePeriodicLaborGroup
{
    public string EmployeeId { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public decimal TotalHours { get; set; }
    public decimal TotalInternalCost { get; set; }
    public decimal TotalBillableValue { get; set; }
    public decimal EffectiveInternalRate => TotalHours > 0 ? Math.Round(TotalInternalCost / TotalHours, 2) : 0m;
    public decimal EffectiveBillableRate => TotalHours > 0 ? Math.Round(TotalBillableValue / TotalHours, 2) : 0m;
    public List<EmployeePeriodicLaborRow> Rows { get; set; } = [];
}

public class EmployeePeriodicLaborRow
{
    public string PeriodKey { get; set; } = string.Empty;
    public long SortKey { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public string TasksSummary { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public decimal RunningHours { get; set; }
    public decimal InternalCost { get; set; }
    public decimal BillableValue { get; set; }
}

public class TaskPeriodicLaborGroup
{
    public string TaskKey { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string SubTaskName { get; set; } = string.Empty;
    public string DisplayTitle => string.IsNullOrWhiteSpace(SubTaskName) || SubTaskName == "General"
        ? TaskName
        : $"{TaskName} — {SubTaskName}";
    public decimal TotalHours { get; set; }
    public decimal TotalInternalCost { get; set; }
    public decimal TotalBillableValue { get; set; }
    public List<EmployeePeriodicLaborRow> Rows { get; set; } = [];
}

public class ReportPurchaseGroupViewModel
{
    public string GroupKey { get; set; } = string.Empty;
    public long SortKey { get; set; }
    public string GroupTitle { get; set; } = string.Empty;
    public List<ReportPurchaseItemSnapshot> Items { get; set; } = [];
    public int ItemCount => Items.Count;
    public decimal NetTotal => Items.Sum(i => i.NetTotal);
    public decimal TaxAmount => Items.Sum(i => i.TaxAmount);
    public decimal GrandTotal => Items.Sum(i => i.GrandTotal);
}

public class ReportExpenseGroupViewModel
{
    public string GroupKey { get; set; } = string.Empty;
    public long SortKey { get; set; }
    public string GroupTitle { get; set; } = string.Empty;
    public List<ReportExpenseItemSnapshot> Items { get; set; } = [];
    public int ItemCount => Items.Count;
    public decimal SubTotal => Items.Sum(i => i.SubTotal);
    public decimal TaxAmount => Items.Sum(i => i.TaxAmount);
    public decimal Total => Items.Sum(i => i.Total);
}

public class ProjectReportService
{
    private readonly ProjectsContext _projectsContext;
    private readonly HrContext _hrContext;
    private readonly AccountingContext _accountingContext;
    private readonly IBusinessOverheadService _overheadService;

    [ActivatorUtilitiesConstructor]
    public ProjectReportService(
        ProjectsContext projectsContext,
        HrContext hrContext,
        AccountingContext accountingContext,
        IBusinessOverheadService? overheadService = null)
    {
        ArgumentNullException.ThrowIfNull(projectsContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(accountingContext);

        _projectsContext = projectsContext;
        _hrContext = hrContext;
        _accountingContext = accountingContext;
        _overheadService = overheadService ?? new BusinessOverheadService(projectsContext, hrContext, accountingContext);
    }

    /// <summary>
    /// Builds a complete, self-contained point-in-time snapshot of the project's state,
    /// financials, labor, procurement, expenses, billing, commissions, and timeline notes
    /// scoped to <paramref name="periodStartDate"/> .. <paramref name="asOfDate"/> (inclusive).
    /// </summary>
    public ProjectReportSnapshot BuildSnapshot(
        Project project,
        DateTime? periodStartDate,
        DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(project);

        var startCutoff = periodStartDate?.Date;
        var endCutoff = asOfDate.Date;
        if (startCutoff.HasValue && startCutoff.Value > endCutoff)
        {
            startCutoff = endCutoff;
        }

        var employees = _hrContext.Employees.GetAll()
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var teams = _hrContext.Teams.GetAll()
            .ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var workTypes = _projectsContext.WorkTypes.GetAll()
            .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var rateCards = _projectsContext.RateCards.GetAll()
            .ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        RateCard? projectRateCard = null;
        if (!string.IsNullOrWhiteSpace(project.RateCardId))
        {
            rateCards.TryGetValue(project.RateCardId, out projectRateCard);
        }

        employees.TryGetValue(project.ProjectManagerId ?? string.Empty, out var pm);

        var snapshot = new ProjectReportSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            PeriodStartDate = startCutoff,
            AsOfDate = endCutoff,
            ProjectName = project.Name,
            ProjectNumber = project.ProjectNumber,
            ProjectDescription = project.Description,
            ProjectStatus = project.Status,
            BillingMethod = project.BillingMethod,
            IsInternalProject = project.IsInternal,
            ProjectManagerId = project.ProjectManagerId ?? string.Empty,
            ProjectManagerName = pm?.FullName ?? string.Empty,
            ProjectManagerEmail = pm?.IsEmailPublic == true ? pm.Email : string.Empty,
            Client = CloneClientInfo(project.Client)
        };

        // 1. Quotes up to AsOfDate
        var allProjectQuotes = _projectsContext.Quotes.GetByProject(project.Id)
            .Where(q => q.Date.Date <= endCutoff)
            .OrderBy(q => q.Date)
            .ToList();

        foreach (var q in allProjectQuotes)
        {
            snapshot.Quotes.Add(new ReportQuoteSnapshot
            {
                QuoteId = q.Id,
                QuoteNumber = q.QuoteNumber,
                Title = q.Title,
                Date = q.Date,
                Status = q.Status,
                TotalLabor = q.TotalLabor,
                TotalItems = q.TotalItems,
                SubTotal = q.SubTotal,
                TaxAmount = q.TaxAmount,
                GrandTotal = q.GrandTotal,
                TotalClientBought = q.TotalClientBought
            });
        }

        var (acceptedQuotes, primaryAcceptedQuote, budgetQuotes) = ResolveBudgetQuotes(project.Id, allProjectQuotes);

        if (primaryAcceptedQuote != null)
        {
            snapshot.ProjectStartDate = primaryAcceptedQuote.StartDate;
            snapshot.ProjectDeadline = primaryAcceptedQuote.Deadline;
        }
        else if (budgetQuotes.Count > 0)
        {
            var latestBudgetQuote = budgetQuotes.Last();
            snapshot.ProjectStartDate = latestBudgetQuote.StartDate;
            snapshot.ProjectDeadline = latestBudgetQuote.Deadline;
        }

        if (acceptedQuotes.Count > 0)
        {
            snapshot.ExpectedRevenueSubTotal = acceptedQuotes.Sum(q => q.SubTotal);
            snapshot.ExpectedRevenueTax = acceptedQuotes.Sum(q => q.TaxAmount);
            snapshot.ExpectedRevenue = acceptedQuotes.Sum(q => q.GrandTotal);
            snapshot.TotalClientBoughtBudget = acceptedQuotes.Sum(q => q.TotalClientBought);
        }
        else if (primaryAcceptedQuote != null)
        {
            snapshot.ExpectedRevenueSubTotal = primaryAcceptedQuote.SubTotal;
            snapshot.ExpectedRevenueTax = primaryAcceptedQuote.TaxAmount;
            snapshot.ExpectedRevenue = primaryAcceptedQuote.GrandTotal;
            snapshot.TotalClientBoughtBudget = primaryAcceptedQuote.TotalClientBought;
        }

        // 2. Client POs up to AsOfDate
        if (project.PurchaseOrders != null)
        {
            snapshot.ClientPos = project.PurchaseOrders
                .Where(po => !po.DateReceived.HasValue || po.DateReceived.Value.Date <= endCutoff)
                .OrderBy(po => po.DateReceived ?? DateTime.MinValue)
                .Select(po => new ClientPo
                {
                    Id = po.Id,
                    PoNumber = po.PoNumber,
                    Amount = po.Amount,
                    FilePath = po.FilePath,
                    FileName = po.FileName,
                    DateReceived = po.DateReceived,
                    MimeType = po.MimeType
                })
                .ToList();
        }

        // 3. Posted Invoices & Payments up to AsOfDate (Draft and Void invoices are excluded)
        var projectInvoices = _accountingContext.Invoices.GetByProject(project.Id)
            .Where(i => InvoiceStatus.IsPosted(i.Status)
                        && i.Date.Date <= endCutoff)
            .OrderBy(i => i.Date)
            .ToList();

        var billedTimeEntryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var billedPoItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var billedExpenseItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inv in projectInvoices)
        {
            if (inv.BilledTimeEntryIds != null)
            {
                foreach (var id in inv.BilledTimeEntryIds) billedTimeEntryIds.Add(id);
            }
            if (inv.BilledPoItemIds != null)
            {
                foreach (var id in inv.BilledPoItemIds) billedPoItemIds.Add(id);
            }
            if (inv.BilledExpenseItemIds != null)
            {
                foreach (var id in inv.BilledExpenseItemIds) billedExpenseItemIds.Add(id);
            }

            if (inv.LineItems != null)
            {
                foreach (var line in inv.LineItems)
                {
                    if (string.IsNullOrWhiteSpace(line.SourceId)) continue;
                    if (string.Equals(line.SourceType, "Time", StringComparison.OrdinalIgnoreCase))
                        billedTimeEntryIds.Add(line.SourceId);
                    else if (string.Equals(line.SourceType, "PO", StringComparison.OrdinalIgnoreCase))
                        billedPoItemIds.Add(line.SourceId);
                    else if (string.Equals(line.SourceType, "Expense", StringComparison.OrdinalIgnoreCase))
                        billedExpenseItemIds.Add(line.SourceId);
                }
            }

            var paymentsAsOf = inv.Payments?
                .Where(p => p.Date.Date <= endCutoff)
                .ToList() ?? [];
            var hasPaymentsAfterCutoff = inv.Payments?.Any(p => p.Date.Date > endCutoff) == true;
            var writeOffsAsOf = inv.WriteOffs?
                .Where(w => w.Date.Date <= endCutoff)
                .ToList() ?? [];
            var hasWriteOffsAfterCutoff = inv.WriteOffs?.Any(w => w.Date.Date > endCutoff) == true;

            var subTotal = Math.Round(inv.SubTotal, 2, MidpointRounding.AwayFromZero);
            var taxAmount = Math.Round(inv.TaxAmount, 2, MidpointRounding.AwayFromZero);
            var grandTotal = Math.Round(inv.GrandTotal, 2, MidpointRounding.AwayFromZero);

            var paidAsOf = Math.Round(paymentsAsOf.Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            var writtenOffAsOf = Math.Round(writeOffsAsOf.Sum(w => w.Amount), 2, MidpointRounding.AwayFromZero);
            var settledAsOf = Math.Round(paidAsOf + writtenOffAsOf, 2, MidpointRounding.AwayFromZero);
            var lastPaymentDate = paymentsAsOf.Count > 0
                ? paymentsAsOf.Max(p => p.Date)
                : (inv.DatePaid.HasValue && inv.DatePaid.Value.Date <= endCutoff ? inv.DatePaid : null);

            var effectiveStatus = inv.Status;
            if (grandTotal > 0m && (grandTotal - settledAsOf) <= 0.05m)
            {
                if (paidAsOf <= 0m && writtenOffAsOf > 0m)
                {
                    effectiveStatus = InvoiceStatus.WrittenOff;
                }
                else
                {
                    effectiveStatus = InvoiceStatus.Paid;
                    if (writtenOffAsOf <= 0m)
                    {
                        paidAsOf = grandTotal;
                    }
                }
            }
            else if (InvoiceStatus.IsSettled(inv.Status))
            {
                var settledAfterCutoff = hasPaymentsAfterCutoff || hasWriteOffsAfterCutoff || (inv.DatePaid.HasValue && inv.DatePaid.Value.Date > endCutoff);
                if (settledAfterCutoff)
                {
                    // Settled after AsOfDate -> was still Final/Partial at AsOfDate
                    effectiveStatus = settledAsOf > 0m ? InvoiceStatus.Partial : InvoiceStatus.Final;
                }
                else
                {
                    effectiveStatus = inv.Status;
                    if (string.Equals(inv.Status, InvoiceStatus.Paid, StringComparison.OrdinalIgnoreCase) &&
                        writtenOffAsOf <= 0m &&
                        (paidAsOf <= 0m || (grandTotal - paidAsOf) <= 0.05m))
                    {
                        paidAsOf = grandTotal;
                    }
                }
            }

            // If periodStartDate is set, we still keep invoices within the window in the list (or all up to AsOfDate if no start date)
            if (!startCutoff.HasValue || inv.Date.Date >= startCutoff.Value)
            {
                snapshot.Invoices.Add(new ReportInvoiceSnapshot
                {
                    InvoiceId = inv.Id,
                    InvoiceNumber = inv.InvoiceNumber,
                    Date = inv.Date,
                    DueDate = inv.DueDate,
                    Status = effectiveStatus,
                    SubTotal = subTotal,
                    TaxAmount = taxAmount,
                    GrandTotal = grandTotal,
                    AmountPaid = paidAsOf,
                    AmountWrittenOff = writtenOffAsOf,
                    DatePaid = lastPaymentDate
                });
            }
        }

        snapshot.TotalInvoicedSubTotal = snapshot.Invoices.Sum(i => i.SubTotal);
        snapshot.TotalInvoicedTax = snapshot.Invoices.Sum(i => i.TaxAmount);
        snapshot.TotalInvoiced = snapshot.Invoices.Sum(i => i.GrandTotal);
        snapshot.TotalPaid = snapshot.Invoices.Sum(i => i.AmountPaid);
        snapshot.TotalWrittenOff = snapshot.Invoices.Sum(i => i.AmountWrittenOff);

        // 4. Labor & Timesheets up to AsOfDate
        var allTimesheets = _hrContext.Timesheets.GetAll();
        var employerContributions = _accountingContext.EmployerContributions.GetAll();
        decimal cumulativeHoursToAsOf = 0m;

        foreach (var ts in allTimesheets)
        {
            if (ts?.Entries == null) continue;
            var isApprovedOrSubmitted = string.Equals(ts.Status, TimesheetStatus.Approved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ts.Status, TimesheetStatus.Submitted, StringComparison.OrdinalIgnoreCase);
            if (!isApprovedOrSubmitted) continue;

            employees.TryGetValue(ts.EmployeeId ?? string.Empty, out var emp);
            var empName = emp != null && !string.IsNullOrWhiteSpace(emp.FullName)
                ? emp.FullName
                : (!string.IsNullOrWhiteSpace(ts.EmployeeName) ? ts.EmployeeName : "Unknown");
            var teamId = emp?.TeamId ?? string.Empty;
            var teamName = !string.IsNullOrWhiteSpace(teamId) && teams.TryGetValue(teamId, out var t)
                ? t.Name
                : string.Empty;
            var empTotalHourlyCost = EmployerContribution.CalculateTotalHourlyCost(emp, employerContributions);

            foreach (var entry in ts.Entries)
            {
                if (entry == null || !string.Equals(entry.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entryDate = TimeEntryTypes.ResolveEntryDate(ts, entry);
                if (entryDate > endCutoff)
                    continue;

                cumulativeHoursToAsOf += entry.Hours;

                if (startCutoff.HasValue && entryDate < startCutoff.Value)
                    continue;

                workTypes.TryGetValue(entry.WorkTypeId ?? string.Empty, out var workType);
                var taskName = workType?.Name ?? "General";
                var subTaskName = "General";
                if (!string.IsNullOrWhiteSpace(entry.SubTaskId) && workType?.SubTasks != null)
                {
                    var st = workType.SubTasks.FirstOrDefault(s => string.Equals(s.Id, entry.SubTaskId, StringComparison.OrdinalIgnoreCase));
                    if (st != null && !string.IsNullOrWhiteSpace(st.Name))
                    {
                        subTaskName = st.Name;
                    }
                }

                var internalCostRate = empTotalHourlyCost > 0m
                    ? empTotalHourlyCost
                    : (workType?.CostRate ?? 0m);

                var billableRate = entry.IsBillable
                    ? project.GetEffectiveRate(entry.WorkTypeId ?? string.Empty, workType?.DefaultRate ?? 0m, projectRateCard)
                    : 0m;

                var isoYear = ts.Year > 0 ? ts.Year : ISOWeek.GetYear(entryDate);
                var isoWeek = ts.WeekNumber > 0 ? ts.WeekNumber : ISOWeek.GetWeekOfYear(entryDate);

                snapshot.TimeEntries.Add(new ReportTimeEntrySnapshot
                {
                    EntryId = entry.Id,
                    EmployeeId = emp?.Id ?? ts.EmployeeId ?? string.Empty,
                    EmployeeName = empName,
                    TeamId = teamId,
                    TeamName = teamName,
                    WorkTypeId = entry.WorkTypeId ?? string.Empty,
                    TaskName = taskName,
                    SubTaskId = entry.SubTaskId,
                    SubTaskName = subTaskName,
                    Date = entryDate,
                    IsoYear = isoYear,
                    IsoWeek = isoWeek,
                    Hours = entry.Hours,
                    InternalCostRate = internalCostRate,
                    BillableRate = billableRate,
                    Description = entry.Description ?? string.Empty
                });
            }
        }

        snapshot.TimeEntries = snapshot.TimeEntries
            .OrderBy(e => e.Date)
            .ThenBy(e => e.EmployeeName)
            .ToList();

        snapshot.CumulativeProjectHoursToAsOfDate = cumulativeHoursToAsOf;
        snapshot.TotalLaborHours = snapshot.TimeEntries.Sum(e => e.Hours);
        snapshot.TotalLaborInternalCost = snapshot.TimeEntries.Sum(e => e.InternalCost);
        snapshot.TotalLaborBillableValue = snapshot.TimeEntries.Sum(e => e.BillableValue);
        snapshot.UnbilledLaborValue = snapshot.TimeEntries
            .Where(e => !billedTimeEntryIds.Contains(e.EntryId))
            .Sum(e => e.BillableValue);

        // Build TaskLaborRows (merging QuotedHours from Accepted Quotes / Budget Quotes / Allocations)
        var taskBudgets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (budgetQuotes.Count > 0)
        {
            foreach (var item in budgetQuotes.SelectMany(q => q.LaborItems))
            {
                if (string.IsNullOrWhiteSpace(item.WorkTypeId)) continue;
                taskBudgets[item.WorkTypeId] = taskBudgets.GetValueOrDefault(item.WorkTypeId) + item.Hours;
            }
        }
        else if (project.Allocations != null && project.Allocations.Count > 0)
        {
            foreach (var alloc in project.Allocations)
            {
                if (string.IsNullOrWhiteSpace(alloc.WorkTypeId)) continue;
                taskBudgets[alloc.WorkTypeId] = taskBudgets.GetValueOrDefault(alloc.WorkTypeId) + alloc.QuotedHours;
            }
        }

        var taskRows = snapshot.TimeEntries
            .GroupBy(e => new { e.WorkTypeId, SubTaskId = e.SubTaskId ?? string.Empty, e.TaskName, e.SubTaskName })
            .Select(g => new ReportTaskLaborRow
            {
                WorkTypeId = g.Key.WorkTypeId,
                TaskName = g.Key.TaskName,
                SubTaskId = string.IsNullOrWhiteSpace(g.Key.SubTaskId) ? null : g.Key.SubTaskId,
                SubTaskName = g.Key.SubTaskName,
                ContributorNames = g.Select(x => x.EmployeeName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n).ToList(),
                ActualHours = g.Sum(x => x.Hours),
                InternalCost = g.Sum(x => x.InternalCost),
                BillableValue = g.Sum(x => x.BillableValue),
                SharePercent = snapshot.TotalLaborHours > 0
                    ? Math.Round(g.Sum(x => x.Hours) / snapshot.TotalLaborHours * 100m, 1)
                    : 0m
            })
            .ToList();

        foreach (var kvp in taskBudgets)
        {
            var wid = kvp.Key;
            var quotedHours = kvp.Value;
            var matching = taskRows.Where(r => string.Equals(r.WorkTypeId, wid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count > 0)
            {
                var target = matching.FirstOrDefault(r => r.SubTaskName == "General") ?? matching.First();
                target.QuotedHours = quotedHours;
            }
            else
            {
                var wtName = workTypes.TryGetValue(wid, out var wt) ? wt.Name : "General";
                taskRows.Add(new ReportTaskLaborRow
                {
                    WorkTypeId = wid,
                    TaskName = wtName,
                    SubTaskName = "General",
                    QuotedHours = quotedHours,
                    ActualHours = 0m
                });
            }
        }

        snapshot.TaskLaborRows = taskRows
            .OrderByDescending(r => r.ActualHours)
            .ThenBy(r => r.TaskName)
            .ThenBy(r => r.SubTaskName)
            .ToList();

        snapshot.TotalQuotedHours = snapshot.TaskLaborRows.Sum(r => r.QuotedHours);

        PopulateQuoteCostEstimates(snapshot, allProjectQuotes, budgetQuotes, employees, employerContributions, workTypes);

        // Build EmployeeLaborRows
        snapshot.EmployeeLaborRows = snapshot.TimeEntries
            .GroupBy(e => new { e.EmployeeId, e.EmployeeName, e.TeamId, e.TeamName })
            .Select(g =>
            {
                var hours = g.Sum(x => x.Hours);
                employees.TryGetValue(g.Key.EmployeeId, out var empObj);
                var baseRate = Math.Round(EmployerContribution.CalculateBaseHourlyRate(empObj), 2, MidpointRounding.AwayFromZero);
                var contribRate = Math.Round(EmployerContribution.CalculateHourlyContribution(empObj, employerContributions), 2, MidpointRounding.AwayFromZero);

                return new ReportEmployeeLaborRow
                {
                    EmployeeId = g.Key.EmployeeId,
                    EmployeeName = g.Key.EmployeeName,
                    TeamId = g.Key.TeamId,
                    TeamName = g.Key.TeamName,
                    TotalHours = hours,
                    SharePercent = snapshot.TotalLaborHours > 0
                        ? Math.Round(hours / snapshot.TotalLaborHours * 100m, 1)
                        : 0m,
                    BaseHourlyRate = baseRate,
                    EmployerContributionHourlyRate = contribRate,
                    InternalCost = g.Sum(x => x.InternalCost),
                    BillableValue = g.Sum(x => x.BillableValue)
                };
            })
            .OrderByDescending(r => r.TotalHours)
            .ThenBy(r => r.EmployeeName)
            .ToList();

        // 5. Purchases (Purchase Orders) up to AsOfDate
        var allPurchaseOrders = _accountingContext.PurchaseOrders.GetAll()
            .Where(po => !string.Equals(po.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase)
                         && po.Date.Date <= endCutoff
                         && (!startCutoff.HasValue || po.Date.Date >= startCutoff.Value))
            .OrderBy(po => po.Date)
            .ToList();

        foreach (var po in allPurchaseOrders)
        {
            if (po.LineItems == null) continue;
            var supplierName = !string.IsNullOrWhiteSpace(po.Supplier?.BusinessName)
                ? po.Supplier.BusinessName
                : (!string.IsNullOrWhiteSpace(po.SupplierRepName) ? po.SupplierRepName : "Unknown Supplier");

            foreach (var item in po.LineItems)
            {
                if (!string.Equals(item.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
                    continue;

                var netTotal = Math.Round(item.Total, 2);
                var taxAmount = po.ApplyTax ? Math.Round(item.Total * po.TaxRate, 2) : 0m;

                snapshot.PurchaseItems.Add(new ReportPurchaseItemSnapshot
                {
                    PoId = po.Id,
                    PoItemId = item.Id,
                    PoNumber = po.PoNumber,
                    Date = po.Date,
                    SupplierName = supplierName,
                    PartNumber = item.PartNumber ?? string.Empty,
                    Description = item.Description ?? string.Empty,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    NetTotal = netTotal,
                    TaxAmount = taxAmount
                });
            }
        }

        snapshot.PurchaseItems = snapshot.PurchaseItems
            .OrderBy(p => p.Date)
            .ThenBy(p => p.PoNumber)
            .ToList();

        snapshot.TotalPurchaseNet = snapshot.PurchaseItems.Sum(p => p.NetTotal);
        snapshot.TotalPurchaseTax = snapshot.PurchaseItems.Sum(p => p.TaxAmount);

        // 6. Employee Expenses up to AsOfDate
        var allExpenseReports = _accountingContext.ExpenseReports.GetAll();
        foreach (var expReport in allExpenseReports)
        {
            if (expReport?.LineItems == null) continue;
            if ((!expReport.DateSubmitted.HasValue && !expReport.DatePaid.HasValue) ||
                string.Equals(expReport.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var status = expReport.DatePaid.HasValue && expReport.DatePaid.Value.Date <= endCutoff
                ? ExpenseReportStatus.Paid
                : (expReport.DateSubmitted.HasValue && expReport.DateSubmitted.Value.Date <= endCutoff
                    ? ExpenseReportStatus.Submitted
                    : ExpenseReportStatus.Draft);
            if (string.Equals(status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in expReport.LineItems)
            {
                if (!string.Equals(item.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (item.Date.Date > endCutoff)
                    continue;
                if (startCutoff.HasValue && item.Date.Date < startCutoff.Value)
                    continue;

                var subTotal = Math.Round(item.EffectiveSubTotal, 2);
                var taxAmount = Math.Round(item.EffectiveTpsAmount + item.EffectiveTvqAmount, 2);
                var total = Math.Round(item.Total, 2);

                snapshot.ExpenseItems.Add(new ReportExpenseItemSnapshot
                {
                    ExpenseReportId = expReport.Id,
                    ExpenseItemId = item.Id,
                    ReportNumber = expReport.ReportNumber,
                    EmployeeId = expReport.EmployeeId,
                    EmployeeName = expReport.EmployeeName,
                    Date = item.Date,
                    Description = item.Description ?? string.Empty,
                    IsKilometrage = item.IsKilometrage,
                    Kilometers = item.Kilometers,
                    KilometrageRate = item.KilometrageRate,
                    SubTotal = subTotal,
                    TaxAmount = taxAmount,
                    Total = total,
                    Status = status
                });
            }
        }

        snapshot.ExpenseItems = snapshot.ExpenseItems
            .OrderBy(e => e.Date)
            .ThenBy(e => e.ReportNumber)
            .ToList();

        snapshot.TotalExpenseSubTotal = snapshot.ExpenseItems.Sum(e => e.SubTotal);
        snapshot.TotalExpenseTax = snapshot.ExpenseItems.Sum(e => e.TaxAmount);

        // 6b. Project-Linked Direct Transactions (Money In & Money Out, excluding Transfers & Void)
        var directSummary = _accountingContext.DirectTransactions?.SummarizeForProject(project.Id, startCutoff, endCutoff);
        if (directSummary != null && directSummary.Items.Count > 0)
        {
            var glAccountsByCode = (_accountingContext.GlAccounts?.GetAll() ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.Code))
                .GroupBy(a => a.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().DisplayLabel, StringComparer.OrdinalIgnoreCase);

            foreach (var item in directSummary.Items)
            {
                var catLabel = !string.IsNullOrWhiteSpace(item.GlAccountCode) && glAccountsByCode.TryGetValue(item.GlAccountCode, out var lbl)
                    ? lbl
                    : item.GlAccountCode;
                snapshot.DirectTransactions.Add(new ReportDirectTransactionSnapshot
                {
                    TransactionId = item.TransactionId,
                    TransactionNumber = item.TransactionNumber,
                    Date = item.Date,
                    Direction = item.Direction,
                    PartyName = item.PartyName,
                    Description = item.Description,
                    CategoryLabel = catLabel,
                    SubTotal = item.SubTotal,
                    TaxAmount = item.TaxAmount,
                    GrandTotal = item.GrandTotal
                });
            }

            snapshot.TotalDirectIncomeSubTotal = directSummary.DirectIncomeSubTotal;
            snapshot.TotalDirectIncomeTax = directSummary.DirectIncomeTax;
            snapshot.TotalDirectExpenseSubTotal = directSummary.DirectExpenseSubTotal;
            snapshot.TotalDirectExpenseTax = directSummary.DirectExpenseTax;
        }

        var unbilledPurchases = snapshot.PurchaseItems
            .Where(p => !billedPoItemIds.Contains(p.PoItemId))
            .Sum(p => p.GrandTotal);
        var unbilledExpenses = snapshot.ExpenseItems
            .Where(e => !billedExpenseItemIds.Contains(e.ExpenseItemId))
            .Sum(e => e.Total);
        snapshot.UnbilledExpensesValue = unbilledPurchases + unbilledExpenses;

        // 7. Commissions
        if (project.Commissions != null && project.Commissions.Count > 0)
        {
            var ledgers = _accountingContext.CommissionLedgerRecords.GetByProject(project.Id)
                .Where(l => !string.Equals(l.Status, CommissionStatus.Clawback, StringComparison.OrdinalIgnoreCase)
                            && l.DateGenerated.Date <= endCutoff
                            && (!startCutoff.HasValue || l.DateGenerated.Date >= startCutoff.Value))
                .ToList();

            if (ledgers.Count > 0)
            {
                foreach (var l in ledgers)
                {
                    var pct = project.Commissions.FirstOrDefault(c =>
                        string.Equals(c.Name, l.BeneficiaryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.EmployeeId, l.BeneficiaryId, StringComparison.OrdinalIgnoreCase))?.Percentage ?? 0m;

                    snapshot.Commissions.Add(new ReportCommissionSnapshot
                    {
                        BeneficiaryName = l.BeneficiaryName,
                        Basis = "Ledger",
                        Percentage = pct,
                        BaseAmount = l.BaseAmount,
                        CommissionAmount = l.CommissionAmount,
                        Status = l.Status
                    });
                }
            }
            else
            {
                var baseIncome = snapshot.TotalInvoiced > 0 ? snapshot.TotalInvoiced : snapshot.ExpectedRevenue;
                foreach (var comm in project.Commissions)
                {
                    var basis = string.IsNullOrWhiteSpace(comm.CommissionBasis) ? CommissionBasisTypes.Revenue : comm.CommissionBasis;
                    var baseAmount = basis.Contains("Margin", StringComparison.OrdinalIgnoreCase)
                        ? snapshot.GrossMargin
                        : baseIncome;

                    if (baseAmount <= 0m) continue;

                    snapshot.Commissions.Add(new ReportCommissionSnapshot
                    {
                        BeneficiaryName = comm.Name,
                        Basis = basis,
                        Percentage = comm.Percentage,
                        BaseAmount = baseAmount,
                        CommissionAmount = Math.Round(baseAmount * (comm.Percentage / 100m), 2),
                        Status = "Estimated"
                    });
                }
            }

            snapshot.TotalCommissions = snapshot.Commissions.Sum(c => c.CommissionAmount);
        }

        // 8. Project Timeline Notes (Non-system milestones/notes)
        var notes = _projectsContext.ProjectNotes.GetByProject(project.Id)
            .Where(n => !n.IsSystemEvent
                        && n.Date.Date <= endCutoff
                        && (!startCutoff.HasValue || n.Date.Date >= startCutoff.Value))
            .OrderBy(n => n.Date)
            .ToList();

        foreach (var note in notes)
        {
            snapshot.TimelineNotes.Add(new ReportTimelineNoteSnapshot
            {
                NoteId = note.Id,
                Date = note.Date,
                Category = note.Category ?? string.Empty,
                Title = note.Title ?? string.Empty,
                Content = note.Content ?? string.Empty,
                CreatedBy = note.CreatedBy ?? string.Empty
            });
        }

        // 9. Business Overhead Allocation (External Client Projects only)
        PopulateOverheadAllocation(snapshot, project);

        return snapshot;
    }

    /// <summary>
    /// Ensures that an existing snapshot has its business overhead allocation populated.
    /// </summary>
    public void EnsureOverheadAllocation(ProjectReportSnapshot snapshot, Project? project = null, Employee? actor = null)
    {
        if (snapshot == null) return;
        if (snapshot.IsInternalProject || project?.IsInternal == true) return;
        if (snapshot.OverheadRows.Count > 0 || snapshot.CompanyOverheadPoolPreTax > 0m || snapshot.TotalCompanyDirectLaborHours > 0m)
            return;

        PopulateOverheadAllocation(snapshot, project, actor);
    }

    private void PopulateOverheadAllocation(ProjectReportSnapshot snapshot, Project? project, Employee? actor = null)
    {
        if (snapshot.IsInternalProject || project?.IsInternal == true)
            return;

        var endCutoff = snapshot.AsOfDate.Date;
        DateTime effectiveStart;

        if (snapshot.PeriodStartDate.HasValue)
        {
            effectiveStart = snapshot.PeriodStartDate.Value.Date;
        }
        else
        {
            var activityDates = new List<DateTime>();
            if (snapshot.ProjectStartDate.HasValue && snapshot.ProjectStartDate.Value.ToDateTime(TimeOnly.MinValue) <= endCutoff)
                activityDates.Add(snapshot.ProjectStartDate.Value.ToDateTime(TimeOnly.MinValue));
            if (snapshot.TimeEntries.Count > 0)
                activityDates.Add(snapshot.TimeEntries.Min(t => t.Date.Date));
            if (snapshot.PurchaseItems.Count > 0)
                activityDates.Add(snapshot.PurchaseItems.Min(p => p.Date.Date));
            if (snapshot.ExpenseItems.Count > 0)
                activityDates.Add(snapshot.ExpenseItems.Min(e => e.Date.Date));
            if (snapshot.DirectTransactions.Count > 0)
                activityDates.Add(snapshot.DirectTransactions.Min(d => d.Date.Date));
            if (snapshot.Invoices.Count > 0)
                activityDates.Add(snapshot.Invoices.Min(i => i.Date.Date));

            if (activityDates.Count > 0)
            {
                effectiveStart = activityDates.Min();
            }
            else if (snapshot.Quotes.Count > 0)
            {
                effectiveStart = snapshot.Quotes.Min(q => q.Date.Date);
            }
            else
            {
                effectiveStart = new DateTime(endCutoff.Year, endCutoff.Month, 1);
            }
        }

        if (effectiveStart > endCutoff)
            effectiveStart = endCutoff;

        var effectiveActor = actor ?? new Employee
        {
            Id = "project-report-fallback",
            IsActive = true,
            IsSystem = true,
            Permissions = [Constants.AppPermissions.Projects.View, Constants.AppPermissions.Hr.ManageSalaries]
        };
        var overhead = _overheadService.CalculatePeriodOverhead(effectiveStart, endCutoff, project?.Id, actor: effectiveActor);

        snapshot.TotalCompanyDirectLaborHours = overhead.TotalCompanyDirectLaborHours;
        snapshot.OverheadAllocationSharePercent = overhead.AllocationSharePercent;
        snapshot.CompanyOverheadPoolPreTax = overhead.CompanyOverheadPoolPreTax;
        snapshot.CompanyOverheadPoolTax = overhead.CompanyOverheadPoolTax;
        snapshot.CompanyFixedOverheadPoolPreTax = overhead.FixedOverheadPoolPreTax;
        snapshot.CompanyFixedOverheadPoolTax = overhead.FixedOverheadPoolTax;
        snapshot.AllocatedFixedOverheadPreTax = overhead.AllocatedFixedOverheadPreTax;
        snapshot.AllocatedFixedOverheadTax = overhead.AllocatedFixedOverheadTax;
        snapshot.CompanyInternalCostsPoolPreTax = overhead.InternalCostsPoolPreTax;
        snapshot.CompanyInternalCostsPoolTax = overhead.InternalCostsPoolTax;
        snapshot.AllocatedInternalCostsPreTax = overhead.AllocatedInternalCostsPreTax;
        snapshot.AllocatedInternalCostsTax = overhead.AllocatedInternalCostsTax;
        snapshot.AllocatedOverheadPreTax = overhead.AllocatedOverheadPreTax;
        snapshot.AllocatedOverheadTax = overhead.AllocatedOverheadTax;
        snapshot.OverheadRows = overhead.BreakdownRows;
    }

    /// <summary>
    /// Ensures that an existing snapshot (including snapshots saved before quote internal-cost fields were introduced)
    /// has its quoted hours, estimated internal labor cost, and estimated material cost (with taxes) populated.
    /// </summary>
    public void EnsureQuoteCostEstimates(ProjectReportSnapshot snapshot, string projectId)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(projectId)) return;
        if (snapshot.QuotedLaborCost > 0m || snapshot.QuotedMaterialCostNet > 0m) return;

        var endCutoff = snapshot.AsOfDate.Date;
        var allProjectQuotes = _projectsContext.Quotes.GetByProject(projectId)
            .Where(q => q.Date.Date <= endCutoff)
            .OrderBy(q => q.Date)
            .ToList();

        if (allProjectQuotes.Count == 0) return;

        var (_, _, budgetQuotes) = ResolveBudgetQuotes(projectId, allProjectQuotes);

        var employees = _hrContext.Employees.GetAll()
            .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var employerContributions = _accountingContext.EmployerContributions.GetAll();
        var workTypes = _projectsContext.WorkTypes.GetAll()
            .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);

        PopulateQuoteCostEstimates(snapshot, allProjectQuotes, budgetQuotes, employees, employerContributions, workTypes);
    }

    private static void PopulateQuoteCostEstimates(
        ProjectReportSnapshot snapshot,
        IReadOnlyList<Quote> allProjectQuotes,
        IReadOnlyList<Quote> budgetQuotes,
        IReadOnlyDictionary<string, Employee> employees,
        IReadOnlyList<EmployerContribution> employerContributions,
        IReadOnlyDictionary<string, WorkType> workTypes)
    {
        decimal blendedEmployeeCostRate = 0m;
        if (snapshot.TotalLaborHours > 0m && snapshot.TotalLaborInternalCost > 0m)
        {
            blendedEmployeeCostRate = snapshot.TotalLaborInternalCost / snapshot.TotalLaborHours;
        }
        else
        {
            var activeRates = employees.Values
                .Where(e => e.IsActive)
                .Select(e => EmployerContribution.CalculateTotalHourlyCost(e, employerContributions))
                .Where(r => r > 0m)
                .ToList();
            if (activeRates.Count > 0)
            {
                blendedEmployeeCostRate = activeRates.Average();
            }
        }

        var quoteSnapById = snapshot.Quotes.ToDictionary(q => q.QuoteId, StringComparer.OrdinalIgnoreCase);

        foreach (var q in allProjectQuotes)
        {
            var quotedHours = q.LaborItems?.Sum(x => x.Hours) ?? 0m;
            decimal estLaborCost = 0m;
            if (q.LaborItems != null)
            {
                foreach (var item in q.LaborItems)
                {
                    var costRate = blendedEmployeeCostRate > 0m
                        ? blendedEmployeeCostRate
                        : (workTypes.TryGetValue(item.WorkTypeId ?? string.Empty, out var wt) && wt.CostRate > 0m
                            ? wt.CostRate
                            : item.Rate);
                    estLaborCost += item.Hours * costRate;
                }
            }
            estLaborCost = Math.Round(estLaborCost, 2, MidpointRounding.AwayFromZero);

            decimal estMatCostNet = 0m;
            if (q.MaterialLineItems != null)
            {
                foreach (var item in q.MaterialLineItems.Where(x => !x.IsClientBought))
                {
                    decimal unitCost = item.UnitCost;
                    if (unitCost <= 0m && item.UnitPrice > 0m)
                    {
                        unitCost = item.MarkupPercentage > 0m
                            ? item.UnitPrice / (1m + item.MarkupPercentage / 100m)
                            : item.UnitPrice;
                    }
                    estMatCostNet += Math.Round(item.Quantity * unitCost, 2, MidpointRounding.AwayFromZero);
                }
            }

            var effectiveTaxRate = q.TaxRate > 0m ? q.TaxRate : Spokes_Server.Core.Services.Accounting.FinancialCalculationEngine.ActiveCombinedTaxRate;
            var estMatCostTax = Math.Round(estMatCostNet * effectiveTaxRate, 2, MidpointRounding.AwayFromZero);

            if (quoteSnapById.TryGetValue(q.Id, out var qSnap))
            {
                qSnap.QuotedHours = quotedHours;
                qSnap.EstimatedLaborCost = estLaborCost;
                qSnap.EstimatedMaterialCostNet = estMatCostNet;
                qSnap.EstimatedMaterialCostTax = estMatCostTax;
            }
        }

        var budgetIds = budgetQuotes.Select(q => q.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var budgetSnaps = snapshot.Quotes.Where(qs => budgetIds.Contains(qs.QuoteId)).ToList();

        if (budgetSnaps.Count > 0)
        {
            snapshot.QuotedLaborCost = budgetSnaps.Sum(qs => qs.EstimatedLaborCost);
            snapshot.QuotedMaterialCostNet = budgetSnaps.Sum(qs => qs.EstimatedMaterialCostNet);
            snapshot.QuotedMaterialCostTax = budgetSnaps.Sum(qs => qs.EstimatedMaterialCostTax);
            if (snapshot.TotalQuotedHours <= 0m)
            {
                snapshot.TotalQuotedHours = budgetSnaps.Sum(qs => qs.QuotedHours);
            }
        }
        else if (snapshot.TotalQuotedHours > 0m && blendedEmployeeCostRate > 0m)
        {
            snapshot.QuotedLaborCost = Math.Round(snapshot.TotalQuotedHours * blendedEmployeeCostRate, 2, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Resolves the formatting <see cref="CultureInfo"/> for a document template so month and period names
    /// automatically match the template's language without requiring manual month-by-month translations.
    /// </summary>
    public static CultureInfo ResolveTemplateCulture(DocumentTemplate? template) =>
        DocumentTemplateHelper.ResolveCulture(template);

    private static string FormatMonthYear(DateTime date, CultureInfo? culture)
    {
        var effectiveCulture = culture ?? CultureInfo.CurrentUICulture;
        var formatted = date.ToString("MMMM yyyy", effectiveCulture);
        if (string.IsNullOrEmpty(formatted)) return formatted;
        return char.ToUpper(formatted[0], effectiveCulture) + formatted[1..];
    }

    /// <summary>
    /// Builds periodic labor matrices (Rows = Weeks or Months, Columns = Employees),
    /// automatically chunking wide employee rosters into multiple tables of at most
    /// <paramref name="maxEmployeeColumnsPerTable"/> columns so portrait 8.5"x11" PDF pages never overflow.
    /// </summary>
    public List<PeriodicLaborMatrixChunk> BuildPeriodicLaborMatrices(
        ProjectReportSnapshot snapshot,
        string granularity,
        int maxEmployeeColumnsPerTable = 10,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TimeEntries.Count == 0) return [];

        var isMonthly = string.Equals(granularity, ProjectReportPeriodGranularity.Monthly, StringComparison.OrdinalIgnoreCase);

        var distinctEmployees = snapshot.TimeEntries
            .GroupBy(e => e.EmployeeId)
            .Select(g =>
            {
                var first = g.First();
                return new PeriodicLaborMatrixColumn
                {
                    EmployeeId = first.EmployeeId,
                    EmployeeName = first.EmployeeName,
                    ShortName = FormatShortEmployeeName(first.EmployeeName),
                    TeamName = first.TeamName
                };
            })
            .OrderBy(c => c.EmployeeName)
            .ToList();

        // Group all time entries by period
        var periodGroups = snapshot.TimeEntries
            .GroupBy(e => GetPeriodKeyAndLabels(e, isMonthly, culture))
            .OrderBy(g => g.Key.SortKey)
            .ToList();

        var allRows = new List<PeriodicLaborMatrixRow>();
        decimal runningTotalHours = 0m;

        foreach (var pg in periodGroups)
        {
            var hoursByEmp = pg
                .GroupBy(e => e.EmployeeId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Hours), StringComparer.OrdinalIgnoreCase);
            var costByEmp = pg
                .GroupBy(e => e.EmployeeId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.InternalCost), StringComparer.OrdinalIgnoreCase);
            var billableByEmp = pg
                .GroupBy(e => e.EmployeeId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.BillableValue), StringComparer.OrdinalIgnoreCase);

            var periodTotalHours = pg.Sum(x => x.Hours);
            runningTotalHours += periodTotalHours;

            allRows.Add(new PeriodicLaborMatrixRow
            {
                PeriodKey = pg.Key.PeriodKey,
                SortKey = pg.Key.SortKey,
                PeriodLabel = pg.Key.PeriodLabel,
                SubLabel = pg.Key.SubLabel,
                HoursByEmployee = hoursByEmp,
                InternalCostByEmployee = costByEmp,
                BillableValueByEmployee = billableByEmp,
                PeriodTotalHours = periodTotalHours,
                RunningTotalHours = runningTotalHours,
                PeriodTotalInternalCost = pg.Sum(x => x.InternalCost),
                PeriodTotalBillableValue = pg.Sum(x => x.BillableValue)
            });
        }

        var safeChunkSize = Math.Max(3, maxEmployeeColumnsPerTable);
        var columnChunks = distinctEmployees
            .Chunk(safeChunkSize)
            .ToList();

        var result = new List<PeriodicLaborMatrixChunk>();
        var grandHours = allRows.Sum(r => r.PeriodTotalHours);
        var grandCost = allRows.Sum(r => r.PeriodTotalInternalCost);
        var grandBillable = allRows.Sum(r => r.PeriodTotalBillableValue);

        for (int i = 0; i < columnChunks.Count; i++)
        {
            result.Add(new PeriodicLaborMatrixChunk
            {
                ChunkIndex = i + 1,
                TotalChunks = columnChunks.Count,
                Columns = columnChunks[i].ToList(),
                Rows = allRows,
                GrandTotalHours = grandHours,
                GrandTotalInternalCost = grandCost,
                GrandTotalBillableValue = grandBillable
            });
        }

        return result;
    }

    /// <summary>
    /// Builds per-employee periodic breakdown groups ("Détailler par employé et par semaine / mois"),
    /// with each employee's weekly or monthly rows, task summaries, hours, and financials.
    /// </summary>
    public List<EmployeePeriodicLaborGroup> BuildEmployeePeriodicGroups(
        ProjectReportSnapshot snapshot,
        string granularity,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TimeEntries.Count == 0) return [];

        var isMonthly = string.Equals(granularity, ProjectReportPeriodGranularity.Monthly, StringComparison.OrdinalIgnoreCase);

        return snapshot.TimeEntries
            .GroupBy(e => new { e.EmployeeId, e.EmployeeName, e.TeamName })
            .Select(empGroup =>
            {
                var rows = BuildPeriodicLaborRows(
                    empGroup,
                    isMonthly,
                    culture,
                    pg => pg
                        .Select(x => string.IsNullOrWhiteSpace(x.SubTaskName) || x.SubTaskName == "General"
                            ? x.TaskName
                            : $"{x.TaskName} ({x.SubTaskName})")
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct()
                        .OrderBy(t => t));

                return new EmployeePeriodicLaborGroup
                {
                    EmployeeId = empGroup.Key.EmployeeId,
                    EmployeeName = empGroup.Key.EmployeeName,
                    TeamName = empGroup.Key.TeamName,
                    TotalHours = empGroup.Sum(x => x.Hours),
                    TotalInternalCost = empGroup.Sum(x => x.InternalCost),
                    TotalBillableValue = empGroup.Sum(x => x.BillableValue),
                    Rows = rows
                };
            })
            .OrderByDescending(g => g.TotalHours)
            .ThenBy(g => g.EmployeeName)
            .ToList();
    }

    /// <summary>
    /// Builds per-task periodic breakdown groups.
    /// </summary>
    public List<TaskPeriodicLaborGroup> BuildTaskPeriodicGroups(
        ProjectReportSnapshot snapshot,
        string granularity,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TimeEntries.Count == 0) return [];

        var isMonthly = string.Equals(granularity, ProjectReportPeriodGranularity.Monthly, StringComparison.OrdinalIgnoreCase);

        return snapshot.TimeEntries
            .GroupBy(e => new { e.WorkTypeId, SubTaskId = e.SubTaskId ?? string.Empty, e.TaskName, e.SubTaskName })
            .Select(taskGroup =>
            {
                var rows = BuildPeriodicLaborRows(
                    taskGroup,
                    isMonthly,
                    culture,
                    pg => pg
                        .Select(x => x.EmployeeName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct()
                        .OrderBy(n => n));

                return new TaskPeriodicLaborGroup
                {
                    TaskKey = $"{taskGroup.Key.WorkTypeId}_{taskGroup.Key.SubTaskId}",
                    TaskName = taskGroup.Key.TaskName,
                    SubTaskName = taskGroup.Key.SubTaskName,
                    TotalHours = taskGroup.Sum(x => x.Hours),
                    TotalInternalCost = taskGroup.Sum(x => x.InternalCost),
                    TotalBillableValue = taskGroup.Sum(x => x.BillableValue),
                    Rows = rows
                };
            })
            .OrderByDescending(g => g.TotalHours)
            .ThenBy(g => g.TaskName)
            .ToList();
    }

    /// <summary>
    /// Groups purchase items by Month ("Achats par mois"), by Supplier, or Chronologically.
    /// </summary>
    public List<ReportPurchaseGroupViewModel> BuildPurchaseGroups(
        ProjectReportSnapshot snapshot,
        string grouping,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PurchaseItems.Count == 0) return [];

        if (string.Equals(grouping, ProjectReportPurchasesGrouping.BySupplier, StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.PurchaseItems
                .GroupBy(p => string.IsNullOrWhiteSpace(p.SupplierName) ? "Unknown Supplier" : p.SupplierName)
                .Select((g, idx) => new ReportPurchaseGroupViewModel
                {
                    GroupKey = g.Key,
                    SortKey = idx,
                    GroupTitle = g.Key,
                    Items = g.OrderBy(i => i.Date).ThenBy(i => i.PoNumber).ToList()
                })
                .OrderByDescending(g => g.GrandTotal)
                .ThenBy(g => g.GroupTitle)
                .ToList();
        }

        if (string.Equals(grouping, ProjectReportPurchasesGrouping.Chronological, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new ReportPurchaseGroupViewModel
                {
                    GroupKey = "ALL",
                    SortKey = 0,
                    GroupTitle = string.Empty,
                    Items = snapshot.PurchaseItems.OrderBy(i => i.Date).ThenBy(i => i.PoNumber).ToList()
                }
            ];
        }

        // Default: ByMonthDetailed or ByMonthSummaryOnly
        return snapshot.PurchaseItems
            .GroupBy(p => new { p.Date.Year, p.Date.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var monthDate = new DateTime(g.Key.Year, g.Key.Month, 1);
                return new ReportPurchaseGroupViewModel
                {
                    GroupKey = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                    SortKey = g.Key.Year * 100L + g.Key.Month,
                    GroupTitle = FormatMonthYear(monthDate, culture),
                    Items = g.OrderBy(i => i.Date).ThenBy(i => i.PoNumber).ToList()
                };
            })
            .ToList();
    }

    /// <summary>
    /// Groups employee expense items by Month, by Employee, or Chronologically.
    /// </summary>
    public List<ReportExpenseGroupViewModel> BuildExpenseGroups(
        ProjectReportSnapshot snapshot,
        string grouping,
        bool submittedOrPaidOnly,
        CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var items = snapshot.ExpenseItems
            .Where(e => !string.Equals(e.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase));
        if (submittedOrPaidOnly)
        {
            items = items.Where(e =>
                string.Equals(e.Status, ExpenseReportStatus.Submitted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Status, ExpenseReportStatus.Paid, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = items.ToList();
        if (filtered.Count == 0) return [];

        if (string.Equals(grouping, ProjectReportExpensesGrouping.ByEmployee, StringComparison.OrdinalIgnoreCase))
        {
            return filtered
                .GroupBy(e => string.IsNullOrWhiteSpace(e.EmployeeName) ? "Unknown" : e.EmployeeName)
                .Select((g, idx) => new ReportExpenseGroupViewModel
                {
                    GroupKey = g.Key,
                    SortKey = idx,
                    GroupTitle = g.Key,
                    Items = g.OrderBy(i => i.Date).ThenBy(i => i.ReportNumber).ToList()
                })
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.GroupTitle)
                .ToList();
        }

        if (string.Equals(grouping, ProjectReportExpensesGrouping.Chronological, StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new ReportExpenseGroupViewModel
                {
                    GroupKey = "ALL",
                    SortKey = 0,
                    GroupTitle = string.Empty,
                    Items = filtered.OrderBy(i => i.Date).ThenBy(i => i.ReportNumber).ToList()
                }
            ];
        }

        // Default: ByMonth
        return filtered
            .GroupBy(e => new { e.Date.Year, e.Date.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g =>
            {
                var monthDate = new DateTime(g.Key.Year, g.Key.Month, 1);
                return new ReportExpenseGroupViewModel
                {
                    GroupKey = $"{g.Key.Year:D4}-{g.Key.Month:D2}",
                    SortKey = g.Key.Year * 100L + g.Key.Month,
                    GroupTitle = FormatMonthYear(monthDate, culture),
                    Items = g.OrderBy(i => i.Date).ThenBy(i => i.ReportNumber).ToList()
                };
            })
            .ToList();
    }

    private (List<Quote> AcceptedQuotes, Quote? PrimaryAcceptedQuote, List<Quote> BudgetQuotes) ResolveBudgetQuotes(string projectId, List<Quote> allProjectQuotes)
    {
        var acceptedQuotes = allProjectQuotes
            .Where(q => string.Equals(q.Status, QuoteStatus.Accepted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var primaryAcceptedQuote = acceptedQuotes.LastOrDefault()
            ?? _projectsContext.Quotes.GetByProject(projectId)
                .FirstOrDefault(q => string.Equals(q.Status, QuoteStatus.Accepted, StringComparison.OrdinalIgnoreCase));

        var budgetQuotes = acceptedQuotes.Count > 0
            ? acceptedQuotes
            : (primaryAcceptedQuote != null
                ? [primaryAcceptedQuote]
                : allProjectQuotes
                    .Where(q => !string.Equals(q.Status, QuoteStatus.Rejected, StringComparison.OrdinalIgnoreCase))
                    .ToList());

        return (acceptedQuotes, primaryAcceptedQuote, budgetQuotes);
    }

    private static List<EmployeePeriodicLaborRow> BuildPeriodicLaborRows(
        IEnumerable<ReportTimeEntrySnapshot> entries,
        bool isMonthly,
        CultureInfo? culture,
        Func<IGrouping<(string PeriodKey, long SortKey, string PeriodLabel, string SubLabel), ReportTimeEntrySnapshot>, IEnumerable<string>> summaryItemsSelector)
    {
        decimal running = 0m;
        return entries
            .GroupBy(e => GetPeriodKeyAndLabels(e, isMonthly, culture))
            .OrderBy(pg => pg.Key.SortKey)
            .Select(pg =>
            {
                var hrs = pg.Sum(x => x.Hours);
                running += hrs;

                return new EmployeePeriodicLaborRow
                {
                    PeriodKey = pg.Key.PeriodKey,
                    SortKey = pg.Key.SortKey,
                    PeriodLabel = string.IsNullOrWhiteSpace(pg.Key.SubLabel)
                        ? pg.Key.PeriodLabel
                        : $"{pg.Key.PeriodLabel} ({pg.Key.SubLabel})",
                    TasksSummary = string.Join(", ", summaryItemsSelector(pg)),
                    Hours = hrs,
                    RunningHours = running,
                    InternalCost = pg.Sum(x => x.InternalCost),
                    BillableValue = pg.Sum(x => x.BillableValue)
                };
            })
            .ToList();
    }

    private static (string PeriodKey, long SortKey, string PeriodLabel, string SubLabel) GetPeriodKeyAndLabels(
        ReportTimeEntrySnapshot entry,
        bool isMonthly,
        CultureInfo? culture = null)
    {
        var effectiveCulture = culture ?? CultureInfo.CurrentUICulture;
        if (isMonthly)
        {
            var year = entry.Date.Year;
            var month = entry.Date.Month;
            var monthStart = new DateTime(year, month, 1);
            return (
                $"{year:D4}-{month:D2}",
                year * 100L + month,
                FormatMonthYear(monthStart, effectiveCulture),
                string.Empty
            );
        }

        var isoYear = entry.IsoYear > 0 ? entry.IsoYear : ISOWeek.GetYear(entry.Date);
        var isoWeek = entry.IsoWeek > 0 ? entry.IsoWeek : ISOWeek.GetWeekOfYear(entry.Date);
        string subLabel = string.Empty;
        try
        {
            var monday = ISOWeek.ToDateTime(isoYear, isoWeek, DayOfWeek.Monday);
            var sunday = monday.AddDays(6);
            subLabel = monday.Month == sunday.Month
                ? $"{monday.ToString("MMM dd", effectiveCulture)}–{sunday.ToString("dd", effectiveCulture)}"
                : $"{monday.ToString("MMM dd", effectiveCulture)}–{sunday.ToString("MMM dd", effectiveCulture)}";
        }
        catch
        {
            // Ignore date conversion errors
        }

        return (
            $"{isoYear:D4}-W{isoWeek:D2}",
            isoYear * 100L + isoWeek,
            $"{isoYear}-W{isoWeek:D2}",
            subLabel
        );
    }

    private static string FormatShortEmployeeName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "Unknown";
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0];
        return $"{parts[0]} {parts[^1][0]}.";
    }

    public sealed record ProjectReportViewerPermissions(
        bool CanViewAccounting,
        bool CanViewCostRates,
        bool CanViewPurchases,
        bool CanViewLaborRates,
        bool CanViewAllLabor);

    public static ProjectReportViewerPermissions GetViewerPermissions(Employee? emp)
    {
        if (emp == null)
        {
            return new ProjectReportViewerPermissions(false, false, false, false, false);
        }

        var canViewAccounting = emp.IsEffectiveAdmin || emp.HasPermission(AppPermissions.Finance.AccessAccounting);
        var canViewCostRates = canViewAccounting ||
                               emp.HasPermission(AppPermissions.Admin.ManageUsers) ||
                               emp.HasPermission(AppPermissions.Hr.ManageSalaries);
        var canViewPurchases = canViewAccounting ||
                               (emp.IsAdmin && emp.IsActive && !emp.IsBanned) ||
                               emp.HasEffectivePermission(AppPermissions.Purchases.ViewPOs, allowSuspended: true) ||
                               emp.HasEffectivePermission(AppPermissions.Purchases.ManagePOs, allowSuspended: true);
        var canViewLaborRates = canViewCostRates || emp.HasPermission(AppPermissions.Projects.ApproveQuotes);
        var canViewAllLabor = emp.HasEffectivePermission(AppPermissions.Timesheets.ViewReport, allowSuspended: true);
        return new ProjectReportViewerPermissions(
            canViewAccounting,
            canViewCostRates,
            canViewPurchases,
            canViewLaborRates,
            canViewAllLabor);
    }

    /// <summary>
    /// Returns a viewer-scoped shallow clone of <paramref name="source"/> without mutating the persisted snapshot.
    /// Enforces purchase, expense, internal-cost, labor-rate, team discovery, and employee discovery boundaries.
    /// </summary>
    public ProjectReportSnapshot ScopeSnapshotForViewer(
        ProjectReportSnapshot source,
        Employee? viewer,
        IUserPrivacyService privacyService,
        HRService hrService,
        IEnumerable<Employee> allEmployees,
        IEnumerable<Team> allTeams,
        bool canViewPurchases,
        bool canViewExpenses,
        bool canViewLaborFinancials,
        bool canViewInternalCosts)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(privacyService);
        ArgumentNullException.ThrowIfNull(hrService);
        ArgumentNullException.ThrowIfNull(allEmployees);
        ArgumentNullException.ThrowIfNull(allTeams);

        var laborScope = ProjectLaborCalculator.ResolveViewerLaborScope(viewer, allEmployees, allTeams, hrService, privacyService);
        var empMap = laborScope.EmployeeLookup;
        var visibleTeamNames = laborScope.VisibleTeams.Values
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsVisibleContributor(string? empId) =>
            laborScope.IsVisibleContributor(viewer, empId, privacyService);

        var visibleDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopedTimeEntries = new List<ReportTimeEntrySnapshot>(source.TimeEntries.Count);
        foreach (var entry in source.TimeEntries)
        {
            if (entry == null) continue;
            var visible = IsVisibleContributor(entry.EmployeeId);
            if (visible && !string.IsNullOrWhiteSpace(entry.EmployeeName))
            {
                visibleDisplayNames.Add(entry.EmployeeName);
            }

            var teamVisible = visible && !string.IsNullOrWhiteSpace(entry.TeamName) && visibleTeamNames.Contains(entry.TeamName);
            scopedTimeEntries.Add(new ReportTimeEntrySnapshot
            {
                EntryId = entry.EntryId,
                EmployeeId = visible ? entry.EmployeeId : ProjectLaborCalculator.RestrictedContributorBucketId,
                EmployeeName = visible ? entry.EmployeeName : "Restricted Contributors",
                TeamId = teamVisible ? entry.TeamId : string.Empty,
                TeamName = teamVisible ? entry.TeamName : string.Empty,
                WorkTypeId = entry.WorkTypeId,
                TaskName = entry.TaskName,
                SubTaskId = entry.SubTaskId,
                SubTaskName = entry.SubTaskName,
                Date = entry.Date,
                IsoYear = entry.IsoYear,
                IsoWeek = entry.IsoWeek,
                Hours = entry.Hours,
                InternalCostRate = canViewInternalCosts ? entry.InternalCostRate : 0m,
                BillableRate = canViewLaborFinancials ? entry.BillableRate : 0m,
                Description = visible ? entry.Description : string.Empty
            });
        }

        var scopedEmployeeLaborRows = new List<ReportEmployeeLaborRow>();
        foreach (var row in source.EmployeeLaborRows)
        {
            if (row == null || !IsVisibleContributor(row.EmployeeId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.EmployeeName))
            {
                visibleDisplayNames.Add(row.EmployeeName);
            }

            var teamVisible = !string.IsNullOrWhiteSpace(row.TeamName) && visibleTeamNames.Contains(row.TeamName);
            scopedEmployeeLaborRows.Add(new ReportEmployeeLaborRow
            {
                EmployeeId = row.EmployeeId,
                EmployeeName = row.EmployeeName,
                TeamId = teamVisible ? row.TeamId : string.Empty,
                TeamName = teamVisible ? row.TeamName : string.Empty,
                TotalHours = row.TotalHours,
                SharePercent = row.SharePercent,
                BaseHourlyRate = canViewInternalCosts ? row.BaseHourlyRate : 0m,
                EmployerContributionHourlyRate = canViewInternalCosts ? row.EmployerContributionHourlyRate : 0m,
                InternalCost = canViewInternalCosts ? row.InternalCost : 0m,
                BillableValue = canViewLaborFinancials ? row.BillableValue : 0m
            });
        }

        var scopedTaskLaborRows = source.TaskLaborRows
            .Where(r => r != null)
            .Select(r => new ReportTaskLaborRow
            {
                WorkTypeId = r.WorkTypeId,
                TaskName = r.TaskName,
                SubTaskId = r.SubTaskId,
                SubTaskName = r.SubTaskName,
                ContributorNames = r.ContributorNames
                    .Where(n => !string.IsNullOrWhiteSpace(n) && visibleDisplayNames.Contains(n))
                    .ToList(),
                QuotedHours = r.QuotedHours,
                ActualHours = r.ActualHours,
                InternalCost = canViewInternalCosts ? r.InternalCost : 0m,
                BillableValue = canViewLaborFinancials ? r.BillableValue : 0m,
                SharePercent = r.SharePercent
            })
            .ToList();

        var scopedExpenses = canViewExpenses
            ? source.ExpenseItems
                .Where(e => e != null && !string.Equals(e.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
                .Select(e => new ReportExpenseItemSnapshot
                {
                    ExpenseReportId = e.ExpenseReportId,
                    ExpenseItemId = e.ExpenseItemId,
                    ReportNumber = e.ReportNumber,
                    EmployeeId = e.EmployeeId,
                    EmployeeName = e.EmployeeName,
                    Date = e.Date,
                    Description = e.Description,
                    IsKilometrage = e.IsKilometrage,
                    Kilometers = e.Kilometers,
                    KilometrageRate = e.KilometrageRate,
                    SubTotal = e.SubTotal,
                    TaxAmount = e.TaxAmount,
                    Total = e.Total,
                    Status = e.Status
                })
                .ToList()
            : [];

        var scopedPurchases = canViewPurchases
            ? source.PurchaseItems
                .Where(p => p != null)
                .Select(p => new ReportPurchaseItemSnapshot
                {
                    PoId = p.PoId,
                    PoItemId = p.PoItemId,
                    PoNumber = p.PoNumber,
                    Date = p.Date,
                    SupplierName = p.SupplierName,
                    PartNumber = p.PartNumber,
                    Description = p.Description,
                    Quantity = p.Quantity,
                    UnitPrice = p.UnitPrice,
                    NetTotal = p.NetTotal,
                    TaxAmount = p.TaxAmount
                })
                .ToList()
            : [];

        var scopedQuotes = source.Quotes
            .Where(q => q != null)
            .Select(q => new ReportQuoteSnapshot
            {
                QuoteId = q.QuoteId,
                QuoteNumber = q.QuoteNumber,
                Title = q.Title,
                Date = q.Date,
                Status = q.Status,
                QuotedHours = q.QuotedHours,
                EstimatedLaborCost = canViewInternalCosts ? q.EstimatedLaborCost : 0m,
                EstimatedMaterialCostNet = canViewInternalCosts ? q.EstimatedMaterialCostNet : 0m,
                EstimatedMaterialCostTax = canViewInternalCosts ? q.EstimatedMaterialCostTax : 0m,
                TotalLabor = q.TotalLabor,
                TotalItems = q.TotalItems,
                SubTotal = q.SubTotal,
                TaxAmount = q.TaxAmount,
                GrandTotal = q.GrandTotal,
                TotalClientBought = q.TotalClientBought
            })
            .ToList();

        var pmEmail = source.ProjectManagerEmail;
        if (!string.IsNullOrWhiteSpace(source.ProjectManagerId) &&
            empMap.TryGetValue(source.ProjectManagerId, out var pm) &&
            !pm.IsEmailPublic)
        {
            pmEmail = string.Empty;
        }

        return new ProjectReportSnapshot
        {
            CapturedAtUtc = source.CapturedAtUtc,
            PeriodStartDate = source.PeriodStartDate,
            AsOfDate = source.AsOfDate,
            ProjectName = source.ProjectName,
            ProjectNumber = source.ProjectNumber,
            ProjectDescription = source.ProjectDescription,
            ProjectStatus = source.ProjectStatus,
            BillingMethod = source.BillingMethod,
            IsInternalProject = source.IsInternalProject,
            ProjectManagerId = source.ProjectManagerId,
            ProjectManagerName = source.ProjectManagerName,
            ProjectManagerEmail = pmEmail,
            ProjectStartDate = source.ProjectStartDate,
            ProjectDeadline = source.ProjectDeadline,
            Client = CloneClientInfo(source.Client),

            ExpectedRevenueSubTotal = source.ExpectedRevenueSubTotal,
            ExpectedRevenueTax = source.ExpectedRevenueTax,
            ExpectedRevenue = source.ExpectedRevenue,
            TotalClientBoughtBudget = source.TotalClientBoughtBudget,

            TotalInvoicedSubTotal = source.TotalInvoicedSubTotal,
            TotalInvoicedTax = source.TotalInvoicedTax,
            TotalInvoiced = source.TotalInvoiced,
            TotalPaid = source.TotalPaid,
            UnbilledLaborValue = canViewLaborFinancials ? source.UnbilledLaborValue : 0m,
            UnbilledExpensesValue = canViewExpenses ? source.UnbilledExpensesValue : 0m,

            TotalQuotedHours = source.TotalQuotedHours,
            QuotedLaborCost = canViewInternalCosts ? source.QuotedLaborCost : 0m,
            QuotedMaterialCostNet = canViewInternalCosts ? source.QuotedMaterialCostNet : 0m,
            QuotedMaterialCostTax = canViewInternalCosts ? source.QuotedMaterialCostTax : 0m,

            TotalLaborHours = source.TotalLaborHours,
            CumulativeProjectHoursToAsOfDate = source.CumulativeProjectHoursToAsOfDate,
            TotalLaborInternalCost = canViewInternalCosts ? source.TotalLaborInternalCost : 0m,
            TotalLaborBillableValue = canViewLaborFinancials ? source.TotalLaborBillableValue : 0m,

            TotalPurchaseNet = canViewPurchases ? source.TotalPurchaseNet : 0m,
            TotalPurchaseTax = canViewPurchases ? source.TotalPurchaseTax : 0m,

            TotalExpenseSubTotal = canViewExpenses ? scopedExpenses.Sum(e => e.SubTotal) : 0m,
            TotalExpenseTax = canViewExpenses ? scopedExpenses.Sum(e => e.TaxAmount) : 0m,

            TotalCommissions = canViewInternalCosts ? source.TotalCommissions : 0m,

            TotalCompanyDirectLaborHours = canViewInternalCosts ? source.TotalCompanyDirectLaborHours : 0m,
            OverheadAllocationSharePercent = canViewInternalCosts ? source.OverheadAllocationSharePercent : 0m,
            CompanyOverheadPoolPreTax = canViewInternalCosts ? source.CompanyOverheadPoolPreTax : 0m,
            CompanyOverheadPoolTax = canViewInternalCosts ? source.CompanyOverheadPoolTax : 0m,
            CompanyFixedOverheadPoolPreTax = canViewInternalCosts ? source.CompanyFixedOverheadPoolPreTax : 0m,
            CompanyFixedOverheadPoolTax = canViewInternalCosts ? source.CompanyFixedOverheadPoolTax : 0m,
            AllocatedFixedOverheadPreTax = canViewInternalCosts ? source.AllocatedFixedOverheadPreTax : 0m,
            AllocatedFixedOverheadTax = canViewInternalCosts ? source.AllocatedFixedOverheadTax : 0m,
            CompanyInternalCostsPoolPreTax = canViewInternalCosts ? source.CompanyInternalCostsPoolPreTax : 0m,
            CompanyInternalCostsPoolTax = canViewInternalCosts ? source.CompanyInternalCostsPoolTax : 0m,
            AllocatedInternalCostsPreTax = canViewInternalCosts ? source.AllocatedInternalCostsPreTax : 0m,
            AllocatedInternalCostsTax = canViewInternalCosts ? source.AllocatedInternalCostsTax : 0m,
            AllocatedOverheadPreTax = canViewInternalCosts ? source.AllocatedOverheadPreTax : 0m,
            AllocatedOverheadTax = canViewInternalCosts ? source.AllocatedOverheadTax : 0m,

            OverheadRows = canViewInternalCosts ? source.OverheadRows.ToList() : [],
            TaskLaborRows = scopedTaskLaborRows,
            EmployeeLaborRows = scopedEmployeeLaborRows,
            TimeEntries = scopedTimeEntries,
            PurchaseItems = scopedPurchases,
            ExpenseItems = scopedExpenses,
            Quotes = scopedQuotes,
            Invoices = source.Invoices.ToList(),
            ClientPos = source.ClientPos.ToList(),
            Commissions = canViewInternalCosts ? source.Commissions.ToList() : [],
            TimelineNotes = source.TimelineNotes.ToList()
        };
    }

    public bool CanViewerAccessUnredactedReport(
        ProjectReport? report,
        Employee? viewer,
        IUserPrivacyService privacyService,
        IEnumerable<Employee> allEmployees) =>
        CanViewerAccessUnredactedReportStatic(report, viewer, privacyService, allEmployees);

    public static bool CanViewerAccessUnredactedReportStatic(
        ProjectReport? report,
        Employee? viewer,
        IUserPrivacyService privacyService,
        IEnumerable<Employee> allEmployees)
    {
        ArgumentNullException.ThrowIfNull(privacyService);
        ArgumentNullException.ThrowIfNull(allEmployees);

        if (report == null || viewer is not { IsActive: true, IsBanned: false, IsSystem: false })
        {
            return false;
        }

        var canViewAccounting = viewer.IsEffectiveAdmin || viewer.HasPermission(AppPermissions.Finance.AccessAccounting);
        var canViewCostRates = canViewAccounting ||
                               viewer.HasPermission(AppPermissions.Admin.ManageUsers) ||
                               viewer.HasPermission(AppPermissions.Hr.ManageSalaries);
        var canViewPurchases = canViewAccounting ||
                               (viewer.IsAdmin && viewer.IsActive && !viewer.IsBanned) ||
                               viewer.HasEffectivePermission(AppPermissions.Purchases.ViewPOs, allowSuspended: true) ||
                               viewer.HasEffectivePermission(AppPermissions.Purchases.ManagePOs, allowSuspended: true);
        var canViewLaborRates = canViewCostRates || viewer.HasPermission(AppPermissions.Projects.ApproveQuotes);
        var canViewAllLabor = viewer.HasEffectivePermission(AppPermissions.Timesheets.ViewReport, allowSuspended: true);

        var options = report.Options ?? new ProjectReportOptions();
        var showFinancialAmounts = !string.Equals(options.FinancialMode, ProjectReportFinancialMode.HoursAndQuantitiesOnly, StringComparison.OrdinalIgnoreCase);
        var isInternalCostMode = string.Equals(options.FinancialMode, ProjectReportFinancialMode.InternalCostAndProfit, StringComparison.OrdinalIgnoreCase);

        if (isInternalCostMode &&
            (options.ShowFinancialSummary || options.ShowCashFlowPosition || options.ShowOverheadSection || options.ShowCommissionsSection || options.ShowLaborFinancials) &&
            !canViewCostRates)
        {
            return false;
        }

        if (options.ShowOverheadSection && !canViewCostRates)
        {
            return false;
        }

        if (options.ShowCommissionsSection && isInternalCostMode && !canViewCostRates)
        {
            return false;
        }

        if (options.ShowPurchasesSection && !canViewPurchases)
        {
            return false;
        }

        if (options.ShowExpensesSection && !canViewAccounting)
        {
            return false;
        }

        if (options.ShowLaborSection && showFinancialAmounts && options.ShowLaborFinancials && !canViewLaborRates)
        {
            return false;
        }

        if (options.ShowLaborSection && report.Snapshot != null)
        {
            var contributorIds = report.Snapshot.EmployeeLaborRows
                .Select(r => r.EmployeeId)
                .Concat(report.Snapshot.TimeEntries.Select(e => e.EmployeeId))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var empMap = allEmployees
                .Where(e => e != null && !string.IsNullOrEmpty(e.Id))
                .ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var contributorId in contributorIds)
            {
                if (string.Equals(contributorId, viewer.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!canViewAllLabor)
                {
                    return false;
                }

                if (!empMap.TryGetValue(contributorId, out var empObj))
                {
                    if (!viewer.IsAdmin)
                    {
                        return false;
                    }
                    continue;
                }

                if (!privacyService.CanDiscoverUser(viewer, empObj))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static ClientInfo CloneClientInfo(ClientInfo? source)
    {
        if (source == null) return new ClientInfo();
        return new ClientInfo
        {
            BusinessName = source.BusinessName,
            ContactPersonPrefix = source.ContactPersonPrefix,
            ContactPersonName = source.ContactPersonName,
            ContactPersonTitle = source.ContactPersonTitle,
            ContactPersonEmail = source.ContactPersonEmail,
            ContactPersonPhone = source.ContactPersonPhone,
            BusinessAddressNumber = source.BusinessAddressNumber,
            BusinessAddressStreet = source.BusinessAddressStreet,
            BusinessAddressCity = source.BusinessAddressCity,
            BusinessAddressState = source.BusinessAddressState,
            BusinessAddressCountry = source.BusinessAddressCountry,
            BusinessAddressZip = source.BusinessAddressZip,
            TaxId = source.TaxId,
            DefaultGlAccountCode = source.DefaultGlAccountCode
        };
    }
}
