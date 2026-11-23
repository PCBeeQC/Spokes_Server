namespace Spokes_Server.Tests;

using System;
using System.IO;
using System.Text;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Xunit;

public class UniversalInvoiceExtractorTests
{
    [Fact]
    public void TestParseAmount_MultiLocale()
    {
        // French Canadian / European comma decimals
        Assert.Equal(6397.10m, UniversalTaxReconciler.ParseAmount("6 397,10 $"));
        Assert.Equal(7355.06m, UniversalTaxReconciler.ParseAmount("7 355,06 CAD"));
        Assert.Equal(319.86m, UniversalTaxReconciler.ParseAmount("319,86"));
        Assert.Equal(15.04m, UniversalTaxReconciler.ParseAmount("15.04"));
        Assert.Equal(0.75m, UniversalTaxReconciler.ParseAmount("0.75"));
        Assert.Equal(1.50m, UniversalTaxReconciler.ParseAmount("1.50"));

        // Anglo-American comma thousands
        Assert.Equal(1234.56m, UniversalTaxReconciler.ParseAmount("$1,234.56"));
        Assert.Equal(25000.00m, UniversalTaxReconciler.ParseAmount("25,000.00 USD"));

        // European dot thousands and comma decimal
        Assert.Equal(1234.56m, UniversalTaxReconciler.ParseAmount("1.234,56 €"));

        // Negative amounts (credit notes)
        Assert.Equal(-50.00m, UniversalTaxReconciler.ParseAmount("-50.00"));
        Assert.Equal(-150.25m, UniversalTaxReconciler.ParseAmount("(150.25)"));
        Assert.Equal(-123.45m, UniversalTaxReconciler.ParseAmount("123.45 CR"));

        // Swiss apostrophe thousands
        Assert.Equal(1234.50m, UniversalTaxReconciler.ParseAmount("1'234.50 CHF"));
        Assert.Equal(12500.00m, UniversalTaxReconciler.ParseAmount("12’500.00"));

        // Multi-group thousands
        Assert.Equal(1234567.89m, UniversalTaxReconciler.ParseAmount("$1,234,567.89"));
        Assert.Equal(1234567.89m, UniversalTaxReconciler.ParseAmount("1.234.567,89 €"));
    }

