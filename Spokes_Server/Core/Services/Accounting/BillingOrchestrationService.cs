using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Reports.Helpers;

namespace Spokes_Server.Core.Services.Accounting;

public class AvailablePrepaymentCredit
{
    public string PrepaymentLineId { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal OriginalAmount { get; set; }
    public decimal RemainingBalance { get; set; }
    public decimal AmountToApply { get; set; }
}

public class BillingLaborGroup
{
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public string? SubTaskId { get; set; }
    public string TaskName { get; set; } = "";
    public string SubTaskName { get; set; } = "";
    public decimal Rate { get; set; }
    public decimal AvailableHours { get; set; }
    public decimal HoursToBill { get; set; }
    public List<BillingLaborEntryRef> Entries { get; set; } = new();
}

public class BillingLaborEntryRef
{
    public TimeEntry Entry { get; set; } = default!;
    public Timesheet ParentSheet { get; set; } = default!;
}

public class BillingExpenseItem
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public DateTime Date { get; set; }
    public string Description { get; set; } = "";
    public decimal Cost { get; set; }
    public decimal Total { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitCost { get; set; }
    public decimal MarkupRate { get; set; }
    public string Source { get; set; } = "";

    public override bool Equals(object? obj) => obj is BillingExpenseItem other && Id == other.Id;
    public override int GetHashCode() => Id.GetHashCode();
}

public class BillingMilestoneSelection
{
    public PaymentTerm Term { get; set; } = default!;
    public Quote Quote { get; set; } = default!;
    public string InvoiceDescription { get; set; } = string.Empty;
    public decimal PreTaxAmount => Quote.ShowTax ? Term.Amount / (1 + Quote.TaxRate) : Term.Amount;

    public override bool Equals(object? obj) => obj is BillingMilestoneSelection other && Term.Id == other.Term.Id;
    public override int GetHashCode() => Term.Id.GetHashCode();
}

public class BillingMilestonesAndQuotesResult
{
    public List<Quote> AcceptedQuotes { get; set; } = new();
    public List<BillingMilestoneSelection> AvailableTerms { get; set; } = new();
    public Dictionary<string, HashSet<QuoteLaborItem>> LaborSelections { get; set; } = new();
    public Dictionary<string, HashSet<QuoteItemRow>> ItemSelections { get; set; } = new();
}

public class BillingPrePayItem
{
    public bool IsTaskBased { get; set; }
    public string Description { get; set; } = "";
    public string WorkTypeId { get; set; } = "";
    public decimal Hours { get; set; }
    public decimal Rate { get; set; }
    public decimal ManualAmount { get; set; }
    public decimal Total => IsTaskBased ? (Hours * Rate) : ManualAmount;
}

public class BillingSubmissionRequest
{
    public Employee? Actor { get; set; }
    public Invoice Invoice { get; set; } = default!;
    public DocumentTemplate? Template { get; set; }
    public decimal DefaultMarkupRate { get; set; } = 0.15m;
    public List<BillingLaborGroup> LaborGroups { get; set; } = new();
    public List<BillingExpenseItem> SelectedExpenses { get; set; } = new();
    public List<Quote> AcceptedQuotes { get; set; } = new();
    public List<BillingMilestoneSelection> SelectedMilestones { get; set; } = new();
    public List<QuoteLaborItem> SelectedQuoteLabor { get; set; } = new();
    public List<QuoteItemRow> SelectedQuoteItems { get; set; } = new();
    public List<BillingPrePayItem> PrePayItems { get; set; } = new();
    public List<InvoiceLine> CustomLaborLines { get; set; } = new();
    public List<InvoiceLine> CustomMaterialLines { get; set; } = new();
    public List<InvoiceLine> GeneralLines { get; set; } = new();
    public List<AvailablePrepaymentCredit> AppliedCredits { get; set; } = new();
}

public class BillingOrchestrationService
{
    private readonly AccountingContext _accounting;
    private readonly ProjectsContext _projects;
    private readonly HrContext _hr;
    private readonly CoreContext _core;

    [ActivatorUtilitiesConstructor]
    public BillingOrchestrationService(
        AccountingContext accounting,
        ProjectsContext projects,
        HrContext hr,
        CoreContext core)
    {
        _accounting = accounting;
        _projects = projects;
        _hr = hr;
        _core = core;
    }

    private static bool CanActorReadBillingData(Employee actor) =>
        actor is { IsActive: true, IsBanned: false, IsSystem: false } &&
        (actor.HasEffectivePermission(AppPermissions.Finance.AccessAccounting, allowSuspended: true) ||
         actor.HasEffectivePermission(AppPermissions.Finance.ApproveInvoices, allowSuspended: true));

    private List<Project> FilterReadableProjects(IEnumerable<Project> projects, Employee? actor)
    {
        var nonNullProjects = projects?.Where(p => p != null).ToList() ?? [];
        if (actor == null) return nonNullProjects;
        if (!CanActorReadBillingData(actor)) return [];

        return nonNullProjects
            .Where(p => ProjectAccessHelper.CanAccess(actor, p, _hr.Teams, allowAccountingFinancialRead: true))
            .ToList();
    }

    private void EnsureCanAccessInvoiceTarget(Employee actor, Invoice invoice)
    {
        if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
        {
            var group = _projects.ProjectGroups.GetById(invoice.ProjectGroupId);
            if (group == null || group.ProjectIds == null || group.ProjectIds.Count == 0)
            {
                throw new UnauthorizedAccessException("Referenced project group could not be resolved.");
            }

            foreach (var pid in group.ProjectIds)
            {
                var gp = _projects.Projects.GetById(pid);
                if (gp == null || !ProjectAccessHelper.CanAccess(actor, gp, _hr.Teams, allowAccountingFinancialRead: true))
                {
                    throw new UnauthorizedAccessException("You do not have access to all projects in the referenced project group.");
                }
            }
        }

        if (!string.IsNullOrEmpty(invoice.ProjectId))
        {
            var proj = _projects.Projects.GetById(invoice.ProjectId);
            if (proj == null || !ProjectAccessHelper.CanAccess(actor, proj, _hr.Teams, allowAccountingFinancialRead: true))
            {
                throw new UnauthorizedAccessException("You do not have access to the referenced project.");
            }
        }
    }

