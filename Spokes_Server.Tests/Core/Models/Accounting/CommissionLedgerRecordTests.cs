using System;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class CommissionLedgerRecordTests
{
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

}
