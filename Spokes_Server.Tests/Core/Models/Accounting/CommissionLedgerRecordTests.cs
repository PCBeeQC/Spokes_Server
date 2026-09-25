using System;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class CommissionLedgerRecordTests
{
    [Fact]
    public void Initialization_SetsDefaultValues()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var record = new CommissionLedgerRecord();
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.NotNull(record.Id);
        Assert.True(Guid.TryParse(record.Id, out _));
        Assert.Equal(CommissionStatus.Payable, record.Status);
        Assert.Equal("Payable", record.Status);
        Assert.Equal("Internal", record.Type);
        Assert.True(record.DateGenerated >= before && record.DateGenerated <= after);
        Assert.Equal(string.Empty, record.ProjectId);
        Assert.Null(record.InvoiceId);
        Assert.Equal(string.Empty, record.BeneficiaryId);
        Assert.Equal(string.Empty, record.BeneficiaryName);
        Assert.Equal(0m, record.BaseAmount);
        Assert.Equal(0m, record.CommissionAmount);
        Assert.Null(record.DatePaid);
        Assert.Null(record.PaymentReference);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var id = Guid.NewGuid().ToString();
        var record1 = new CommissionLedgerRecord { Id = id };
        var record2 = new CommissionLedgerRecord { Id = id };
        var record3 = new CommissionLedgerRecord { Id = Guid.NewGuid().ToString() };

        // Reference equality
        Assert.True(record1.Equals(record1));

        // Id-based equality
        Assert.True(record1.Equals(record2));
        Assert.True(record2.Equals(record1));
        Assert.Equal(record1.GetHashCode(), record2.GetHashCode());

        // Unequal IDs
        Assert.False(record1.Equals(record3));
        Assert.False(record3.Equals(record1));

        // Null and different type
        Assert.False(record1.Equals(null));
        Assert.False(record1.Equals("some string"));

        // Null Id fallback GetHashCode
        var recordNullId = new CommissionLedgerRecord { Id = null! };
        var hashCode = recordNullId.GetHashCode();
        Assert.NotEqual(0, hashCode);
    }

    [Fact]
    public void CommissionStatus_Constants_HaveExpectedValues()
    {
        Assert.Equal("Pending", CommissionStatus.Pending);
        Assert.Equal("Payable", CommissionStatus.Payable);
        Assert.Equal("Paid", CommissionStatus.Paid);
        Assert.Equal("Clawback", CommissionStatus.Clawback);
    }

    [Fact]
    public void CommissionBasisTypes_Constants_HaveExpectedValues()
    {
        Assert.Equal("Revenue", CommissionBasisTypes.Revenue);
        Assert.Equal("Margin", CommissionBasisTypes.Margin);
        Assert.Equal("Project Revenue", CommissionBasisTypes.ProjectRevenue);
        Assert.Equal("Project Margin", CommissionBasisTypes.ProjectMargin);
    }
}
