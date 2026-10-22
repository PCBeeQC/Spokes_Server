using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
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
    public Invoice Invoice { get; set; } = default!;
    public DocumentTemplate? Template { get; set; }
    public decimal DefaultMarkupRate { get; set; } = 0.15m;
    public List<BillingLaborGroup> LaborGroups { get; set; } = new();
    public List<BillingExpenseItem> SelectedExpenses { get; set; } = new();
    public List<Quote> AcceptedQuotes { get; set; } = new();
    public List<BillingMilestoneSelection> SelectedMilestones { get; set; } = new();
    public List<QuoteLaborItem> SelectedQuoteLabor { get; set; } = new();
    public List<QuoteItemRow> SelectedQuoteItems { get; set; } = new();
    public List<QuoteItemRow> SelectedQuoteMaterialItems
    {
        get => SelectedQuoteItems;
        set => SelectedQuoteItems = value;
    }
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

    public List<AvailablePrepaymentCredit> CalculateAvailablePrepaymentCredits(IEnumerable<Project> projects, Invoice invoice)
    {
        var result = new List<AvailablePrepaymentCredit>();

        var groupInvoices = !string.IsNullOrEmpty(invoice.ProjectGroupId)
            ? _accounting.Invoices.GetByProjectGroup(invoice.ProjectGroupId)
            : new List<Invoice>();

        var allInvoices = new List<Invoice>();
        foreach (var project in projects)
        {
            allInvoices.AddRange(_accounting.Invoices.GetByProject(project.Id));
        }
        allInvoices.AddRange(groupInvoices);
        allInvoices = allInvoices.DistinctBy(i => i.Id).ToList();

        // Find all paid prepayments
        var paidPrepayments = allInvoices
            .Where(i => i.Status == InvoiceStatus.Paid)
            .SelectMany(i => i.LineItems.Where(l => l.IsPrepayment).Select(l => new { Invoice = i, Line = l }))
            .ToList();

        // Find all used credits
        var usedCredits = allInvoices
            .Where(i => i.Status != InvoiceStatus.Void)
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

            // Subtract linked used credits
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

            // Also check if THIS current invoice already applied some of this specific credit
            decimal currentlyApplied = invoice.LineItems
                .Where(l => l.IsCreditUsed && l.SourceId == prepay.Line.Id)
                .Sum(l => Math.Abs(l.Total));

            // When editing, add back what this invoice currently applied to the remaining balance
            remaining += currentlyApplied;

            if (remaining > 0 || currentlyApplied > 0)
            {
                result.Add(new AvailablePrepaymentCredit
                {
                    PrepaymentLineId = prepay.Line.Id,
                    InvoiceNumber = prepay.Invoice.InvoiceNumber,
                    Description = prepay.Line.Description,
                    OriginalAmount = original,
                    RemainingBalance = remaining,
                    AmountToApply = currentlyApplied
                });
            }
        }

        return result;
    }

    public List<BillingLaborGroup> LoadUnbilledLabor(IEnumerable<Project> projects, Invoice invoice, IReadOnlyDictionary<string, WorkType> workTypes)
    {
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var billedTimeIds = GetBilledTimeEntryIds(projectIds, invoice);
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
        foreach (var g in rawGroups.Values)
        {
            g.Entries = g.Entries.OrderBy(x => x.Entry.Date).ToList();
            result.Add(g);
        }
        return result;
    }

    public List<BillingExpenseItem> LoadUnbilledExpenses(IEnumerable<Project> projects, Invoice invoice, decimal defaultMarkupRate)
    {
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var billedPoIds = GetBilledPoItemIds(projectIds, invoice);
        var billedExpenseIds = GetBilledExpenseItemIds(projectIds, invoice);
        var currentInvoicePoIds = invoice.BilledPoItemIds.ToHashSet();
        var currentInvoiceExpenseIds = invoice.BilledExpenseItemIds.ToHashSet();

        var result = new List<BillingExpenseItem>();

        var allPos = _accounting.PurchaseOrders.GetAll();
        foreach (var po in allPos)
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
        foreach (var report in allReports.Where(r => r.DateSubmitted.HasValue))
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

    public BillingMilestonesAndQuotesResult LoadMilestonesAndQuotes(IEnumerable<Project> projects, Invoice invoice)
    {
        var projectIds = projects.Select(p => p.Id).ToHashSet();
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
                    if (!term.IsInvoiced || term.InvoiceId == invoice.Id)
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
        var invoice = request.Invoice;

        // Clear all Billed ID lists for this invoice so we can rebuild them
        invoice.BilledTimeEntryIds.Clear();
        invoice.BilledPoItemIds.Clear();
        invoice.BilledExpenseItemIds.Clear();
        invoice.LineItems.Clear();

        var template = request.Template
            ?? (!string.IsNullOrEmpty(invoice.TemplateId) ? _core.DocumentTemplates.GetById(invoice.TemplateId) : null)
            ?? _core.DocumentTemplates.GetDefault();

        // 1. Labor Groups (Unbilled)
        foreach (var group in request.LaborGroups.Where(g => g.HoursToBill > 0))
        {
            decimal remainingToBill = group.HoursToBill;
            string taskLabel = TimesheetSearchHelper.FormatTaskAndSubTask(group.TaskName, group.SubTaskName, ": ");
            string lineDescription = $"{group.ProjectName} - {taskLabel} {template?.Label_ItemLaborTag ?? "(Labor)"}";

            invoice.LineItems.Add(new InvoiceLine
            {
                Description = lineDescription,
                WorkTypeId = group.WorkTypeId,
                SubTaskId = group.SubTaskId,
                Quantity = group.HoursToBill,
                UnitPrice = group.Rate,
                Type = "Labor",
                SourceType = "Time"
            });

            foreach (var refObj in group.Entries)
            {
                if (remainingToBill <= 0) break;
                var entry = refObj.Entry;
                var sheet = refObj.ParentSheet;

                if (entry.Hours <= remainingToBill)
                {
                    invoice.BilledTimeEntryIds.Add(entry.Id);
                    remainingToBill -= entry.Hours;
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
                    invoice.BilledTimeEntryIds.Add(billedEntry.Id);
                    _hr.Timesheets.Save(sheet);
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

        // 3. Milestones & Quotes
        var selectedMilestoneTermIds = request.SelectedMilestones.Select(m => m.Term.Id).ToHashSet();
        foreach (var quote in request.AcceptedQuotes)
        {
            foreach (var term in quote.PaymentTerms)
            {
                var milestone = request.SelectedMilestones.FirstOrDefault(v => v.Term.Id == term.Id);
                if (milestone != null && selectedMilestoneTermIds.Contains(term.Id))
                {
                    term.InvoiceId = invoice.Id;
                    invoice.LineItems.Add(new InvoiceLine
                    {
                        Description = milestone.InvoiceDescription,
                        Quantity = 1,
                        UnitPrice = milestone.PreTaxAmount,
                        Type = "General",
                        SourceId = term.Id,
                        SourceType = "Milestone"
                    });
                }
                else if (term.InvoiceId == invoice.Id)
                {
                    term.InvoiceId = "";
                }
            }
            _projects.Quotes.Save(quote);
        }

        // 4. Quote Labor & Items
        foreach (var l in request.SelectedQuoteLabor)
        {
            invoice.LineItems.Add(new InvoiceLine
            {
                Type = "Labor",
                Description = TimesheetSearchHelper.FormatTaskAndSubTask(l.WorkTypeName, l.SubTaskName, ": "),
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

        // 7. Apply Specific Credits
        foreach (var credit in request.AppliedCredits.Where(c => c.AmountToApply > 0))
        {
            invoice.LineItems.Add(new InvoiceLine
            {
                Description = $"Credit Applied from {credit.InvoiceNumber}: {credit.Description}",
                Quantity = 1,
                UnitPrice = -credit.AmountToApply,
                IsCreditUsed = true,
                Type = "General",
                SourceId = credit.PrepaymentLineId,
                SourceType = "Prepayment"
            });
        }

        // 8. Status transitions
        if (invoice.BalanceDue <= 0 && invoice.GrandTotal > 0 && invoice.Status != InvoiceStatus.Paid)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.DatePaid ??= DateTime.Today;
        }
        else if (invoice.Status == InvoiceStatus.Paid && invoice.BalanceDue > 0)
        {
            invoice.Status = InvoiceStatus.Sent;
            if (!invoice.Payments.Any()) invoice.DatePaid = null;
        }

        _accounting.Invoices.Save(invoice);
    }

    private HashSet<string> GetBilledTimeEntryIds(IEnumerable<string> projectIds, Invoice currentInvoice)
    {
        var billedTimeIds = new HashSet<string>();
        foreach (var projectId in projectIds)
        {
            var projectInvoices = _accounting.Invoices.GetByProject(projectId).Where(i => i.Id != currentInvoice.Id);
            billedTimeIds.UnionWith(projectInvoices.SelectMany(i => i.BilledTimeEntryIds));
        }

        if (!string.IsNullOrEmpty(currentInvoice.ProjectGroupId))
        {
            var groupInvoices = _accounting.Invoices.GetByProjectGroup(currentInvoice.ProjectGroupId).Where(i => i.Id != currentInvoice.Id);
            billedTimeIds.UnionWith(groupInvoices.SelectMany(i => i.BilledTimeEntryIds));
        }

        return billedTimeIds;
    }

    private HashSet<string> GetBilledPoItemIds(IEnumerable<string> projectIds, Invoice currentInvoice)
    {
        var billedPoIds = new HashSet<string>();
        foreach (var projectId in projectIds)
        {
            var projectInvoices = _accounting.Invoices.GetByProject(projectId).Where(i => i.Id != currentInvoice.Id);
            billedPoIds.UnionWith(projectInvoices.SelectMany(i => i.BilledPoItemIds));
        }

        if (!string.IsNullOrEmpty(currentInvoice.ProjectGroupId))
        {
            var groupInvoices = _accounting.Invoices.GetByProjectGroup(currentInvoice.ProjectGroupId).Where(i => i.Id != currentInvoice.Id);
            billedPoIds.UnionWith(groupInvoices.SelectMany(i => i.BilledPoItemIds));
        }

        return billedPoIds;
    }

    private HashSet<string> GetBilledExpenseItemIds(IEnumerable<string> projectIds, Invoice currentInvoice)
    {
        var billedExpenseIds = new HashSet<string>();
        foreach (var projectId in projectIds)
        {
            var projectInvoices = _accounting.Invoices.GetByProject(projectId).Where(i => i.Id != currentInvoice.Id);
            billedExpenseIds.UnionWith(projectInvoices.SelectMany(i => i.BilledExpenseItemIds));
        }

        if (!string.IsNullOrEmpty(currentInvoice.ProjectGroupId))
        {
            var groupInvoices = _accounting.Invoices.GetByProjectGroup(currentInvoice.ProjectGroupId).Where(i => i.Id != currentInvoice.Id);
            billedExpenseIds.UnionWith(groupInvoices.SelectMany(i => i.BilledExpenseItemIds));
        }

        return billedExpenseIds;
    }
}
