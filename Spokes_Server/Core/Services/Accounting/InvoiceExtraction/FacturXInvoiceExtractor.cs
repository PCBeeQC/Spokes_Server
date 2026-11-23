namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Extracts invoice details from Factur-X / ZUGFeRD / UN/CEFACT Cross Industry Invoice (CII) XML.
/// </summary>
public class FacturXInvoiceExtractor : ElectronicInvoiceExtractorBase
{
    public override bool CanExtract(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return false;
        return xmlContent.Contains("CrossIndustryInvoice", StringComparison.OrdinalIgnoreCase);
    }

    public override ExtractedInvoiceData? Extract(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null || !root.Name.LocalName.Equals("CrossIndustryInvoice", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var result = new ExtractedInvoiceData
            {
                Source = InvoiceExtractionSource.FacturX_CII,
                RawDocumentType = root.Name.LocalName
            };

            // ExchangedDocument: ID, TypeCode & Date
            var exchangedDoc = FindElement(root, "ExchangedDocument");
            if (exchangedDoc != null)
            {
                result.InvoiceNumber = FindElement(exchangedDoc, "ID")?.Value?.Trim();

                var typeCode = FindElement(exchangedDoc, "TypeCode")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(typeCode))
                {
                    result.RawDocumentType = typeCode;
                }
                if (typeCode is "381" or "261" or "81")
                {
                    result.IsCreditNote = true;
                    result.ClassificationReason = $"Credit note (CII TypeCode {typeCode})";
                }

                var dateElem = FindElement(exchangedDoc, "IssueDateTime");
                if (dateElem != null)
                {
                    var dateStr = FindElement(dateElem, "DateTimeString")?.Value?.Trim();
                    if (!string.IsNullOrEmpty(dateStr))
                    {
                        result.InvoiceDate = ParseCiiDate(dateStr);
                    }
                }
            }

            // SupplyChainTradeTransaction
            var tradeTx = FindElement(root, "SupplyChainTradeTransaction");
            if (tradeTx != null)
            {
                // ApplicableHeaderTradeAgreement: Seller & Buyer & Order Ref
                var agreement = FindElement(tradeTx, "ApplicableHeaderTradeAgreement");
                if (agreement != null)
                {
                    var seller = FindElement(agreement, "SellerTradeParty");
                    if (seller != null)
                    {
                        result.SupplierName = FindElement(seller, "Name")?.Value?.Trim();

                        var taxRegs = FindElements(seller, "SpecifiedTaxRegistration").ToList();
                        if (taxRegs.Count > 0)
                        {
                            // Prefer schemeID="VA" (VAT ID) over "FC" (Fiscal Code) when multiple exist
                            var preferredReg = taxRegs.FirstOrDefault(tr =>
                                FindElement(tr, "ID")?.Attribute("schemeID")?.Value?.Equals("VA", StringComparison.OrdinalIgnoreCase) == true)
                                ?? taxRegs[0];
                            result.SupplierTaxId = FindElement(preferredReg, "ID")?.Value?.Trim();
                        }

                        var contact = FindElement(seller, "DefinedTradeContact");
                        var emailComm = (contact != null ? FindElement(contact, "EmailURIUniversalCommunication") : null)
                                        ?? FindElement(seller, "URIUniversalCommunication");
                        if (emailComm != null)
                        {
                            var uri = FindElement(emailComm, "URIID")?.Value?.Trim();
                            if (!string.IsNullOrWhiteSpace(uri) && uri.Contains('@'))
                            {
                                result.SupplierEmail = uri;
                            }
                        }
                    }

                    var buyer = FindElement(agreement, "BuyerTradeParty");
                    if (buyer != null)
                    {
                        result.BuyerName = FindElement(buyer, "Name")?.Value?.Trim();
                    }

                    var buyerOrderRef = FindElement(agreement, "BuyerOrderReferencedDocument");
                    if (buyerOrderRef != null)
                    {
                        result.PurchaseOrderReference = FindElement(buyerOrderRef, "IssuerAssignedID")?.Value?.Trim();
                    }
                }

                // ApplicableHeaderTradeSettlement: Currency, Monetary Totals & Taxes
                var settlement = FindElement(tradeTx, "ApplicableHeaderTradeSettlement");
                if (settlement != null)
                {
                    var currencyCode = FindElement(settlement, "InvoiceCurrencyCode")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(currencyCode))
                    {
                        result.Currency = currencyCode.ToUpperInvariant();
                    }

                    var monetarySummation = FindElement(settlement, "SpecifiedTradeSettlementHeaderMonetarySummation");
                    if (monetarySummation != null)
                    {
                        var grandTotalElem = FindElement(monetarySummation, "GrandTotalAmount");
                        var lineTotalElem = FindElement(monetarySummation, "LineTotalAmount");

                        if (string.IsNullOrWhiteSpace(result.Currency))
                        {
                            var attrCurr = grandTotalElem?.Attribute("currencyID")?.Value?.Trim()
                                           ?? lineTotalElem?.Attribute("currencyID")?.Value?.Trim();
                            if (!string.IsNullOrWhiteSpace(attrCurr))
                            {
                                result.Currency = attrCurr.ToUpperInvariant();
                            }
                        }

                        result.SubTotal = ParseDecimal(lineTotalElem?.Value);
                        if (result.SubTotal == 0)
                        {
                            result.SubTotal = ParseDecimal(FindElement(monetarySummation, "TaxBasisTotalAmount")?.Value);
                        }
                        result.TaxAmount = ParseDecimal(FindElement(monetarySummation, "TaxTotalAmount")?.Value);
                        result.TotalAmount = ParseDecimal(grandTotalElem?.Value);
                        result.PaidAmount = ParseDecimal(FindElement(monetarySummation, "TotalPrepaidAmount")?.Value);
                        result.BalanceDue = ParseDecimal(FindElement(monetarySummation, "DuePayableAmount")?.Value);

                        NormalizeBalanceAmounts(result);
                    }

                    // Taxes breakdown (TPS / TVQ / GST / VAT)
                    var taxElements = FindElements(settlement, "ApplicableTradeTax");
                    foreach (var taxElem in taxElements)
                    {
                        var rate = ParseDecimal(FindElement(taxElem, "RateApplicablePercent")?.Value);
                        var amount = ParseDecimal(FindElement(taxElem, "CalculatedAmount")?.Value);
                        var typeCode = FindElement(taxElem, "TypeCode")?.Value?.Trim() ?? "";

                        // In Canada, 5% is TPS/GST and ~9.975% is TVQ/QST
                        if (Math.Abs(rate - 5.0m) < 0.1m || typeCode.Equals("GST", StringComparison.OrdinalIgnoreCase) || typeCode.Equals("TPS", StringComparison.OrdinalIgnoreCase))
                        {
                            result.TpsAmount += amount;
                        }
                        else if (Math.Abs(rate - 9.975m) < 0.1m || typeCode.Equals("QST", StringComparison.OrdinalIgnoreCase) || typeCode.Equals("TVQ", StringComparison.OrdinalIgnoreCase))
                        {
                            result.TvqAmount += amount;
                        }
                    }

                    // Fallback to standard Canadian/Quebec breakdown if taxes not itemized
                    ApplyCanadianTaxBreakdownFallback(result);

                    // Payment Terms / Due Date
                    var paymentTerms = FindElement(settlement, "SpecifiedTradePaymentTerms");
                    if (paymentTerms != null)
                    {
                        result.PaymentTerms = FindElement(paymentTerms, "Description")?.Value?.Trim();

                        var dueDateElem = FindElement(paymentTerms, "DueDateDateTime");
                        if (dueDateElem != null)
                        {
                            var dueDateStr = FindElement(dueDateElem, "DateTimeString")?.Value?.Trim();
                            if (!string.IsNullOrEmpty(dueDateStr))
                            {
                                result.DueDate = ParseCiiDate(dueDateStr);
                            }
                        }
                    }
                }

                // Line Items: IncludedSupplyChainTradeLineItem
                var lineItemElements = FindElements(tradeTx, "IncludedSupplyChainTradeLineItem");
                foreach (var lineElem in lineItemElements)
                {
                    var item = new ExtractedInvoiceLineItem();

                    var product = FindElement(lineElem, "SpecifiedTradeProduct");
                    if (product != null)
                    {
                        item.Description = FindElement(product, "Name")?.Value?.Trim() ?? string.Empty;
                        item.ItemCode = FindElement(product, "SellerAssignedID")?.Value?.Trim();
                    }

                    var delivery = FindElement(lineElem, "SpecifiedLineTradeDelivery");
                    if (delivery != null)
                    {
                        var qtyElem = FindElement(delivery, "BilledQuantity");
                        item.Quantity = ParseDecimal(qtyElem?.Value, 1.0m);
                    }

                    var lineAgreement = FindElement(lineElem, "SpecifiedLineTradeAgreement");
                    if (lineAgreement != null)
                    {
                        var priceElem = FindElement(lineAgreement, "NetPriceProductTradePrice");
                        if (priceElem != null)
                        {
                            item.UnitPrice = ParseDecimal(FindElement(priceElem, "ChargeAmount")?.Value);
                        }
                    }

                    var lineSettlement = FindElement(lineElem, "SpecifiedLineTradeSettlement");
                    if (lineSettlement != null)
                    {
                        var sumElem = FindElement(lineSettlement, "SpecifiedTradeSettlementLineMonetarySummation");
                        if (sumElem != null)
                        {
                            item.Total = ParseDecimal(FindElement(sumElem, "LineTotalAmount")?.Value);
                        }
                    }

                    if (item.Total == 0 && item.UnitPrice != 0 && item.Quantity != 0)
                    {
                        item.Total = item.UnitPrice * item.Quantity;
                    }

                    if (!string.IsNullOrWhiteSpace(item.Description) || item.Total != 0)
                    {
                        result.LineItems.Add(item);
                    }
                }
            }

            // Fallback calculations if amounts are partially populated
            ApplyFallbackTotalsAndCreditNote(result, "CII Credit Note");

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] CiiDateFormats =
        ["yyyyMMdd", "yyyy-MM-dd", "yyyyMMddTHHmmss", "yyyy-MM-ddTHH:mm:ss"];

    private static DateTime? ParseCiiDate(string dateStr) => ParseDate(dateStr, CiiDateFormats);
}
