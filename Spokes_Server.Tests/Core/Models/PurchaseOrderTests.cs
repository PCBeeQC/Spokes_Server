using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Models
{
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
            Assert.Empty(po.Items);
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
            po.Items.Add(new PoItem { Quantity = 2, UnitPrice = 100 });
            po.Items.Add(new PoItem { Quantity = 1, UnitPrice = 300 });

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
            po.Items.Add(new PoItem { Quantity = 1, UnitPrice = 1000 });

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
            po.Items.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
            po.TotalBilled = 0m;

            Assert.Equal(500m, po.RemainingBalance);
        }

        [Fact]
        public void RemainingBalance_WhenPartiallyBilled_ReturnsRemainingAmount()
        {
            var po = new PurchaseOrder { ApplyTax = false };
            po.Items.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
            po.TotalBilled = 150m;

            Assert.Equal(350m, po.RemainingBalance);
        }

        [Fact]
        public void RemainingBalance_WhenFullyBilled_ReturnsZero()
        {
            var po = new PurchaseOrder { ApplyTax = false };
            po.Items.Add(new PoItem { Quantity = 2, UnitPrice = 250m });
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
    }
}
