using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Models.Accounting;

public class PurchaseOrderTests
{
    [Fact]
    public void PurchaseOrder_Initialization_SetsDefaultsCorrectly()
    {
        var po = new PurchaseOrder();

        Assert.False(string.IsNullOrEmpty(po.Id));
        Assert.Equal(string.Empty, po.PoNumber);
        Assert.Equal(DateTime.Today, po.Date);
        Assert.Equal(string.Empty, po.IssuedBy);
        Assert.Equal(string.Empty, po.SupplierQuoteRef);
        Assert.Equal(string.Empty, po.SupplierRepName);
        Assert.NotNull(po.Supplier);
        Assert.Null(po.Recipient);
        Assert.True(po.ApplyTax);
        Assert.Equal(0.14975m, po.TaxRate);
        Assert.Empty(po.LineItems);
        Assert.Empty(po.CustomHeaders);
        Assert.Null(po.TemplateId);

        Assert.Equal(0, po.SubTotal);
        Assert.Equal(0, po.TaxAmount);
        Assert.Equal(0, po.GrandTotal);
        Assert.Equal(0, po.TotalBilled);
        Assert.Equal(0, po.RemainingBalance);
    }

    [Fact]
    public void PurchaseOrder_Financials_CalculatedCorrectlyWithTax()
    {
        var po = new PurchaseOrder { ApplyTax = true, TaxRate = 0.15m };
        po.LineItems.Add(new PoItem { Quantity = 2, UnitPrice = 100 });
        po.LineItems.Add(new PoItem { Quantity = 1, UnitPrice = 300 });

        // Subtotal: (2*100) + (1*300) = 500
        // Tax: 500 * 0.15 = 75
        // GrandTotal: 575

        Assert.Equal(500m, po.SubTotal);
        Assert.Equal(75m, po.TaxAmount);
        Assert.Equal(575m, po.GrandTotal);
    }

    [Fact]
    public void PurchaseOrder_Financials_CalculatedCorrectlyWithoutTax()
    {
        var po = new PurchaseOrder { ApplyTax = false };
        po.LineItems.Add(new PoItem { Quantity = 1, UnitPrice = 1000 });

        Assert.Equal(1000m, po.SubTotal);
        Assert.Equal(0m, po.TaxAmount);
        Assert.Equal(1000m, po.GrandTotal);
    }

    [Fact]
    public void PoItem_Initialization_SetsDefaultsCorrectly()
    {
        var item = new PoItem();

        Assert.False(string.IsNullOrEmpty(item.Id));
        Assert.Equal(string.Empty, item.ProjectId);
        Assert.Equal(string.Empty, item.PartNumber);
        Assert.Equal(string.Empty, item.Description);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(0, item.UnitPrice);
        Assert.Equal(0, item.Total);
        Assert.Empty(item.CustomData);
    }

    [Fact]
    public void PoItem_Total_CalculatedCorrectly()
    {
        var item = new PoItem { Quantity = 1.5m, UnitPrice = 200m };
        Assert.Equal(300m, item.Total);
    }

    [Fact]
    public void RemainingBalance_WhenUnbilled_ReturnsGrandTotal()
    {
        var po = new PurchaseOrder { ApplyTax = false };
        po.LineItems.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
        po.TotalBilled = 0m;

        Assert.Equal(500m, po.RemainingBalance);
    }

    [Fact]
    public void RemainingBalance_WhenPartiallyBilled_ReturnsRemainingAmount()
    {
        var po = new PurchaseOrder { ApplyTax = false };
        po.LineItems.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
        po.TotalBilled = 150m;

        Assert.Equal(350m, po.RemainingBalance);
    }

    [Fact]
    public void RemainingBalance_WhenFullyBilled_ReturnsZero()
    {
        var po = new PurchaseOrder { ApplyTax = false };
        po.LineItems.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
        po.TotalBilled = 500m;

        Assert.Equal(0m, po.RemainingBalance);
    }

