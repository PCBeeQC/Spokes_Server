using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class InvoiceTests
{
    [Fact]
    public void Invoice_FinancialProjections_CalculatedCorrectly()
    {
        var invoice = new Invoice
        {
            TaxRate = 0.10m, // 10% tax for easy math
        };

        invoice.LineItems.Add(new InvoiceLine { Quantity = 2, UnitPrice = 50 });  // $100
        invoice.LineItems.Add(new InvoiceLine { Quantity = 1, UnitPrice = 100 }); // $100

        Assert.Equal(200m, invoice.SubTotal);
        Assert.Equal(20m, invoice.TaxAmount);  // 10% of 200
        Assert.Equal(220m, invoice.GrandTotal);

        invoice.Payments.Add(new PaymentRecord { Amount = 100m });
        Assert.Equal(120m, invoice.BalanceDue);
    }

    [Fact]
    public void InvoiceLine_Total_CalculatedCorrectly()
    {
        var line = new InvoiceLine { Quantity = 2.5m, UnitPrice = 100m };
        Assert.Equal(250m, line.Total);
    }

    [Fact]
    public void TotalCost_WithLines_CalculatesSumOfQuantityTimesOriginalUnitCost()
    {
        var invoice = new Invoice();
        invoice.LineItems.Add(new InvoiceLine { Quantity = 2, OriginalUnitCost = 30m });
        invoice.LineItems.Add(new InvoiceLine { Quantity = 3, OriginalUnitCost = 25m });

        Assert.Equal(135m, invoice.TotalCost);
    }

    [Fact]
    public void GrossMargin_WithLines_CalculatesSubTotalMinusTotalCost()
    {
        var invoice = new Invoice();
        invoice.LineItems.Add(new InvoiceLine { Quantity = 2, UnitPrice = 50m, OriginalUnitCost = 30m }); // Subtotal = 100, Cost = 60
        invoice.LineItems.Add(new InvoiceLine { Quantity = 3, UnitPrice = 40m, OriginalUnitCost = 25m }); // Subtotal = 120, Cost = 75

        Assert.Equal(220m, invoice.SubTotal);
        Assert.Equal(135m, invoice.TotalCost);
        Assert.Equal(85m, invoice.GrossMargin);
    }

    [Fact]
    public void AmountPaid_WhenPaymentsEmpty_ReturnsZero()
    {
        var invoice = new Invoice();

        Assert.Empty(invoice.Payments);
        Assert.Equal(0m, invoice.AmountPaid);
    }

    [Fact]
    public void BalanceDue_WhenPaymentsEmpty_ReturnsGrandTotal()
    {
        var invoice = new Invoice
        {
            TaxRate = 0.10m
        };
        invoice.LineItems.Add(new InvoiceLine { Quantity = 2, UnitPrice = 100m }); // SubTotal = 200, GrandTotal = 220

        Assert.Equal(220m, invoice.GrandTotal);
        Assert.Equal(220m, invoice.BalanceDue);
    }

    [Fact]
    public void AmountPaid_WhenPaymentsExist_ReturnsSumOfPayments()
    {
        var invoice = new Invoice();
        invoice.Payments.Add(new PaymentRecord { Amount = 60m });
        invoice.Payments.Add(new PaymentRecord { Amount = 40m });

        Assert.Equal(100m, invoice.AmountPaid);
    }

    [Fact]
    public void BalanceDue_WhenPaymentsExist_ReturnsGrandTotalMinusPaymentsSum()
    {
        var invoice = new Invoice
        {
            TaxRate = 0.10m
        };
        invoice.LineItems.Add(new InvoiceLine { Quantity = 2, UnitPrice = 100m }); // SubTotal = 200, GrandTotal = 220
        invoice.Payments.Add(new PaymentRecord { Amount = 60m });
        invoice.Payments.Add(new PaymentRecord { Amount = 40m });

        Assert.Equal(120m, invoice.BalanceDue);
    }

    [Fact]
    public void Invoice_JsonDeserialization_IgnoresObsoleteLegacyFieldsAndReadsCanonicalPayments()
    {
        const string jsonWithObsoleteFields = """
        {
            "Id": "inv-modern-1",
            "Lines": [{ "Description": "Old", "Quantity": 1, "UnitPrice": 50 }],
            "AmountPaid": 75.50,
            "Payments": [{ "Amount": 25.00, "Method": "EFT" }],
            "Status": "Final"
        }
        """;
        var invoice = System.Text.Json.JsonSerializer.Deserialize<Invoice>(jsonWithObsoleteFields);

        Assert.NotNull(invoice);
        Assert.Empty(invoice.LineItems);
        Assert.Single(invoice.Payments);
        Assert.Equal(25.00m, invoice.Payments[0].Amount);
        Assert.Equal("EFT", invoice.Payments[0].Method);
        Assert.Equal(25.00m, invoice.AmountPaid);
    }

    [Fact]
    public void Invoice_Equals_And_GetHashCode_Behavior()
    {
        var invoice1 = new Invoice { Id = "INV-001" };
        var invoice2 = new Invoice { Id = "INV-001" };
        var invoice3 = new Invoice { Id = "INV-002" };

        Assert.True(invoice1.Equals(invoice1));
        Assert.True(invoice1.Equals(invoice2));
        Assert.True(invoice2.Equals(invoice1));
        Assert.Equal(invoice1.GetHashCode(), invoice2.GetHashCode());
        Assert.False(invoice1.Equals(invoice3));
        Assert.False(invoice3.Equals(invoice1));
        Assert.False(invoice1.Equals(null));
        Assert.False(invoice1.Equals("INV-001"));
        Assert.False(invoice1.Equals(new object()));
    }

    [Fact]
    public void InvoiceLine_Equals_And_GetHashCode_Behavior()
    {
        var line1 = new InvoiceLine { Id = "LINE-001" };
        var line2 = new InvoiceLine { Id = "LINE-001" };
        var line3 = new InvoiceLine { Id = "LINE-002" };

        Assert.True(line1.Equals(line1));
        Assert.True(line1.Equals(line2));
        Assert.True(line2.Equals(line1));
        Assert.Equal(line1.GetHashCode(), line2.GetHashCode());
        Assert.False(line1.Equals(line3));
        Assert.False(line3.Equals(line1));
        Assert.False(line1.Equals(null));
        Assert.False(line1.Equals("LINE-001"));
        Assert.False(line1.Equals(new object()));
    }

    [Fact]
    public void Invoice_JsonDeserialization_SupportsLineItemsProperty()
    {
        const string modernJson = """
        {
            "Id": "inv-2",
            "LineItems": [
                { "Description": "Development", "Quantity": 3, "UnitPrice": 150 }
            ]
        }
        """;

        var invoice = System.Text.Json.JsonSerializer.Deserialize<Invoice>(modernJson);
        Assert.NotNull(invoice);
        Assert.Single(invoice.LineItems);
        Assert.Equal("Development", invoice.LineItems[0].Description);
        Assert.Equal(450m, invoice.SubTotal);
    }

    [Fact]
    public void Invoice_JsonSerialization_WritesLineItemsProperty()
    {
        var invoice = new Invoice { Id = "inv-3" };
        invoice.LineItems.Add(new InvoiceLine { Description = "Design", Quantity = 1, UnitPrice = 80m });

        var json = System.Text.Json.JsonSerializer.Serialize(invoice);
        Assert.Contains("\"LineItems\"", json);
        Assert.DoesNotContain("\"LegacyLines\"", json);
    }

    [Fact]
    public void Invoice_LineTotalsAndSubTotal_AreRoundedToTwoDecimals()
    {
        var invoice = new Invoice { TaxRate = 0.14975m };
        invoice.LineItems.Add(new InvoiceLine { Quantity = 14.234m, UnitPrice = 103.05m }); // 1466.8137 -> 1466.81
        invoice.LineItems.Add(new InvoiceLine { Quantity = 1m, UnitPrice = -1466.81m, IsCreditUsed = true });

        Assert.Equal(1466.81m, invoice.LineItems[0].Total);
        Assert.Equal(0.00m, invoice.SubTotal);
        Assert.Equal(0.00m, invoice.TaxAmount);
        Assert.Equal(0.00m, invoice.GrandTotal);
        Assert.Equal(0.00m, invoice.BalanceDue);
    }

    [Fact]
    public void Invoice_DuplicateAsDraft_RegeneratesLineIdsAndClearsBillingLinks()
    {
        var originalLineId = "line-orig-1";
        var original = new Invoice
        {
            Id = "inv-orig",
            ProjectId = "proj-1",
            InvoiceNumber = "INV-2601-0001",
            Status = InvoiceStatus.Paid,
            PdfPath = "/data/PDFs/Invoices/inv-orig.pdf",
            IsOpeningBalance = true,
            MigrationBatchId = "batch-1",
            BilledTimeEntryIds = ["t1"],
            BilledPoItemIds = ["p1"],
            BilledExpenseItemIds = ["e1"],
            Payments = [new PaymentRecord { Amount = 100m }],
            LineItems =
            [
                new InvoiceLine
                {
                    Id = originalLineId,
                    Description = "Engineering",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    SourceId = "t1",
                    SourceType = "Time",
                    IsCreditUsed = true
                }
            ]
        };

        var copy = original.DuplicateAsDraft("INV-2601-0002", "Tester");

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("INV-2601-0002", copy.InvoiceNumber);
        Assert.Equal(InvoiceStatus.Draft, copy.Status);
        Assert.Equal(string.Empty, copy.PdfPath);
        Assert.Empty(copy.Payments);
        Assert.Null(copy.DatePaid);
        Assert.False(copy.IsOpeningBalance);
        Assert.Null(copy.MigrationBatchId);
        Assert.Empty(copy.BilledTimeEntryIds);
        Assert.Empty(copy.BilledPoItemIds);
        Assert.Empty(copy.BilledExpenseItemIds);

        Assert.Single(copy.LineItems);
        Assert.NotEqual(originalLineId, copy.LineItems[0].Id);
        Assert.Null(copy.LineItems[0].SourceId);
        Assert.Null(copy.LineItems[0].SourceType);
        Assert.False(copy.LineItems[0].IsCreditUsed);
        Assert.Equal(100m, copy.LineItems[0].UnitPrice);
    }
}

