using System;
using System.Collections.Generic;
using System.Text.Json;
using Spokes_Server.Core.Models.Reports;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Reports;

public class TimesheetReportModelsTests
{
    [Fact]
    public void TimesheetKPISummary_Calculations_AreAccurate()
    {
        var kpi = new TimesheetKPISummary
        {
            TotalHours = 100m,
            BillableHours = 80m,
            BillableAmount = 10000m,
            CostAmount = 6000m,
            EntriesCount = 50,
            ActiveProjectsCount = 4,
            ActiveEmployeesCount = 3
        };

        Assert.Equal(20m, kpi.NonBillableHours);
        Assert.Equal(80.0m, kpi.BillablePercentage);
        Assert.Equal(4000m, kpi.ProfitAmount);
        Assert.Equal(40.0m, kpi.ProfitMarginPercentage);
    }

    [Fact]
    public void TimesheetKPISummary_WithZeroHours_HandlesDivByZeroGracefully()
    {
        var kpi = new TimesheetKPISummary
        {
            TotalHours = 0m,
            BillableHours = 0m,
            BillableAmount = 0m,
            CostAmount = 0m
        };

        Assert.Equal(0m, kpi.NonBillableHours);
        Assert.Equal(0m, kpi.BillablePercentage);
        Assert.Equal(0m, kpi.ProfitAmount);
        Assert.Equal(0m, kpi.ProfitMarginPercentage);
    }

    [Fact]
    public void TimesheetGroupSummary_BudgetUsedPercentage_CalculatesProperly()
    {
        var group = new TimesheetGroupSummary
        {
            TotalHours = 50m,
            AllocatedHours = 100m
        };

        Assert.Equal(50.0m, group.BudgetUsedPercentage);

        group.AllocatedHours = null;
        Assert.Null(group.BudgetUsedPercentage);
    }

    [Fact]
    public void TimesheetWeeklyRow_CalculatesTotalsFromCells()
    {
        var row = new TimesheetWeeklyRow
        {
            EntityId = "emp_1",
            EntityName = "Alice",
            Cells = new List<TimesheetWeeklyCell>
            {
                new() { Hours = 8m, BillableHours = 6m, BillableAmount = 600m },
                new() { Hours = 7.5m, BillableHours = 7.5m, BillableAmount = 750m }
            }
        };

        Assert.Equal(15.5m, row.TotalHours);
        Assert.Equal(13.5m, row.TotalBillableHours);
        Assert.Equal(1350m, row.TotalBillableAmount);
    }

    [Fact]
    public void TimesheetWeeklyMatrix_GrandTotals_SumAllRows()
    {
        var matrix = new TimesheetWeeklyMatrix
        {
            Rows = new List<TimesheetWeeklyRow>
            {
                new()
                {
                    Cells = new List<TimesheetWeeklyCell>
                    {
                        new() { Hours = 10m, BillableHours = 8m, BillableAmount = 800m }
                    }
                },
                new()
                {
                    Cells = new List<TimesheetWeeklyCell>
                    {
                        new() { Hours = 20m, BillableHours = 15m, BillableAmount = 1500m }
                    }
                }
            }
        };

        Assert.Equal(30m, matrix.GrandTotalHours);
        Assert.Equal(23m, matrix.GrandTotalBillableHours);
        Assert.Equal(2300m, matrix.GrandTotalBillableAmount);
    }
}
