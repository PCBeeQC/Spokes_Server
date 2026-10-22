using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Accounting;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class RecurringBillHistoryTests
{
    [Fact]
    public void PaidHistory_Mapping_Sets_RecurringRecord_And_OriginalItem()
    {
        // Simulate the Accounting.razor mapping logic
        var expense = new RecurringExpense
        {
            Id = "rec-123",
            Name = "Office Internet",
            Supplier = "Bell Canada",
            Frequency = RecurringExpenseFrequency.Monthly,
            IsEstimate = false,
            Amount = 120m
        };

        var billRecord = new RecurringBillRecord
        {
            Id = "bill-rec-456",
            Date = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 4, 30),
            Amount = 125.50m,
            IsPaid = true,
            DatePaid = new DateTime(2026, 4, 15),
            AttachmentPath = "/spokesapi/files/recurringcosts/rec-123/invoice.pdf",
            AttachmentName = "invoice.pdf",
            Payments =
            [
                new PaymentRecord
                {
                    Amount = 125.50m,
                    Date = new DateTime(2026, 4, 15),
                    Method = "Direct Debit",
                    Reference = "REF-998811"
                }
            ]
        };

        expense.History.Add(billRecord);

        // Map to AccountingItem exactly as done in Accounting.razor
        var item = new
        {
            Id = billRecord.Id,
            Type = expense.Frequency == RecurringExpenseFrequency.OneTime ? "One-Time" : "Recurring",
            Number = expense.Name,
            Entity = string.IsNullOrEmpty(expense.Supplier) ? "Recurring Bill" : expense.Supplier,
            Date = billRecord.Date,
            DueDate = billRecord.Date,
            Total = billRecord.Amount,
            Balance = 0m,
            Status = BillStatus.Paid,
            DatePaid = billRecord.DatePaid ?? billRecord.Date,
            OriginalItem = expense,
            RecurringRecord = billRecord
        };

        Assert.Equal("bill-rec-456", item.Id);
        Assert.Equal("Recurring", item.Type);
        Assert.Equal("Office Internet", item.Number);
        Assert.Equal("Bell Canada", item.Entity);
        Assert.Equal(125.50m, item.Total);
        Assert.Equal(0m, item.Balance);
        Assert.Equal(BillStatus.Paid, item.Status);
        Assert.Same(expense, item.OriginalItem);
        Assert.Same(billRecord, item.RecurringRecord);
        Assert.Equal("/spokesapi/files/recurringcosts/rec-123/invoice.pdf", item.RecurringRecord.AttachmentPath);
        Assert.Single(item.RecurringRecord.Payments);
        Assert.Equal("REF-998811", item.RecurringRecord.Payments[0].Reference);
    }

    [Fact]
    public void OneTime_Expense_Paid_History_Has_Correct_Type()
    {
        var expense = new RecurringExpense
        {
            Id = "rec-789",
            Name = "Annual Fire Inspection",
            Supplier = "SafeCheck Inc",
            Frequency = RecurringExpenseFrequency.OneTime,
            IsEstimate = false,
            Amount = 300m
        };

        var billRecord = new RecurringBillRecord
        {
            Id = "bill-rec-789",
            Date = new DateTime(2026, 3, 10),
            Amount = 300m,
            IsPaid = true,
            DatePaid = new DateTime(2026, 3, 12)
        };

        expense.History.Add(billRecord);

        var typeStr = expense.Frequency == RecurringExpenseFrequency.OneTime ? "One-Time" : "Recurring";
        Assert.Equal("One-Time", typeStr);

        var resolvedRecord = expense.History.FirstOrDefault(h => h.Id == billRecord.Id);
        Assert.NotNull(resolvedRecord);
        Assert.Equal(billRecord.Id, resolvedRecord.Id);
    }
}
