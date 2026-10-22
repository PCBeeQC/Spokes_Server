using System;
using System.Text.Json;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class HourBankAdjustmentTests
{
    [Fact]
    public void HourBankAdjustment_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var adj = new HourBankAdjustment
        {
            Id = "adj_1",
            EmployeeId = "emp_1",
            Date = now,
            Hours = 5.0m,
            Reason = "Overtime payout",
            CreatedByEmployeeId = "admin_1"
        };

        Assert.Equal("adj_1", adj.Id);
        Assert.Equal("emp_1", adj.EmployeeId);
        Assert.Equal(now, adj.Date);
        Assert.Equal(5.0m, adj.Hours);
        Assert.Equal("Overtime payout", adj.Reason);
        Assert.Equal("admin_1", adj.CreatedByEmployeeId);

        var json = JsonSerializer.Serialize(adj);
        var copy = JsonSerializer.Deserialize<HourBankAdjustment>(json);
        Assert.NotNull(copy);
        Assert.Equal(5.0m, copy.Hours);
    }
}
