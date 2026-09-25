namespace Spokes_Server.Tests.Core.Services.Reports;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MudBlazor;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Reports;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.Reports;
using Xunit;

public class TimesheetReportServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly ServiceProvider _serviceProvider;
    private readonly Database _db;
    private readonly TimesheetReportService _service;

    public TimesheetReportServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Reports_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var services = new ServiceCollection();

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);
        services.AddSingleton<IConfiguration>(mockConfig.Object);

        services.AddLogging(builder => builder.AddConsole());
        services.AddSingleton<EncryptionService>();
        services.AddSpokesDatabase();

        _serviceProvider = services.BuildServiceProvider();
        _db = _serviceProvider.GetRequiredService<Database>();

        _db.CompanyProfile.Save(new CompanyProfile
        {
            CurrencySymbol = "$",
            TimesheetConfig = new TimesheetSettings
            {
                EnableRounding = true,
                RoundingIntervalMinutes = 15,
                RoundingDirection = "Nearest"
            }
        });

        _service = new TimesheetReportService(_db);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(1.5, TimeDisplayFormat.Duration_HHMM, "1:30")]
    [InlineData(1.5, TimeDisplayFormat.Decimal_Hours, "1.50 h")]
    [InlineData(0.25, TimeDisplayFormat.Duration_HHMM, "0:15")]
    [InlineData(0.0, TimeDisplayFormat.Duration_HHMM, "0:00")]
    [InlineData(42.75, TimeDisplayFormat.Duration_HHMM, "42:45")]
    [InlineData(42.75, TimeDisplayFormat.Decimal_Hours, "42.75 h")]
    public void FormatDuration_ReturnsExpectedStrings(decimal hours, TimeDisplayFormat format, string expected)
    {
        var result = _service.FormatDuration(hours, format);
        Assert.Equal(expected, result);
    }


    [Fact]
    public void CalculateKPISummary_CalculatesTotalsAccurately()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeId = "emp1",
                ProjectId = "proj1",
                HourlyRate = 100m,
                CostRate = 40m,
                Entry = new TimeEntry { Hours = 8.0m, IsBillable = true }
            },
            new()
            {
                EmployeeId = "emp2",
                ProjectId = "proj1",
                HourlyRate = 100m,
                CostRate = 50m,
                Entry = new TimeEntry { Hours = 2.0m, IsBillable = false }
            }
        };

        var kpi = _service.CalculateKPISummary(entries);

        Assert.Equal(10.0m, kpi.TotalHours);
        Assert.Equal(8.0m, kpi.BillableHours);
        Assert.Equal(2.0m, kpi.NonBillableHours);
        Assert.Equal(80.0m, kpi.BillablePercentage);
        Assert.Equal(800.0m, kpi.BillableAmount); // 8h * $100
        Assert.Equal(420.0m, kpi.CostAmount); // (8h * $40) + (2h * $50) = 320 + 100
        Assert.Equal(380.0m, kpi.ProfitAmount); // 800 - 420
        Assert.Equal(47.5m, kpi.ProfitMarginPercentage); // 380 / 800 * 100
        Assert.Equal(2, kpi.EntriesCount);
        Assert.Equal(1, kpi.ActiveProjectsCount);
        Assert.Equal(2, kpi.ActiveEmployeesCount);
    }

    [Fact]
    public void BuildSummaryGroups_AggregatesPrimaryAndSecondaryGroups()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                ProjectName = "Project Alpha",
                ClientId = "Acme Inc",
                HourlyRate = 120m,
                Entry = new TimeEntry { Description = "Frontend dev", Hours = 5.0m, IsBillable = true }
            },
            new()
            {
                ProjectName = "Project Alpha",
                ClientId = "Acme Inc",
                HourlyRate = 120m,
                Entry = new TimeEntry { Description = "Frontend dev", Hours = 3.0m, IsBillable = true }
            },
            new()
            {
                ProjectName = "Project Alpha",
                ClientId = "Acme Inc",
                HourlyRate = 120m,
                Entry = new TimeEntry { Description = "Bug fixing", Hours = 2.0m, IsBillable = false }
            },
            new()
            {
                ProjectName = "Project Beta",
                ClientId = "Beta Corp",
                HourlyRate = 150m,
                Entry = new TimeEntry { Description = "Architecture", Hours = 4.0m, IsBillable = true }
            }
        };

        var groups = _service.BuildSummaryGroups(entries, ReportGroupBy.Project, ReportSubGroupBy.Description);

        Assert.Equal(2, groups.Count);
        var alpha = groups.First(g => g.Key == "Project Alpha");
        Assert.Equal("Acme Inc", alpha.Subtitle);
        Assert.Equal(10.0m, alpha.TotalHours);
        Assert.Equal(8.0m, alpha.BillableHours);
        Assert.Equal(960.0m, alpha.BillableAmount); // 8h * 120
        Assert.Equal(3, alpha.EntryCount);

        Assert.Equal(2, alpha.Subgroups.Count);
        var frontend = alpha.Subgroups.First(s => s.Key == "Frontend dev");
        Assert.Equal(8.0m, frontend.TotalHours);
        Assert.Equal(2, frontend.EntryCount);
    }

    [Fact]
    public void BuildWeeklyMatrix_PopulatesDaysMonThroughSun_WithAccurateTotals()
    {
        // Monday: 2026-08-17, Tuesday: 2026-08-18, Sunday: 2026-08-23
        var monday = new DateTime(2026, 8, 17);
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeId = "emp1",
                EmployeeName = "Alice",
                HourlyRate = 100m,
                Entry = new TimeEntry { Date = monday, Hours = 8.0m, IsBillable = true }
            },
            new()
            {
                EmployeeId = "emp1",
                EmployeeName = "Alice",
                HourlyRate = 100m,
                Entry = new TimeEntry { Date = monday.AddDays(1), Hours = 7.5m, IsBillable = true }
            },
            new()
            {
                EmployeeId = "emp2",
                EmployeeName = "Bob",
                HourlyRate = 80m,
                Entry = new TimeEntry { Date = monday.AddDays(1), Hours = 6.0m, IsBillable = true }
            }
        };

        var matrix = _service.BuildWeeklyMatrix(entries, monday, WeeklyGroupBy.Employee);

        Assert.Equal(monday.Date, matrix.WeekStartDate.Date);
        Assert.Equal(monday.AddDays(6).Date, matrix.WeekEndDate.Date);
        Assert.Equal(2, matrix.Rows.Count);

        var alice = matrix.Rows.First(r => r.EntityName == "Alice");
        Assert.Equal(15.5m, alice.TotalHours);
        Assert.Equal(8.0m, alice.Cells[0].Hours); // Mon
        Assert.Equal(7.5m, alice.Cells[1].Hours); // Tue
        Assert.Equal(0.0m, alice.Cells[2].Hours); // Wed

        Assert.Equal(8.0m, matrix.DayTotals[0]); // Monday total
        Assert.Equal(13.5m, matrix.DayTotals[1]); // Tuesday total: 7.5 + 6.0
        Assert.Equal(21.5m, matrix.GrandTotalHours);
    }

    [Fact]
    public void ShiftDateRange_ShiftsForwardAndBackward()
    {
        var start = new DateTime(2026, 8, 10);
        var end = new DateTime(2026, 8, 16);
        var range = new DateRange(start, end);

        var nextWeek = _service.ShiftDateRange(range, 1);
        Assert.Equal(new DateTime(2026, 8, 17), nextWeek.Start);
        Assert.Equal(new DateTime(2026, 8, 23), nextWeek.End);

        var prevWeek = _service.ShiftDateRange(range, -1);
        Assert.Equal(new DateTime(2026, 8, 3), prevWeek.Start);
        Assert.Equal(new DateTime(2026, 8, 9), prevWeek.End);
    }

    [Fact]
    public void GenerateDetailedCsv_ProducesCsvWithHeaderAndRows()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                EmployeeName = "Jane Doe",
                ProjectName = "Portal Overhaul",
                ClientId = "TechCorp",
                TaskName = "Testing",
                HourlyRate = 110m,
                Entry = new TimeEntry
                {
                    Date = new DateTime(2026, 9, 1),
                    Hours = 4.5m,
                    Description = "Unit testing",
                    IsBillable = true
                }
            }
        };

        var csv = _service.GenerateDetailedCsv(entries, TimeDisplayFormat.Duration_HHMM);

        Assert.Contains("Date,Employee,Project,Client,Task,Description,Is Billable,Rate,Hours,Amount", csv);
        Assert.Contains("Jane Doe", csv);
        Assert.Contains("Portal Overhaul", csv);
        Assert.Contains("4:30", csv);
        Assert.Contains("495.00", csv); // 4.5 * 110
    }

    [Fact]
    public void FormatCurrency_UsesCompanyProfileCurrencySymbol()
    {
        var result = _service.FormatCurrency(1234.50m);
        Assert.Equal("1,234.50 $", result);
    }

    [Fact]
    public void GetPresetDateRange_ReturnsValidDateRanges()
    {
        var today = _service.GetPresetDateRange("Today");
        Assert.NotNull(today.Start);
        Assert.NotNull(today.End);
        Assert.Equal(DateTime.Today, today.Start!.Value.Date);

        var thisWeek = _service.GetPresetDateRange("This Week");
        Assert.Equal(DayOfWeek.Monday, thisWeek.Start!.Value.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, thisWeek.End!.Value.DayOfWeek);
    }

    [Fact]
    public void GenerateSummaryCsv_ProducesCsvWithHeaderAndRows()
    {
        var groups = new List<TimesheetGroupSummary>
        {
            new()
            {
                Key = "Project Alpha",
                Subtitle = "Acme Inc",
                TotalHours = 10.0m,
                BillableHours = 8.0m,
                BillableAmount = 800m
            }
        };

        var csv = _service.GenerateSummaryCsv(groups, TimeDisplayFormat.Decimal_Hours);

        Assert.Contains("Title,Subtitle,Duration,Billable Hours,Billable Amount", csv);
        Assert.Contains("Project Alpha", csv);
        Assert.Contains("10.00 h", csv);
        Assert.Contains("800.00", csv);
    }

    [Fact]
    public void GenerateWeeklyCsv_ProducesCsvWithHeaderAndRows()
    {
        var monday = new DateTime(2026, 8, 17);
        var matrix = new TimesheetWeeklyMatrix
        {
            WeekStartDate = monday,
            WeekEndDate = monday.AddDays(6),
            DayHeaders = ["Mon 17", "Tue 18", "Wed 19", "Thu 20", "Fri 21", "Sat 22", "Sun 23"],
            DayTotals = [8, 8, 8, 8, 8, 0, 0],
            Rows =
            [
                new TimesheetWeeklyRow
                {
                    EntityName = "Alice",
                    Subtitle = "Dev Team",
                    Cells = Enumerable.Range(0, 7).Select(i => new TimesheetWeeklyCell
                    {
                        Date = monday.AddDays(i),
                        Hours = i < 5 ? 8 : 0
                    }).ToList()
                }
            ]
        };

        var csv = _service.GenerateWeeklyCsv(matrix, TimeDisplayFormat.Duration_HHMM);

        Assert.Contains("Alice", csv);
        Assert.Contains("Total", csv);
        Assert.Contains("40:00", csv);
    }

    [Fact]
    public async Task LoadAllEntriesAsync_ResolvesRatesEmployeesAndProjects()
    {
        var emp = new Employee { Id = "emp-101", FirstName = "John", LastName = "Developer", HourlyRate = 45m };
        _db.Employees.Save(emp);

        var task = new WorkType { Id = "task-1", Name = "Development", DefaultRate = 120m, CostRate = 40m };
        _db.WorkTypes.Save(task);

        var proj = new Project
        {
            Id = "proj-1",
            Name = "Mobile App",
            Client = new ClientInfo { BusinessName = "Client XYZ" },
            Allocations = [new ProjectTaskAllocation { WorkTypeId = "task-1", QuotedHours = 100 }]
        };
        _db.Projects.Save(proj);

        var timesheet = new Timesheet
        {
            Id = "ts-101",
            EmployeeId = "emp-101",
            Status = "Approved",
            Entries =
            [
                new TimeEntry
                {
                    Id = "e-1",
                    ProjectId = "proj-1",
                    WorkTypeId = "task-1",
                    Date = DateTime.Today,
                    Hours = 5.0m,
                    Description = "Building UI",
                    IsBillable = true
                }
            ]
        };
        _db.Timesheets.Save(timesheet);

        var loaded = await _service.LoadAllEntriesAsync();

        Assert.Single(loaded);
        var entry = loaded[0];
        Assert.Equal("John Developer", entry.EmployeeName);
        Assert.Equal("JD", entry.EmployeeInitials);
        Assert.Equal("Mobile App", entry.ProjectName);
        Assert.Equal("Client XYZ", entry.ClientId);
        Assert.Equal("Development", entry.TaskName);
        Assert.Equal(120m, entry.HourlyRate);
        Assert.Equal(45m, entry.CostRate);
        Assert.Equal(100m, entry.ProjectAllocatedHours);
        Assert.Equal(600m, entry.BillableAmount); // 5h * 120
        Assert.Equal(225m, entry.CostAmount); // 5h * 45
    }

    [Fact]
    public void ApplyFiltersAndRounding_RoundsHoursWhenEnabled()
    {
        var entries = new List<TimesheetReportEntry>
        {
            new()
            {
                Entry = new TimeEntry { Date = DateTime.Today, Hours = 1.18m, IsBillable = true }
            }
        };

        var criteria = new TimesheetReportFilterCriteria();

        // Rounding enabled with 15min interval nearest: 1.18h = 70.8m -> nearest 15m is 75m = 1.25h
        var rounded = _service.ApplyFiltersAndRounding(entries, criteria, applyRounding: true);
        Assert.Single(rounded);
        Assert.Equal(1.25m, rounded[0].Entry.Hours);

        // Rounding disabled
        var unrounded = _service.ApplyFiltersAndRounding(entries, criteria, applyRounding: false);
        Assert.Single(unrounded);
        Assert.Equal(1.18m, unrounded[0].Entry.Hours);
    }
}