    [Fact]
    public void TestUniversalTaxReconciliation_EuropeanVat()
    {
        var data = new ExtractedInvoiceData();
        // Construct spatial text with subtotal 100, VAT 20, Total 120
        var sb = new StringBuilder();
        sb.AppendLine("ACME Europe SARL");
        sb.AppendLine("VAT ID: FR12345678901");
        sb.AppendLine("Facture Nº FR-2026-999");
        sb.AppendLine("Date: 15/09/2026");
        sb.AppendLine("Sous-total : 100.00 EUR");
        sb.AppendLine("TVA 20% : 20.00 EUR");
        sb.AppendLine("Total : 120.00 EUR");

        // Test math reconciliation
        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(100.00m, data.SubTotal);
        Assert.Equal(20.00m, data.TaxAmount);
        Assert.Equal(120.00m, data.TotalAmount);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_UsSalesTax()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Tech Supplies Inc.");
        sb.AppendLine("EIN: 12-3456789");
        sb.AppendLine("Invoice #: US-5542");
        sb.AppendLine("Date: 2026-08-01");
        sb.AppendLine("Subtotal: $250.00");
        sb.AppendLine("Sales Tax: $20.00");
        sb.AppendLine("Total: $270.00");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(250.00m, data.SubTotal);
        Assert.Equal(20.00m, data.TaxAmount);
        Assert.Equal(270.00m, data.TotalAmount);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_ZeroTaxB2b()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Global Consulting Ltd.");
        sb.AppendLine("Invoice Number: INV-8877");
        sb.AppendLine("Date: 2026-09-01");
        sb.AppendLine("Subtotal: $500.00");
        sb.AppendLine("Total: $500.00");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(500.00m, data.SubTotal);
        Assert.Equal(0m, data.TaxAmount);
        Assert.Equal(500.00m, data.TotalAmount);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_FrenchCanadianReminderPayment()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Alumax Usinage");
        sb.AppendLine("Facture #: 1631");
        sb.AppendLine("Date: 2026-06-22");
        sb.AppendLine("Sous-total: 6 397,10 $");
        sb.AppendLine("TPS (5%): 319,86 $");
        sb.AppendLine("TVQ (9.975%): 638,10 $");
        sb.AppendLine("Total: 7 355,06 CAD");
        sb.AppendLine("Paiement: 3 677,53 $");
        sb.AppendLine("Solde à payer: 3 677,53 CAD");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(6397.10m, data.SubTotal);
        Assert.Equal(957.96m, data.TaxAmount);
        Assert.Equal(7355.06m, data.TotalAmount);
        Assert.Equal(3677.53m, data.PaidAmount);
        Assert.Equal(3677.53m, data.BalanceDue);
        Assert.True(data.HasPaymentDetected);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_EnglishInvoiceWithPriorPayment()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Industrial Parts Supply LLC");
        sb.AppendLine("Invoice #: INV-49201");
        sb.AppendLine("Date: 2026-05-10");
        sb.AppendLine("Subtotal: $1,000.00");
        sb.AppendLine("Sales Tax: $80.00");
        sb.AppendLine("Total Amount: $1,080.00");
        sb.AppendLine("Less Previous Payments: $500.00");
        sb.AppendLine("Balance Due: $580.00");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(1000.00m, data.SubTotal);
        Assert.Equal(80.00m, data.TaxAmount);
        Assert.Equal(1080.00m, data.TotalAmount);
        Assert.Equal(500.00m, data.PaidAmount);
        Assert.Equal(580.00m, data.BalanceDue);
        Assert.True(data.HasPaymentDetected);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_PaymentWithMissingTotal()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Consulting Group");
        sb.AppendLine("Invoice #: INV-77");
        sb.AppendLine("Subtotal: $2,000.00");
        sb.AppendLine("Tax: $200.00");
        sb.AppendLine("Amount Paid: $1,000.00");
        sb.AppendLine("Net Payable: $1,200.00");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(2000.00m, data.SubTotal);
        Assert.Equal(200.00m, data.TaxAmount);
        Assert.Equal(2200.00m, data.TotalAmount);
        Assert.Equal(1000.00m, data.PaidAmount);
        Assert.Equal(1200.00m, data.BalanceDue);
        Assert.True(data.HasPaymentDetected);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_StoreChargeAccountTicket()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("SHERWIN-WILLIAMS");
        sb.AppendLine("FACTURE");
        sb.AppendLine("No. 42629130040726");
        sb.AppendLine("TOTAL AVANT TAXES 99.20");
        sb.AppendLine("TVQ (1000853581) 9.90");
        sb.AppendLine("TPS (105250005RT) 4.96");
        sb.AppendLine("CHARG $114.06");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(99.20m, data.SubTotal);
        Assert.Equal(4.96m, data.TpsAmount);
        Assert.Equal(9.90m, data.TvqAmount);
        Assert.Equal(14.86m, data.TaxAmount);
        Assert.Equal(114.06m, data.TotalAmount);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_TaxLineWithTaxableBaseAndMultiRateVat()
    {
        // Case 1: Tax line shows taxable base before the actual tax amount ("TPS (5% sur 6 397,10 $) : 319,86 $")
        var caData = new ExtractedInvoiceData();
        var caSb = new StringBuilder();
        caSb.AppendLine("Usinage Précision Inc.");
        caSb.AppendLine("Sous-total : 6 397,10 $");
        caSb.AppendLine("TPS (5% sur 6 397,10 $) : 319,86 $");
        caSb.AppendLine("TVQ (9.975% sur 6 397,10 $) : 638,10 $");
        caSb.AppendLine("Total : 7 355,06 $");

        UniversalTaxReconciler.ReconcileTotals(caData, SpatialTextDocumentTestHelper.Create(caSb.ToString()));
        Assert.Equal(6397.10m, caData.SubTotal);
        Assert.Equal(319.86m, caData.TpsAmount);
        Assert.Equal(638.10m, caData.TvqAmount);
        Assert.Equal(957.96m, caData.TaxAmount);
        Assert.Equal(7355.06m, caData.TotalAmount);

        // Case 2: Multi-rate European VAT (5.5% + 20%)
        var euData = new ExtractedInvoiceData();
        var euSb = new StringBuilder();
        euSb.AppendLine("Boulangerie & Équipements SAS");
        euSb.AppendLine("Total HT : 300,00 €");
        euSb.AppendLine("TVA 5,5% : 5,50 €");
        euSb.AppendLine("TVA 20% : 40,00 €");
        euSb.AppendLine("Total TTC : 345,50 €");

        UniversalTaxReconciler.ReconcileTotals(euData, SpatialTextDocumentTestHelper.Create(euSb.ToString()));
        Assert.Equal(300.00m, euData.SubTotal);
        Assert.Equal(45.50m, euData.TaxAmount);
        Assert.Equal(345.50m, euData.TotalAmount);
    }

    [Fact]
    public void TestUniversalTaxReconciliation_DiscountAndShipping()
    {
        var data = new ExtractedInvoiceData();
        var sb = new StringBuilder();
        sb.AppendLine("Precision Motion Corp.");
        sb.AppendLine("Subtotal: $500.00");
        sb.AppendLine("Discount: -$50.00");
        sb.AppendLine("Shipping & Handling: $25.00");
        sb.AppendLine("Sales Tax: $38.00");
        sb.AppendLine("Total Due: $513.00");

        UniversalTaxReconciler.ReconcileTotals(data, SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(500.00m, data.SubTotal);
        Assert.Equal(50.00m, data.DiscountAmount);
        Assert.Equal(25.00m, data.ShippingAmount);
        Assert.Equal(38.00m, data.TaxAmount);
        Assert.Equal(513.00m, data.TotalAmount);
    }

    [Fact]
    public void TestMetadataExtraction_WrittenMonthsAnd2DBoxedHeader()
    {
        // 2-row boxed header: labels on one line, values directly below
        var sb = new StringBuilder();
        sb.AppendLine("Mustertechnik GmbH");
        sb.AppendLine("Invoice Date | Invoice No. | Purchase Order");
        sb.AppendLine("13. Oktober 2026 | RE-2026-8410 | PO-2026-099");
        sb.AppendLine("P.O. Box 4421, 80331 München");
        sb.AppendLine("Fällig am: 12. November 2026");
        sb.AppendLine("Zahlungsbedingungen: 30 Tage netto");

        var doc = SpatialTextDocumentTestHelper.Create(sb.ToString());
        var data = new ExtractedInvoiceData
        {
            InvoiceNumber = UniversalMetadataExtractor.ExtractInvoiceNumber(doc, "invoice.pdf"),
            PurchaseOrderReference = UniversalMetadataExtractor.ExtractPurchaseOrder(doc)
        };
        UniversalMetadataExtractor.ExtractDates(doc, data);

        Assert.Equal("RE-2026-8410", data.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 10, 13), data.InvoiceDate);
        Assert.Equal(new DateTime(2026, 11, 12), data.DueDate);
        Assert.Equal("PO-2026-099", data.PurchaseOrderReference);
    }

    [Fact]
    public void TestMetadataExtraction_DmyVsMdyDisambiguation()
    {
        // US Invoice with EIN & USD -> 05/08/2026 is May 8, 2026 (MDY)
        var usSb = new StringBuilder();
        usSb.AppendLine("Midwest Fasteners LLC");
        usSb.AppendLine("EIN: 34-5678901");
        usSb.AppendLine("Invoice #: US-1042");
        usSb.AppendLine("Invoice Date: 05/08/2026");
        usSb.AppendLine("Total: $450.00 USD");

        var usDoc = SpatialTextDocumentTestHelper.Create(usSb.ToString());
        var usData = new ExtractedInvoiceData();
        UniversalMetadataExtractor.ExtractDates(usDoc, usData);
        Assert.Equal(new DateTime(2026, 5, 8), usData.InvoiceDate);

        // French/European Invoice with EUR & VAT -> 05/08/2026 is August 5, 2026 (DMY)
        var euSb = new StringBuilder();
        euSb.AppendLine("Ateliers Mécaniques Lyon SAS");
        euSb.AppendLine("TVA: FR 42 812345678");
        euSb.AppendLine("Facture N°: FA-2026-71");
        euSb.AppendLine("Date de facture: 05/08/2026");
        euSb.AppendLine("Total TTC: 450,00 EUR");

        var euDoc = SpatialTextDocumentTestHelper.Create(euSb.ToString());
        var euData = new ExtractedInvoiceData();
        UniversalMetadataExtractor.ExtractDates(euDoc, euData);
        Assert.Equal(new DateTime(2026, 8, 5), euData.InvoiceDate);
    }

    [Fact]
    public void TestPartyExtractor_TwoColumnBuyerExclusionAndFooterFallback()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FACTURE");
        sb.AppendLine("Alumax Usinage Inc. | Facturé à : Poly-Robotics Inc.");
        sb.AppendLine("1200 Boul. Industriel | 500 Rue Principale");
        sb.AppendLine("comptabilite@alumax.ca | factures@poly-robotics.com");
        sb.AppendLine("TPS: 123456789 RT0001");
        sb.AppendLine("TVQ: 1023456789 TQ0001");

        var context = new InvoiceExtractionContext
        {
            BuyerCompanyName = "Poly-Robotics Inc.",
            BuyerEmailDomains = ["poly-robotics.com"]
        };

        var data = new ExtractedInvoiceData();
        UniversalPartyExtractor.ExtractParties(SpatialTextDocumentTestHelper.Create(sb.ToString()), "facture.pdf", data, context);

        Assert.Equal("Alumax Usinage Inc.", data.SupplierName);
        Assert.Equal("comptabilite@alumax.ca", data.SupplierEmail);
        Assert.Contains("123456789RT0001", data.SupplierTaxId);
        Assert.Contains("1023456789TQ0001", data.SupplierTaxId);
    }

