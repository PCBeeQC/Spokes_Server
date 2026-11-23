using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class ExpenseReportTests
{
    [Fact]
    public void Status_WhenDatePaidHasValue_ReturnsPaid()
    {
        // Arrange
        var report = new ExpenseReport
        {
            DateSubmitted = DateTime.UtcNow.AddDays(-2),
            DatePaid = DateTime.UtcNow
        };

        // Assert
        Assert.Equal(ExpenseReportStatus.Paid, report.Status);

        // Also verify even if DateSubmitted is null, DatePaid takes precedence
        var reportOnlyPaid = new ExpenseReport
        {
            DatePaid = DateTime.UtcNow
        };
        Assert.Equal(ExpenseReportStatus.Paid, reportOnlyPaid.Status);
    }

    [Fact]
    public void Status_WhenDateSubmittedHasValueAndDatePaidNull_ReturnsSubmitted()
    {
        // Arrange
        var report = new ExpenseReport
        {
            DateSubmitted = DateTime.UtcNow,
            DatePaid = null
        };

        // Assert
        Assert.Equal(ExpenseReportStatus.Submitted, report.Status);
    }

    [Fact]
    public void Status_WhenNeitherDatePaidNorDateSubmittedSet_ReturnsDraft()
    {
        // Arrange
        var report = new ExpenseReport
        {
            DateSubmitted = null,
            DatePaid = null
        };

        // Assert
        Assert.Equal(ExpenseReportStatus.Draft, report.Status);
    }

    [Fact]
    public void TotalAmount_CalculatesSumOfItemTotals()
    {
        // Arrange
        var report = new ExpenseReport();
        report.LineItems.Add(new ExpenseItem { Amount = 120.50m, IsKilometrage = false });
        report.LineItems.Add(new ExpenseItem { IsKilometrage = true, Kilometers = 100m, KilometrageRate = 0.55m }); // 55.00
        report.LineItems.Add(new ExpenseItem { Amount = 24.50m, IsKilometrage = false });

        // Act & Assert
        // 120.50 + 55.00 + 24.50 = 200.00
        Assert.Equal(200.00m, report.TotalAmount);
    }

    [Fact]
    public void ExpenseItem_Total_WhenKilometrage_CalculatesKmTimesRate()
    {
        // Arrange
        var item = new ExpenseItem
        {
            IsKilometrage = true,
            Kilometers = 150m,
            KilometrageRate = 0.62m,
            Amount = 999m // Should be ignored when IsKilometrage is true
        };

        // Act & Assert
        // 150 * 0.62 = 93.00
        Assert.Equal(93.00m, item.Total);
    }

    [Fact]
    public void ExpenseItem_Total_WhenNotKilometrage_ReturnsAmount()
    {
        // Arrange
        var item = new ExpenseItem
        {
            IsKilometrage = false,
            Amount = 85.75m,
            Kilometers = 200m,
            KilometrageRate = 0.50m // Should be ignored when IsKilometrage is false
        };

        // Act & Assert
        Assert.Equal(85.75m, item.Total);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        // Arrange
        var id = Guid.NewGuid().ToString();
        var report1 = new ExpenseReport { Id = id, ReportNumber = "EXP-001" };
        var report2 = new ExpenseReport { Id = id, ReportNumber = "EXP-002" };
        var report3 = new ExpenseReport { Id = Guid.NewGuid().ToString(), ReportNumber = "EXP-001" };

        // Reference equality
        Assert.True(report1.Equals(report1));

        // Value / ID-based equality
        Assert.True(report1.Equals(report2));
        Assert.True(report2.Equals(report1));
        Assert.Equal(report1.GetHashCode(), report2.GetHashCode());

        // Unequal IDs
        Assert.False(report1.Equals(report3));
        Assert.False(report3.Equals(report1));

        // Null and different type comparison
        Assert.False(report1.Equals(null));
        Assert.False(report1.Equals("some string"));
        Assert.False(report1.Equals(new object()));

        // Null Id fallback GetHashCode
        var nullIdReport = new ExpenseReport { Id = null! };
        Assert.IsType<int>(nullIdReport.GetHashCode());
    }

    [Fact]
    public void ExpenseReport_JsonDeserialization_SupportsLegacyItemsProperty()
    {
        const string legacyJson = """
        {
            "Id": "exp-1",
            "Items": [
                { "Description": "Hotel", "Amount": 120 }
            ]
        }
        """;

        var report = System.Text.Json.JsonSerializer.Deserialize<ExpenseReport>(legacyJson);
        Assert.NotNull(report);
        Assert.Single(report.LineItems);
        Assert.Equal("Hotel", report.LineItems[0].Description);
        Assert.Equal(120m, report.TotalAmount);
    }

    [Fact]
    public void ExpenseReport_JsonDeserialization_SupportsLineItemsProperty()
    {
        const string modernJson = """
        {
            "Id": "exp-2",
            "LineItems": [
                { "Description": "Flight", "Amount": 300 }
            ]
        }
        """;

        var report = System.Text.Json.JsonSerializer.Deserialize<ExpenseReport>(modernJson);
        Assert.NotNull(report);
        Assert.Single(report.LineItems);
        Assert.Equal("Flight", report.LineItems[0].Description);
        Assert.Equal(300m, report.TotalAmount);
    }

    [Fact]
    public void ExpenseReport_JsonSerialization_WritesLineItemsProperty()
    {
        var report = new ExpenseReport { Id = "exp-3" };
        report.LineItems.Add(new ExpenseItem { Description = "Taxi", Amount = 40m });

        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.Contains("\"LineItems\"", json);
        Assert.DoesNotContain("\"LegacyItems\"", json);
    }
}

