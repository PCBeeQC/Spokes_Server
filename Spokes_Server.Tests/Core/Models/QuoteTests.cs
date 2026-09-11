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
    public class QuoteTests
    {
        [Fact]
        public void Quote_Initialization_SetsDefaultsCorrectly()
        {
            var quote = new Quote();

            Assert.False(string.IsNullOrEmpty(quote.Id));
            Assert.Equal(string.Empty, quote.ProjectId);
            Assert.Equal(string.Empty, quote.QuoteNumber);
            Assert.Equal("Main Estimate", quote.Title);
            Assert.Equal(DateTime.Today, quote.Date);
            Assert.Equal("Draft", quote.Status);
            Assert.Null(quote.TemplateId);
            Assert.Null(quote.StartDate);
            Assert.Null(quote.Deadline);

            Assert.Empty(quote.LaborItems);
            Assert.Empty(quote.ItemRows);
            Assert.Empty(quote.PaymentTerms);

            Assert.False(quote.ShowMonthlyPayments);
            Assert.Equal(12, quote.MonthlyPaymentMonths);
            Assert.Equal("Monthly payments over {0} months", quote.MonthlyPaymentDescription);

            Assert.Equal(0.0m, quote.TaxRate);
            Assert.True(quote.ShowTax);

            Assert.Equal(0, quote.TotalLabor);
            Assert.Equal(0, quote.TotalItems);
            Assert.Equal(0, quote.TotalClientBought);
            Assert.Equal(0, quote.SubTotal);
            Assert.Equal(0, quote.TaxAmount);
            Assert.Equal(0, quote.GrandTotal);
            Assert.Equal(0, quote.TotalProjectBudget);

            Assert.Equal(string.Empty, quote.ScopeOfWork);
            Assert.Equal(string.Empty, quote.MainImageBase64);
            Assert.Equal(string.Empty, quote.TermsAndConditions);
            Assert.False(quote.IsConfidential);
            Assert.False(quote.SignatureRequired);
            Assert.False(quote.ShowIntermediateSignature);
            Assert.Equal(string.Empty, quote.ClientRepresentativeName);
            Assert.Equal(string.Empty, quote.CompanyRepresentativeName);
        }

        [Fact]
        public void Quote_Financials_CalculatedCorrectly()
        {
            var quote = new Quote { TaxRate = 0.15m, ShowTax = true };

            // Labor: 10 hrs * $100 = $1000
            quote.LaborItems.Add(new QuoteLaborItem { Hours = 10, Rate = 100 });

            // Items: 2 * $250 = $500
            quote.ItemRows.Add(new QuoteItemRow { Quantity = 2, UnitPrice = 250, IsClientBought = false });

            // Client Bought: 1 * $1000 = $1000 (Should not be in SubTotal)
            quote.ItemRows.Add(new QuoteItemRow { Quantity = 1, UnitPrice = 1000, IsClientBought = true });

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

        [Fact]
        public void QuoteLaborItem_Initialization_SetsDefaults()
        {
            var item = new QuoteLaborItem();
            Assert.False(string.IsNullOrEmpty(item.Id));
            Assert.Equal(0, item.Hours);
            Assert.Equal(0, item.Rate);
            Assert.Equal(0, item.Total);
        }

        [Fact]
        public void QuoteItemRow_Initialization_SetsDefaults()
        {
            var row = new QuoteItemRow();
            Assert.False(string.IsNullOrEmpty(row.Id));
            Assert.Equal(1, row.Quantity);
            Assert.False(row.IsClientBought);
            Assert.Equal(0, row.UnitCost);
            Assert.Equal(0, row.UnitPrice);
            Assert.Equal(0, row.Total);
        }

        [Fact]
        public void PaymentTerm_Initialization_SetsDefaults()
        {
            var term = new PaymentTerm();
            Assert.False(string.IsNullOrEmpty(term.Id));
            Assert.Equal(string.Empty, term.InvoiceId);
            Assert.False(term.IsInvoiced);
        }
    }
}