    public List<AvailablePrepaymentCredit> CalculateAvailablePrepaymentCredits(IEnumerable<Project> projects, Invoice invoice, Employee? actor = null)
    {
        if (actor != null && !CanActorReadBillingData(actor))
        {
            return [];
        }

        var authorizedProjects = FilterReadableProjects(projects, actor);
        var result = new List<AvailablePrepaymentCredit>();

        var groupInvoices = new List<Invoice>();
        if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
        {
            bool canReadGroup = true;
            if (actor != null)
            {
                var group = _projects.ProjectGroups.GetById(invoice.ProjectGroupId);
                canReadGroup = group != null && group.ProjectIds.Count > 0 && group.ProjectIds.All(pid =>
                {
                    var gp = _projects.Projects.GetById(pid);
                    return gp != null && ProjectAccessHelper.CanAccess(actor, gp, _hr.Teams, allowAccountingFinancialRead: true);
                });
            }

            if (canReadGroup)
            {
                groupInvoices = _accounting.Invoices.GetByProjectGroup(invoice.ProjectGroupId);
            }
        }

        var allInvoices = new List<Invoice>();
        foreach (var project in authorizedProjects)
        {
            allInvoices.AddRange(_accounting.Invoices.GetByProject(project.Id));
        }
        allInvoices.AddRange(groupInvoices);
        allInvoices = allInvoices.DistinctBy(i => i.Id).ToList();

        // Find all paid prepayments (excluding the current invoice itself)
        var paidPrepayments = allInvoices
            .Where(i => i.Status == InvoiceStatus.Paid && i.Id != invoice.Id)
            .SelectMany(i => i.LineItems.Where(l => l.IsPrepayment).Select(l => new { Invoice = i, Line = l }))
            .ToList();

        // Find all used credits from OTHER posted invoices (Draft and Void invoices never consume credits)
        var usedCredits = allInvoices
            .Where(i => i.Id != invoice.Id && InvoiceStatus.IsPosted(i.Status))
            .SelectMany(i => i.LineItems.Where(l => l.IsCreditUsed))
            .ToList();

        // Find unlinked credits (migration logic)
        decimal unlinkedCreditUsed = usedCredits
            .Where(l => string.IsNullOrEmpty(l.SourceId))
            .Sum(l => Math.Abs(l.Total));

        // Deduct linked credits and then unlinked credits from oldest to newest
        var sortedPrepayments = paidPrepayments.OrderBy(p => p.Invoice.Date).ToList();

        foreach (var prepay in sortedPrepayments)
        {
            decimal original = prepay.Line.Total;

            // Subtract linked used credits from other posted invoices
            decimal linkedUsed = usedCredits
                .Where(l => l.SourceId == prepay.Line.Id)
                .Sum(l => Math.Abs(l.Total));

            decimal remaining = original - linkedUsed;

            // Subtract unlinked used credits
            if (unlinkedCreditUsed > 0 && remaining > 0)
            {
                decimal amountToDeduct = Math.Min(remaining, unlinkedCreditUsed);
                remaining -= amountToDeduct;
                unlinkedCreditUsed -= amountToDeduct;
            }

            remaining = Math.Max(0m, remaining);

            // Check if THIS current invoice already staged/applied some of this specific credit
            decimal currentlyApplied = invoice.LineItems
                .Where(l => l.IsCreditUsed && l.SourceId == prepay.Line.Id)
                .Sum(l => Math.Abs(l.Total));

            if (remaining > 0 || currentlyApplied > 0)
            {
                result.Add(new AvailablePrepaymentCredit
                {
                    PrepaymentLineId = prepay.Line.Id,
                    InvoiceNumber = prepay.Invoice.InvoiceNumber,
                    Description = prepay.Line.Description,
                    OriginalAmount = original,
                    RemainingBalance = remaining,
                    AmountToApply = Math.Min(currentlyApplied, remaining)
                });
            }
        }

        return result;
    }