    [Fact]
    public void PurchaseOrder_Equals_And_GetHashCode()
    {
        var id = Guid.NewGuid().ToString();
        var po1 = new PurchaseOrder { Id = id, PoNumber = "PO-2401-001" };
        var po2 = new PurchaseOrder { Id = id, PoNumber = "PO-2401-002" };
        var po3 = new PurchaseOrder { Id = Guid.NewGuid().ToString(), PoNumber = "PO-2401-003" };

        // Reference equality
        Assert.True(po1.Equals(po1));

        // Id-based equality
        Assert.True(po1.Equals(po2));
        Assert.True(po2.Equals(po1));
        Assert.Equal(po1.GetHashCode(), po2.GetHashCode());

        // Unequal IDs
        Assert.False(po1.Equals(po3));
        Assert.False(po3.Equals(po1));

        // Null and different type inequality
        Assert.False(po1.Equals(null));
        Assert.False(po1.Equals("some string"));
        Assert.False(po1.Equals(new object()));
    }

    [Fact]
    public void PoItem_Equals_And_GetHashCode()
    {
        var id = Guid.NewGuid().ToString();
        var item1 = new PoItem { Id = id, Description = "Part A" };
        var item2 = new PoItem { Id = id, Description = "Part B" };
        var item3 = new PoItem { Id = Guid.NewGuid().ToString(), Description = "Part C" };

        // Reference equality
        Assert.True(item1.Equals(item1));

        // Id-based equality
        Assert.True(item1.Equals(item2));
        Assert.True(item2.Equals(item1));
        Assert.Equal(item1.GetHashCode(), item2.GetHashCode());

        // Unequal IDs
        Assert.False(item1.Equals(item3));
        Assert.False(item3.Equals(item1));

        // Null and different type inequality
        Assert.False(item1.Equals(null));
        Assert.False(item1.Equals("some string"));
        Assert.False(item1.Equals(new object()));
    }

#pragma warning disable CS0618 // Obsolete member testing
    [Fact]
    public void PurchaseOrder_LineItems_And_Items_AreSynchronized()
    {
        var po = new PurchaseOrder();
        var item = new PoItem { Description = "Widget A", Quantity = 3, UnitPrice = 10m };

        po.LineItems.Add(item);
        Assert.Single(po.Items);
        Assert.Same(item, po.Items[0]);
        Assert.Equal(30m, po.SubTotal);

        var item2 = new PoItem { Description = "Widget B", Quantity = 1, UnitPrice = 20m };
        po.Items.Add(item2);
        Assert.Equal(2, po.LineItems.Count);
        Assert.Equal(50m, po.SubTotal);
    }

    [Fact]
    public void PurchaseOrder_JsonDeserialization_SupportsLegacyItemsProperty()
    {
        const string legacyJson = """
        {
            "Id": "po-1",
            "Items": [
                { "Description": "Bolts", "Quantity": 10, "UnitPrice": 2 }
            ]
        }
        """;

        var po = System.Text.Json.JsonSerializer.Deserialize<PurchaseOrder>(legacyJson);
        Assert.NotNull(po);
        Assert.Single(po.LineItems);
        Assert.Equal("Bolts", po.LineItems[0].Description);
        Assert.Equal("Bolts", po.Items[0].Description);
        Assert.Equal(20m, po.SubTotal);
    }

    [Fact]
    public void PurchaseOrder_JsonDeserialization_SupportsLineItemsProperty()
    {
        const string modernJson = """
        {
            "Id": "po-2",
            "LineItems": [
                { "Description": "Screws", "Quantity": 20, "UnitPrice": 1 }
            ]
        }
        """;

        var po = System.Text.Json.JsonSerializer.Deserialize<PurchaseOrder>(modernJson);
        Assert.NotNull(po);
        Assert.Single(po.LineItems);
        Assert.Equal("Screws", po.LineItems[0].Description);
        Assert.Equal("Screws", po.Items[0].Description);
        Assert.Equal(20m, po.SubTotal);
    }
#pragma warning restore CS0618

    [Fact]
    public void PurchaseOrder_JsonSerialization_WritesLineItemsProperty()
    {
        var po = new PurchaseOrder { Id = "po-3" };
        po.LineItems.Add(new PoItem { Description = "Steel plate", Quantity = 1, UnitPrice = 150m });

        var json = System.Text.Json.JsonSerializer.Serialize(po);
        Assert.Contains("\"LineItems\"", json);
        Assert.DoesNotContain("\"LegacyItems\"", json);
    }
}