    [Fact]
    public void TestLineItemTableParser_QtyFirstAndUnitOfMeasureAndEmbeddedNumbers()
    {
        var sb = new StringBuilder();
        sb.AppendLine("QTY | ITEM | DESCRIPTION | UNIT PRICE | TOTAL");
        sb.AppendLine("16   54142038 Button socket cap screw   0.44   7.04");
        sb.AppendLine("2 EA   91251A537 Aluminum Spacer   12.50   25.00");
        sb.AppendLine("6016-49379 3.8 L F75CC2 SHER-KEM 1 99.20 99.20");
        sb.AppendLine("Subtotal: 131.24");

        var items = UniversalLineItemTableParser.ExtractLineItems(SpatialTextDocumentTestHelper.Create(sb.ToString()));

        Assert.Equal(3, items.Count);
        Assert.Equal(16m, items[0].Quantity);
        Assert.Equal(0.44m, items[0].UnitPrice);
        Assert.Equal(7.04m, items[0].Total);

        Assert.Equal(2m, items[1].Quantity);
        Assert.Equal(12.50m, items[1].UnitPrice);
        Assert.Equal(25.00m, items[1].Total);

        Assert.Equal(1m, items[2].Quantity);
        Assert.Equal(99.20m, items[2].UnitPrice);
        Assert.Equal(99.20m, items[2].Total);
    }

