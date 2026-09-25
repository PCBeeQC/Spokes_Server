using Spokes_Server.Core.Models.Projects;

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

        [Fact]
        public void Quote_PropertyMutations_WorkAsExpected()
        {
            var laborItem = new QuoteLaborItem();
            var itemRow = new QuoteItemRow();
            var paymentTerm = new PaymentTerm();
            var testDate = new DateTime(2026, 3, 15);
            var startDate = new DateOnly(2026, 3, 20);
            var deadline = new DateOnly(2026, 6, 30);

            var quote = new Quote
            {
                Id = "Q-123",
                ProjectId = "PRJ-001",
                QuoteNumber = "QUO-2026-001",
                Title = "Custom Estimate",
                Date = testDate,
                Status = "Approved",
                PdfPath = "/path/to/quote.pdf",
                TemplateId = "TMP-001",
                StartDate = startDate,
                Deadline = deadline,
                LaborItems = new List<QuoteLaborItem> { laborItem },
                ItemRows = new List<QuoteItemRow> { itemRow },
                PaymentTerms = new List<PaymentTerm> { paymentTerm },
                ShowMonthlyPayments = true,
                MonthlyPaymentMonths = 24,
                MonthlyPaymentDescription = "24 months financing",
                TaxRate = 0.05m,
                ShowTax = false,
                ScopeOfWork = "Full overhaul",
                MainImageBase64 = "base64data",
                TermsAndConditions = "Standard terms",
                IsConfidential = true,
                SignatureRequired = true,
                ShowIntermediateSignature = true,
                ClientRepresentativeName = "Alice Smith",
                CompanyRepresentativeName = "Bob Jones"
            };

            Assert.Equal("Q-123", quote.Id);
            Assert.Equal("PRJ-001", quote.ProjectId);
            Assert.Equal("QUO-2026-001", quote.QuoteNumber);
            Assert.Equal("Custom Estimate", quote.Title);
            Assert.Equal(testDate, quote.Date);
            Assert.Equal("Approved", quote.Status);
            Assert.Equal("/path/to/quote.pdf", quote.PdfPath);
            Assert.Equal("TMP-001", quote.TemplateId);
            Assert.Equal(startDate, quote.StartDate);
            Assert.Equal(deadline, quote.Deadline);
            Assert.Single(quote.LaborItems);
            Assert.Same(laborItem, quote.LaborItems[0]);
            Assert.Single(quote.ItemRows);
            Assert.Same(itemRow, quote.ItemRows[0]);
            Assert.Single(quote.PaymentTerms);
            Assert.Same(paymentTerm, quote.PaymentTerms[0]);
            Assert.True(quote.ShowMonthlyPayments);
            Assert.Equal(24, quote.MonthlyPaymentMonths);
            Assert.Equal("24 months financing", quote.MonthlyPaymentDescription);
            Assert.Equal(0.05m, quote.TaxRate);
            Assert.False(quote.ShowTax);
            Assert.Equal("Full overhaul", quote.ScopeOfWork);
            Assert.Equal("base64data", quote.MainImageBase64);
            Assert.Equal("Standard terms", quote.TermsAndConditions);
            Assert.True(quote.IsConfidential);
            Assert.True(quote.SignatureRequired);
            Assert.True(quote.ShowIntermediateSignature);
            Assert.Equal("Alice Smith", quote.ClientRepresentativeName);
            Assert.Equal("Bob Jones", quote.CompanyRepresentativeName);
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

        [Fact]
        public void QuoteLaborItem_PropertyMutations_WorkAsExpected()
        {
            var item = new QuoteLaborItem
            {
                Id = "QLI-001",
                WorkTypeId = "WT-1",
                WorkTypeName = "Engineering",
                SubTaskId = "ST-1",
                SubTaskName = "Design",
                Hours = 8.5m,
                Rate = 125.0m
            };

            Assert.Equal("QLI-001", item.Id);
            Assert.Equal("WT-1", item.WorkTypeId);
            Assert.Equal("Engineering", item.WorkTypeName);
            Assert.Equal("ST-1", item.SubTaskId);
            Assert.Equal("Design", item.SubTaskName);
            Assert.Equal(8.5m, item.Hours);
            Assert.Equal(125.0m, item.Rate);
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

        [Fact]
        public void QuoteItemRow_PropertyMutations_WorkAsExpected()
        {
            var row = new QuoteItemRow
            {
                Id = "QIR-001",
                Description = "Steel Plate",
                Quantity = 5m,
                IsClientBought = true,
                UnitCost = 50.0m,
                MarkupPercentage = 20.0m,
                UnitPrice = 60.0m
            };

            Assert.Equal("QIR-001", row.Id);
            Assert.Equal("Steel Plate", row.Description);
            Assert.Equal(5m, row.Quantity);
            Assert.True(row.IsClientBought);
            Assert.Equal(50.0m, row.UnitCost);
            Assert.Equal(20.0m, row.MarkupPercentage);
            Assert.Equal(60.0m, row.UnitPrice);
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
        public void PaymentTerm_PropertyMutations_WorkAsExpected()
        {
            var term = new PaymentTerm
            {
                Id = "PT-001",
                Description = "50% upfront",
                Percentage = 50m,
                Amount = 2500m,
                InvoiceId = "INV-100"
            };

            Assert.Equal("PT-001", term.Id);
            Assert.Equal("50% upfront", term.Description);
            Assert.Equal(50m, term.Percentage);
            Assert.Equal(2500m, term.Amount);
            Assert.Equal("INV-100", term.InvoiceId);
        }

        #endregion
    }
}
