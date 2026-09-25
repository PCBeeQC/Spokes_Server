using System;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class BillTests
{
    [Fact]
    public void AmountPaid_WhenNoPayments_ReturnsZero()
    {
        var bill = new Bill
        {
            Amount = 500m,
            Status = "Awaiting Approval",
            DatePaid = null
        };

        Assert.Equal(0m, bill.AmountPaid);
        Assert.Equal(500m, bill.BalanceDue);
    }

    [Fact]
    public void AmountPaid_WhenPaymentsExist_ReturnsSumOfPayments()
    {
        var bill = new Bill
        {
            Amount = 1000m,
            Status = "Awaiting Approval",
            DatePaid = null,
            Payments =
            [
                new PaymentRecord { Amount = 250m },
                new PaymentRecord { Amount = 350m }
            ]
        };

        Assert.Equal(600m, bill.AmountPaid);
        Assert.Equal(400m, bill.BalanceDue);
    }

    [Fact]
    public void AmountPaid_WhenNoPaymentsButDatePaidSet_ReturnsFullAmount()
    {
        var bill = new Bill
        {
            Amount = 750m,
            Status = "Approved",
            DatePaid = DateTime.Today
        };

        Assert.Equal(750m, bill.AmountPaid);
        Assert.Equal(0m, bill.BalanceDue);
    }

    [Fact]
    public void AmountPaid_WhenNoPaymentsButStatusIsPaid_ReturnsFullAmount()
    {
        var bill = new Bill
        {
            Amount = 300m,
            Status = "Paid",
            DatePaid = null
        };

        Assert.Equal(300m, bill.AmountPaid);
        Assert.Equal(0m, bill.BalanceDue);
    }

    [Fact]
    public void BillLineItem_Total_CalculatesQuantityTimesUnitPrice()
    {
        var lineItem = new BillLineItem
        {
            Quantity = 4m,
            UnitPrice = 25.50m
        };

        Assert.Equal(102.00m, lineItem.Total);
    }

    [Fact]
    public void Equals_And_GetHashCode_Behavior()
    {
        var id = Guid.NewGuid().ToString();
        var bill1 = new Bill { Id = id, Description = "Bill 1" };
        var bill2 = new Bill { Id = id, Description = "Bill 2" };
        var bill3 = new Bill { Id = Guid.NewGuid().ToString(), Description = "Bill 3" };

        // Reference equality
        Assert.True(bill1.Equals(bill1));

        // Same ID equality
        Assert.True(bill1.Equals(bill2));
        Assert.Equal(bill1.GetHashCode(), bill2.GetHashCode());

        // Different ID inequality
        Assert.False(bill1.Equals(bill3));

        // Null and different type inequality
        Assert.False(bill1.Equals(null));
        Assert.False(bill1.Equals("some string"));
        Assert.False(bill1.Equals(new object()));
    }
}
