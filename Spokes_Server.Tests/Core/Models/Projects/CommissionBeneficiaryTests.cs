using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Projects;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class CommissionBeneficiaryTests
{
    [Fact]
    public void CommissionBeneficiary_Properties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var b = new CommissionBeneficiary
        {
            Name = "Jane Agent",
            Type = "Internal",
            Role = "Sales Lead",
            CommissionBasis = CommissionBasisTypes.Revenue,
            EmployeeId = "emp_jane",
            Percentage = 10.0m,
            DatePaid = now,
            PaymentReference = "COMM-101"
        };

        Assert.Equal("Jane Agent", b.Name);
        Assert.Equal("Internal", b.Type);
        Assert.Equal("Sales Lead", b.Role);
        Assert.Equal(CommissionBasisTypes.Revenue, b.CommissionBasis);
        Assert.Equal("emp_jane", b.EmployeeId);
        Assert.Equal(10.0m, b.Percentage);
        Assert.Equal(now, b.DatePaid);
        Assert.Equal("COMM-101", b.PaymentReference);

        var json = JsonSerializer.Serialize(b);
        var copy = JsonSerializer.Deserialize<CommissionBeneficiary>(json);
        Assert.NotNull(copy);
        Assert.Equal(10.0m, copy.Percentage);
    }
}
