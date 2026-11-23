using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models.Projects;

public class QuoteTests
{
    [Fact]
    public void Quote_Financials_CalculatedCorrectly()
    {
        var quote = new Quote { TaxRate = 0.15m, ShowTax = true };

        // Labor: 10 hrs * $100 = $1000
        quote.LaborItems.Add(new QuoteLaborItem { Hours = 10, Rate = 100 });

        // Items: 2 * $250 = $500
        quote.MaterialLineItems.Add(new QuoteItemRow { Quantity = 2, UnitPrice = 250, IsClientBought = false });

        // Client Bought: 1 * $1000 = $1000 (Should not be in SubTotal)
        quote.MaterialLineItems.Add(new QuoteItemRow { Quantity = 1, UnitPrice = 1000, IsClientBought = true });

        Assert.Equal(1000m, quote.TotalLabor);
        Assert.Equal(500m, quote.TotalItems);
        Assert.Equal(1000m, quote.TotalClientBought);

        // Subtotal = 1000 + 500 = 1500
        Assert.Equal(1500m, quote.SubTotal);

        // Tax = 1500 * 0.15 = 225
        Assert.Equal(225m, quote.TaxAmount);

        // GrandTotal = 1500 + 225 = 1725
        Assert.Equal(1725m, quote.GrandTotal);

        // TotalProjectBudget = 1725 + 1000 = 2725
        Assert.Equal(2725m, quote.TotalProjectBudget);
    }

    [Fact]
    public void Quote_ShowTax_Disabled_SetsTaxAmountToZero()
    {
        var quote = new Quote { TaxRate = 0.15m, ShowTax = false };
        quote.LaborItems.Add(new QuoteLaborItem { Hours = 1, Rate = 100 });

        Assert.Equal(100m, quote.SubTotal);
        Assert.Equal(0m, quote.TaxAmount);
        Assert.Equal(100m, quote.GrandTotal);
    }

    #region Quote Tests

    [Fact]
    public void Quote_Equals_SameReference_ReturnsTrue()
    {
        var quote = new Quote { Id = "Q-001" };
        Assert.True(quote.Equals(quote));
    }

    [Fact]
    public void Quote_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var quote1 = new Quote { Id = "Q-001" };
        var quote2 = new Quote { Id = "Q-001" };

