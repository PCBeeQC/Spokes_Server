namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Extracts invoice details from OASIS UBL 2.0 / 2.1 e-invoices.
/// </summary>
public class UblInvoiceExtractor : ElectronicInvoiceExtractorBase
{
    public override bool CanExtract(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return false;
        return xmlContent.Contains("urn:oasis:names:specification:ubl:schema:xsd:Invoice-2", StringComparison.OrdinalIgnoreCase) ||
               xmlContent.Contains("urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2", StringComparison.OrdinalIgnoreCase) ||
               ((xmlContent.Contains("<Invoice", StringComparison.OrdinalIgnoreCase) || xmlContent.Contains("<CreditNote", StringComparison.OrdinalIgnoreCase)) &&
                xmlContent.Contains("LegalMonetaryTotal", StringComparison.OrdinalIgnoreCase));
    }

    public override ExtractedInvoiceData? Extract(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            bool isCreditNoteRoot = root.Name.LocalName.Equals("CreditNote", StringComparison.OrdinalIgnoreCase);
            bool isInvoiceRoot = root.Name.LocalName.Equals("Invoice", StringComparison.OrdinalIgnoreCase);
            if (!isInvoiceRoot && !isCreditNoteRoot)
            {
                return null;
            }

            var typeCode = FindElement(root, "InvoiceTypeCode")?.Value?.Trim()
                           ?? FindElement(root, "CreditNoteTypeCode")?.Value?.Trim();
            bool isCreditNote = isCreditNoteRoot || typeCode is "381" or "261" or "81";

            var result = new ExtractedInvoiceData
            {
                Source = InvoiceExtractionSource.UBL,
                RawDocumentType = isCreditNote ? "UBL-CreditNote" : "UBL-Invoice",
                IsCreditNote = isCreditNote,
                ClassificationReason = isCreditNote ? "Credit note (UBL CreditNote)" : null
            };

            // Invoice ID, Currency & Dates
            result.InvoiceNumber = FindElement(root, "ID")?.Value?.Trim();
            result.InvoiceDate = ParseDate(FindElement(root, "IssueDate")?.Value);
            result.DueDate = ParseDate(FindElement(root, "DueDate")?.Value);

            var docCurrency = FindElement(root, "DocumentCurrencyCode")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(docCurrency))
            {
                result.Currency = docCurrency.ToUpperInvariant();
            }

            var paymentTermsElem = FindElement(root, "PaymentTerms");
            if (paymentTermsElem != null)
            {
                result.PaymentTerms = FindElement(paymentTermsElem, "Note")?.Value?.Trim();
            }

            if (result.DueDate == null)
            {
                var paymentMeans = FindElement(root, "PaymentMeans");
                if (paymentMeans != null)
                {
                    result.DueDate = ParseDate(FindElement(paymentMeans, "PaymentDueDate")?.Value);
                }
            }

            // Supplier Party
            var supplierParty = FindElement(root, "AccountingSupplierParty");
            if (supplierParty != null)
            {
                var party = FindElement(supplierParty, "Party");
                if (party != null)
                {
                    result.SupplierName = ExtractPartyName(party);

                    var taxScheme = FindElement(party, "PartyTaxScheme");
                    if (taxScheme != null)
                    {
                        result.SupplierTaxId = FindElement(taxScheme, "CompanyID")?.Value?.Trim();
                    }

                    var contact = FindElement(party, "Contact");
                    if (contact != null)
                    {
                        result.SupplierEmail = FindElement(contact, "ElectronicMail")?.Value?.Trim();
                    }
                }
            }

            // Buyer Party
            var customerParty = FindElement(root, "AccountingCustomerParty");
            if (customerParty != null)
            {
                var party = FindElement(customerParty, "Party");
                if (party != null)
                {
                    result.BuyerName = ExtractPartyName(party);
                }
            }

            // Order Reference (PO)
            var orderRef = FindElement(root, "OrderReference");
            if (orderRef != null)
            {
                result.PurchaseOrderReference = FindElement(orderRef, "ID")?.Value?.Trim();
            }

            // Monetary Totals
            var monetaryTotal = FindElement(root, "LegalMonetaryTotal");
            if (monetaryTotal != null)
            {
                var lineExtElem = FindElement(monetaryTotal, "LineExtensionAmount");
                var taxInclusiveElem = FindElement(monetaryTotal, "TaxInclusiveAmount");
                var payableElem = FindElement(monetaryTotal, "PayableAmount");

                if (string.IsNullOrWhiteSpace(result.Currency))
                {
                    var attrCurr = payableElem?.Attribute("currencyID")?.Value?.Trim()
                                   ?? taxInclusiveElem?.Attribute("currencyID")?.Value?.Trim()
                                   ?? lineExtElem?.Attribute("currencyID")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(attrCurr))
                    {
                        result.Currency = attrCurr.ToUpperInvariant();
                    }
                }

