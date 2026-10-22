using System;
using System.Text.Json;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class PaymentRecordTests
{
    [Fact]
    public void PaymentRecord_DefaultValuesAndProperties_WorkCorrectly()
    {
        var now = DateTime.UtcNow;
        var payment = new PaymentRecord
        {
            Id = "pay_1",
            Amount = 1500.50m,
            Date = now,
            Method = "CreditCard",
            Reference = "TXN-9876",
            Notes = "Invoice full payment"
        };

        Assert.Equal("pay_1", payment.Id);
        Assert.Equal(1500.50m, payment.Amount);
        Assert.Equal(now, payment.Date);
        Assert.Equal("CreditCard", payment.Method);
        Assert.Equal("TXN-9876", payment.Reference);
        Assert.Equal("Invoice full payment", payment.Notes);

        var json = JsonSerializer.Serialize(payment);
        var roundtripped = JsonSerializer.Deserialize<PaymentRecord>(json);
        Assert.NotNull(roundtripped);
        Assert.Equal(1500.50m, roundtripped.Amount);
        Assert.Equal("TXN-9876", roundtripped.Reference);
        Assert.Equal("Invoice full payment", roundtripped.Notes);
    }
}
