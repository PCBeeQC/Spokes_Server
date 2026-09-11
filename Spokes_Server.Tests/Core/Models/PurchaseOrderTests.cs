using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.Linq;

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
    }
}


