using Spokes_Server.Core.Models.Core;

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

        [Fact]
        public void Equals_SameReference_ReturnsTrue()
        {
            var template = new DocumentTemplate();

            Assert.True(template.Equals(template));
        }

        [Fact]
        public void Equals_SameId_ReturnsTrue()
        {
            var id = Guid.NewGuid().ToString();
            var template1 = new DocumentTemplate { Id = id, Name = "Template A" };
            var template2 = new DocumentTemplate { Id = id, Name = "Template B" };

            Assert.True(template1.Equals(template2));
            Assert.True(template2.Equals(template1));
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var template1 = new DocumentTemplate { Id = "id-1" };
            var template2 = new DocumentTemplate { Id = "id-2" };

            Assert.False(template1.Equals(template2));
        }

        [Fact]
        public void Equals_NullOrDifferentType_ReturnsFalse()
        {
            var template = new DocumentTemplate();

            Assert.False(template.Equals(null));
            Assert.False(template.Equals("string object"));
            Assert.False(template.Equals(new object()));
        }

        [Fact]
        public void GetHashCode_SameId_ReturnsSameHashCode()
        {
            var id = "template-id-12345";
            var template1 = new DocumentTemplate { Id = id };
            var template2 = new DocumentTemplate { Id = id };

            Assert.Equal(template1.GetHashCode(), template2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_NullId_DoesNotThrow()
        {
            var template = new DocumentTemplate { Id = null! };

            var exception = Record.Exception(() => template.GetHashCode());

            Assert.Null(exception);
        }

        [Fact]
        public void Properties_CanBeMutatedAndRead()
        {
            var template = new DocumentTemplate
            {
                Id = "custom-template-id",
                Name = "French Canadian",
                LanguageCode = "fr-CA",
                TaskTranslationLanguage = "fr",
                IsDefault = true,

                // Financials
                TaxRate = 0.05m,
                CurrencySymbol = "CAD$",
                CurrencySymbolAfter = true,
                DateFormat = "yyyy-MM-dd",

                // Banking / Wire Transfer Info
                BankName = "National Bank",
                BankAccountNumber = "987654321",
                BankSwiftCode = "NATBCA22XXX",
                TaxId1 = "123456789RT0001",
                TaxId2 = "9876543210TQ0001",
                PaymentInstructions = "Virement Interac à info@example.com",

                // Basic Labels
                Label_Invoice = "FACTURE",
                Label_Quote = "SOUMISSION",
                Label_PurchaseOrder = "BON DE COMMANDE",

                // Line Items
                Label_Description = "Description des travaux",
                Label_Quantity = "Quantité",
                Label_Price = "Prix unitaire",
                Label_Total = "Total partiel",

                // Totals
                Label_Subtotal = "Sous-total",
                Label_Tax = "Taxes",
                Label_GrandTotal = "Total net à payer",
                Label_QuoteTotal = "Budget global",
                Label_BalanceDue = "Solde restant",
                Label_AmountPaid = "Paiement reçu",

                // Client Bought
                Label_ClientBoughtTag = "(Acheté par le client)",
                Label_ClientBoughtTotal = "Total matériel client",

                // Payment Instructions Labels
                Label_PaymentInstructions = "Modalités de règlement",
                Label_BankName = "Institution bancaire",
                Label_BankAccount = "Numéro de compte",
                Label_SwiftCode = "Transit / Swift",

                // Other Labels
                Label_TaxId1 = "TPS",
                Label_TaxId2 = "TVQ",
                Label_ProjectManager = "Gestionnaire de compte",
                Label_ItemLaborTag = "(Main-d'oeuvre)",
                Label_BlockOfHours = "Banque d'heures prépayée",

                // Content Defaults
                DefaultTermsAndConditions = "1. Validité 60 jours.\n2. Dépôt de 50%.",

                // Proposal Labels
                Label_ScopeOfWork = "Portée du mandat",
                Label_TermsAndConditions = "Modalités et conditions",
                Label_Acceptance = "Approbation du projet",
                Label_Signature = "Signature autorisée",
                Label_DateSigned = "Date de ratification",

                // Quote Specific
                Label_QuoteTo = "Destinataire du devis",
                Label_PresentedBy = "Conseiller assigné",
                Label_ManagerEmail = "Courriel direct",
                Label_AcceptanceText = "En signant, vous acceptez l'offre.",

                // Confidentiality
                Label_Confidential = "STRICTEMENT CONFIDENTIEL",
                Label_ConfidentialText = "Usage interne uniquement.",

                // Signatures
                Label_SignatureClient = "Signature du donneur d'ordre",
                Label_SignatureCompany = "Signature du représentant Spokes",

                // Other Pricing
                Label_PricingBreakdown = "Ventilation des coûts",
                Label_PaymentSchedule = "Calendrier de facturation",
                DefaultCostPlusTerms = "Facturation selon temps et matériel.",

                // PO Specific
                Label_Vendor = "FOURNISSEUR ACCRÉDITÉ",
                Label_IssuedBy = "Approuvé par",
                Label_PoTotal = "Total de la commande",
                Label_SupplierRef = "Référence devis fournisseur",

                // Branding Colors
                ColorPrimary = "#FF5722",
                ColorSecondary = "#009688",

                // Meta
                Label_Date = "Date de création",
                Label_DueDate = "Date d'échéance",
                Label_BillTo = "Facturé à",
                Label_ShipTo = "Livré à",
                Label_ProjectNumber = "Numéro de dossier"
            };

            Assert.Equal("custom-template-id", template.Id);
            Assert.Equal("French Canadian", template.Name);
            Assert.Equal("fr-CA", template.LanguageCode);
            Assert.Equal("fr", template.TaskTranslationLanguage);
            Assert.True(template.IsDefault);

            // Financials
            Assert.Equal(0.05m, template.TaxRate);
            Assert.Equal("CAD$", template.CurrencySymbol);
            Assert.True(template.CurrencySymbolAfter);
            Assert.Equal("yyyy-MM-dd", template.DateFormat);

            // Banking
            Assert.Equal("National Bank", template.BankName);
            Assert.Equal("987654321", template.BankAccountNumber);
            Assert.Equal("NATBCA22XXX", template.BankSwiftCode);
            Assert.Equal("123456789RT0001", template.TaxId1);
            Assert.Equal("9876543210TQ0001", template.TaxId2);
            Assert.Equal("Virement Interac à info@example.com", template.PaymentInstructions);

            // Basic Labels
            Assert.Equal("FACTURE", template.Label_Invoice);
            Assert.Equal("SOUMISSION", template.Label_Quote);
            Assert.Equal("BON DE COMMANDE", template.Label_PurchaseOrder);

            // Line Items
            Assert.Equal("Description des travaux", template.Label_Description);
            Assert.Equal("Quantité", template.Label_Quantity);
            Assert.Equal("Prix unitaire", template.Label_Price);
            Assert.Equal("Total partiel", template.Label_Total);

            // Totals
            Assert.Equal("Sous-total", template.Label_Subtotal);
            Assert.Equal("Taxes", template.Label_Tax);
            Assert.Equal("Total net à payer", template.Label_GrandTotal);
            Assert.Equal("Budget global", template.Label_QuoteTotal);
            Assert.Equal("Solde restant", template.Label_BalanceDue);
            Assert.Equal("Paiement reçu", template.Label_AmountPaid);

            // Client Bought
            Assert.Equal("(Acheté par le client)", template.Label_ClientBoughtTag);
            Assert.Equal("Total matériel client", template.Label_ClientBoughtTotal);

            // Payment Instructions Labels
            Assert.Equal("Modalités de règlement", template.Label_PaymentInstructions);
            Assert.Equal("Institution bancaire", template.Label_BankName);
            Assert.Equal("Numéro de compte", template.Label_BankAccount);
            Assert.Equal("Transit / Swift", template.Label_SwiftCode);

            // Other Labels
            Assert.Equal("TPS", template.Label_TaxId1);
            Assert.Equal("TVQ", template.Label_TaxId2);
            Assert.Equal("Gestionnaire de compte", template.Label_ProjectManager);
            Assert.Equal("(Main-d'oeuvre)", template.Label_ItemLaborTag);
            Assert.Equal("Banque d'heures prépayée", template.Label_BlockOfHours);

            // Content Defaults
            Assert.Equal("1. Validité 60 jours.\n2. Dépôt de 50%.", template.DefaultTermsAndConditions);

            // Proposal Labels
            Assert.Equal("Portée du mandat", template.Label_ScopeOfWork);
            Assert.Equal("Modalités et conditions", template.Label_TermsAndConditions);
            Assert.Equal("Approbation du projet", template.Label_Acceptance);
            Assert.Equal("Signature autorisée", template.Label_Signature);
            Assert.Equal("Date de ratification", template.Label_DateSigned);

            // Quote Specific
            Assert.Equal("Destinataire du devis", template.Label_QuoteTo);
            Assert.Equal("Conseiller assigné", template.Label_PresentedBy);
            Assert.Equal("Courriel direct", template.Label_ManagerEmail);
            Assert.Equal("En signant, vous acceptez l'offre.", template.Label_AcceptanceText);

            // Confidentiality
            Assert.Equal("STRICTEMENT CONFIDENTIEL", template.Label_Confidential);
            Assert.Equal("Usage interne uniquement.", template.Label_ConfidentialText);

            // Signatures
            Assert.Equal("Signature du donneur d'ordre", template.Label_SignatureClient);
            Assert.Equal("Signature du représentant Spokes", template.Label_SignatureCompany);

            // Other Pricing
            Assert.Equal("Ventilation des coûts", template.Label_PricingBreakdown);
            Assert.Equal("Calendrier de facturation", template.Label_PaymentSchedule);
            Assert.Equal("Facturation selon temps et matériel.", template.DefaultCostPlusTerms);

            // PO Specific
            Assert.Equal("FOURNISSEUR ACCRÉDITÉ", template.Label_Vendor);
            Assert.Equal("Approuvé par", template.Label_IssuedBy);
            Assert.Equal("Total de la commande", template.Label_PoTotal);
            Assert.Equal("Référence devis fournisseur", template.Label_SupplierRef);

            // Branding Colors
            Assert.Equal("#FF5722", template.ColorPrimary);
            Assert.Equal("#009688", template.ColorSecondary);

            // Meta
            Assert.Equal("Date de création", template.Label_Date);
            Assert.Equal("Date d'échéance", template.Label_DueDate);
            Assert.Equal("Facturé à", template.Label_BillTo);
            Assert.Equal("Livré à", template.Label_ShipTo);
            Assert.Equal("Numéro de dossier", template.Label_ProjectNumber);
        }
    }
}
