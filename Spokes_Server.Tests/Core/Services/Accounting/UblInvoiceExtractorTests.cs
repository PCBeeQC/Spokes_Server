using System;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class UblInvoiceExtractorTests
{
    private readonly UblInvoiceExtractor _extractor = new();

    private const string SampleUblXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<Invoice xmlns=""urn:oasis:names:specification:ubl:schema:xsd:Invoice-2""
         xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2""
         xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"">
    <cbc:ID>UBL-INV-500</cbc:ID>
    <cbc:IssueDate>2026-05-10</cbc:IssueDate>
    <cbc:DueDate>2026-06-10</cbc:DueDate>
    <cac:OrderReference>
        <cbc:ID>PO-7788</cbc:ID>
    </cac:OrderReference>
    <cac:AccountingSupplierParty>
        <cac:Party>
            <cac:PartyName>
                <cbc:Name>Global Components Ltd.</cbc:Name>
            </cac:PartyName>
        </cac:Party>
    </cac:AccountingSupplierParty>
    <cac:TaxTotal>
        <cbc:TaxAmount currencyID=""CAD"">74.88</cbc:TaxAmount>
    </cac:TaxTotal>
    <cac:LegalMonetaryTotal>
        <cbc:LineExtensionAmount currencyID=""CAD"">500.00</cbc:LineExtensionAmount>
        <cbc:TaxExclusiveAmount currencyID=""CAD"">500.00</cbc:TaxExclusiveAmount>
        <cbc:TaxInclusiveAmount currencyID=""CAD"">574.88</cbc:TaxInclusiveAmount>
        <cbc:PayableAmount currencyID=""CAD"">574.88</cbc:PayableAmount>
    </cac:LegalMonetaryTotal>
    <cac:InvoiceLine>
        <cbc:InvoicedQuantity unitCode=""EA"">2</cbc:InvoicedQuantity>
        <cac:Item>
            <cbc:Name>Aluminum Extrusion 2020</cbc:Name>
        </cac:Item>
        <cac:Price>
            <cbc:PriceAmount currencyID=""CAD"">250.00</cbc:PriceAmount>
        </cac:Price>
    </cac:InvoiceLine>
</Invoice>";

    [Fact]
    public void CanExtract_ValidUblXml_ReturnsTrue()
    {
        Assert.True(_extractor.CanExtract(SampleUblXml));
    }

    [Fact]
    public void CanExtract_NonUblXml_ReturnsFalse()
    {
        Assert.False(_extractor.CanExtract("<SomethingElse />"));
    }

    [Fact]
    public void Extract_ValidUbl_ExtractsAllFields()
    {
        var result = _extractor.Extract(SampleUblXml);

        Assert.NotNull(result);
        Assert.Equal("UBL-INV-500", result.InvoiceNumber);
        Assert.Equal("Global Components Ltd.", result.SupplierName);
        Assert.Equal(new DateTime(2026, 5, 10), result.InvoiceDate);
        Assert.Equal(new DateTime(2026, 6, 10), result.DueDate);
        Assert.Equal(500.00m, result.SubTotal);
        Assert.Equal(74.88m, result.TaxAmount);
        Assert.Equal(574.88m, result.TotalAmount);
        Assert.Equal(25.00m, result.TpsAmount);
        Assert.Equal(49.88m, result.TvqAmount);
        Assert.Equal("PO-7788", result.PurchaseOrderReference);
        Assert.Equal(InvoiceExtractionSource.UBL, result.Source);

        Assert.Single(result.LineItems);
        var item = result.LineItems[0];
        Assert.Equal("Aluminum Extrusion 2020", item.Description);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(250.00m, item.UnitPrice);
    }

    [Fact]
    public void Extract_UblCreditNote_ExtractsAndNormalizes()
    {
        const string ublCreditNote = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<CreditNote xmlns=""urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2""
            xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2""
            xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"">
    <cbc:ID>CN-UBL-88</cbc:ID>
    <cbc:IssueDate>2026-08-12</cbc:IssueDate>
    <cbc:CreditNoteTypeCode>381</cbc:CreditNoteTypeCode>
    <cbc:DocumentCurrencyCode>EUR</cbc:DocumentCurrencyCode>
    <cac:AccountingSupplierParty>
        <cac:Party>
            <cac:PartyName>
                <cbc:Name>Nordic Automation AB</cbc:Name>
            </cac:PartyName>
        </cac:Party>
    </cac:AccountingSupplierParty>
    <cac:TaxTotal>
        <cbc:TaxAmount currencyID=""EUR"">25.00</cbc:TaxAmount>
    </cac:TaxTotal>
    <cac:LegalMonetaryTotal>
        <cbc:TaxExclusiveAmount currencyID=""EUR"">100.00</cbc:TaxExclusiveAmount>
        <cbc:PayableAmount currencyID=""EUR"">125.00</cbc:PayableAmount>
    </cac:LegalMonetaryTotal>
    <cac:CreditNoteLine>
        <cbc:CreditedQuantity unitCode=""EA"">1</cbc:CreditedQuantity>
        <cac:Item>
            <cbc:Name>Returned Sensor Module</cbc:Name>
            <cac:SellersItemIdentification>
                <cbc:ID>SNS-400</cbc:ID>
            </cac:SellersItemIdentification>
        </cac:Item>
        <cac:Price>
            <cbc:PriceAmount currencyID=""EUR"">100.00</cbc:PriceAmount>
        </cac:Price>
    </cac:CreditNoteLine>
</CreditNote>";

        Assert.True(_extractor.CanExtract(ublCreditNote));
        var result = _extractor.Extract(ublCreditNote);

        Assert.NotNull(result);
        Assert.Equal("CN-UBL-88", result.InvoiceNumber);
        Assert.True(result.IsCreditNote);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal("Nordic Automation AB", result.SupplierName);
        Assert.Equal(-100.00m, result.SubTotal);
        Assert.Equal(-25.00m, result.TaxAmount);
        Assert.Equal(-125.00m, result.TotalAmount);
        Assert.Single(result.LineItems);
        Assert.Equal("SNS-400", result.LineItems[0].ItemCode);
        Assert.Equal(-100.00m, result.LineItems[0].Total);
    }
}
