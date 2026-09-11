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
    public class DocumentTemplateTests
    {
        [Fact]
        public void DocumentTemplate_Initialization_SetsDefaultsCorrectly()
        {
            var template = new DocumentTemplate();

            Assert.False(string.IsNullOrEmpty(template.Id));
            Assert.Equal("Standard English", template.Name);
            Assert.Equal("en", template.LanguageCode);
            Assert.False(template.IsDefault);

            // Financials
            Assert.Equal(0.14975m, template.TaxRate);
            Assert.Equal("$", template.CurrencySymbol);
            Assert.False(template.CurrencySymbolAfter);

            // Banking
            Assert.Equal(string.Empty, template.BankName);
            Assert.Equal(string.Empty, template.BankAccountNumber);
            Assert.Equal(string.Empty, template.BankSwiftCode);
            Assert.Equal(string.Empty, template.TaxId1);
            Assert.Equal(string.Empty, template.TaxId2);
            Assert.Equal(string.Empty, template.PaymentInstructions);

            // Basic Labels
            Assert.Equal("INVOICE", template.Label_Invoice);
            Assert.Equal("QUOTE", template.Label_Quote);
            Assert.Equal("PURCHASE ORDER", template.Label_PurchaseOrder);

            // Line Itmes
            Assert.Equal("Description", template.Label_Description);
            Assert.Equal("Qty", template.Label_Quantity);
            Assert.Equal("Price", template.Label_Price);
            Assert.Equal("Total", template.Label_Total);

            // Totals
            Assert.Equal("Subtotal", template.Label_Subtotal);
            Assert.Equal("Tax", template.Label_Tax);
            Assert.Equal("Total Due", template.Label_GrandTotal);
            Assert.Equal("Total Budget", template.Label_QuoteTotal);
            Assert.Equal("Balance Due", template.Label_BalanceDue);
            Assert.Equal("Amount Paid", template.Label_AmountPaid);

            // Client Bought
            Assert.Equal(" (Client Responsibility)", template.Label_ClientBoughtTag);
            Assert.Equal("Client Bought Items", template.Label_ClientBoughtTotal);

            // Payment Instructions Labels
            Assert.Equal("Payment Instructions", template.Label_PaymentInstructions);
            Assert.Equal("Bank", template.Label_BankName);
            Assert.Equal("Account", template.Label_BankAccount);
            Assert.Equal("Swift / Transit", template.Label_SwiftCode);

            // Other Labels
            Assert.Equal("Tax ID 1", template.Label_TaxId1);
            Assert.Equal("Tax ID 2", template.Label_TaxId2);
            Assert.Equal("Project Manager", template.Label_ProjectManager);
            Assert.Equal("(Labor)", template.Label_ItemLaborTag);
            Assert.Equal("Block of Hours", template.Label_BlockOfHours);

            // Content Defaults
            Assert.Contains("Validity", template.DefaultTermsAndConditions);

            // Proposal Labels
            Assert.Equal("Scope of Work", template.Label_ScopeOfWork);
            Assert.Equal("Terms & Conditions", template.Label_TermsAndConditions);
            Assert.Equal("Acceptance", template.Label_Acceptance);
            Assert.Equal("Authorized Signature", template.Label_Signature);
            Assert.Equal("Date", template.Label_DateSigned);

            // Quote Specific
            Assert.Equal("Quote For", template.Label_QuoteTo);
            Assert.Equal("Presented By", template.Label_PresentedBy);
            Assert.Equal("Email", template.Label_ManagerEmail);
            Assert.Contains("By signing below", template.Label_AcceptanceText);

            // Confidentiality
            Assert.Equal("CONFIDENTIAL", template.Label_Confidential);
            Assert.Contains("proprietary", template.Label_ConfidentialText);

            // Signatures
            Assert.Equal("Client Signature", template.Label_SignatureClient);
            Assert.Equal("Authorized Signature (Spokes)", template.Label_SignatureCompany);

            // Other
            Assert.Equal("Pricing Breakdown", template.Label_PricingBreakdown);
            Assert.Equal("Payment Schedule", template.Label_PaymentSchedule);
            Assert.Contains("Time & Materials", template.DefaultCostPlusTerms);

            // PO
            Assert.Equal("VENDOR", template.Label_Vendor);
            Assert.Equal("Issued By", template.Label_IssuedBy);
            Assert.Equal("Total", template.Label_PoTotal);
            Assert.Equal("Supplier Quote Ref", template.Label_SupplierRef);

            // Colors
            Assert.Equal("#594AE2", template.ColorPrimary);
            Assert.Equal("#1E88E5", template.ColorSecondary);

            // Meta
            Assert.Equal("Date", template.Label_Date);
            Assert.Equal("Due Date", template.Label_DueDate);
            Assert.Equal("Bill To", template.Label_BillTo);
            Assert.Equal("Ship To", template.Label_ShipTo);
            Assert.Equal("Project #", template.Label_ProjectNumber);
        }
    }
}


