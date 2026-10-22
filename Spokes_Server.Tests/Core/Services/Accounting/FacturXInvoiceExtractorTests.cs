using System;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Accounting;

public class FacturXInvoiceExtractorTests
{
    private readonly FacturXInvoiceExtractor _extractor = new();

    private const string SampleFacturXXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rsm:CrossIndustryInvoice xmlns:rsm=""urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100""
                          xmlns:ram=""urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100""
                          xmlns:udt=""urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"">
    <rsm:ExchangedDocument>
        <ram:ID>INV-2026-999</ram:ID>
        <ram:IssueDateTime>
            <udt:DateTimeString format=""102"">20260315</udt:DateTimeString>
        </ram:IssueDateTime>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>Acme Robotics Inc.</ram:Name>
            </ram:SellerTradeParty>
            <ram:BuyerOrderReferencedDocument>
                <ram:IssuerAssignedID>PO-2026-0042</ram:IssuerAssignedID>
            </ram:BuyerOrderReferencedDocument>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:SpecifiedTradePaymentTerms>
                <ram:DueDateDateTime>
                    <udt:DateTimeString format=""102"">20260415</udt:DateTimeString>
                </ram:DueDateDateTime>
            </ram:SpecifiedTradePaymentTerms>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
                <ram:TaxBasisTotalAmount>1000.00</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount>149.75</ram:TaxTotalAmount>
                <ram:GrandTotalAmount>1149.75</ram:GrandTotalAmount>
                <ram:DuePayableAmount>1149.75</ram:DuePayableAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
        <ram:IncludedSupplyChainTradeLineItem>
            <ram:SpecifiedTradeProduct>
                <ram:Name>Servo Motor XL</ram:Name>
            </ram:SpecifiedTradeProduct>
            <ram:SpecifiedLineTradeAgreement>
                <ram:NetPriceProductTradePrice>
                    <ram:ChargeAmount>250.00</ram:ChargeAmount>
                </ram:NetPriceProductTradePrice>
            </ram:SpecifiedLineTradeAgreement>
            <ram:SpecifiedLineTradeDelivery>
                <ram:BilledQuantity>4</ram:BilledQuantity>
            </ram:SpecifiedLineTradeDelivery>
            <ram:SpecifiedLineTradeSettlement>
                <ram:SpecifiedTradeSettlementLineMonetarySummation>
                    <ram:LineTotalAmount>1000.00</ram:LineTotalAmount>
                </ram:SpecifiedTradeSettlementLineMonetarySummation>
            </ram:SpecifiedLineTradeSettlement>
        </ram:IncludedSupplyChainTradeLineItem>
    </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>";

    [Fact]
    public void CanExtract_ValidCiiRoot_ReturnsTrue()
    {
        Assert.True(_extractor.CanExtract(SampleFacturXXml));
    }

    [Fact]
    public void CanExtract_IrrelevantXml_ReturnsFalse()
    {
        Assert.False(_extractor.CanExtract("<root><item>Not an invoice</item></root>"));
    }

    [Fact]
    public void Extract_ValidFacturX_ExtractsAllFields()
    {
        var result = _extractor.Extract(SampleFacturXXml);

        Assert.NotNull(result);
        Assert.Equal("INV-2026-999", result.InvoiceNumber);
        Assert.Equal("Acme Robotics Inc.", result.SupplierName);
        Assert.Equal(new DateTime(2026, 3, 15), result.InvoiceDate);
        Assert.Equal(new DateTime(2026, 4, 15), result.DueDate);
        Assert.Equal(1000.00m, result.SubTotal);
        Assert.Equal(149.75m, result.TaxAmount);
        Assert.Equal(1149.75m, result.TotalAmount);
        Assert.Equal(50.00m, result.TpsAmount);
        Assert.Equal(99.75m, result.TvqAmount);
        Assert.Equal("PO-2026-0042", result.PurchaseOrderReference);
        Assert.Equal(InvoiceExtractionSource.FacturX_CII, result.Source);

        Assert.Single(result.LineItems);
        var item = result.LineItems[0];
        Assert.Equal("Servo Motor XL", item.Description);
        Assert.Equal(4m, item.Quantity);
        Assert.Equal(250.00m, item.UnitPrice);
    }

    [Fact]
    public void Extract_CreditNoteAndCurrencyAndSellerContact_ExtractsAndNormalizes()
    {
        const string creditNoteXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rsm:CrossIndustryInvoice xmlns:rsm=""urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100""
                          xmlns:ram=""urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100""
                          xmlns:udt=""urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100"">
    <rsm:ExchangedDocument>
        <ram:ID>CN-2026-044</ram:ID>
        <ram:TypeCode>381</ram:TypeCode>
        <ram:IssueDateTime>
            <udt:DateTimeString format=""102"">20260710</udt:DateTimeString>
        </ram:IssueDateTime>
    </rsm:ExchangedDocument>
    <rsm:SupplyChainTradeTransaction>
        <ram:ApplicableHeaderTradeAgreement>
            <ram:SellerTradeParty>
                <ram:Name>EuroTech GmbH</ram:Name>
                <ram:DefinedTradeContact>
                    <ram:EmailURIUniversalCommunication>
                        <ram:URIID>billing@eurotech.de</ram:URIID>
                    </ram:EmailURIUniversalCommunication>
                </ram:DefinedTradeContact>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID=""FC"">123/456/7890</ram:ID>
                </ram:SpecifiedTaxRegistration>
                <ram:SpecifiedTaxRegistration>
                    <ram:ID schemeID=""VA"">DE998877665</ram:ID>
                </ram:SpecifiedTaxRegistration>
            </ram:SellerTradeParty>
        </ram:ApplicableHeaderTradeAgreement>
        <ram:ApplicableHeaderTradeSettlement>
            <ram:InvoiceCurrencyCode>EUR</ram:InvoiceCurrencyCode>
            <ram:SpecifiedTradeSettlementHeaderMonetarySummation>
                <ram:TaxBasisTotalAmount>200.00</ram:TaxBasisTotalAmount>
                <ram:TaxTotalAmount>38.00</ram:TaxTotalAmount>
                <ram:GrandTotalAmount>238.00</ram:GrandTotalAmount>
            </ram:SpecifiedTradeSettlementHeaderMonetarySummation>
        </ram:ApplicableHeaderTradeSettlement>
    </rsm:SupplyChainTradeTransaction>
</rsm:CrossIndustryInvoice>";

        var result = _extractor.Extract(creditNoteXml);

        Assert.NotNull(result);
        Assert.Equal("CN-2026-044", result.InvoiceNumber);
        Assert.Equal("381", result.RawDocumentType);
        Assert.True(result.IsCreditNote);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal("EuroTech GmbH", result.SupplierName);
        Assert.Equal("billing@eurotech.de", result.SupplierEmail);
        Assert.Equal("DE998877665", result.SupplierTaxId);
        Assert.Equal(-200.00m, result.SubTotal);
        Assert.Equal(-38.00m, result.TaxAmount);
        Assert.Equal(-238.00m, result.TotalAmount);
    }
}