    [Fact]
    public void TestUniversalDocumentClassifier_StatementsReceiptsCreditNotesAndFalsePositives()
    {
        // 1. Statement of Account via aging buckets
        var stmtSb = new StringBuilder();
        stmtSb.AppendLine("RELEVÉ DE COMPTE / STATEMENT");
        stmtSb.AppendLine("Current | 1-30 Days | 31-60 Days | 61-90 Days | Over 90 Days");
        stmtSb.AppendLine("100.00 | 250.00 | 0.00 | 0.00 | 350.00");
        var stmtData = new ExtractedInvoiceData { TotalAmount = 350.00m };
        UniversalDocumentClassifier.ClassifyDocument(stmtData, SpatialTextDocumentTestHelper.Create(stmtSb.ToString()));
        Assert.True(stmtData.IsAccountStatement);
        Assert.NotEmpty(stmtData.ClassificationReason!);

        // 2. Unpaid invoice with "Due upon receipt" and French verb "avoir" must NOT be classified as Receipt or Credit Note
        var invSb = new StringBuilder();
        invSb.AppendLine("FACTURE / INVOICE");
        invSb.AppendLine("Facture No: INV-2026-100");
        invSb.AppendLine("Conditions: Payable à réception de la facture / Due upon receipt");
        invSb.AppendLine("Merci d'avoir choisi nos services professionnels.");
        invSb.AppendLine("Total à payer: 450,00 $");
        var invData = new ExtractedInvoiceData
        {
            InvoiceNumber = "INV-2026-100",
            TotalAmount = 450.00m,
            PaymentTerms = "Due upon receipt"
        };
        UniversalDocumentClassifier.ClassifyDocument(invData, SpatialTextDocumentTestHelper.Create(invSb.ToString()));
        Assert.False(invData.IsAccountStatement);
        Assert.False(invData.IsPaymentReceipt);
        Assert.False(invData.IsCreditNote);

        // 3. Credit Note (Note de crédit / Gutschrift) normalizes positive totals to negative
        var cnSb = new StringBuilder();
        cnSb.AppendLine("NOTE DE CRÉDIT / CREDIT NOTE");
        cnSb.AppendLine("No: CN-2026-07");
        cnSb.AppendLine("Sous-total: 100,00 $");
        cnSb.AppendLine("Total: 114,98 $");
        var cnData = new ExtractedInvoiceData
        {
            InvoiceNumber = "CN-2026-07",
            SubTotal = 100.00m,
            TaxAmount = 14.98m,
            TotalAmount = 114.98m
        };
        UniversalDocumentClassifier.ClassifyDocument(cnData, SpatialTextDocumentTestHelper.Create(cnSb.ToString()));
        Assert.True(cnData.IsCreditNote);
        Assert.Equal(-114.98m, cnData.TotalAmount);
        Assert.Equal(-100.00m, cnData.SubTotal);
    }

