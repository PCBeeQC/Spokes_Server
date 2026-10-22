using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class InvoiceTests
{
    [Fact]
    public void Invoice_Initialization_SetsDefaultsCorrectly()
    {
        var invoice = new Invoice();

        Assert.False(string.IsNullOrEmpty(invoice.Id));
        Assert.Equal(string.Empty, invoice.ProjectId);
        Assert.Null(invoice.ProjectGroupId);
        Assert.Equal(string.Empty, invoice.InvoiceNumber);

        Assert.Equal(DateTime.Today, invoice.Date);
        Assert.Equal(DateTime.Today.AddDays(30), invoice.DueDate);
        Assert.Equal("Draft", invoice.Status);

        Assert.Null(invoice.TemplateId);
        Assert.False(invoice.HideCostAndMarkup);
        Assert.Equal(string.Empty, invoice.Note);

        Assert.Empty(invoice.LineItems);
        Assert.Empty(invoice.BilledTimeEntryIds);
        Assert.Empty(invoice.BilledPoItemIds);
        Assert.Empty(invoice.BilledExpenseItemIds);

        Assert.Equal(0.14975m, invoice.TaxRate);
        Assert.Equal(0, invoice.SubTotal);
        Assert.Equal(0, invoice.TaxAmount);
        Assert.Equal(0, invoice.GrandTotal);

        Assert.Equal(0, invoice.AmountPaid);
        Assert.Equal(0, invoice.BalanceDue);
        Assert.Null(invoice.DatePaid);
        
        Assert.Equal(string.Empty, invoice.PaymentTerms);
    }

    [Fact]
    public void Invoice_PaymentTerms_CanBeModifiedAndPersisted()
    {
        var invoice = new Invoice { PaymentTerms = "Net 45 days. **Late fees** apply." };
        Assert.Equal("Net 45 days. **Late fees** apply.", invoice.PaymentTerms);
        
        var json = System.Text.Json.JsonSerializer.Serialize(invoice);
        Assert.Contains("\"PaymentTerms\":\"Net 45 days. **Late fees** apply.\"", json);
    }

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
    public void InvoiceLine_Initialization_SetsDefaultsCorrectly()
    {
        var line = new InvoiceLine();

        Assert.Equal(string.Empty, line.Description);
        Assert.Equal(0, line.Quantity);
        Assert.Equal(0, line.UnitPrice);
        Assert.Equal(0, line.Total);

        Assert.False(line.IsPrepayment);
        Assert.False(line.IsCreditUsed);

        Assert.Equal("General", line.Type);
        Assert.Equal(0, line.OriginalUnitCost);
        Assert.Equal(0, line.MarkupPercentage);
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
    public void Invoice_JsonDeserialization_MigratesLegacyAmountPaidToPaymentRecord()
    {
        const string legacyJson = "{\"Id\":\"inv-legacy-1\",\"AmountPaid\":75.50,\"Status\":\"Final\"}";
        var invoice = System.Text.Json.JsonSerializer.Deserialize<Invoice>(legacyJson);

        Assert.NotNull(invoice);
        Assert.Single(invoice.Payments);
        Assert.Equal(75.50m, invoice.Payments[0].Amount);
        Assert.Equal("Legacy", invoice.Payments[0].Method);
        Assert.Equal(75.50m, invoice.AmountPaid);
    }

    [Fact]
    public void Invoice_Equals_SameReference_ReturnsTrue()
    {
        var invoice = new Invoice { Id = "INV-001" };

        Assert.True(invoice.Equals(invoice));
    }

    [Fact]
    public void Invoice_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var invoice1 = new Invoice { Id = "INV-001" };
        var invoice2 = new Invoice { Id = "INV-001" };

        Assert.True(invoice1.Equals(invoice2));
        Assert.True(invoice2.Equals(invoice1));
        Assert.Equal(invoice1.GetHashCode(), invoice2.GetHashCode());
    }

    [Fact]
    public void Invoice_Equals_DifferentId_ReturnsFalse()
    {
        var invoice1 = new Invoice { Id = "INV-001" };
        var invoice2 = new Invoice { Id = "INV-002" };

        Assert.False(invoice1.Equals(invoice2));
        Assert.False(invoice2.Equals(invoice1));
    }

    [Fact]
    public void Invoice_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var invoice = new Invoice { Id = "INV-001" };

        Assert.False(invoice.Equals(null));
        Assert.False(invoice.Equals("INV-001"));
        Assert.False(invoice.Equals(new object()));
    }

    [Fact]
    public void InvoiceLine_Equals_SameReference_ReturnsTrue()
    {
        var line = new InvoiceLine { Id = "LINE-001" };

        Assert.True(line.Equals(line));
    }

    [Fact]
    public void InvoiceLine_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var line1 = new InvoiceLine { Id = "LINE-001" };
        var line2 = new InvoiceLine { Id = "LINE-001" };

        Assert.True(line1.Equals(line2));
        Assert.True(line2.Equals(line1));
        Assert.Equal(line1.GetHashCode(), line2.GetHashCode());
    }

    [Fact]
    public void InvoiceLine_Equals_DifferentId_ReturnsFalse()
    {
        var line1 = new InvoiceLine { Id = "LINE-001" };
        var line2 = new InvoiceLine { Id = "LINE-002" };

        Assert.False(line1.Equals(line2));
        Assert.False(line2.Equals(line1));
    }

    [Fact]
    public void InvoiceLine_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var line = new InvoiceLine { Id = "LINE-001" };

        Assert.False(line.Equals(null));
        Assert.False(line.Equals("LINE-001"));
        Assert.False(line.Equals(new object()));
    }

