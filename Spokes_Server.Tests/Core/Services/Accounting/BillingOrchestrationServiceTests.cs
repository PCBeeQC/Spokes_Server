using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
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
            Lines = new List<InvoiceLine>
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
            Lines = new List<InvoiceLine>
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
            Lines = new List<InvoiceLine>
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
        // Remaining should be: 1000 - 300 (used on inv-2) = 700 available to this invoice (including the 200 currently applied)
        Assert.Equal(900m, credit.RemainingBalance);
        Assert.Equal(200m, credit.AmountToApply);
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
            Items = new List<PoItem>
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

        // Verify timesheet was saved and split
        var updatedSheet = _db.Timesheets.GetById("sheet-split");
        Assert.NotNull(updatedSheet);
        Assert.Equal(2, updatedSheet.Entries.Count);
        // Original entry reduced to 3 hours
        Assert.Equal(3m, updatedSheet.Entries.First(e => e.Id == "orig-entry").Hours);
        // New billed entry created for 5 hours
        var splitEntry = updatedSheet.Entries.First(e => e.Id != "orig-entry");
        Assert.Equal(5m, splitEntry.Hours);
        Assert.Contains("(Billed)", splitEntry.Description);

        // Verify invoice was saved and contains lines
        var savedInvoice = _db.Invoices.GetById("inv-target");
        Assert.NotNull(savedInvoice);
        Assert.Contains(splitEntry.Id, savedInvoice.BilledTimeEntryIds);
        Assert.Equal(2, savedInvoice.LineItems.Count); // Labor + Milestone

        // Verify quote milestone updated with InvoiceId
        var updatedQuote = _db.Quotes.GetById("q-1");
        Assert.Equal("inv-target", updatedQuote!.PaymentTerms[0].InvoiceId);
    }
}
