using System;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class RecurringExpenseTests
{
    [Fact]
    public void RecurringExpense_Initialization_SetsDefaults()
    {
        var expense = new RecurringExpense();

        Assert.False(string.IsNullOrWhiteSpace(expense.Id));
        Assert.True(expense.IsEstimate);
        Assert.Equal(string.Empty, expense.Name);
        Assert.Equal(string.Empty, expense.Supplier);
        Assert.Equal(string.Empty, expense.Description);
        Assert.Equal(0m, expense.Amount);
        Assert.Equal(RecurringExpenseFrequency.Monthly, expense.Frequency);
        Assert.Equal(DateTime.Today, expense.CreatedDate);
        Assert.Equal(DateTime.Today, expense.NextDueDate);
        Assert.NotNull(expense.History);
        Assert.Empty(expense.History);
    }

    [Fact]
    public void RecurringExpense_Equals_And_GetHashCode()
    {
        var id = Guid.NewGuid().ToString();
        var expense1 = new RecurringExpense { Id = id, Name = "Internet" };
        var expense2 = new RecurringExpense { Id = id, Name = "Office Internet" };
        var expense3 = new RecurringExpense { Id = Guid.NewGuid().ToString(), Name = "Rent" };

        // Reference equality
        Assert.True(expense1.Equals(expense1));

        // Same ID equality
        Assert.True(expense1.Equals(expense2));
        Assert.Equal(expense1.GetHashCode(), expense2.GetHashCode());

        // Unequal IDs
        Assert.False(expense1.Equals(expense3));

        // Null and different type inequality
        Assert.False(expense1.Equals(null));
        Assert.False(expense1.Equals("some string"));
        Assert.False(expense1.Equals(new object()));
    }

    [Fact]
    public void RecurringBillRecord_AmountPaid_WhenNoPaymentsAndNotPaid_ReturnsZero()
    {
        var record = new RecurringBillRecord
        {
            Amount = 250m,
            IsPaid = false,
            Payments = []
        };

        Assert.Equal(0m, record.AmountPaid);
        Assert.Equal(250m, record.BalanceDue);
    }

    [Fact]
    public void RecurringBillRecord_AmountPaid_WhenPaymentsExist_ReturnsSumOfPayments()
    {
        var record = new RecurringBillRecord
        {
            Amount = 500m,
            IsPaid = false,
            Payments =
            [
                new PaymentRecord { Amount = 150m },
                new PaymentRecord { Amount = 200m }
            ]
        };

        Assert.Equal(350m, record.AmountPaid);
        Assert.Equal(150m, record.BalanceDue);
    }

    [Fact]
    public void RecurringBillRecord_AmountPaid_WhenNoPaymentsAndIsPaidTrue_ReturnsAmount()
    {
        var record = new RecurringBillRecord
        {
            Amount = 300m,
            IsPaid = true,
            Payments = []
        };

        Assert.Equal(300m, record.AmountPaid);
        Assert.Equal(0m, record.BalanceDue);
    }

    [Fact]
    public void RecurringExpenseFrequency_Constants_HaveExpectedValues()
    {
        Assert.Equal("Weekly", RecurringExpenseFrequency.Weekly);
        Assert.Equal("Bi-Weekly", RecurringExpenseFrequency.BiWeekly);
        Assert.Equal("Monthly", RecurringExpenseFrequency.Monthly);
        Assert.Equal("Yearly", RecurringExpenseFrequency.Yearly);
        Assert.Equal("One-Time", RecurringExpenseFrequency.OneTime);
        Assert.Equal("As Needed", RecurringExpenseFrequency.AsNeeded);
    }
}