    [Fact]
    public void TestZeroBalancePaidInvoice_TwoColumnBuyerTaxExclusion_AndSubscriptionTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("FACTURE");
        sb.AppendLine("Destinataire : | CloudMetricsSoftwareLtd");
        sb.AppendLine("Poly-Robotics Inc | 100 Tech Park");
        sb.AppendLine("2889 av kepler | Dublin");
        sb.AppendLine("Québec | Ireland");
        sb.AppendLine("CA | N° de taxe : IE 9501858N");
        sb.AppendLine("815256391RT0001");
        sb.AppendLine("Date : 06 octobre 2026");
        sb.AppendLine("Numéro : INV-411476-1661502");
        sb.AppendLine("Description | Devise : CAD");
        sb.AppendLine("Abonnement mensuel pour 100 crédits   37.00");
        sb.AppendLine("Sous-total   37.00");
        sb.AppendLine("Sales Non EU - Rest of World 0%   0.00");
        sb.AppendLine("Total   37.00");
        sb.AppendLine("Payée   37.00");
        sb.AppendLine("Solde   0.00");
        sb.AppendLine("Siège social : CloudMetrics Software Ltd, 100 Tech Park, Dublin, Ireland");

        var doc = SpatialTextDocumentTestHelper.Create(sb.ToString());
        var data = new ExtractedInvoiceData();
        var context = new InvoiceExtractionContext { BuyerCompanyName = "Poly-Robotics Inc" };