    public List<BillingLaborGroup> LoadUnbilledLabor(IEnumerable<Project> projects, Invoice invoice, IReadOnlyDictionary<string, WorkType> workTypes, Employee? actor = null)
    {
        var authorizedProjects = FilterReadableProjects(projects, actor);
        if (authorizedProjects.Count == 0) return [];

        var projectIds = authorizedProjects.Select(p => p.Id).ToHashSet();
        var billedTimeIds = GetBilledIds(projectIds, invoice, i => i.BilledTimeEntryIds);
        var currentInvoiceTimeIds = invoice.BilledTimeEntryIds.ToHashSet();

        var allSheets = _hr.Timesheets.GetAll();
        var rawGroups = new Dictionary<string, BillingLaborGroup>();

        foreach (var sheet in allSheets)
        {
            var candidates = sheet.Entries.Where(e => projectIds.Contains(e.ProjectId) &&
                (!billedTimeIds.Contains(e.Id) || currentInvoiceTimeIds.Contains(e.Id)));

            foreach (var entry in candidates)
            {
                var project = _projects.Projects.GetById(entry.ProjectId);
                if (project == null) continue;

                var key = $"{entry.ProjectId}|{entry.WorkTypeId}|{entry.SubTaskId}";

                if (!rawGroups.ContainsKey(key))
                {
                    var task = workTypes.TryGetValue(entry.WorkTypeId, out var wt) ? wt : new WorkType { Name = "Unknown" };
                    var subTaskName = task.SubTasks.FirstOrDefault(s => s.Id == entry.SubTaskId)?.Name ?? "General";
                    var card = project.RateCardId != null ? _projects.RateCards.GetById(project.RateCardId) : null;
                    var rate = project.GetEffectiveRate(entry.WorkTypeId, task.DefaultRate, card);

                    rawGroups[key] = new BillingLaborGroup
                    {
                        ProjectId = entry.ProjectId,
                        ProjectName = project.DisplayName,
                        WorkTypeId = entry.WorkTypeId,
                        SubTaskId = entry.SubTaskId,
                        TaskName = task.Name,
                        SubTaskName = subTaskName,
                        Rate = rate,
                        AvailableHours = 0,
                        HoursToBill = 0
                    };
                }
                rawGroups[key].AvailableHours += entry.Hours;
                if (currentInvoiceTimeIds.Contains(entry.Id))
                {
                    rawGroups[key].HoursToBill += entry.Hours;
                }
                rawGroups[key].Entries.Add(new BillingLaborEntryRef { Entry = entry, ParentSheet = sheet });
            }
        }

        var result = new List<BillingLaborGroup>();
        var timeLines = invoice.LineItems
            .Where(l => string.Equals(l.SourceType, "Time", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var g in rawGroups.Values)
        {
            g.Entries = g.Entries.OrderBy(x => x.Entry.Date).ToList();

            // Prefer staged/saved line quantity when matching by ProjectId (or single-project invoice),
            // so partial-hour drafts restore their exact HoursToBill without splitting TimeEntries on disk.
            var exactProjectLineHours = timeLines
                .Where(l => !string.IsNullOrEmpty(l.ProjectId) &&
                            string.Equals(l.ProjectId, g.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(l.WorkTypeId ?? "", g.WorkTypeId ?? "", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(l.SubTaskId ?? "", g.SubTaskId ?? "", StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.Quantity);

            if (exactProjectLineHours > 0m)
            {
                g.HoursToBill = Math.Min(g.AvailableHours, exactProjectLineHours);
            }
            else if (projectIds.Count == 1)
            {
                var legacyLineHours = timeLines
                    .Where(l => string.IsNullOrEmpty(l.ProjectId) &&
                                string.Equals(l.WorkTypeId ?? "", g.WorkTypeId ?? "", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(l.SubTaskId ?? "", g.SubTaskId ?? "", StringComparison.OrdinalIgnoreCase))
                    .Sum(l => l.Quantity);
                if (legacyLineHours > 0m)
                {
                    g.HoursToBill = Math.Min(g.AvailableHours, legacyLineHours);
                }
            }

            result.Add(g);
        }
        return result;
    }

    public List<BillingExpenseItem> LoadUnbilledExpenses(IEnumerable<Project> projects, Invoice invoice, decimal defaultMarkupRate, Employee? actor = null)
    {
        var authorizedProjects = FilterReadableProjects(projects, actor);
        if (authorizedProjects.Count == 0) return [];

        var projectIds = authorizedProjects.Select(p => p.Id).ToHashSet();
        var billedPoIds = GetBilledIds(projectIds, invoice, i => i.BilledPoItemIds);
        var billedExpenseIds = GetBilledIds(projectIds, invoice, i => i.BilledExpenseItemIds);
        var currentInvoicePoIds = invoice.BilledPoItemIds.ToHashSet();
        var currentInvoiceExpenseIds = invoice.BilledExpenseItemIds.ToHashSet();

        var result = new List<BillingExpenseItem>();

        var allPos = _accounting.PurchaseOrders.GetAll();
        foreach (var po in allPos.Where(p => !string.Equals(p.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase)))
        {
            var myItems = po.LineItems.Where(i => projectIds.Contains(i.ProjectId) &&
                (!billedPoIds.Contains(i.Id) || currentInvoicePoIds.Contains(i.Id)));

            foreach (var item in myItems)
            {
                var project = _projects.Projects.GetById(item.ProjectId);
                var card = project?.RateCardId != null ? _projects.RateCards.GetById(project.RateCardId) : null;
                var markupRate = card?.MarkupRate ?? defaultMarkupRate;
                var markupMult = 1 + markupRate;

                result.Add(new BillingExpenseItem
                {
                    Id = item.Id,
                    ProjectId = item.ProjectId,
                    ProjectName = project?.DisplayName ?? "Unknown",
                    Date = po.Date,
                    Description = item.Description,
                    Cost = item.Total,
                    Total = item.Total * markupMult,
                    Quantity = item.Quantity,
                    UnitCost = item.UnitPrice,
                    MarkupRate = markupRate,
                    Source = "PO"
                });
            }
        }

        var allReports = _accounting.ExpenseReports.GetAll();
        foreach (var report in allReports.Where(r => r.DateSubmitted.HasValue && !string.Equals(r.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase)))
        {
            var myItems = report.LineItems.Where(i => projectIds.Contains(i.ProjectId) &&
                (!billedExpenseIds.Contains(i.Id) || currentInvoiceExpenseIds.Contains(i.Id)));

            foreach (var item in myItems)
            {
                var project = _projects.Projects.GetById(item.ProjectId);
                var card = project?.RateCardId != null ? _projects.RateCards.GetById(project.RateCardId) : null;
                var markupRate = card?.MarkupRate ?? defaultMarkupRate;
                var markupMult = 1 + markupRate;

                result.Add(new BillingExpenseItem
                {
                    Id = item.Id,
                    ProjectId = item.ProjectId,
                    ProjectName = project?.DisplayName ?? "Unknown",
                    Date = item.Date,
                    Description = $"{report.EmployeeName}: {item.Description}",
                    Cost = item.Total,
                    Total = item.Total * markupMult,
                    Quantity = item.IsKilometrage ? item.Kilometers : 1m,
                    UnitCost = item.IsKilometrage ? item.KilometrageRate : item.Total,
                    MarkupRate = markupRate,
                    Source = "ExpenseReport"
                });
            }
        }

        return result;
    }

    public BillingMilestonesAndQuotesResult LoadMilestonesAndQuotes(IEnumerable<Project> projects, Invoice invoice, Employee? actor = null)
    {
        var authorizedProjects = FilterReadableProjects(projects, actor);
        if (authorizedProjects.Count == 0) return new BillingMilestonesAndQuotesResult();

        var projectIds = authorizedProjects.Select(p => p.Id).ToHashSet();
        var acceptedQuotes = new List<Quote>();
        var availableTerms = new List<BillingMilestoneSelection>();
        var laborSelections = new Dictionary<string, HashSet<QuoteLaborItem>>();
        var itemSelections = new Dictionary<string, HashSet<QuoteItemRow>>();

        foreach (var projectId in projectIds)
        {
            var quotes = _projects.Quotes.GetByProject(projectId).Where(q => q.Status == QuoteStatus.Accepted).ToList();
            acceptedQuotes.AddRange(quotes);

            foreach (var quote in quotes)
            {
                foreach (var term in quote.PaymentTerms)
                {
                    if (!IsMilestoneClaimedByOtherPostedInvoice(term, invoice.Id))
                    {
                        var existingLine = invoice.LineItems.FirstOrDefault(l => l.SourceType == "Milestone" && l.SourceId == term.Id);
                        var vm = new BillingMilestoneSelection
                        {
                            Term = term,
                            Quote = quote,
                            InvoiceDescription = existingLine?.Description ?? $"{term.Description} ({term.Percentage}% - {quote.QuoteNumber})"
                        };
                        availableTerms.Add(vm);
                    }
                }

                laborSelections[quote.Id] = new HashSet<QuoteLaborItem>();
                itemSelections[quote.Id] = new HashSet<QuoteItemRow>();

                var quoteLaborLineIds = invoice.LineItems.Where(l => l.SourceType == "QuoteLabor").Select(l => l.SourceId).ToHashSet();
                var quoteItemLineIds = invoice.LineItems.Where(l => l.SourceType == "QuoteItem").Select(l => l.SourceId).ToHashSet();

                foreach (var l in quote.LaborItems)
                {
                    if (quoteLaborLineIds.Contains(l.Id)) laborSelections[quote.Id].Add(l);
                }

                foreach (var i in quote.MaterialLineItems)
                {
                    if (quoteItemLineIds.Contains(i.Id)) itemSelections[quote.Id].Add(i);
                }
            }
        }

        return new BillingMilestonesAndQuotesResult
        {
            AcceptedQuotes = acceptedQuotes,
            AvailableTerms = availableTerms,
            LaborSelections = laborSelections,
            ItemSelections = itemSelections
        };
    }

    public void ProcessAndSaveInvoice(BillingSubmissionRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));

        var actor = request.Actor;
        if (actor is not { IsSystem: false, IsActiveUnsuspended: true } ||
            (!actor.HasEffectivePermission(AppPermissions.Finance.AccessAccounting) &&
             !actor.HasEffectivePermission(AppPermissions.Finance.ApproveInvoices)))
        {
            throw new UnauthorizedAccessException("You do not have permission to create or modify invoices.");
        }

        var invoice = request.Invoice ?? throw new ArgumentNullException(nameof(request.Invoice));

        var existingInvoice = !string.IsNullOrWhiteSpace(invoice.Id)
            ? _accounting.Invoices.GetById(invoice.Id)
            : null;
        if (existingInvoice != null)
        {
            EnsureCanAccessInvoiceTarget(actor, existingInvoice);
        }

        bool isDraftTarget = string.Equals(invoice.Status, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase);
        bool isDraftExisting = existingInvoice == null || string.Equals(existingInvoice.Status, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase);
        if ((!isDraftTarget || !isDraftExisting) &&
            !actor.HasEffectivePermission(AppPermissions.Finance.ApproveInvoices))
        {
            throw new UnauthorizedAccessException("Invoice approval permission is required to finalize or modify non-draft invoices.");
        }

        EnsureCanAccessInvoiceTarget(actor, invoice);

        var invoiceProjects = new List<Project>();
        if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
        {
            var group = _projects.ProjectGroups.GetById(invoice.ProjectGroupId);
            if (group != null)
            {
                foreach (var pid in group.ProjectIds)
                {
                    var p = _projects.Projects.GetById(pid);
                    if (p != null && !invoiceProjects.Any(x => x.Id == p.Id))
                    {
                        invoiceProjects.Add(p);
                    }
                }
            }
        }
        if (!string.IsNullOrEmpty(invoice.ProjectId))
        {
            var p = _projects.Projects.GetById(invoice.ProjectId);
            if (p != null && !invoiceProjects.Any(x => x.Id == p.Id))
            {
                invoiceProjects.Add(p);
            }
        }

        if (invoiceProjects.Count == 0)
        {
            throw new UnauthorizedAccessException("Invoice must reference at least one accessible project.");
        }

        var invoiceProjectIds = invoiceProjects.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var billedElsewhereTimeIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledTimeEntryIds);
        var billedElsewherePoIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledPoItemIds);
        var billedElsewhereExpenseIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledExpenseItemIds);

        // Pre-validate all submitted entities belong to the target invoice's projects and are eligible for billing
        var sheetsToSave = new Dictionary<string, Timesheet>(StringComparer.OrdinalIgnoreCase);
        var resolvedLaborGroups = new List<(BillingLaborGroup Group, List<(Timesheet Sheet, TimeEntry Entry)> Entries)>();
        foreach (var group in request.LaborGroups.Where(g => g.HoursToBill > 0))
        {
            var resolvedEntries = new List<(Timesheet Sheet, TimeEntry Entry)>();
            foreach (var refObj in group.Entries)
            {
                var sheetId = refObj.ParentSheet?.Id;
                var entryId = refObj.Entry?.Id;
                if (string.IsNullOrWhiteSpace(sheetId) || string.IsNullOrWhiteSpace(entryId))
                {
                    throw new UnauthorizedAccessException("Invalid timesheet reference in billing submission.");
                }

                if (!sheetsToSave.TryGetValue(sheetId, out var liveSheet))
                {
                    liveSheet = _hr.Timesheets.GetById(sheetId)
                        ?? throw new UnauthorizedAccessException("Referenced timesheet could not be found.");
                    sheetsToSave[sheetId] = liveSheet;
                }

                var liveEntry = liveSheet.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal))
                    ?? throw new UnauthorizedAccessException("Referenced time entry could not be found.");
                if (!invoiceProjectIds.Contains(liveEntry.ProjectId))
                {
                    throw new UnauthorizedAccessException("Cannot bill time entries from projects outside this invoice.");
                }
                if (billedElsewhereTimeIds.Contains(liveEntry.Id))
                {
                    throw new InvalidOperationException("One or more selected time entries are already billed on another invoice.");
                }

                resolvedEntries.Add((liveSheet, liveEntry));
            }
            resolvedLaborGroups.Add((group, resolvedEntries));
        }