        Assert.True(quote1.Equals(quote2));
        Assert.True(quote2.Equals(quote1));
        Assert.Equal(quote1.GetHashCode(), quote2.GetHashCode());
    }

    [Fact]
    public void Quote_Equals_DifferentId_ReturnsFalse()
    {
        var quote1 = new Quote { Id = "Q-001" };
        var quote2 = new Quote { Id = "Q-002" };

        Assert.False(quote1.Equals(quote2));
        Assert.False(quote2.Equals(quote1));
    }

    [Fact]
    public void Quote_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var quote = new Quote { Id = "Q-001" };

        Assert.False(quote.Equals(null));
        Assert.False(quote.Equals("Q-001"));
        Assert.False(quote.Equals(new object()));
    }

    [Fact]
    public void Quote_GetHashCode_NullId_FallsBackToBaseHashCode()
    {
        var quote = new Quote { Id = null! };
        var hash = quote.GetHashCode();

        Assert.Equal(hash, quote.GetHashCode());
    }

    #endregion

    #region QuoteLaborItem Tests

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(10, 50, 500)]
    [InlineData(7.5, 120.50, 903.75)]
    public void QuoteLaborItem_Total_CalculatedCorrectly(decimal hours, decimal rate, decimal expectedTotal)
    {
        var item = new QuoteLaborItem { Hours = hours, Rate = rate };
        Assert.Equal(expectedTotal, item.Total);
    }

    [Fact]
    public void QuoteLaborItem_Equals_SameReference_ReturnsTrue()
    {
        var item = new QuoteLaborItem { Id = "QLI-001" };
        Assert.True(item.Equals(item));
    }

    [Fact]
    public void QuoteLaborItem_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var item1 = new QuoteLaborItem { Id = "QLI-001" };
        var item2 = new QuoteLaborItem { Id = "QLI-001" };

        Assert.True(item1.Equals(item2));
        Assert.True(item2.Equals(item1));
        Assert.Equal(item1.GetHashCode(), item2.GetHashCode());
    }

    [Fact]
    public void QuoteLaborItem_Equals_DifferentId_ReturnsFalse()
    {
        var item1 = new QuoteLaborItem { Id = "QLI-001" };
        var item2 = new QuoteLaborItem { Id = "QLI-002" };

        Assert.False(item1.Equals(item2));
        Assert.False(item2.Equals(item1));
    }

    [Fact]
    public void QuoteLaborItem_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var item = new QuoteLaborItem { Id = "QLI-001" };

        Assert.False(item.Equals(null));
        Assert.False(item.Equals("QLI-001"));
        Assert.False(item.Equals(new object()));
    }

    [Fact]
    public void QuoteLaborItem_GetHashCode_NullId_FallsBackToBaseHashCode()
    {
        var item = new QuoteLaborItem { Id = null! };
        var hash = item.GetHashCode();

        Assert.Equal(hash, item.GetHashCode());
    }

    #endregion

    #region QuoteItemRow Tests

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 45.5, 136.5)]
    [InlineData(10.5, 20, 210)]
    public void QuoteItemRow_Total_CalculatedCorrectly(decimal quantity, decimal unitPrice, decimal expectedTotal)
    {
        var row = new QuoteItemRow { Quantity = quantity, UnitPrice = unitPrice };
        Assert.Equal(expectedTotal, row.Total);
    }

    [Fact]
    public void QuoteItemRow_Equals_SameReference_ReturnsTrue()
    {
        var row = new QuoteItemRow { Id = "QIR-001" };
        Assert.True(row.Equals(row));
    }

    [Fact]
    public void QuoteItemRow_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var row1 = new QuoteItemRow { Id = "QIR-001" };
        var row2 = new QuoteItemRow { Id = "QIR-001" };

        Assert.True(row1.Equals(row2));
        Assert.True(row2.Equals(row1));
        Assert.Equal(row1.GetHashCode(), row2.GetHashCode());
    }

    [Fact]
    public void QuoteItemRow_Equals_DifferentId_ReturnsFalse()
    {
        var row1 = new QuoteItemRow { Id = "QIR-001" };
        var row2 = new QuoteItemRow { Id = "QIR-002" };

        Assert.False(row1.Equals(row2));
        Assert.False(row2.Equals(row1));
    }

    [Fact]
    public void QuoteItemRow_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var row = new QuoteItemRow { Id = "QIR-001" };

        Assert.False(row.Equals(null));
        Assert.False(row.Equals("QIR-001"));
        Assert.False(row.Equals(new object()));
    }

    [Fact]
    public void QuoteItemRow_GetHashCode_NullId_FallsBackToBaseHashCode()
    {
        var row = new QuoteItemRow { Id = null! };
        var hash = row.GetHashCode();

        Assert.Equal(hash, row.GetHashCode());
    }

    #endregion

    #region PaymentTerm Tests

    [Theory]
    [InlineData("INV-001", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PaymentTerm_IsInvoiced_ReturnsExpectedResult(string? invoiceId, bool expected)
    {
        var term = new PaymentTerm { InvoiceId = invoiceId! };
        Assert.Equal(expected, term.IsInvoiced);
    }

    [Fact]
    public void PaymentTerm_Equals_SameReference_ReturnsTrue()
    {
        var term = new PaymentTerm { Id = "PT-001" };
        Assert.True(term.Equals(term));
    }

    [Fact]
    public void PaymentTerm_EqualsAndGetHashCode_SameId_ReturnsTrueAndEqualHashCode()
    {
        var term1 = new PaymentTerm { Id = "PT-001" };
        var term2 = new PaymentTerm { Id = "PT-001" };

        Assert.True(term1.Equals(term2));
        Assert.True(term2.Equals(term1));
        Assert.Equal(term1.GetHashCode(), term2.GetHashCode());
    }

    [Fact]
    public void PaymentTerm_Equals_DifferentId_ReturnsFalse()
    {
        var term1 = new PaymentTerm { Id = "PT-001" };
        var term2 = new PaymentTerm { Id = "PT-002" };

        Assert.False(term1.Equals(term2));
        Assert.False(term2.Equals(term1));
    }

    [Fact]
    public void PaymentTerm_Equals_NullOrDifferentType_ReturnsFalse()
    {
        var term = new PaymentTerm { Id = "PT-001" };

        Assert.False(term.Equals(null));
        Assert.False(term.Equals("PT-001"));
        Assert.False(term.Equals(new object()));
    }

    [Fact]
    public void PaymentTerm_GetHashCode_NullId_FallsBackToBaseHashCode()
    {
        var term = new PaymentTerm { Id = null! };
        var hash = term.GetHashCode();

        Assert.Equal(hash, term.GetHashCode());
    }

    [Fact]
    public void Quote_JsonDeserialization_SupportsLegacyItemRowsProperty()
    {
        const string legacyJson = """
        {
            "Id": "quo-1",
            "ItemRows": [
                { "Description": "Enclosure", "Quantity": 1, "UnitPrice": 200, "IsClientBought": false }
            ]
        }
        """;

        var quote = System.Text.Json.JsonSerializer.Deserialize<Quote>(legacyJson);
        Assert.NotNull(quote);
        Assert.Single(quote.MaterialLineItems);
        Assert.Equal("Enclosure", quote.MaterialLineItems[0].Description);
        Assert.Equal(200m, quote.TotalItems);
    }

    [Fact]
    public void Quote_JsonDeserialization_SupportsMaterialLineItemsProperty()
    {
        const string modernJson = """
        {
            "Id": "quo-2",
            "MaterialLineItems": [
                { "Description": "Controller", "Quantity": 2, "UnitPrice": 300, "IsClientBought": false }
            ]
        }
        """;

        var quote = System.Text.Json.JsonSerializer.Deserialize<Quote>(modernJson);
        Assert.NotNull(quote);
        Assert.Single(quote.MaterialLineItems);
        Assert.Equal("Controller", quote.MaterialLineItems[0].Description);
        Assert.Equal(600m, quote.TotalItems);
    }

    [Fact]
    public void Quote_JsonSerialization_WritesMaterialLineItemsProperty()
    {
        var quote = new Quote { Id = "quo-3" };
        quote.MaterialLineItems.Add(new QuoteItemRow { Description = "Power supply", Quantity = 1, UnitPrice = 75m });

        var json = System.Text.Json.JsonSerializer.Serialize(quote);
        Assert.Contains("\"MaterialLineItems\"", json);
        Assert.DoesNotContain("\"LegacyItemRows\"", json);
    }

    [Fact]
    public void Quote_TotalsAndTax_AreRoundedToTwoDecimals()
    {
        var quote = new Quote { TaxRate = 0.14975m, ShowTax = true };
        quote.LaborItems.Add(new QuoteLaborItem { Hours = 1.333m, Rate = 100.01m }); // 133.31333 -> 133.31
        quote.MaterialLineItems.Add(new QuoteItemRow { Quantity = 3m, UnitPrice = 10.335m }); // 31.005 -> 31.01

        Assert.Equal(133.31m, quote.LaborItems[0].Total);
        Assert.Equal(31.01m, quote.MaterialLineItems[0].Total);
        Assert.Equal(164.32m, quote.SubTotal);
        Assert.Equal(24.61m, quote.TaxAmount); // 164.32 * 0.14975 = 24.60692 -> 24.61
        Assert.Equal(188.93m, quote.GrandTotal);
    }

    [Fact]
    public void Quote_DuplicateAsDraft_RegeneratesChildItemIdsAndClearsPdf()
    {
        var original = new Quote
        {
            Id = "quo-orig",
            ProjectId = "proj-1",
            QuoteNumber = "EST-2601-0001",
            Title = "Original Quote",
            Status = QuoteStatus.Accepted,
            PdfPath = "/data/PDFs/Quotes/quo-orig.pdf",
            LaborItems = [new QuoteLaborItem { Id = "labor-1", Hours = 5m, Rate = 100m }],
            MaterialLineItems = [new QuoteItemRow { Id = "mat-1", Quantity = 2m, UnitPrice = 50m }],
            PaymentTerms = [new PaymentTerm { Id = "term-1", Description = "Deposit", Percentage = 50m }]
        };

        var copy = original.DuplicateAsDraft("EST-2601-0002", "Tester");

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("EST-2601-0002", copy.QuoteNumber);
        Assert.Equal(QuoteStatus.Draft, copy.Status);
        Assert.Equal(string.Empty, copy.PdfPath);
        Assert.Equal("Original Quote (Copy)", copy.Title);
        Assert.NotEqual("labor-1", copy.LaborItems[0].Id);
        Assert.NotEqual("mat-1", copy.MaterialLineItems[0].Id);
        Assert.NotEqual("term-1", copy.PaymentTerms[0].Id);
    }

    #endregion
}

