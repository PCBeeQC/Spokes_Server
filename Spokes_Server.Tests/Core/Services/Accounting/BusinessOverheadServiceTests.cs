namespace Spokes_Server.Tests.Core.Services.Accounting;

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Accounting;
using Xunit;

public class BusinessOverheadServiceTests : TestDataTestBase
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly IBusinessOverheadService _overheadService;

    public BusinessOverheadServiceTests()
    {
        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataPath);
        mockConfig.Setup(c => c["SPOKES_MASTER_PASSWORD"]).Returns("UnitTest-Master-Password-2026!");
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();
        services.AddScoped<IBusinessOverheadService, BusinessOverheadService>();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();
        _overheadService = _serviceProvider.GetRequiredService<IBusinessOverheadService>();
    }

    public override void Dispose()
    {
        _serviceProvider.Dispose();
        base.Dispose();
    }

    [Fact]
    public void GetRecurringProfileMonthlyPreTax_ProratesAllFrequenciesCorrectly()
    {
        var monthly = new RecurringExpense
        {
            Name = "Rent",
            Amount = 1200m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 1, 1)
        };
        var quarterly = new RecurringExpense
        {
            Name = "Quarterly Insurance",
            Amount = 900m,
            Frequency = RecurringExpenseFrequency.Quarterly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 1, 1)
        };
        var yearly = new RecurringExpense
        {
            Name = "CAD Annual Subscription",
            Amount = 3600m,
            Frequency = RecurringExpenseFrequency.Yearly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 1, 1)
        };
        var weekly = new RecurringExpense
        {
            Name = "Shop Cleaning",
            Amount = 120m,
            Frequency = RecurringExpenseFrequency.Weekly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 1, 1)
        };

        Assert.Equal(1200m, _overheadService.GetRecurringProfileMonthlyPreTax(monthly, 2026, 3));
        Assert.Equal(300m, _overheadService.GetRecurringProfileMonthlyPreTax(quarterly, 2026, 3));
        Assert.Equal(300m, _overheadService.GetRecurringProfileMonthlyPreTax(yearly, 2026, 3));
        Assert.Equal(520m, _overheadService.GetRecurringProfileMonthlyPreTax(weekly, 2026, 3)); // 120 * 52 / 12 = 520
    }

    [Fact]
    public void CalculatePeriodOverhead_AmortizesOneTimeOverheadBillsOver12Months_AndStopsAfterMonth12()
    {
        // Standalone overhead bill in Jan 2026 for $2,400 pre-tax -> $200/month from Jan 2026 through Dec 2026
        _db.Bills.Save(new Bill
        {
            Id = "b-amort",
            SupplierName = "Legal Counsel",
            Description = "Corporate Restructuring",
            Date = new DateTime(2026, 1, 15),
            DueDate = new DateTime(2026, 2, 15),
            SubTotal = 2400m,
            TaxAmount = 360m,
            Amount = 2760m,
            IsBusinessOverhead = true,
            Status = BillStatus.Paid
        });

        // Non-overhead standalone bill (IsBusinessOverhead = false) should NOT be included
        _db.Bills.Save(new Bill
        {
            Id = "b-non-ovh",
            SupplierName = "Project Supplier",
            Description = "Unlinked Project Material",
            Date = new DateTime(2026, 1, 20),
            DueDate = new DateTime(2026, 2, 20),
            SubTotal = 5000m,
            TaxAmount = 0m,
            Amount = 5000m,
            IsBusinessOverhead = false,
            Status = BillStatus.Unpaid
        });

        // Active in June 2026 (Month 6 of 12) -> $200 pre-tax, $30 tax
        var actor = new Employee { Id = "overhead-admin", FirstName = "Overhead", LastName = "Admin", IsActive = true, IsAdmin = true };
        var juneResult = _overheadService.CalculatePeriodOverhead(
            new DateTime(2026, 6, 1),
            new DateTime(2026, 6, 30),
            actor: actor);
        Assert.Equal(200m, juneResult.OneTimeAmortizedPreTax);
        Assert.Equal(30m, juneResult.OneTimeAmortizedTax);

        // Full Q1 2026 (Jan, Feb, Mar = 3 months) -> $600 pre-tax, $90 tax
        var q1Result = _overheadService.CalculatePeriodOverhead(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 3, 31),
            actor: actor);
        Assert.Equal(600m, q1Result.OneTimeAmortizedPreTax);
        Assert.Equal(90m, q1Result.OneTimeAmortizedTax);

        // Expired in Jan 2027 (Month 13) -> $0
        var jan2027Result = _overheadService.CalculatePeriodOverhead(
            new DateTime(2027, 1, 1),
            new DateTime(2027, 1, 31),
            actor: actor);
        Assert.Equal(0m, jan2027Result.OneTimeAmortizedPreTax);
    }

    [Fact]
    public void NormalizeLegacyEstimates_MergesMatchingProfiles_AndConvertsRemainingEstimates()
    {
        var estimate = new RecurringExpense
        {
            Id = "est-hydro",
            Name = "Hydro-Quebec",
            Supplier = "Hydro-Québec",
            Amount = 450m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsEstimate = true
        };
        var actual = new RecurringExpense
        {
            Id = "act-hydro",
            Name = "Hydro-Quebec",
            Supplier = "",
            Amount = 0m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsEstimate = false
        };
        var standaloneEstimate = new RecurringExpense
        {
            Id = "est-internet",
            Name = "Bell Fiber",
            Supplier = "Bell",
            Amount = 150m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsEstimate = true
        };

        _db.RecurringExpenses.Save(estimate);
        _db.RecurringExpenses.Save(actual);
        _db.RecurringExpenses.Save(standaloneEstimate);

        // Act
        _overheadService.NormalizeLegacyEstimates(new Employee { Id = "overhead-admin", IsActive = true, IsAdmin = true });

        // Assert
        var all = _db.RecurringExpenses.GetAll();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, r => r.IsEstimate);

        var mergedHydro = _db.RecurringExpenses.GetById("act-hydro")!;
        Assert.Equal(450m, mergedHydro.Amount);
        Assert.Equal("Hydro-Québec", mergedHydro.Supplier);

        var normalizedInternet = _db.RecurringExpenses.GetById("est-internet")!;
        Assert.False(normalizedInternet.IsEstimate);
        Assert.Equal(150m, normalizedInternet.Amount);
    }

    [Fact]
    public void CalculatePeriodOverhead_AllocatesMonthByMonth_ExcludesZeroHourTrailingMonths_AndSplitsFixedVsInternal()
    {
        var emp = new Employee { Id = "emp-m", FirstName = "A", LastName = "B", HourlyRate = 50m, IsActive = true };
        _db.Employees.Save(emp);
        var wt = new WorkType { Id = "wt-m", Name = "Eng", CostRate = 50m };
        _db.WorkTypes.Save(wt);

        var projA = new Project { Id = "proj-a", Name = "Project A", ProjectNumber = "P-A", IsInternal = false };
        var projB = new Project { Id = "proj-b", Name = "Project B", ProjectNumber = "P-B", IsInternal = false };
        var projInt = new Project { Id = "proj-int", Name = "Admin", ProjectNumber = "INT-1", IsInternal = true };
        _db.Projects.Save(projA);
        _db.Projects.Save(projB);
        _db.Projects.Save(projInt);

        // Monthly rent $1,000 (tax-exempt) across Sept, Oct, Nov
        _db.RecurringExpenses.Save(new RecurringExpense
        {
            Id = "rec-rent-m",
            Name = "Rent",
            Amount = 1000m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 1, 1)
        });

        // Sept: A = 20h, B = 80h (A = 20%), Int = 10h ($500) -> Fixed $1,000, Int $500 -> A gets 20% = $200 Fixed + $100 Int
        // Oct:  A = 80h, B = 20h (A = 80%), Int = 0h -> Fixed $1,000, Int $0 -> A gets 80% = $800 Fixed + $0 Int
        // Nov:  A = 0h,  B = 50h (A = 0%),  Int = 20h ($1000) -> Project A had 0 hours in Nov, so Nov is excluded completely!
        _db.Timesheets.Save(new Timesheet
        {
            Id = "ts-m",
            EmployeeId = emp.Id,
            Year = 2026,
            WeekNumber = 36,
            Status = TimesheetStatus.Approved,
            Entries =
            [
                new TimeEntry { Id = "s1", ProjectId = projA.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 9, 10), Hours = 20m },
                new TimeEntry { Id = "s2", ProjectId = projB.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 9, 11), Hours = 80m },
                new TimeEntry { Id = "s3", ProjectId = projInt.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 9, 12), Hours = 10m },
                new TimeEntry { Id = "o1", ProjectId = projA.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 10, 10), Hours = 80m },
                new TimeEntry { Id = "o2", ProjectId = projB.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 10, 11), Hours = 20m },
                new TimeEntry { Id = "n1", ProjectId = projB.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 11, 5), Hours = 50m },
                new TimeEntry { Id = "n2", ProjectId = projInt.Id, WorkTypeId = wt.Id, Date = new DateTime(2026, 11, 6), Hours = 20m }
            ]
        });

        var res = _overheadService.CalculatePeriodOverhead(
            new DateTime(2026, 9, 1),
            new DateTime(2026, 11, 30),
            projA.Id,
            actor: new Employee { Id = "overhead-admin", IsActive = true, IsAdmin = true });

        Assert.Equal(2, res.MonthCount); // Only Sept & Oct (Nov excluded since A had 0h)
        Assert.Equal(2000m, res.FixedOverheadPoolPreTax);
        Assert.Equal(500m, res.InternalCostsPoolPreTax);
        Assert.Equal(1000m, res.AllocatedFixedOverheadPreTax); // $200 (Sep) + $800 (Oct)
        Assert.Equal(100m, res.AllocatedInternalCostsPreTax);  // $100 (Sep) + $0 (Oct)
        Assert.Equal(1100m, res.AllocatedOverheadPreTax);
    }

    [Fact]
    public void CalculatePeriodOverhead_DoesNotExtrapolateZeroAmountProfileBackward_AndRespectsPartialWindowDates()
    {
        // Zero-Amount historical tracker: only bills in Sept ($300) and Oct ($200) -> Mar..Aug & Nov must be $0
        _db.RecurringExpenses.Save(new RecurringExpense
        {
            Id = "rec-hist-only",
            Name = "Bell Internet Affaire",
            Supplier = "Bell",
            Amount = 0m,
            Frequency = RecurringExpenseFrequency.Monthly,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 10, 14),
            History =
            [
                new RecurringBillRecord { Date = new DateTime(2026, 9, 11), Amount = 300m },
                new RecurringBillRecord { Date = new DateTime(2026, 10, 9), Amount = 200m }
            ]
        });

        // As-Needed bill dated BEFORE partial start cutoff (2026-03-23 < 2026-03-25) -> must be excluded
        _db.RecurringExpenses.Save(new RecurringExpense
        {
            Id = "rec-as-needed",
            Name = "BV Comm",
            Amount = 0m,
            Frequency = RecurringExpenseFrequency.AsNeeded,
            IsTaxExempt = true,
            CreatedDate = new DateTime(2026, 3, 23),
            History =
            [
                new RecurringBillRecord { Date = new DateTime(2026, 3, 23), Amount = 100m },
                new RecurringBillRecord { Date = new DateTime(2026, 9, 16), Amount = 150m }
            ]
        });

        var res = _overheadService.CalculatePeriodOverhead(
            new DateTime(2026, 3, 25),
            new DateTime(2026, 11, 12),
            actor: new Employee { Id = "overhead-admin", IsActive = true, IsAdmin = true });

        // Expected: Bell Internet Affaire = $300 + $200 = $500; BV Comm = $150 only (Mar 23 excluded) -> $650 total
        Assert.Equal(650m, res.RecurringFixedPreTax);
    }
}