        var eligiblePoItems = _accounting.PurchaseOrders.GetAll()
            .Where(po => !string.Equals(po.Status, PurchaseOrderStatus.Void, StringComparison.OrdinalIgnoreCase))
            .SelectMany(po => po.LineItems)
            .Where(li => invoiceProjectIds.Contains(li.ProjectId))
            .ToDictionary(li => li.Id, StringComparer.OrdinalIgnoreCase);

        var eligibleExpenseItems = _accounting.ExpenseReports.GetAll()
            .Where(r => r.DateSubmitted.HasValue && !string.Equals(r.Status, ExpenseReportStatus.Draft, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.LineItems)
            .Where(li => invoiceProjectIds.Contains(li.ProjectId))
            .ToDictionary(li => li.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var item in request.SelectedExpenses)
        {
            if (!invoiceProjectIds.Contains(item.ProjectId))
            {
                throw new UnauthorizedAccessException("Cannot bill expenses from projects outside this invoice.");
            }

            if (string.Equals(item.Source, "PO", StringComparison.OrdinalIgnoreCase))
            {
                if (!eligiblePoItems.TryGetValue(item.Id, out var livePoItem) ||
                    !string.Equals(livePoItem.ProjectId, item.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Referenced purchase order item is not eligible for billing on this invoice.");
                }
                if (billedElsewherePoIds.Contains(item.Id))
                {
                    throw new InvalidOperationException("One or more selected purchase order items are already billed on another invoice.");
                }
            }
            else
            {
                if (!eligibleExpenseItems.TryGetValue(item.Id, out var liveExpItem) ||
                    !string.Equals(liveExpItem.ProjectId, item.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("Referenced expense report item is not eligible for billing on this invoice.");
                }
                if (billedElsewhereExpenseIds.Contains(item.Id))
                {
                    throw new InvalidOperationException("One or more selected expense items are already billed on another invoice.");
                }
            }
        }

        var liveQuotesById = invoiceProjectIds
            .SelectMany(pid => _projects.Quotes.GetByProject(pid))
            .Where(q => q.Status == QuoteStatus.Accepted)
            .ToDictionary(q => q.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var quote in request.AcceptedQuotes)
        {
            if (quote == null || !liveQuotesById.ContainsKey(quote.Id))
            {
                throw new UnauthorizedAccessException("Referenced quote is not an accepted quote for this invoice's projects.");
            }
        }

        var availableMilestoneTermIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var milestone in request.SelectedMilestones)
        {
            if (milestone?.Quote == null || milestone.Term == null ||
                !liveQuotesById.TryGetValue(milestone.Quote.Id, out var liveQuote))
            {
                throw new UnauthorizedAccessException("Referenced milestone quote is outside this invoice's projects.");
            }

            var liveTerm = liveQuote.PaymentTerms.FirstOrDefault(t => string.Equals(t.Id, milestone.Term.Id, StringComparison.Ordinal));
            if (liveTerm == null || IsMilestoneClaimedByOtherPostedInvoice(liveTerm, invoice.Id))
            {
                throw new InvalidOperationException("Referenced milestone payment term is invalid or already invoiced.");
            }

            availableMilestoneTermIds.Add(liveTerm.Id);
        }

        var availableQuoteLaborIds = liveQuotesById.Values
            .SelectMany(q => q.LaborItems)
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var l in request.SelectedQuoteLabor)
        {
            if (l == null || !availableQuoteLaborIds.Contains(l.Id))
            {
                throw new UnauthorizedAccessException("Referenced quote labor item does not belong to an accepted quote for this invoice.");
            }
        }

        var availableQuoteItemIds = liveQuotesById.Values
            .SelectMany(q => q.MaterialLineItems)
            .Select(i => i.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in request.SelectedQuoteItems)
        {
            if (i == null || !availableQuoteItemIds.Contains(i.Id))
            {
                throw new UnauthorizedAccessException("Referenced quote material item does not belong to an accepted quote for this invoice.");
            }
        }

        var availableCreditsById = CalculateAvailablePrepaymentCredits(invoiceProjects, invoice, actor)
            .ToDictionary(c => c.PrepaymentLineId, StringComparer.OrdinalIgnoreCase);

        // Clear all Billed ID lists for this invoice so we can rebuild them
        invoice.BilledTimeEntryIds.Clear();
        invoice.BilledPoItemIds.Clear();
        invoice.BilledExpenseItemIds.Clear();
        invoice.LineItems.Clear();

        var template = request.Template
            ?? (!string.IsNullOrEmpty(invoice.TemplateId) ? _core.DocumentTemplates.GetById(invoice.TemplateId) : null)
            ?? _core.DocumentTemplates.GetDefault();

        bool isPostedTarget = InvoiceStatus.IsPosted(invoice.Status);

        // 1. Labor Groups (Unbilled)
        foreach (var (group, resolvedEntries) in resolvedLaborGroups)
        {
            decimal remainingToBill = group.HoursToBill;
            string taskLabel = TimesheetSearchHelper.FormatTaskAndSubTask(group.TaskName, group.SubTaskName, ": ");
            string lineDescription = $"{group.ProjectName} - {taskLabel} {template?.Label_ItemLaborTag ?? "(Labor)"}";

            invoice.LineItems.Add(new InvoiceLine
            {
                Description = lineDescription,
                ProjectId = group.ProjectId,
                WorkTypeId = group.WorkTypeId,
                SubTaskId = group.SubTaskId,
                Quantity = group.HoursToBill,
                UnitPrice = group.Rate,
                Type = "Labor",
                SourceType = "Time"
            });

            foreach (var (sheet, entry) in resolvedEntries)
            {
                if (remainingToBill <= 0) break;

                if (entry.Hours <= remainingToBill)
                {
                    invoice.BilledTimeEntryIds.Add(entry.Id);
                    remainingToBill -= entry.Hours;
                }
                else if (isPostedTarget)
                {
                    var billedEntry = new TimeEntry
                    {
                        ProjectId = entry.ProjectId,
                        WorkTypeId = entry.WorkTypeId,
                        SubTaskId = entry.SubTaskId,
                        Date = entry.Date,
                        Description = entry.Description + " (Billed)",
                        Hours = remainingToBill,
                        Id = Guid.NewGuid().ToString()
                    };
                    entry.Hours -= remainingToBill;
                    sheet.Entries.Add(billedEntry);
                    invoice.BilledTimeEntryIds.Add(billedEntry.Id);
                    _hr.Timesheets.Save(sheet);
                    remainingToBill = 0;
                }
                else
                {
                    // Drafts never mutate Timesheets on disk; record the candidate entry ID and defer any partial split until finalization.
                    invoice.BilledTimeEntryIds.Add(entry.Id);
                    remainingToBill = 0;
                }
            }
        }

        // 2. Expenses (Unbilled)
        foreach (var item in request.SelectedExpenses)
        {
            var project = _projects.Projects.GetById(item.ProjectId);
            var card = project?.RateCardId != null ? _projects.RateCards.GetById(project.RateCardId) : null;
            var markupRate = card?.MarkupRate ?? request.DefaultMarkupRate;

            invoice.LineItems.Add(new InvoiceLine
            {
                Description = $"{item.ProjectName} - {item.Description}",
                ProjectId = item.ProjectId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitCost * (1 + markupRate),
                OriginalUnitCost = item.UnitCost,
                MarkupPercentage = markupRate,
                Type = "Material",
                SourceId = item.Id,
                SourceType = item.Source
            });

            if (item.Source == "PO") invoice.BilledPoItemIds.Add(item.Id);
            else invoice.BilledExpenseItemIds.Add(item.Id);
        }

        // 3. Milestones & Quotes (Drafts never stamp Quote.PaymentTerms[].InvoiceId; only posted invoices claim milestones)
        foreach (var quote in request.AcceptedQuotes)
        {
            var liveQuote = liveQuotesById[quote.Id];
            bool quoteChanged = false;
            foreach (var term in liveQuote.PaymentTerms)
            {
                var milestone = request.SelectedMilestones.FirstOrDefault(v => v.Term.Id == term.Id);
                if (milestone != null && availableMilestoneTermIds.Contains(term.Id))
                {
                    if (isPostedTarget)
                    {
                        if (!string.Equals(term.InvoiceId, invoice.Id, StringComparison.Ordinal))
                        {
                            term.InvoiceId = invoice.Id;
                            quoteChanged = true;
                        }
                    }
                    else if (string.Equals(term.InvoiceId, invoice.Id, StringComparison.Ordinal))
                    {
                        term.InvoiceId = "";
                        quoteChanged = true;
                    }

                    invoice.LineItems.Add(new InvoiceLine
                    {
                        Description = milestone.InvoiceDescription,
                        ProjectId = liveQuote.ProjectId,
                        Quantity = 1,
                        UnitPrice = milestone.PreTaxAmount,
                        Type = "General",
                        SourceId = term.Id,
                        SourceType = "Milestone"
                    });
                }
                else if (string.Equals(term.InvoiceId, invoice.Id, StringComparison.Ordinal))
                {
                    term.InvoiceId = "";
                    quoteChanged = true;
                }
            }
            if (quoteChanged)
            {
                _projects.Quotes.Save(liveQuote);
            }
        }

        // 4. Quote Labor & Items
        foreach (var l in request.SelectedQuoteLabor)
        {
            invoice.LineItems.Add(new InvoiceLine
            {
                Type = "Labor",
                Description = TimesheetSearchHelper.FormatTaskAndSubTask(l.WorkTypeName, l.SubTaskName, ": ", l.Note),
                Note = l.Note,
                WorkTypeId = l.WorkTypeId,
                SubTaskId = l.SubTaskId,
                Quantity = l.Hours,
                UnitPrice = l.Rate,
                SourceId = l.Id,
                SourceType = "QuoteLabor"
            });
        }
        foreach (var i in request.SelectedQuoteItems)
        {
            invoice.LineItems.Add(new InvoiceLine
            {
                Type = "Material",
                Description = i.Description,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                OriginalUnitCost = i.UnitCost,
                MarkupPercentage = i.MarkupPercentage / 100m,
                SourceId = i.Id,
                SourceType = "QuoteItem"
            });
        }

        // 5. Pre-Pays
        foreach (var pp in request.PrePayItems)
        {
            invoice.LineItems.Add(new InvoiceLine
            {
                Description = pp.Description + (pp.IsTaskBased ? $" ({pp.Hours}h)" : ""),
                WorkTypeId = pp.IsTaskBased ? pp.WorkTypeId : null,
                Quantity = pp.IsTaskBased ? pp.Hours : 1,
                UnitPrice = pp.IsTaskBased ? pp.Rate : pp.Total,
                IsPrepayment = true,
                Type = "General"
            });
        }

        // 6. Custom Lines
        invoice.LineItems.AddRange(request.CustomLaborLines);
        invoice.LineItems.AddRange(request.CustomMaterialLines);
        invoice.LineItems.AddRange(request.GeneralLines);

        // 7. Apply Specific Credits (clamped to remaining balance and positive invoice subtotal)
        decimal remainingPositiveSubtotal = Math.Max(0m, invoice.LineItems.Where(l => !l.IsCreditUsed).Sum(l => l.Total));
        foreach (var credit in request.AppliedCredits.Where(c => c.AmountToApply > 0))
        {
            if (remainingPositiveSubtotal <= 0m) break;
            if (!availableCreditsById.TryGetValue(credit.PrepaymentLineId, out var availableCredit) || availableCredit.RemainingBalance <= 0m)
            {
                continue;
            }

            decimal clampedAmount = Math.Min(credit.AmountToApply, Math.Min(availableCredit.RemainingBalance, remainingPositiveSubtotal));
            if (clampedAmount <= 0m) continue;

            availableCredit.RemainingBalance -= clampedAmount;
            remainingPositiveSubtotal -= clampedAmount;

            invoice.LineItems.Add(new InvoiceLine
            {
                Description = $"Credit Applied from {credit.InvoiceNumber}: {credit.Description}",
                Quantity = 1,
                UnitPrice = -clampedAmount,
                IsCreditUsed = true,
                Type = "General",
                SourceId = credit.PrepaymentLineId,
                SourceType = "Prepayment"
            });
        }

        // 8. Status transitions (do not transition out of Draft unless actor has ApproveInvoices)
        if (!string.Equals(invoice.Status, InvoiceStatus.Draft, StringComparison.OrdinalIgnoreCase) ||
            actor.HasEffectivePermission(AppPermissions.Finance.ApproveInvoices))
        {
            if (Math.Round(invoice.BalanceDue, 2, MidpointRounding.AwayFromZero) <= 0 && invoice.GrandTotal > 0 && !InvoiceStatus.IsSettled(invoice.Status))
            {
                if (invoice.AmountPaid <= 0m && invoice.HasWriteOffs)
                {
                    invoice.Status = InvoiceStatus.WrittenOff;
                    invoice.DatePaid = null;
                }
                else
                {
                    invoice.Status = InvoiceStatus.Paid;
                    invoice.DatePaid ??= DateTime.Today;
                }
            }
            else if (InvoiceStatus.IsSettled(invoice.Status) && Math.Round(invoice.BalanceDue, 2, MidpointRounding.AwayFromZero) > 0)
            {
                invoice.Status = (invoice.AmountPaid > 0 || invoice.AmountWrittenOff > 0) ? InvoiceStatus.Partial : InvoiceStatus.Final;
                if (!invoice.Payments.Any()) invoice.DatePaid = null;
            }
        }

        if (!string.IsNullOrEmpty(invoice.PdfPath))
        {
            if (File.Exists(invoice.PdfPath))
            {
                try { File.Delete(invoice.PdfPath); } catch { }
            }
            invoice.PdfPath = string.Empty;
        }

        _accounting.Invoices.Save(invoice);
    }

