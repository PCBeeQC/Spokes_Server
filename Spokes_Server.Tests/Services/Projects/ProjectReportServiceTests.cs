namespace Spokes_Server.Tests.Services.Projects;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;
using Xunit;

public class ProjectReportServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly ProjectReportService _reportService;

    public ProjectReportServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddScoped<ProjectReportService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _reportService = _serviceProvider.GetRequiredService<ProjectReportService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void MidProjectSnapshot_RemainsFrozen_WhenSubsequentProjectActivityOccurs()
    {
        // Arrange: Create Airbus project, employees, tasks, and mid-project activity (Jan-Feb 2026)
        var empAlice = new Employee { Id = "emp-alice", FirstName = "Alice", LastName = "Martin", HourlyRate = 50m };
        var empBob = new Employee { Id = "emp-bob", FirstName = "Bob", LastName = "Dupont", HourlyRate = 60m };
        _db.Employees.Save(empAlice);
        _db.Employees.Save(empBob);

        var workType = new WorkType { Id = "wt-eng", Name = "Mechanical Engineering", DefaultRate = 120m, CostRate = 55m };
        _db.WorkTypes.Save(workType);

        var project = new Project
        {
            Id = "proj-airbus",
            Name = "Airbus 2026 Tooling",
            ProjectNumber = "P26-001",
            Status = "In Progress",
            ProjectManagerId = empAlice.Id
        };
        _db.Projects.Save(project);

        var quote = new Quote
        {
            Id = "q-1",
            ProjectId = project.Id,
            QuoteNumber = "Q-001",
            Date = new DateTime(2026, 1, 5),
            Status = QuoteStatus.Accepted,
            TaxRate = 0m,
            LaborItems =
            [
                new QuoteLaborItem { WorkTypeId = workType.Id, WorkTypeName = "Engineering", Hours = 100m, Rate = 120m }
            ]
        };
        _db.Quotes.Save(quote);

        // Week 2 (Jan 2026) and Week 6 (Feb 2026) timesheets
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-1",
            EmployeeId = empAlice.Id,
            EmployeeName = empAlice.FullName,
            Year = 2026,
            WeekNumber = 2,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry
                {
                    Id = "te-1",
                    ProjectId = project.Id,
                    WorkTypeId = workType.Id,
                    Date = new DateTime(2026, 1, 6),
                    Hours = 20m,
                    IsBillable = true
                }
            ]
        });

        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-jan",
            PoNumber = "PO-2601",
            Date = new DateTime(2026, 1, 15),
            Status = PurchaseOrderStatus.Final,
            ApplyTax = false,
            Supplier = new Spokes_Server.Core.Models.Core.PartyContactInfo { BusinessName = "McMaster-Carr" },
            LineItems =
            [
                new PoItem
                {
                    Id = "poi-1",
                    ProjectId = project.Id,
                    PartNumber = "AL-6061",
                    Description = "Aluminum Plate",
                    Quantity = 2m,
                    UnitPrice = 250m
                }
            ]
        });

        // Act 1: Generate mid-project report as of 2026-01-31
        var midProjectCutoff = new DateTime(2026, 1, 31);
        var midReport = new ProjectReport
        {
            Id = "rpt-mid",
            ProjectId = project.Id,
            ReportNumber = _db.ProjectReports.GenerateReportNumber("RPT"),
            Title = "Mid-Project Bilan (Jan 2026)",
            AsOfDate = midProjectCutoff,
            Snapshot = _reportService.BuildSnapshot(project, null, midProjectCutoff)
        };
        _db.ProjectReports.Save(midReport);

        // Act 2: Project continues in March 2026 (more labor, more POs, status change)
        project.Status = "Completed";
        _db.Projects.Save(project);

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-2",
            EmployeeId = empBob.Id,
            EmployeeName = empBob.FullName,
            Year = 2026,
            WeekNumber = 10,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry
                {
                    Id = "te-2",
                    ProjectId = project.Id,
                    WorkTypeId = workType.Id,
                    Date = new DateTime(2026, 3, 4),
                    Hours = 35m,
                    IsBillable = true
                }
            ]
        });

        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-mar",
            PoNumber = "PO-2603",
            Date = new DateTime(2026, 3, 10),
            Status = PurchaseOrderStatus.Final,
            ApplyTax = false,
            Supplier = new Spokes_Server.Core.Models.Core.PartyContactInfo { BusinessName = "Keyence" },
            LineItems =
            [
                new PoItem
                {
                    Id = "poi-2",
                    ProjectId = project.Id,
                    PartNumber = "SR-1000",
                    Description = "Laser Sensor",
                    Quantity = 1m,
                    UnitPrice = 1200m
                }
            ]
        });

        // Generate end-of-project report as of 2026-03-31
        var endProjectCutoff = new DateTime(2026, 3, 31);
        var endReport = new ProjectReport
        {
            Id = "rpt-end",
            ProjectId = project.Id,
            ReportNumber = _db.ProjectReports.GenerateReportNumber("RPT"),
            Title = "Final Project Bilan (Mar 2026)",
            AsOfDate = endProjectCutoff,
            Snapshot = _reportService.BuildSnapshot(project, null, endProjectCutoff)
        };
        _db.ProjectReports.Save(endReport);

        // Assert: Mid-project report still has Jan 2026 figures and "In Progress" status
        var reloadedMid = _db.ProjectReports.GetById("rpt-mid")!;
        Assert.Equal("In Progress", reloadedMid.Snapshot.ProjectStatus);
        Assert.Equal(20m, reloadedMid.Snapshot.TotalLaborHours);
        Assert.Equal(1000m, reloadedMid.Snapshot.TotalLaborInternalCost); // 20h * $50/h
        Assert.Equal(500m, reloadedMid.Snapshot.TotalPurchaseCost);
        Assert.Single(reloadedMid.Snapshot.EmployeeLaborRows);
        Assert.Single(reloadedMid.Snapshot.PurchaseItems);

        // Assert: End-of-project report has full Jan-Mar 2026 figures and "Completed" status
        var reloadedEnd = _db.ProjectReports.GetById("rpt-end")!;
        Assert.Equal("Completed", reloadedEnd.Snapshot.ProjectStatus);
        Assert.Equal(55m, reloadedEnd.Snapshot.TotalLaborHours); // 20h + 35h
        Assert.Equal(3100m, reloadedEnd.Snapshot.TotalLaborInternalCost); // $1000 + (35h * $60)
        Assert.Equal(1700m, reloadedEnd.Snapshot.TotalPurchaseCost); // $500 + $1200
        Assert.Equal(2, reloadedEnd.Snapshot.EmployeeLaborRows.Count);
        Assert.Equal(2, reloadedEnd.Snapshot.PurchaseItems.Count);
    }

    [Fact]
    public void FiscalYearBilan_FiltersByPeriodStartDate_AndGroupsByEmployeeWeekAndPurchasesByMonth()
    {
        // Arrange: "100% du bilan Poly 2026 pour le projet Airbus, jusqu'à maintenant.
        // Idéalement, détailler par employé et par semaine, et les achats par mois."
        var emp1 = new Employee { Id = "e1", FirstName = "Jean", LastName = "Tremblay", HourlyRate = 45m };
        var emp2 = new Employee { Id = "e2", FirstName = "Marie", LastName = "Gagnon", HourlyRate = 55m };
        _db.Employees.Save(emp1);
        _db.Employees.Save(emp2);

        var wt = new WorkType { Id = "wt1", Name = "Automation", DefaultRate = 110m, CostRate = 50m };
        _db.WorkTypes.Save(wt);

        var project = new Project { Id = "proj-airbus-2026", Name = "Airbus", ProjectNumber = "AB-2026" };
        _db.Projects.Save(project);

        // 2025 timesheet (should be excluded from 2026 period entries, but included in CumulativeProjectHoursToAsOfDate)
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-2025",
            EmployeeId = emp1.Id,
            Year = 2025,
            WeekNumber = 50,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-2025", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2025, 12, 10), Hours = 15m }
            ]
        });

        // 2026 timesheets across Week 3 and Week 4
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-2026-w3",
            EmployeeId = emp1.Id,
            Year = 2026,
            WeekNumber = 3,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-26-1", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 1, 13), Hours = 12m }
            ]
        });
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-2026-w4",
            EmployeeId = emp2.Id,
            Year = 2026,
            WeekNumber = 4,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-26-2", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 1, 20), Hours = 18m }
            ]
        });

        // 2026 Purchases in January and February
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-26-01",
            PoNumber = "PO-101",
            Date = new DateTime(2026, 1, 18),
            Status = PurchaseOrderStatus.Final,
            ApplyTax = true,
            TaxRate = 0.10m,
            Supplier = new Spokes_Server.Core.Models.Core.PartyContactInfo { BusinessName = "Festo" },
            LineItems =
            [
                new PoItem { Id = "pi-1", ProjectId = project.Id, Description = "Pneumatic Valve", Quantity = 2m, UnitPrice = 100m }
            ]
        });
        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-26-02",
            PoNumber = "PO-102",
            Date = new DateTime(2026, 2, 12),
            Status = PurchaseOrderStatus.Final,
            ApplyTax = true,
            TaxRate = 0.10m,
            Supplier = new Spokes_Server.Core.Models.Core.PartyContactInfo { BusinessName = "SMC" },
            LineItems =
            [
                new PoItem { Id = "pi-2", ProjectId = project.Id, Description = "Cylinder", Quantity = 1m, UnitPrice = 400m }
            ]
        });

        // Act: Build snapshot for 2026-01-01 .. 2026-03-01
        var snapshot = _reportService.BuildSnapshot(
            project,
            new DateTime(2026, 1, 1),
            new DateTime(2026, 3, 1));

        var empWeeklyGroups = _reportService.BuildEmployeePeriodicGroups(snapshot, ProjectReportPeriodGranularity.Weekly);
        var weeklyMatrices = _reportService.BuildPeriodicLaborMatrices(snapshot, ProjectReportPeriodGranularity.Weekly);
        var monthlyPurchases = _reportService.BuildPurchaseGroups(snapshot, ProjectReportPurchasesGrouping.ByMonthDetailed);

        // Assert: 2025 hours excluded from period TotalLaborHours (30h), but tracked in CumulativeProjectHoursToAsOfDate (45h)
        Assert.Equal(30m, snapshot.TotalLaborHours);
        Assert.Equal(45m, snapshot.CumulativeProjectHoursToAsOfDate);

        // Assert: Employee weekly breakdown has Marie (18h, W04) and Jean (12h, W03)
        Assert.Equal(2, empWeeklyGroups.Count);
        Assert.Equal("Marie Gagnon", empWeeklyGroups[0].EmployeeName);
        Assert.Equal(18m, empWeeklyGroups[0].TotalHours);
        Assert.Single(empWeeklyGroups[0].Rows);
        Assert.Contains("2026-W04", empWeeklyGroups[0].Rows[0].PeriodKey);

        Assert.Equal("Jean Tremblay", empWeeklyGroups[1].EmployeeName);
        Assert.Equal(12m, empWeeklyGroups[1].TotalHours);
        Assert.Single(empWeeklyGroups[1].Rows);
        Assert.Contains("2026-W03", empWeeklyGroups[1].Rows[0].PeriodKey);

        // Assert: Weekly cross-tab matrix has 1 chunk (2 employees <= 7) and 2 weekly rows with running total
        Assert.Single(weeklyMatrices);
        Assert.Equal(2, weeklyMatrices[0].Rows.Count);
        Assert.Equal(12m, weeklyMatrices[0].Rows[0].RunningTotalHours);
        Assert.Equal(30m, weeklyMatrices[0].Rows[1].RunningTotalHours);

        // Assert: Purchases by month has 2 months (2026-01 and 2026-02)
        Assert.Equal(2, monthlyPurchases.Count);
        Assert.Equal("2026-01", monthlyPurchases[0].GroupKey);
        Assert.Equal(200m, monthlyPurchases[0].NetTotal);
        Assert.Equal(20m, monthlyPurchases[0].TaxAmount);
        Assert.Equal(220m, monthlyPurchases[0].GrandTotal);

        Assert.Equal("2026-02", monthlyPurchases[1].GroupKey);
        Assert.Equal(400m, monthlyPurchases[1].NetTotal);
        Assert.Equal(40m, monthlyPurchases[1].TaxAmount);
        Assert.Equal(440m, monthlyPurchases[1].GrandTotal);
    }

    [Fact]
    public void FinancialSummaryTaxBreakdowns_ComputesPreTaxTaxAndTotalCorrectly()
    {
        // Arrange
        var emp = new Employee { Id = "emp-tax", FirstName = "Luc", LastName = "Roy", HourlyRate = 50m };
        _db.Employees.Save(emp);

        var wt = new WorkType { Id = "wt-tax", Name = "Assembly", DefaultRate = 100m, CostRate = 50m };
        _db.WorkTypes.Save(wt);

        var project = new Project { Id = "proj-tax", Name = "Tax Breakdown Project", ProjectNumber = "TX-100" };
        _db.Projects.Save(project);

        _db.Quotes.Save(new Quote
        {
            Id = "q-tax",
            ProjectId = project.Id,
            QuoteNumber = "Q-TAX-1",
            Date = new DateTime(2026, 1, 10),
            Status = QuoteStatus.Accepted,
            TaxRate = 0.15m,
            LaborItems =
            [
                new QuoteLaborItem { WorkTypeId = wt.Id, WorkTypeName = "Assembly", Hours = 100m, Rate = 100m }
            ]
        });

        _db.Invoices.Save(new Invoice
        {
            Id = "inv-tax",
            ProjectId = project.Id,
            InvoiceNumber = "INV-TAX-1",
            Date = new DateTime(2026, 2, 1),
            DueDate = new DateTime(2026, 3, 1),
            Status = InvoiceStatus.Final,
            TaxRate = 0.15m,
            Payments =
            [
                new PaymentRecord { Amount = 5000m, Date = new DateTime(2026, 2, 10) }
            ],
            LineItems =
            [
                new InvoiceLine { Description = "Milestone 1", Quantity = 1m, UnitPrice = 8000m }
            ]
        });

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-tax",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 5,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-tax", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 1, 28), Hours = 10m }
            ]
        });

        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-tax",
            PoNumber = "PO-TAX-1",
            Date = new DateTime(2026, 1, 25),
            Status = PurchaseOrderStatus.Final,
            ApplyTax = true,
            TaxRate = 0.15m,
            Supplier = new Spokes_Server.Core.Models.Core.PartyContactInfo { BusinessName = "Supplier Inc." },
            LineItems =
            [
                new PoItem { Id = "poi-tax", ProjectId = project.Id, Description = "Frame", Quantity = 1m, UnitPrice = 2000m }
            ]
        });

        // Act
        var snapshot = _reportService.BuildSnapshot(project, null, new DateTime(2026, 3, 1));

        // Assert
        // Quote: $10,000 subtotal + $1,500 tax = $11,500 total
        Assert.Equal(10000m, snapshot.GetExpectedRevenueSubTotal());
        Assert.Equal(1500m, snapshot.GetExpectedRevenueTax());
        Assert.Equal(11500m, snapshot.ExpectedRevenue);

        // Invoice: $8,000 subtotal + $1,200 tax = $9,200 total
        Assert.Equal(8000m, snapshot.GetTotalInvoicedSubTotal());
        Assert.Equal(1200m, snapshot.GetTotalInvoicedTax());
        Assert.Equal(9200m, snapshot.TotalInvoiced);

        // Costs: Labor = 10h * $50 = $500 (no tax); Purchases = $2,000 net + $300 tax = $2,300 total
        Assert.Equal(2500m, snapshot.TotalInternalSpentPreTax);
        Assert.Equal(300m, snapshot.TotalInternalSpentTax);
        Assert.Equal(2800m, snapshot.TotalInternalSpent);

        // Profit:
        // Pre-tax base revenue = $10,000, Pre-tax cost = $2,500 -> NetProfitPreTax = $7,500 (75%)
        // Inc-tax base revenue = $11,500, Inc-tax cost = $2,800 -> NetProfit = $8,700
        Assert.Equal(7500m, snapshot.NetProfitPreTax);
        Assert.Equal(75m, snapshot.NetMarginPreTaxPercent);
        Assert.Equal(8700m, snapshot.NetProfit);
    }

    [Fact]
    public void BuildSnapshot_IncludesEmployerContributionsInEmployeeTotalHourlyCost()
    {
        // Arrange: $50/h * 40h/w * 52w = $104,000 annual gross (2,080 hours/year)
        var emp = new Employee
        {
            Id = "emp-contrib",
            FirstName = "Jolain",
            LastName = "Dallaire",
            HourlyRate = 50m,
            WeeklyHours = 40m
        };
        _db.Employees.Save(emp);

        // 10% global contribution (+$5/h) + $2,080 fixed employee contribution (+$1/h) = +$6/h ($56/h total cost)
        _db.EmployerContributions.Save(new EmployerContribution
        {
            Id = "ec-pct",
            Name = "RRQ / RQAP / FSS",
            Type = EmployerContributionType.Percentage,
            Value = 10m,
            Scope = EmployerContributionScope.Global
        });
        _db.EmployerContributions.Save(new EmployerContribution
        {
            Id = "ec-fix",
            Name = "Group Insurance",
            Type = EmployerContributionType.FixedAmount,
            Value = 2080m,
            Scope = EmployerContributionScope.Employee,
            TargetIds = [emp.Id]
        });

        var wt = new WorkType { Id = "wt-c", Name = "Engineering", DefaultRate = 120m, CostRate = 50m };
        _db.WorkTypes.Save(wt);

        var project = new Project { Id = "proj-contrib", Name = "Transfert", ProjectNumber = "KON701B" };
        _db.Projects.Save(project);

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-contrib",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 6,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-c", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 2, 4), Hours = 10m }
            ]
        });

        // Act
        var snapshot = _reportService.BuildSnapshot(project, null, new DateTime(2026, 3, 1));

        // Assert
        var empRow = Assert.Single(snapshot.EmployeeLaborRows);
        Assert.Equal(50m, empRow.BaseHourlyRate);
        Assert.Equal(6m, empRow.EmployerContributionHourlyRate);
        Assert.Equal(56m, empRow.EffectiveInternalRate);
        Assert.Equal(560m, empRow.InternalCost);
        Assert.Equal(560m, snapshot.TotalLaborInternalCost);
    }

    [Fact]
    public void BuildSnapshot_MarksInvoicesPaidWhenPaidOnOrBeforeAsOfDate_OrWithinRoundingTolerance()
    {
        // Arrange
        var project = new Project { Id = "proj-inv-paid", Name = "Cosinus", ProjectNumber = "KON701B" };
        _db.Projects.Save(project);

        // Invoice 1: Paid via Payments list with 1-cent tax rounding difference ($14,946.74 vs $14,946.75)
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-0046",
            ProjectId = project.Id,
            InvoiceNumber = "F-2606-0046",
            Date = new DateTime(2026, 6, 8),
            DueDate = new DateTime(2026, 7, 8),
            Status = InvoiceStatus.Paid,
            DatePaid = new DateTime(2026, 6, 11),
            TaxRate = 0.14975m,
            Payments =
            [
                new PaymentRecord { Amount = 14946.74m, Date = new DateTime(2026, 6, 11) }
            ],
            LineItems =
            [
                new InvoiceLine { Description = "Progress Billing 1", Quantity = 1m, UnitPrice = 13000m }
            ]
        });

        // Invoice 2: Marked Paid directly via Status + DatePaid without Payments records
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-0051",
            ProjectId = project.Id,
            InvoiceNumber = "F-2606-0051",
            Date = new DateTime(2026, 6, 29),
            DueDate = new DateTime(2026, 7, 29),
            Status = InvoiceStatus.Paid,
            DatePaid = new DateTime(2026, 7, 9),
            TaxRate = 0.14975m,
            LineItems =
            [
                new InvoiceLine { Description = "Progress Billing 2", Quantity = 1m, UnitPrice = 13000m }
            ]
        });

        // Act
        var snapshot = _reportService.BuildSnapshot(project, null, new DateTime(2026, 11, 11));

        // Assert
        Assert.Equal(2, snapshot.Invoices.Count);
        Assert.All(snapshot.Invoices, inv =>
        {
            Assert.Equal(InvoiceStatus.Paid, inv.Status);
            Assert.Equal(14946.75m, inv.GrandTotal);
            Assert.Equal(14946.75m, inv.AmountPaid);
            Assert.Equal(0m, inv.BalanceDue);
        });
    }

    [Fact]
    public void ResolveTemplateCulture_FormatsMonthsInFrench_WhenTemplateHasFrenchLanguageOrLabels()
    {
        // Arrange: Template where LanguageCode was left at default "en" but French labels were configured
        var frenchTemplate = new Spokes_Server.Core.Models.Documents.DocumentTemplate
        {
            LanguageCode = "en",
            Label_Month = "Mois",
            Label_ProjectReport = "Rapport de projet"
        };

        var snapshot = new ProjectReportSnapshot
        {
            PurchaseItems =
            [
                new ReportPurchaseItemSnapshot { Date = new DateTime(2026, 5, 15), PoNumber = "PO-01", NetTotal = 100m },
                new ReportPurchaseItemSnapshot { Date = new DateTime(2026, 6, 10), PoNumber = "PO-02", NetTotal = 200m }
            ]
        };

        // Act
        var culture = ProjectReportService.ResolveTemplateCulture(frenchTemplate);
        var groups = _reportService.BuildPurchaseGroups(snapshot, ProjectReportPurchasesGrouping.ByMonthSummaryOnly, culture);

        // Assert
        Assert.StartsWith("fr", culture.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, groups.Count);
        Assert.Equal("Mai 2026", groups[0].GroupTitle);
        Assert.Equal("Juin 2026", groups[1].GroupTitle);
    }

    [Fact]
    public void BuildSnapshot_CalculatesQuoteInternalCostAndMaterialTaxes_ForQuoteVsActualComparison()
    {
        // Arrange: Employee with $50/h + 10% employer contribution = $55/h internal cost
        var emp = new Employee
        {
            Id = "emp-qva",
            FirstName = "Jolain",
            LastName = "Dallaire",
            HourlyRate = 50m,
            WeeklyHours = 40m,
            IsActive = true
        };
        _db.Employees.Save(emp);

        _db.EmployerContributions.Save(new EmployerContribution
        {
            Id = "ec-qva",
            Name = "Employer Taxes",
            Type = EmployerContributionType.Percentage,
            Value = 10m,
            Scope = EmployerContributionScope.Global
        });

        var wt = new WorkType { Id = "wt-qva", Name = "Robotics", DefaultRate = 120m, CostRate = 50m };
        _db.WorkTypes.Save(wt);

        var project = new Project { Id = "proj-qva", Name = "Transfert", ProjectNumber = "KON701B" };
        _db.Projects.Save(project);

        // Quote:
        // - 100h quoted @ $120/h sell rate ($12,000 sell), internal cost should be 100h * $55/h = $5,500
        // - Material item: Qty 2, UnitCost = $1,000, Markup = 25% -> UnitPrice = $1,250 ($2,500 sell)
        //   Internal Material Cost Net = 2 * $1,000 = $2,000 (excludes sell markup!)
        //   Material Cost Tax (15%) = $300 -> Material Cost Incl. Tax = $2,300
        // - Client-bought item: excluded from our internal cost
        _db.Quotes.Save(new Quote
        {
            Id = "q-qva",
            ProjectId = project.Id,
            QuoteNumber = "Q-2601-0099",
            Date = new DateTime(2026, 1, 15),
            Status = QuoteStatus.Accepted,
            ShowTax = true,
            TaxRate = 0.15m,
            LaborItems =
            [
                new QuoteLaborItem { WorkTypeId = wt.Id, Hours = 100m, Rate = 120m }
            ],
            MaterialLineItems =
            [
                new QuoteItemRow { Description = "Servo Drive", Quantity = 2m, UnitCost = 1000m, MarkupPercentage = 25m, UnitPrice = 1250m },
                new QuoteItemRow { Description = "Client PLC", Quantity = 1m, UnitCost = 5000m, UnitPrice = 5000m, IsClientBought = true }
            ]
        });

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-qva",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 5,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-qva", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 1, 28), Hours = 80m }
            ]
        });

        // Act
        var snapshot = _reportService.BuildSnapshot(project, null, new DateTime(2026, 3, 1));

        // Assert
        Assert.True(snapshot.HasQuoteBudget);
        Assert.Equal(100m, snapshot.TotalQuotedHours);
        Assert.Equal(5500m, snapshot.QuotedLaborCost); // 100h * $55/h internal cost (not $12,000 sell price)
        Assert.Equal(2000m, snapshot.QuotedMaterialCostNet); // 2 * $1,000 unit cost (not $2,500 sell price)
        Assert.Equal(300m, snapshot.QuotedMaterialCostTax); // 15% tax on $2,000 material cost
        Assert.Equal(2300m, snapshot.QuotedMaterialCostIncTax);
        Assert.Equal(7500m, snapshot.QuotedTotalCostPreTax); // $5,500 + $2,000
        Assert.Equal(7800m, snapshot.QuotedTotalCostIncTax); // $5,500 + $2,300
    }

    [Fact]
    public void BuildSnapshot_AllocatesMonthlyProratedBusinessOverhead_ByDirectClientHoursShare()
    {
        // Arrange:
        // Employee @ $50/h (no extra contributions for simplicity)
        var emp = new Employee
        {
            Id = "emp-ovh",
            FirstName = "Marc",
            LastName = "Gagnon",
            HourlyRate = 50m,
            WeeklyHours = 40m,
            IsActive = true
        };
        _db.Employees.Save(emp);

        var wt = new WorkType { Id = "wt-ovh", Name = "Engineering", DefaultRate = 150m, CostRate = 50m };
        _db.WorkTypes.Save(wt);

        // Two external client projects + one internal project
        var projectA = new Project { Id = "proj-client-a", Name = "Client A", ProjectNumber = "P-A", IsInternal = false };
        var projectB = new Project { Id = "proj-client-b", Name = "Client B", ProjectNumber = "P-B", IsInternal = false };
        var internalProject = new Project { Id = "proj-internal", Name = "R&D & Admin", ProjectNumber = "INT-01", IsInternal = true };
        _db.Projects.Save(projectA);
        _db.Projects.Save(projectB);
        _db.Projects.Save(internalProject);

        // Invoice on Project A: $20,000 pre-tax
        _db.Invoices.Save(new Invoice
        {
            Id = "inv-ovh-a",
            ProjectId = projectA.Id,
            InvoiceNumber = "INV-A1",
            Date = new DateTime(2026, 3, 15),
            DueDate = new DateTime(2026, 4, 15),
            Status = InvoiceStatus.Sent,
            TaxRate = 0m,
            LineItems = [new InvoiceLine { Description = "Engineering", Quantity = 1m, UnitPrice = 20000m }]
        });

        // March 2026 Overhead Sources:
        // 1. Monthly Rent = $2,000/month (Tax Exempt)
        _db.RecurringExpenses.Save(new RecurringExpense
        {
            Id = "rec-rent",
            Name = "Shop Rent",
            Amount = 2000m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsTaxExempt = true,
            IsEstimate = false,
            CreatedDate = new DateTime(2026, 1, 1)
        });

        // 2. Standalone Overhead Bill (IsBusinessOverhead = true) for $1,200 pre-tax in Feb 2026 -> $100/month amortized
        _db.Bills.Save(new Bill
        {
            Id = "bill-ovh-1",
            SupplierName = "Accounting Firm",
            Description = "Annual Corporate Tax Filing",
            Date = new DateTime(2026, 2, 10),
            DueDate = new DateTime(2026, 3, 10),
            SubTotal = 1200m,
            TaxAmount = 0m,
            Amount = 1200m,
            IsTaxExempt = true,
            IsBusinessOverhead = true,
            Status = BillStatus.Paid
        });

        // 3. March 2026 Timesheets:
        // - Project A: 60h direct ($3,000 direct labor cost)
        // - Project B: 40h direct (Total Company Direct Client Hours = 100h -> Project A share = 60%)
        // - Internal Project: 18h internal @ $50/h = $900 internal labor overhead
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-ovh-mar",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 11,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-a", ProjectId = projectA.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 3, 10), Hours = 60m },
                new TimeEntry { Id = "te-b", ProjectId = projectB.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 3, 11), Hours = 40m },
                new TimeEntry { Id = "te-int", ProjectId = internalProject.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 3, 12), Hours = 18m }
            ]
        });

        // Act: Build snapshot for Project A for March 2026
        var snapshotA = _reportService.BuildSnapshot(
            projectA,
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31));

        // Assert:
        // Company Overhead Pool in March 2026 = $2,000 (Rent) + $100 (Amortized Bill) + $900 (Internal Project Labor) = $3,000
        // Direct Client Hours = 60h (Project A) + 40h (Project B) = 100h -> Project A Share = 60%
        // Allocated Overhead to Project A = 60% of $3,000 = $1,800
        // Direct Project Profit = $20,000 - $3,000 = $17,000 (85%)
        // Net Profit After Overhead = $17,000 - $1,800 = $15,200 (76%)
        Assert.Equal(100m, snapshotA.TotalCompanyDirectLaborHours);
        Assert.Equal(60m, snapshotA.OverheadAllocationSharePercent);
        Assert.Equal(3000m, snapshotA.CompanyOverheadPoolPreTax);
        Assert.Equal(1800m, snapshotA.AllocatedOverheadPreTax);
        Assert.Equal(17000m, snapshotA.NetProfitPreTax);
        Assert.Equal(15200m, snapshotA.NetProfitAfterOverheadPreTax);
        Assert.Equal(76.0m, snapshotA.NetMarginAfterOverheadPreTaxPercent);
        Assert.Equal(3, snapshotA.OverheadRows.Count);

        // Internal projects should NOT absorb overhead
        var snapshotInternal = _reportService.BuildSnapshot(
            internalProject,
            new DateTime(2026, 3, 1),
            new DateTime(2026, 3, 31));
        Assert.Equal(0m, snapshotInternal.AllocatedOverheadPreTax);
        Assert.Empty(snapshotInternal.OverheadRows);
    }

    [Fact]
    public void BuildSnapshot_PopulatesBaseHourlyRateAndContributionsForSalariedEmployee()
    {
        var emp = new Employee
        {
            Id = "emp-salaried",
            FirstName = "Marc",
            LastName = "Tremblay",
            HourlyRate = 0m,
            WeeklyHours = 40m,
            Payroll = new EmployeePayrollProfile
            {
                CompensationType = PayrollCompensationType.Salaried,
                AnnualSalary = 104000m // $50/h over 2,080h/yr
            }
        };
        _db.Employees.Save(emp);

        _db.EmployerContributions.Save(new EmployerContribution
        {
            Id = "ec-sal-pct",
            Name = "Employer Burden",
            Type = EmployerContributionType.Percentage,
            Value = 10m, // $5/h on $50/h
            Scope = EmployerContributionScope.Global
        });

        var wt = new WorkType { Id = "wt-sal", Name = "Design", DefaultRate = 120m, CostRate = 40m };
        _db.WorkTypes.Save(wt);

        var project = new Project { Id = "proj-sal", Name = "Salaried Split", ProjectNumber = "SAL001" };
        _db.Projects.Save(project);

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-sal",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 7,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "te-sal", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 2, 11), Hours = 10m }
            ]
        });

        var snapshot = _reportService.BuildSnapshot(project, null, new DateTime(2026, 3, 1));

        var empRow = Assert.Single(snapshot.EmployeeLaborRows);
        Assert.Equal(50m, empRow.BaseHourlyRate);
        Assert.Equal(5m, empRow.EmployerContributionHourlyRate);
        Assert.Equal(55m, empRow.EffectiveInternalRate);
        Assert.Equal(550m, empRow.InternalCost);
        Assert.Equal(550m, snapshot.TotalLaborInternalCost);
    }

    [Fact]
    public void BuildSnapshot_ExcludesDraftExpenses_AndScopeSnapshotForViewer_RedactsSensitiveFieldsWithoutMutatingSource()
    {
        var profile = _db.CompanyProfile.Get();
        profile.Privacy ??= new PrivacyConfig();
        profile.Privacy.BaseScope = NetworkScope.SameTeam;
        _db.CompanyProfile.Save(profile);

        var teamOpen = new Team { Id = "team-open", Name = "Open Team" };
        var teamSecret = new Team { Id = "team-secret", Name = "Secret Team" };
        _db.Teams.Save(teamOpen);
        _db.Teams.Save(teamSecret);

        var pm = new Employee
        {
            Id = "emp-pm",
            FirstName = "Paula",
            LastName = "Manager",
            Email = "private.pm@example.com",
            IsEmailPublic = false,
            IsActive = true,
            TeamId = teamOpen.Id
        };
        var hiddenWorker = new Employee
        {
            Id = "emp-hidden-worker",
            FirstName = "Secret",
            LastName = "Contributor",
            Email = "secret@example.com",
            IsEmailPublic = false,
            IsActive = true,
            HourlyRate = 75m,
            TeamId = teamSecret.Id
        };
        var viewer = new Employee
        {
            Id = "emp-viewer-scoped",
            FirstName = "Viewer",
            LastName = "Only",
            IsActive = true,
            TeamId = teamOpen.Id,
            Permissions = [AppPermissions.Projects.View]
        };
        _db.Employees.Save(pm);
        _db.Employees.Save(hiddenWorker);
        _db.Employees.Save(viewer);

        var wt = new WorkType { Id = "wt-scope", Name = "Build", DefaultRate = 150m, CostRate = 60m };
        _db.WorkTypes.Save(wt);

        var project = new Project
        {
            Id = "proj-scope",
            Name = "Scoped Report",
            ProjectNumber = "SCP-1",
            ProjectManagerId = pm.Id,
            Allocations = [new ProjectTaskAllocation { WorkTypeId = wt.Id, QuotedHours = 20m }]
        };
        _db.Projects.Save(project);

        _db.PurchaseOrders.Save(new PurchaseOrder
        {
            Id = "po-scope",
            PoNumber = "PO-SCP-1",
            Status = PurchaseOrderStatus.Final,
            Date = new DateTime(2026, 3, 5),
            Supplier = new PartyContactInfo { BusinessName = "Vendor X" },
            LineItems = [new PoItem { Description = "Motors", Quantity = 1m, UnitPrice = 400m, ProjectId = project.Id }]
        });

        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-scope-sub",
            ReportNumber = "EXP-SCP-SUB",
            EmployeeId = pm.Id,
            EmployeeName = pm.FullName,
            DateSubmitted = new DateTime(2026, 3, 6),
            LineItems = [new ExpenseItem { ProjectId = project.Id, Date = new DateTime(2026, 3, 6), Description = "Travel", Amount = 120m }]
        });
        _db.ExpenseReports.Save(new ExpenseReport
        {
            Id = "exp-scope-draft",
            ReportNumber = "EXP-SCP-DRAFT",
            EmployeeId = pm.Id,
            EmployeeName = pm.FullName,
            DateSubmitted = null,
            LineItems = [new ExpenseItem { ProjectId = project.Id, Date = new DateTime(2026, 3, 7), Description = "Draft", Amount = 999m }]
        });

        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-scope-hidden",
            EmployeeId = hiddenWorker.Id,
            EmployeeName = hiddenWorker.FullName,
            Year = 2026,
            WeekNumber = 10,
            Status = TimesheetStatus.Approved,
            Entries = [new TimeEntry { Id = "te-hidden", ProjectId = project.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 3, 4), Hours = 8m, Description = "Secret work" }]
        });

        var source = _reportService.BuildSnapshot(project, new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        // Draft expense report and non-public PM email are excluded even when building a new snapshot
        Assert.DoesNotContain(source.ExpenseItems, e => e.ExpenseReportId == "exp-scope-draft");
        Assert.Single(source.ExpenseItems, e => e.ExpenseReportId == "exp-scope-sub");
        Assert.Equal(string.Empty, source.ProjectManagerEmail);

        // Simulate an older persisted snapshot that still carried a PM email before masking
        source.ProjectManagerEmail = "private.pm@example.com";

        var privacyService = new UserPrivacyService(_db.CompanyProfile, _db.Employees, _db.Teams);
        var hrService = new HRService(_db.HrContext);

        var redacted = _reportService.ScopeSnapshotForViewer(
            source,
            viewer,
            privacyService,
            hrService,
            _db.Employees.GetAll(),
            _db.Teams.GetAll(),
            canViewPurchases: false,
            canViewExpenses: false,
            canViewLaborFinancials: false,
            canViewInternalCosts: false);

        Assert.Equal(string.Empty, redacted.ProjectManagerEmail);
        Assert.Empty(redacted.PurchaseItems);
        Assert.Empty(redacted.ExpenseItems);
        Assert.Equal(0m, redacted.TotalPurchaseNet);
        Assert.Equal(0m, redacted.TotalExpenseSubTotal);
        Assert.Equal(0m, redacted.TotalCommissions);
        Assert.Equal(0m, redacted.TotalLaborInternalCost);
        Assert.Equal(0m, redacted.TotalLaborBillableValue);
        Assert.Equal(0m, redacted.UnbilledLaborValue);
        Assert.Equal(0m, redacted.UnbilledExpensesValue);

        var scopedEntry = Assert.Single(redacted.TimeEntries);
        Assert.Equal(ProjectLaborCalculator.RestrictedContributorBucketId, scopedEntry.EmployeeId);
        Assert.Equal("Restricted Contributors", scopedEntry.EmployeeName);
        Assert.Equal(string.Empty, scopedEntry.Description);
        Assert.Empty(redacted.EmployeeLaborRows);
        Assert.All(redacted.TaskLaborRows, r => Assert.Empty(r.ContributorNames));

        // Source snapshot remains untouched
        Assert.Equal("private.pm@example.com", source.ProjectManagerEmail);
        Assert.Single(source.PurchaseItems);
        Assert.Single(source.ExpenseItems);
        Assert.True(source.TotalLaborInternalCost > 0m);
        Assert.Single(source.EmployeeLaborRows);
    }
}
