using Spokes_Server.Core.Models.Accounting;

namespace Spokes_Server.Tests.Core.Models
{
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

            Assert.Empty(invoice.Lines);
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
        }

        [Fact]
        public void Invoice_FinancialProjections_CalculatedCorrectly()
        {
            var invoice = new Invoice
            {
                TaxRate = 0.10m, // 10% tax for easy math
            };

            invoice.Lines.Add(new InvoiceLine { Quantity = 2, UnitPrice = 50 });  // $100
            invoice.Lines.Add(new InvoiceLine { Quantity = 1, UnitPrice = 100 }); // $100

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
            invoice.Lines.Add(new InvoiceLine { Quantity = 2, OriginalUnitCost = 30m });
            invoice.Lines.Add(new InvoiceLine { Quantity = 3, OriginalUnitCost = 25m });

            Assert.Equal(135m, invoice.TotalCost);
        }

        [Fact]
        public void GrossMargin_WithLines_CalculatesSubTotalMinusTotalCost()
        {
            var invoice = new Invoice();
            invoice.Lines.Add(new InvoiceLine { Quantity = 2, UnitPrice = 50m, OriginalUnitCost = 30m }); // Subtotal = 100, Cost = 60
            invoice.Lines.Add(new InvoiceLine { Quantity = 3, UnitPrice = 40m, OriginalUnitCost = 25m }); // Subtotal = 120, Cost = 75

            Assert.Equal(220m, invoice.SubTotal);
            Assert.Equal(135m, invoice.TotalCost);
            Assert.Equal(85m, invoice.GrossMargin);
        }

        [Fact]
        public void AmountPaid_WhenPaymentsEmpty_ReturnsLegacyAmountPaid()
        {
            var invoice = new Invoice
            {
                LegacyAmountPaid = 75m
            };

            Assert.Empty(invoice.Payments);
            Assert.Equal(75m, invoice.AmountPaid);
        }

        [Fact]
        public void BalanceDue_WhenPaymentsEmpty_ReturnsGrandTotalMinusLegacyAmountPaid()
        {
            var invoice = new Invoice
            {
                TaxRate = 0.10m,
                LegacyAmountPaid = 50m
            };
            invoice.Lines.Add(new InvoiceLine { Quantity = 2, UnitPrice = 100m }); // SubTotal = 200, GrandTotal = 220

            Assert.Equal(220m, invoice.GrandTotal);
            Assert.Equal(170m, invoice.BalanceDue);
        }

        [Fact]
        public void AmountPaid_WhenPaymentsExist_ReturnsSumOfPaymentsAndIgnoresLegacyAmountPaid()
        {
            var invoice = new Invoice
            {
                LegacyAmountPaid = 50m
            };
            invoice.Payments.Add(new PaymentRecord { Amount = 60m });
            invoice.Payments.Add(new PaymentRecord { Amount = 40m });

            Assert.Equal(100m, invoice.AmountPaid);
        }

        [Fact]
        public void BalanceDue_WhenPaymentsExist_ReturnsGrandTotalMinusPaymentsSum()
        {
            var invoice = new Invoice
            {
                TaxRate = 0.10m,
                LegacyAmountPaid = 50m
            };
            invoice.Lines.Add(new InvoiceLine { Quantity = 2, UnitPrice = 100m }); // SubTotal = 200, GrandTotal = 220
            invoice.Payments.Add(new PaymentRecord { Amount = 60m });
            invoice.Payments.Add(new PaymentRecord { Amount = 40m });

            Assert.Equal(120m, invoice.BalanceDue);
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
    }
}