    /// <summary>
    /// Validates a Draft invoice before finalization against all other posted invoices
    /// (preventing double-billed time entries, PO/expense items, quote milestones, or over-consumed prepayment credits),
    /// then commits deferred partial timesheet splits and Quote milestone links.
    /// Throws <see cref="InvalidOperationException"/> without mutating state if any conflict is detected.
    /// </summary>
    public void CommitFinalization(Invoice invoice, Employee? actor = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var invoiceProjects = ResolveInvoiceProjects(invoice);
        var invoiceProjectIds = invoiceProjects
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (invoiceProjectIds.Count == 0 && !string.IsNullOrWhiteSpace(invoice.ProjectId))
        {
            invoiceProjectIds.Add(invoice.ProjectId);
        }

        // 1. Validate no double-billing against other POSTED invoices
        var billedElsewhereTimeIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledTimeEntryIds);
        if (invoice.BilledTimeEntryIds.Any(billedElsewhereTimeIds.Contains))
        {
            throw new InvalidOperationException(
                "Cannot finalize invoice: one or more time entries have already been billed on another finalized invoice. Edit the draft to refresh unbilled items.");
        }

        var billedElsewherePoIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledPoItemIds);
        if (invoice.BilledPoItemIds.Any(billedElsewherePoIds.Contains))
        {
            throw new InvalidOperationException(
                "Cannot finalize invoice: one or more purchase order items have already been billed on another finalized invoice. Edit the draft to refresh unbilled items.");
        }

        var billedElsewhereExpenseIds = GetBilledIds(invoiceProjectIds, invoice, i => i.BilledExpenseItemIds);
        if (invoice.BilledExpenseItemIds.Any(billedElsewhereExpenseIds.Contains))
        {
            throw new InvalidOperationException(
                "Cannot finalize invoice: one or more expense items have already been billed on another finalized invoice. Edit the draft to refresh unbilled items.");
        }

        // 2. Validate Quote milestones referenced by this invoice
        var milestonesToStamp = new List<(Quote Quote, PaymentTerm Term)>();
        var milestoneSourceIds = invoice.LineItems
            .Where(l => string.Equals(l.SourceType, "Milestone", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(l.SourceId))
            .Select(l => l.SourceId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (milestoneSourceIds.Count > 0)
        {
            var projectQuotes = invoiceProjectIds
                .SelectMany(pid => _projects.Quotes.GetByProject(pid))
                .DistinctBy(q => q.Id)
                .ToList();

            foreach (var termId in milestoneSourceIds)
            {
                foreach (var quote in projectQuotes)
                {
                    var liveTerm = quote.PaymentTerms.FirstOrDefault(t => string.Equals(t.Id, termId, StringComparison.Ordinal));
                    if (liveTerm == null) continue;

                    if (IsMilestoneClaimedByOtherPostedInvoice(liveTerm, invoice.Id))
                    {
                        throw new InvalidOperationException(
                            $"Cannot finalize invoice: milestone '{liveTerm.Description}' has already been invoiced on another finalized invoice.");
                    }

                    milestonesToStamp.Add((quote, liveTerm));
                    break;
                }
            }
        }

        // 3. Validate applied prepayment credits do not exceed remaining posted balance
        var appliedCreditBySource = invoice.LineItems
            .Where(l => l.IsCreditUsed && !string.IsNullOrWhiteSpace(l.SourceId))
            .GroupBy(l => l.SourceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(l => Math.Abs(l.Total)), StringComparer.OrdinalIgnoreCase);

        if (appliedCreditBySource.Count > 0 && invoiceProjects.Count > 0)
        {
            var availableCreditsById = CalculateAvailablePrepaymentCredits(invoiceProjects, invoice, actor)
                .ToDictionary(c => c.PrepaymentLineId, StringComparer.OrdinalIgnoreCase);

            foreach (var (prepayLineId, requestedAmount) in appliedCreditBySource)
            {
                if (!availableCreditsById.TryGetValue(prepayLineId, out var available) ||
                    requestedAmount > available.RemainingBalance + 0.01m)
                {
                    throw new InvalidOperationException(
                        "Cannot finalize invoice: applied prepayment credit exceeds the remaining available credit balance. Edit the draft to adjust credits.");
                }
            }
        }

        // --- All validations passed; commit deferred allocations ---

        // 4. Perform any deferred partial TimeEntry splits so BilledTimeEntryIds match exact billed hours on disk
        if (invoice.BilledTimeEntryIds.Count > 0 && _hr?.Timesheets != null)
        {
            var stagedEntryIds = invoice.BilledTimeEntryIds.ToHashSet(StringComparer.Ordinal);
            var entryLookup = new Dictionary<string, (Timesheet Sheet, TimeEntry Entry)>(StringComparer.Ordinal);
            foreach (var sheet in _hr.Timesheets.GetAll())
            {
                foreach (var entry in sheet.Entries)
                {
                    if (stagedEntryIds.Contains(entry.Id))
                    {
                        entryLookup[entry.Id] = (sheet, entry);
                    }
                }
            }

            var timeLines = invoice.LineItems
                .Where(l => string.Equals(l.SourceType, "Time", StringComparison.OrdinalIgnoreCase) && l.Quantity > 0m)
                .ToList();

            if (timeLines.Count > 0 && entryLookup.Count > 0)
            {
                var updatedBilledIds = new List<string>();
                var handledEntryIds = new HashSet<string>(StringComparer.Ordinal);
                var modifiedSheets = new Dictionary<string, Timesheet>(StringComparer.Ordinal);

                foreach (var line in timeLines)
                {
                    var targetProjectId = !string.IsNullOrWhiteSpace(line.ProjectId) ? line.ProjectId : invoice.ProjectId;
                    var matchingEntries = entryLookup.Values
                        .Where(x => !handledEntryIds.Contains(x.Entry.Id) &&
                                    (string.IsNullOrWhiteSpace(targetProjectId) ||
                                     string.Equals(x.Entry.ProjectId, targetProjectId, StringComparison.OrdinalIgnoreCase)) &&
                                    string.Equals(x.Entry.WorkTypeId ?? "", line.WorkTypeId ?? "", StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(x.Entry.SubTaskId ?? "", line.SubTaskId ?? "", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => x.Entry.Date)
                        .ToList();

                    decimal remainingToBill = line.Quantity;
                    foreach (var (sheet, entry) in matchingEntries)
                    {
                        handledEntryIds.Add(entry.Id);
                        if (remainingToBill <= 0m) continue;

                        if (entry.Hours <= remainingToBill + 0.0001m)
                        {
                            updatedBilledIds.Add(entry.Id);
                            remainingToBill = Math.Max(0m, remainingToBill - entry.Hours);
                        }
                        else
                        {
                            var billedEntry = new TimeEntry
                            {
                                ProjectId = entry.ProjectId,
                                WorkTypeId = entry.WorkTypeId,
                                SubTaskId = entry.SubTaskId,
                                Date = entry.Date,
                                Description = entry.Description + " (Billed)",
                                Hours = remainingToBill,
                                Id = Guid.NewGuid().ToString()
                            };
                            entry.Hours -= remainingToBill;
                            sheet.Entries.Add(billedEntry);
                            modifiedSheets[sheet.Id] = sheet;
                            updatedBilledIds.Add(billedEntry.Id);
                            remainingToBill = 0m;
                        }
                    }
                }

                // Preserve any billed time entry IDs that weren't matched to a specific Time line
                foreach (var origId in invoice.BilledTimeEntryIds)
                {
                    if (!handledEntryIds.Contains(origId) && !updatedBilledIds.Contains(origId))
                    {
                        updatedBilledIds.Add(origId);
                    }
                }

                foreach (var sheet in modifiedSheets.Values)
                {
                    _hr.Timesheets.Save(sheet);
                }

                invoice.BilledTimeEntryIds = updatedBilledIds;
            }
        }

        // 5. Stamp Quote milestone links
        var modifiedQuotes = new Dictionary<string, Quote>(StringComparer.Ordinal);
        foreach (var (quote, term) in milestonesToStamp)
        {
            if (!string.Equals(term.InvoiceId, invoice.Id, StringComparison.Ordinal))
            {
                term.InvoiceId = invoice.Id;
                modifiedQuotes[quote.Id] = quote;
            }
        }
        foreach (var quote in modifiedQuotes.Values)
        {
            _projects.Quotes.Save(quote);
        }
    }

    /// <summary>
    /// Releases external Quote milestone links owned by an invoice when reverting Final -> Draft
    /// or deleting a legacy draft.
    /// </summary>
    public void ReleaseFinalizedAllocations(Invoice invoice)
    {
        if (invoice == null || string.IsNullOrWhiteSpace(invoice.Id)) return;

        var projectIds = ResolveInvoiceProjects(invoice)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(invoice.ProjectId))
        {
            projectIds.Add(invoice.ProjectId);
        }

        var quotes = projectIds.Count > 0
            ? projectIds.SelectMany(pid => _projects.Quotes.GetByProject(pid)).DistinctBy(q => q.Id).ToList()
            : _projects.Quotes.GetAll();

        foreach (var quote in quotes)
        {
            bool changed = false;
            foreach (var term in quote.PaymentTerms)
            {
                if (string.Equals(term.InvoiceId, invoice.Id, StringComparison.Ordinal))
                {
                    term.InvoiceId = "";
                    changed = true;
                }
            }
            if (changed)
            {
                _projects.Quotes.Save(quote);
            }
        }
    }

    private List<Project> ResolveInvoiceProjects(Invoice invoice)
    {
        var projects = new List<Project>();
        if (!string.IsNullOrEmpty(invoice.ProjectGroupId))
        {
            var group = _projects.ProjectGroups.GetById(invoice.ProjectGroupId);
            if (group != null)
            {
                foreach (var pid in group.ProjectIds)
                {
                    var p = _projects.Projects.GetById(pid);
                    if (p != null && !projects.Any(x => x.Id == p.Id))
                    {
                        projects.Add(p);
                    }
                }
            }
        }
        if (!string.IsNullOrEmpty(invoice.ProjectId))
        {
            var p = _projects.Projects.GetById(invoice.ProjectId);
            if (p != null && !projects.Any(x => x.Id == p.Id))
            {
                projects.Add(p);
            }
        }
        return projects;
    }

    private bool IsMilestoneClaimedByOtherPostedInvoice(PaymentTerm term, string currentInvoiceId)
    {
        if (string.IsNullOrWhiteSpace(term.InvoiceId) ||
            string.Equals(term.InvoiceId, currentInvoiceId, StringComparison.Ordinal))
        {
            return false;
        }

        var owner = _accounting.Invoices.GetById(term.InvoiceId);
        return owner != null && InvoiceStatus.IsPosted(owner.Status);
    }

    private HashSet<string> GetBilledIds(
        IEnumerable<string> projectIds,
        Invoice currentInvoice,
        Func<Invoice, IEnumerable<string>> selector)
    {
        var billedIds = new HashSet<string>();
        foreach (var projectId in projectIds)
        {
            var projectInvoices = _accounting.Invoices.GetByProject(projectId)
                .Where(i => i.Id != currentInvoice.Id && InvoiceStatus.IsPosted(i.Status));
            billedIds.UnionWith(projectInvoices.SelectMany(selector));
        }

        if (!string.IsNullOrEmpty(currentInvoice.ProjectGroupId))
        {
            var groupInvoices = _accounting.Invoices.GetByProjectGroup(currentInvoice.ProjectGroupId)
                .Where(i => i.Id != currentInvoice.Id && InvoiceStatus.IsPosted(i.Status));
            billedIds.UnionWith(groupInvoices.SelectMany(selector));
        }

        return billedIds;
    }
}