#pragma warning disable CS0618 // Obsolete member testing
    [Fact]
    public void Invoice_LineItems_And_Lines_AreSynchronized()
    {
        var invoice = new Invoice();
        var item = new InvoiceLine { Description = "Test Service", Quantity = 2, UnitPrice = 50m };

        invoice.LineItems.Add(item);
        Assert.Single(invoice.Lines);
        Assert.Same(item, invoice.Lines[0]);
        Assert.Equal(100m, invoice.SubTotal);

        var item2 = new InvoiceLine { Description = "Parts", Quantity = 1, UnitPrice = 25m };
        invoice.Lines.Add(item2);
        Assert.Equal(2, invoice.LineItems.Count);
        Assert.Equal(125m, invoice.SubTotal);
    }

    [Fact]
    public void Invoice_JsonDeserialization_SupportsLegacyLinesProperty()
    {
        const string legacyJson = """
        {
            "Id": "inv-1",
            "Lines": [
                { "Description": "Consulting", "Quantity": 2, "UnitPrice": 100 }
            ]
        }
        """;

        var invoice = System.Text.Json.JsonSerializer.Deserialize<Invoice>(legacyJson);
        Assert.NotNull(invoice);
        Assert.Single(invoice.LineItems);
        Assert.Equal("Consulting", invoice.LineItems[0].Description);
        Assert.Equal("Consulting", invoice.Lines[0].Description);
        Assert.Equal(200m, invoice.SubTotal);
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
        Assert.Equal("Development", invoice.Lines[0].Description);
        Assert.Equal(450m, invoice.SubTotal);
    }
#pragma warning restore CS0618

    [Fact]
    public void Invoice_JsonSerialization_WritesLineItemsProperty()
    {
        var invoice = new Invoice { Id = "inv-3" };
        invoice.LineItems.Add(new InvoiceLine { Description = "Design", Quantity = 1, UnitPrice = 80m });

        var json = System.Text.Json.JsonSerializer.Serialize(invoice);
        Assert.Contains("\"LineItems\"", json);
        Assert.DoesNotContain("\"LegacyLines\"", json);
    }
}

