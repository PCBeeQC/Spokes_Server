using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

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
    }
}