                result.SubTotal = ParseDecimal(lineExtElem?.Value);
                if (result.SubTotal == 0)
                {
                    result.SubTotal = ParseDecimal(FindElement(monetaryTotal, "TaxExclusiveAmount")?.Value);
                }

                result.TotalAmount = ParseDecimal(taxInclusiveElem?.Value);
                result.PaidAmount = ParseDecimal(FindElement(monetaryTotal, "PrepaidAmount")?.Value);
                result.BalanceDue = ParseDecimal(payableElem?.Value);

                NormalizeBalanceAmounts(result);
            }

            // Tax Total
            var taxTotals = FindElements(root, "TaxTotal");
            foreach (var taxTotal in taxTotals)
            {
                var totalTax = ParseDecimal(FindElement(taxTotal, "TaxAmount")?.Value);
                if (totalTax != 0 && result.TaxAmount == 0)
                {
                    result.TaxAmount = totalTax;
                }

                var subtotals = FindElements(taxTotal, "TaxSubtotal");
                foreach (var sub in subtotals)
                {
                    var amount = ParseDecimal(FindElement(sub, "TaxAmount")?.Value);
                    var category = FindElement(sub, "TaxCategory");
                    var percent = ParseDecimal(FindElement(category ?? sub, "Percent")?.Value);

                    if (Math.Abs(percent - 5.0m) < 0.1m)
                    {
                        result.TpsAmount += amount;
                    }
                    else if (Math.Abs(percent - 9.975m) < 0.1m)
                    {
                        result.TvqAmount += amount;
                    }
                }
            }

            // Fallback to standard Canadian/Quebec breakdown if taxes not itemized
            ApplyCanadianTaxBreakdownFallback(result);

            // Line Items (InvoiceLine or CreditNoteLine)
            var lines = FindElements(root, "InvoiceLine").Concat(FindElements(root, "CreditNoteLine"));
            foreach (var line in lines)
            {
                var item = new ExtractedInvoiceLineItem();

                var qtyElem = FindElement(line, "InvoicedQuantity") ?? FindElement(line, "CreditedQuantity");
                item.Quantity = ParseDecimal(qtyElem?.Value, 1.0m);

                item.Total = ParseDecimal(FindElement(line, "LineExtensionAmount")?.Value);

                var itemElem = FindElement(line, "Item");
                if (itemElem != null)
                {
                    item.Description = FindElement(itemElem, "Name")?.Value?.Trim() ??
                                       FindElement(itemElem, "Description")?.Value?.Trim() ?? string.Empty;

                    var sellerItemId = FindElement(itemElem, "SellersItemIdentification");
                    if (sellerItemId != null)
                    {
                        item.ItemCode = FindElement(sellerItemId, "ID")?.Value?.Trim();
                    }
                }

                var priceElem = FindElement(line, "Price");
                if (priceElem != null)
                {
                    item.UnitPrice = ParseDecimal(FindElement(priceElem, "PriceAmount")?.Value);
                }

                if (item.UnitPrice == 0 && item.Quantity != 0 && item.Total != 0)
                {
                    item.UnitPrice = item.Total / item.Quantity;
                }
                else if (item.Total == 0 && item.Quantity != 0 && item.UnitPrice != 0)
                {
                    item.Total = Math.Round(item.Quantity * item.UnitPrice, 2, MidpointRounding.AwayFromZero);
                }

                if (!string.IsNullOrWhiteSpace(item.Description) || item.Total != 0)
                {
                    result.LineItems.Add(item);
                }
            }

            // Default calculations
            ApplyFallbackTotalsAndCreditNote(result, "UBL Credit Note");

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] UblDateFormats =
        ["yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "dd/MM/yyyy"];

    private static string? ExtractPartyName(XElement party)
    {
        var partyName = FindElement(party, "PartyName");
        var name = FindElement(partyName ?? party, "Name")?.Value?.Trim();
        if (!string.IsNullOrEmpty(name)) return name;
        var legalEntity = FindElement(party, "PartyLegalEntity");
        return FindElement(legalEntity ?? party, "RegistrationName")?.Value?.Trim();
    }

    private static DateTime? ParseDate(string? dateStr) => ParseDate(dateStr, UblDateFormats);
}