        UniversalPartyExtractor.ExtractParties(doc, "facture.pdf", data, context);
        data.InvoiceNumber = UniversalMetadataExtractor.ExtractInvoiceNumber(doc, "facture.pdf");
        UniversalMetadataExtractor.ExtractDates(doc, data);
        data.LineItems = UniversalLineItemTableParser.ExtractLineItems(doc);
        UniversalTaxReconciler.ReconcileTotals(data, doc);

        Assert.Equal("CloudMetrics Software Ltd", data.SupplierName);
        Assert.Equal("IE9501858N", data.SupplierTaxId);
        Assert.DoesNotContain("815256391", data.SupplierTaxId);
        Assert.Equal("INV-411476-1661502", data.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 10, 6), data.InvoiceDate);
        Assert.Null(data.DueDate);
        Assert.Single(data.LineItems);
        Assert.Equal(37.00m, data.LineItems[0].Total);
        Assert.Equal(37.00m, data.SubTotal);
        Assert.Equal(0.00m, data.TaxAmount);
        Assert.Equal(37.00m, data.TotalAmount);
        Assert.Equal(37.00m, data.PaidAmount);
        Assert.Equal(0.00m, data.BalanceDue);
        Assert.True(data.HasPaymentDetected);
    }

    [Fact]
    public void TestClassifyIncomingPaymentAndRemittanceToCompany_RecognizedAsPaymentReceipt()
    {
        // 1. Customer Interac e-Transfer / deposit email longer than 100 chars before payment phrase
        var (_, isReceiptInterac, reasonInterac) = UniversalDocumentClassifier.ClassifyEmail(
            "Deposit notification for your business account",
            "Hello Valued Business Customer, please be advised that Client ABC Corp has sent you an Interac e-Transfer of $1,250.00 which has been automatically deposited into your account.",
            []);
        Assert.True(isReceiptInterac, $"Expected receipt classification, got reason: {reasonInterac}");

        // 2. Customer EFT Remittance Advice PDF that also lists Net 30 / Balance Due 0.00
        var remSb = new StringBuilder();
        remSb.AppendLine("EFT PAYMENT ADVICE / REMITTANCE ADVICE");
        remSb.AppendLine("Invoice # INV-2026-042 | Net 30 | Paid: $575.00 | Balance Due: $0.00");
        remSb.AppendLine("Payment received and deposited into your account.");
        var remData = new ExtractedInvoiceData
        {
            InvoiceNumber = "INV-2026-042",
            TotalAmount = 575.00m,
            PaidAmount = 575.00m,
            BalanceDue = 0.00m,
            PaymentTerms = "Net 30"
        };
        UniversalDocumentClassifier.ClassifyDocument(remData, SpatialTextDocumentTestHelper.Create(remSb.ToString()));
        Assert.True(remData.IsPaymentReceipt);

        // 3. Even if an invoice PDF was attached alongside a clear remittance advice email, ClassifyEmail marks Receipt
        var attachedInv = new ExtractedInvoiceData
        {
            InvoiceNumber = "INV-2026-042",
            TotalAmount = 575.00m
        };
        var (_, isReceiptWithPdf, _) = UniversalDocumentClassifier.ClassifyEmail(
            "EFT Remittance Advice - Payment for INV-2026-042",
            "Attached is the remittance confirmation for the payment sent today.",
            [attachedInv]);
        Assert.True(isReceiptWithPdf);
    }

    [Theory]
    [InlineData("monthly_statement_nov2026.pdf")]
    [InlineData("releve_fournisseur.pdf")]
    [InlineData("relevé-compte.pdf")]
    [InlineData("etat_de_compte_2026.pdf")]
    [InlineData("etat-de-compte-oct.pdf")]
    [InlineData("kontoauszug_10.pdf")]
    [InlineData("extracto_bancario.pdf")]
    [InlineData("sommaire_client.pdf")]
    public void TestUniversalDocumentClassifier_StatementFileNameTokens_PrecedesCreditNote(string fileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Acme Distribution Inc.");
        sb.AppendLine("INV-1001  $250.00");
        sb.AppendLine("Credit Note CN-2026-04 -$50.00");
        sb.AppendLine("Total Due: $200.00");

        var data = new ExtractedInvoiceData
        {
            SubTotal = 200.00m,
            TotalAmount = 200.00m
        };

        UniversalDocumentClassifier.ClassifyDocument(data, SpatialTextDocumentTestHelper.Create(sb.ToString()), fileName);

        Assert.True(data.IsAccountStatement);
        Assert.False(data.IsCreditNote);
        Assert.False(data.IsPaymentReceipt);
        Assert.Equal(200.00m, data.TotalAmount);
        Assert.Contains("filename matched", data.ClassificationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Account Summary")]
    [InlineData("Statement Summary")]
    [InlineData("Statement Date: 2026-11-01")]
    [InlineData("Statement Period: Oct 1 - Oct 31")]
    [InlineData("Aging Summary")]
    [InlineData("Current 30 60 90")]
    [InlineData("Balance Forward: $150.00")]
    [InlineData("Previous Balance: $150.00")]
    [InlineData("Sommaire de compte")]
    [InlineData("Rapport de compte")]
    [InlineData("Date de relevé : 2026-11-01")]
    [InlineData("Rechnungsauszug")]
    [InlineData("Offene Posten")]
    [InlineData("Resumen de cuenta")]
    public void TestUniversalDocumentClassifier_FoldedStatementPhrases_PrecedesCreditNote(string statementPhrase)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Global Industrial Supply");
        sb.AppendLine(statementPhrase);
        sb.AppendLine("Ref Credit Note CN-19  -$25.00");
        sb.AppendLine("Amount Due: $300.00");

        var data = new ExtractedInvoiceData
        {
            SubTotal = 300.00m,
            TotalAmount = 300.00m
        };

        UniversalDocumentClassifier.ClassifyDocument(data, SpatialTextDocumentTestHelper.Create(sb.ToString()), "doc.pdf");

        Assert.True(data.IsAccountStatement);
        Assert.False(data.IsCreditNote);
        Assert.False(data.IsPaymentReceipt);
        Assert.Equal(300.00m, data.TotalAmount);
    }

    [Fact]
    public void TestPdfInvoiceInspectionService_EndToEndGeneratedPdfs_StatementsInvoicesAndCreditNotes()
    {
        var inspector = new PdfInvoiceInspectionService();

        // 1. Standard invoice PDF
        var invoiceBytes = PdfTestBuilderHelper.BuildTextPdf(
            "FACTURE",
            "Acier Quebec Inc.",
            "Facture No: INV-2026-500",
            "Date: 2026-11-01",
            "Sous-total: 100.00 $",
            "TPS: 5.00 $",
            "TVQ: 9.98 $",
            "Total: 114.98 $");
        using (var invStream = new MemoryStream(invoiceBytes))
        {
            var invData = inspector.Inspect(invStream, "INV-2026-500.pdf");
            Assert.NotNull(invData);
            Assert.Equal("PDF-Text", invData.RawDocumentType);
            Assert.False(invData.IsAccountStatement);
            Assert.False(invData.IsCreditNote);
            Assert.Equal(114.98m, invData.TotalAmount);
        }

        // 2. Statement identified by filename even with Credit Note line in transaction table
        var stmtByFilenameBytes = PdfTestBuilderHelper.BuildTextPdf(
            "Acier Quebec Inc.",
            "INV-2026-100   200.00",
            "Credit Note CN-10   -50.00",
            "Total Due: 150.00");
        using (var stmtFnStream = new MemoryStream(stmtByFilenameBytes))
        {
            var stmtData = inspector.Inspect(stmtFnStream, "etat-de-compte-nov2026.pdf");
            Assert.NotNull(stmtData);
            Assert.True(stmtData.IsAccountStatement);
            Assert.False(stmtData.IsCreditNote);
            Assert.Equal("Statement", stmtData.RawDocumentType);
            Assert.True(stmtData.TotalAmount >= 0);
        }

        // 3. Statement identified by folded body keyword ("Account Summary") before Credit Note pass
        var stmtByBodyBytes = PdfTestBuilderHelper.BuildTextPdf(
            "Acier Quebec Inc.",
            "Account Summary",
            "INV-2026-100   200.00",
            "Credit Note CN-10   -50.00",
            "Balance: 150.00");
        using (var stmtBodyStream = new MemoryStream(stmtByBodyBytes))
        {
            var stmtData = inspector.Inspect(stmtBodyStream, "supplier_doc.pdf");
            Assert.NotNull(stmtData);
            Assert.True(stmtData.IsAccountStatement);
            Assert.False(stmtData.IsCreditNote);
            Assert.Equal("Statement", stmtData.RawDocumentType);
        }

        // 4. Genuine Credit Note PDF
        var cnBytes = PdfTestBuilderHelper.BuildTextPdf(
            "CREDIT NOTE",
            "Acier Quebec Inc.",
            "Credit Note No: CN-2026-12",
            "Subtotal: 100.00",
            "Tax: 14.98",
            "Total: 114.98");
        using (var cnStream = new MemoryStream(cnBytes))
        {
            var cnData = inspector.Inspect(cnStream, "cn-2026-12.pdf");
            Assert.NotNull(cnData);
            Assert.True(cnData.IsCreditNote);
            Assert.False(cnData.IsAccountStatement);
            Assert.Equal("CreditNote", cnData.RawDocumentType);
            Assert.Equal(-114.98m, cnData.TotalAmount);
        }

        // 5. Scanned PDF (page with no selectable text) even when filename contains statement token
        var scannedBytes = PdfTestBuilderHelper.BuildBlankPdf();
        using (var scannedStream = new MemoryStream(scannedBytes))
        {
            var scannedData = inspector.Inspect(scannedStream, "statement_scan.pdf");
            Assert.NotNull(scannedData);
            Assert.True(scannedData.IsScannedImagePdf);
            Assert.False(scannedData.IsAccountStatement);
            Assert.Equal("Scanned-PDF", scannedData.RawDocumentType);
        }
    }
}

internal static class PdfTestBuilderHelper
{
    public static byte[] BuildTextPdf(params string[] lines)
    {
        using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);

        double y = 740;
        foreach (var line in lines)
        {
            page.AddText(line, 11, new UglyToad.PdfPig.Core.PdfPoint(50, y), font);
            y -= 18;
        }

        return builder.Build();
    }

    public static byte[] BuildBlankPdf()
    {
        using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.Letter);
        return builder.Build();
    }
}

internal static class SpatialTextDocumentTestHelper
{
    public static SpatialTextDocument Create(string layoutText)
    {
        var lines = layoutText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var page = new SpatialPage
        {
            PageNumber = 1,
            Width = 600,
            Height = 800
        };

        double y = 750;
        foreach (var line in lines)
        {
            page.Lines.Add(new SpatialLine
            {
                Y = y,
                Words = [new SpatialWord(line, 50, y, 550, y + 12)]
            });
            y -= 20;
        }

        var docType = typeof(SpatialTextDocument);
        var ctor = docType.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (SpatialTextDocument)ctor.Invoke([new List<SpatialPage> { page }, layoutText, layoutText]);
    }
}
