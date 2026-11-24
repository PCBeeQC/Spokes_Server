using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Spokes_Server.Tests;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class BillingOrchestrationServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly BillingOrchestrationService _orchestrator;

    public BillingOrchestrationServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _orchestrator = new BillingOrchestrationService(_db.AccountingContext, _db.ProjectsContext, _db.HrContext, _db.CoreContext);
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void CalculateAvailablePrepaymentCredits_ComputesRemainingBalancesAndCurrentlyApplied()
    {
        var project = new Project { Id = "proj-1", Name = "Alpha" };
        _db.Projects.Save(project);

        // Invoice 1 with paid prepayment of 1000
        var inv1 = new Invoice
        {
            Id = "inv-1",
            InvoiceNumber = "INV-001",
            ProjectId = project.Id,
            Status = "Paid",
            Date = DateTime.Today.AddDays(-20),
            LineItems = new List<InvoiceLine>
            {
                new() { Id = "line-prepay-1", Description = "Deposit 1", Quantity = 1, UnitPrice = 1000m, IsPrepayment = true }
            }
        };
        _db.Invoices.Save(inv1);

        // Invoice 2 using 300 of the credit
        var inv2 = new Invoice
        {
            Id = "inv-2",
            InvoiceNumber = "INV-002",
            ProjectId = project.Id,
            Status = "Paid",
            Date = DateTime.Today.AddDays(-10),
            LineItems = new List<InvoiceLine>
            {
                new() { Id = "line-credit-used", Description = "Credit", Quantity = 1, UnitPrice = -300m, IsCreditUsed = true, SourceId = "line-prepay-1" }
            }
        };
        _db.Invoices.Save(inv2);

        // Current invoice being edited, which already applied 200 of the credit
        var currentInvoice = new Invoice
        {
            Id = "inv-current",
            InvoiceNumber = "INV-003",
            ProjectId = project.Id,
            Status = "Draft",
            LineItems = new List<InvoiceLine>
            {
                new() { Id = "line-current-applied", Quantity = 1, UnitPrice = -200m, IsCreditUsed = true, SourceId = "line-prepay-1" }
            }
        };

        var credits = _orchestrator.CalculateAvailablePrepaymentCredits(new[] { project }, currentInvoice);

        Assert.Single(credits);
        var credit = credits[0];
        Assert.Equal("line-prepay-1", credit.PrepaymentLineId);
        Assert.Equal("INV-001", credit.InvoiceNumber);
        Assert.Equal(1000m, credit.OriginalAmount);
        // Remaining should be: 1000 - 300 (used on posted inv-2) = 700 available to this invoice
        Assert.Equal(700m, credit.RemainingBalance);
        Assert.Equal(200m, credit.AmountToApply);
    }

    [Fact]
    public void CalculateAvailablePrepaymentCredits_IgnoresCreditsStagedOnOtherDraftInvoices()
    {
        var project = new Project { Id = "proj-draft-cred-iso", Name = "Credit Iso" };
        _db.Projects.Save(project);

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-prepay-iso",
            InvoiceNumber = "INV-PRE-ISO",
            ProjectId = project.Id,
            Status = InvoiceStatus.Paid,
            Date = DateTime.Today.AddDays(-10),
            LineItems = [new InvoiceLine { Id = "line-prepay-iso", Description = "Deposit", Quantity = 1, UnitPrice = 1000m, IsPrepayment = true }]
        });

        // Another Draft invoice stages $600 of the credit — must NOT reduce remaining balance
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-other-draft",
            InvoiceNumber = "INV-DRAFT-ISO",
            ProjectId = project.Id,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { Id = "line-draft-cred", Quantity = 1, UnitPrice = -600m, IsCreditUsed = true, SourceId = "line-prepay-iso" }]
        });

        var currentInvoice = new Invoice { Id = "inv-cur-iso", ProjectId = project.Id, Status = InvoiceStatus.Draft };
        var credits = _orchestrator.CalculateAvailablePrepaymentCredits([project], currentInvoice);

        var credit = Assert.Single(credits);
        Assert.Equal(1000m, credit.RemainingBalance);
    }

    [Fact]
    public void LoadUnbilledLabor_AggregatesAvailableHoursAndComputesRates()
    {
        var project = new Project { Id = "proj-labor", Name = "Project Labor" };
        _db.Projects.Save(project);

        var workTypes = new Dictionary<string, WorkType>
        {
            ["wt-1"] = new()
            {
                Id = "wt-1",
                Name = "Dev",
                DefaultRate = 120m,
                SubTasks = new List<SubTask> { new() { Id = "sub-1", Name = "Backend" } }
            }
        };

        var sheet = new Timesheet
        {
            Id = "sheet-1",
            EmployeeId = "emp-1",
            Entries = new List<TimeEntry>
            {
                new() { Id = "entry-1", ProjectId = project.Id, WorkTypeId = "wt-1", SubTaskId = "sub-1", Hours = 5m, Date = DateTime.Today }
            }
        };
        _db.Timesheets.Save(sheet);

        var currentInvoice = new Invoice { Id = "inv-new", ProjectId = project.Id };

        var groups = _orchestrator.LoadUnbilledLabor(new[] { project }, currentInvoice, workTypes);

        Assert.Single(groups);
        Assert.Equal("Dev", groups[0].TaskName);
        Assert.Equal("Backend", groups[0].SubTaskName);
        Assert.Equal(5m, groups[0].AvailableHours);
        Assert.Equal(120m, groups[0].Rate);
    }

    [Fact]
    public void LoadUnbilledExpenses_CalculatesTotalsWithMarkup()
    {
        var project = new Project { Id = "proj-exp", Name = "Project Exp" };
        _db.Projects.Save(project);

        var po = new PurchaseOrder
        {
            Id = "po-1",
            Date = DateTime.Today,
            LineItems = new List<PoItem>
            {
                new() { Id = "po-item-1", ProjectId = project.Id, Description = "Hardware", Quantity = 2, UnitPrice = 50m }
            }
        };
        _db.PurchaseOrders.Save(po);

        var currentInvoice = new Invoice { Id = "inv-new", ProjectId = project.Id };

        var items = _orchestrator.LoadUnbilledExpenses(new[] { project }, currentInvoice, 0.20m);

        Assert.Single(items);
        Assert.Equal(100m, items[0].Cost);
        Assert.Equal(120m, items[0].Total); // 100 * (1 + 0.20)
        Assert.Equal("PO", items[0].Source);
    }

    [Fact]
    public void ProcessAndSaveInvoice_AllocatesTimesheetHoursAndSplitsEntry()
    {
        var project = new Project { Id = "proj-full", Name = "Full Flow" };
        _db.Projects.Save(project);

        var sheet = new Timesheet
        {
            Id = "sheet-split",
            EmployeeId = "emp-1",
            Entries = new List<TimeEntry>
            {
                new() { Id = "orig-entry", ProjectId = project.Id, WorkTypeId = "wt-1", SubTaskId = "sub-1", Hours = 8m, Date = DateTime.Today, Description = "Coding" }
            }
        };
        _db.Timesheets.Save(sheet);

        var quote = new Quote
        {
            Id = "q-1",
            ProjectId = project.Id,
            QuoteNumber = "Q-001",
            Status = "Accepted",
            PaymentTerms = new List<PaymentTerm>
            {
                new() { Id = "term-1", Description = "Phase 1", Percentage = 50, Amount = 500m }
            }
        };
        _db.Quotes.Save(quote);

        var invoice = new Invoice
        {
            Id = "inv-target",
            InvoiceNumber = "INV-999",
            ProjectId = project.Id,
            Status = "Draft"
        };

        var laborGroup = new BillingLaborGroup
        {
            ProjectId = project.Id,
            ProjectName = project.DisplayName,
            WorkTypeId = "wt-1",
            SubTaskId = "sub-1",
            TaskName = "Dev",
            SubTaskName = "General",
            Rate = 100m,
            HoursToBill = 5m, // Bill 5 out of 8 hours
            Entries = new List<BillingLaborEntryRef>
            {
                new() { Entry = sheet.Entries[0], ParentSheet = sheet }
            }
        };

        var request = new BillingSubmissionRequest
        {
            Actor = new Employee
            {
                Id = "emp-billing",
                IsActive = true,
                Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Finance.ApproveInvoices]
            },
            Invoice = invoice,
            LaborGroups = new List<BillingLaborGroup> { laborGroup },
            AcceptedQuotes = new List<Quote> { quote },
            SelectedMilestones = new List<BillingMilestoneSelection>
            {
                new() { Term = quote.PaymentTerms[0], Quote = quote, InvoiceDescription = "Milestone 1" }
            },
            AppliedCredits = new List<AvailablePrepaymentCredit>()
        };

        _orchestrator.ProcessAndSaveInvoice(request);

        // Strict Draft Isolation: saving a Draft must NOT split timesheet entries or stamp Quote milestones on disk
        var draftSheet = _db.Timesheets.GetById("sheet-split");
        Assert.NotNull(draftSheet);
        Assert.Single(draftSheet.Entries);
        Assert.Equal(8m, draftSheet.Entries[0].Hours);

        var draftQuote = _db.Quotes.GetById("q-1");
        Assert.True(string.IsNullOrEmpty(draftQuote!.PaymentTerms[0].InvoiceId));

        // Verify invoice was saved with staged lines and candidate entry ID
        var savedInvoice = _db.Invoices.GetById("inv-target");
        Assert.NotNull(savedInvoice);
        Assert.Contains("orig-entry", savedInvoice.BilledTimeEntryIds);
        Assert.Equal(2, savedInvoice.LineItems.Count); // Labor + Milestone

        // Re-loading unbilled labor on the Draft restores exact staged HoursToBill (5h) while showing 8h available
        var reloadedGroups = _orchestrator.LoadUnbilledLabor([project], savedInvoice, new Dictionary<string, WorkType>
        {
            ["wt-1"] = new() { Id = "wt-1", Name = "Dev", DefaultRate = 100m, SubTasks = [new SubTask { Id = "sub-1", Name = "General" }] }
        });
        var reloadedGroup = Assert.Single(reloadedGroups);
        Assert.Equal(8m, reloadedGroup.AvailableHours);
        Assert.Equal(5m, reloadedGroup.HoursToBill);

        // Committing finalization (Draft -> Final) performs the deferred partial split and stamps the Quote milestone
        _orchestrator.CommitFinalization(savedInvoice, request.Actor);
        savedInvoice.Status = InvoiceStatus.Final;
        _db.Invoices.Save(savedInvoice);

        var updatedSheet = _db.Timesheets.GetById("sheet-split");
        Assert.NotNull(updatedSheet);
        Assert.Equal(2, updatedSheet.Entries.Count);
        Assert.Equal(3m, updatedSheet.Entries.First(e => e.Id == "orig-entry").Hours);
        var splitEntry = updatedSheet.Entries.First(e => e.Id != "orig-entry");
        Assert.Equal(5m, splitEntry.Hours);
        Assert.Contains("(Billed)", splitEntry.Description);
        Assert.Contains(splitEntry.Id, savedInvoice.BilledTimeEntryIds);
        Assert.DoesNotContain("orig-entry", savedInvoice.BilledTimeEntryIds);

        var updatedQuote = _db.Quotes.GetById("q-1");
        Assert.Equal("inv-target", updatedQuote!.PaymentTerms[0].InvoiceId);

        // Reverting Final -> Draft releases the Quote milestone link
        _orchestrator.ReleaseFinalizedAllocations(savedInvoice);
        Assert.True(string.IsNullOrEmpty(_db.Quotes.GetById("q-1")!.PaymentTerms[0].InvoiceId));
    }

    [Fact]
    public void CommitFinalization_RejectsOverlappingDraftsForTimeMilestoneAndCredit()
    {
        var project = new Project { Id = "proj-overlap", Name = "Overlap Project" };
        _db.Projects.Save(project);

        var actor = new Employee
        {
            Id = "emp-overlap",
            IsActive = true,
            Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Finance.ApproveInvoices]
        };

        // Prepayment source
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-prepay-ov",
            InvoiceNumber = "INV-PRE-OV",
            ProjectId = project.Id,
            Status = InvoiceStatus.Paid,
            LineItems = [new InvoiceLine { Id = "line-prepay-ov", Quantity = 1m, UnitPrice = 300m, IsPrepayment = true }]
        });

        var quote = new Quote
        {
            Id = "q-ov",
            ProjectId = project.Id,
            Status = QuoteStatus.Accepted,
            PaymentTerms = [new PaymentTerm { Id = "term-ov", Description = "Deposit", Percentage = 50, Amount = 500m }]
        };
        _db.Quotes.Save(quote);

        // First invoice is finalized and claims time entry "te-1", milestone "term-ov", and $300 prepayment credit
        var postedInvoice = new Invoice
        {
            Id = "inv-posted-ov",
            ProjectId = project.Id,
            Status = InvoiceStatus.Final,
            BilledTimeEntryIds = ["te-1"],
            LineItems =
            [
                new InvoiceLine { SourceType = "Milestone", SourceId = "term-ov", Quantity = 1m, UnitPrice = 500m },
                new InvoiceLine { IsCreditUsed = true, SourceType = "Prepayment", SourceId = "line-prepay-ov", Quantity = 1m, UnitPrice = -300m }
            ]
        };
        _db.Invoices.Save(postedInvoice);
        quote.PaymentTerms[0].InvoiceId = postedInvoice.Id;
        _db.Quotes.Save(quote);

        // Overlapping Draft 1: conflicts on time entry
        var draftTime = new Invoice { Id = "draft-ov-time", ProjectId = project.Id, Status = InvoiceStatus.Draft, BilledTimeEntryIds = ["te-1"] };
        Assert.Throws<InvalidOperationException>(() => _orchestrator.CommitFinalization(draftTime, actor));

        // Overlapping Draft 2: conflicts on milestone
        var draftMilestone = new Invoice
        {
            Id = "draft-ov-ms",
            ProjectId = project.Id,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { SourceType = "Milestone", SourceId = "term-ov", Quantity = 1m, UnitPrice = 500m }]
        };
        Assert.Throws<InvalidOperationException>(() => _orchestrator.CommitFinalization(draftMilestone, actor));

        // Overlapping Draft 3: conflicts on exhausted prepayment credit
        var draftCredit = new Invoice
        {
            Id = "draft-ov-cred",
            ProjectId = project.Id,
            Status = InvoiceStatus.Draft,
            LineItems = [new InvoiceLine { IsCreditUsed = true, SourceType = "Prepayment", SourceId = "line-prepay-ov", Quantity = 1m, UnitPrice = -100m }]
        };
        Assert.Throws<InvalidOperationException>(() => _orchestrator.CommitFinalization(draftCredit, actor));
    }

    [Fact]
    public void CalculateAvailablePrepaymentCredits_ExcludesSelfAndClampsAppliedCreditsOnSave()
    {
        var project = new Project { Id = "proj-clamp", Name = "Clamp Project" };
        _db.Projects.Save(project);

        var prepayInvoice = new Invoice
        {
            Id = "inv-prepay",
            InvoiceNumber = "INV-PRE-1",
            ProjectId = project.Id,
            Status = "Paid",
            Date = DateTime.Today.AddDays(-5),
            LineItems = new List<InvoiceLine>
            {
                new() { Id = "line-prepay-clamp", Description = "Deposit", Quantity = 1, UnitPrice = 400m, IsPrepayment = true }
            }
        };
        _db.Invoices.Save(prepayInvoice);

        // Prepayment invoice itself should not see its own deposit as an available credit to apply to itself
        var selfCredits = _orchestrator.CalculateAvailablePrepaymentCredits(new[] { project }, prepayInvoice);
        Assert.Empty(selfCredits);

        // Another invoice with only $250 of positive billable items attempts to apply $9999 of credit
        var targetInvoice = new Invoice
        {
            Id = "inv-target-clamp",
            InvoiceNumber = "INV-CLAMP-2",
            ProjectId = project.Id,
            Status = "Draft"
        };

        var request = new BillingSubmissionRequest
        {
            Actor = new Employee
            {
                Id = "emp-billing",
                IsActive = true,
                Permissions = [AppPermissions.Finance.AccessAccounting, AppPermissions.Finance.ApproveInvoices]
            },
            Invoice = targetInvoice,
            LaborGroups = new List<BillingLaborGroup>(),
            AcceptedQuotes = new List<Quote>(),
            SelectedMilestones = new List<BillingMilestoneSelection>(),
            GeneralLines = new List<InvoiceLine>
            {
                new() { Id = "line-work", Description = "Consulting", Quantity = 1, UnitPrice = 250m }
            },
            AppliedCredits = new List<AvailablePrepaymentCredit>
            {
                new()
                {
                    PrepaymentLineId = "line-prepay-clamp",
                    InvoiceNumber = "INV-PRE-1",
                    OriginalAmount = 400m,
                    RemainingBalance = 400m,
                    AmountToApply = 9999m
                }
            }
        };

        _orchestrator.ProcessAndSaveInvoice(request);

        var saved = _db.Invoices.GetById("inv-target-clamp");
        Assert.NotNull(saved);
        var creditLine = saved.LineItems.SingleOrDefault(l => l.IsCreditUsed);
        Assert.NotNull(creditLine);
        // Clamped to the positive pre-credit subtotal ($250), never driving the invoice negative
        Assert.Equal(-250m, creditLine.UnitPrice);
    }
}
