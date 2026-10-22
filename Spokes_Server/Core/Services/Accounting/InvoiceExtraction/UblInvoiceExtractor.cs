namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Extracts invoice details from OASIS UBL 2.0 / 2.1 e-invoices.
/// </summary>
public class UblInvoiceExtractor : IElectronicInvoiceExtractor
{
    public bool CanExtract(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return false;
        return xmlContent.Contains("urn:oasis:names:specification:ubl:schema:xsd:Invoice-2", StringComparison.OrdinalIgnoreCase) ||
               xmlContent.Contains("urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2", StringComparison.OrdinalIgnoreCase) ||
               ((xmlContent.Contains("<Invoice", StringComparison.OrdinalIgnoreCase) || xmlContent.Contains("<CreditNote", StringComparison.OrdinalIgnoreCase)) &&
                xmlContent.Contains("LegalMonetaryTotal", StringComparison.OrdinalIgnoreCase));
    }

    public ExtractedInvoiceData? Extract(string xmlContent)
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
                    var partyName = FindElement(party, "PartyName");
                    result.SupplierName = FindElement(partyName ?? party, "Name")?.Value?.Trim();
                    if (string.IsNullOrEmpty(result.SupplierName))
                    {
                        var legalEntity = FindElement(party, "PartyLegalEntity");
                        result.SupplierName = FindElement(legalEntity ?? party, "RegistrationName")?.Value?.Trim();
                    }

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
                    var partyName = FindElement(party, "PartyName");
                    result.BuyerName = FindElement(partyName ?? party, "Name")?.Value?.Trim();
                    if (string.IsNullOrEmpty(result.BuyerName))
                    {
                        var legalEntity = FindElement(party, "PartyLegalEntity");
                        result.BuyerName = FindElement(legalEntity ?? party, "RegistrationName")?.Value?.Trim();
                    }
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

                if (result.TotalAmount == 0)
                {
                    result.TotalAmount = result.BalanceDue != 0 && result.PaidAmount != 0
                        ? result.BalanceDue + result.PaidAmount
                        : result.BalanceDue;
                }
                if (result.BalanceDue == 0 && result.TotalAmount > 0 && result.PaidAmount > 0 && result.TotalAmount > result.PaidAmount)
                {
                    result.BalanceDue = result.TotalAmount - result.PaidAmount;
                }
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
            if (result.TpsAmount == 0 && result.TvqAmount == 0 && result.TaxAmount > 0 && result.SubTotal > 0)
            {
                if (Math.Abs(result.TaxAmount - (result.SubTotal * 0.14975m)) <= 0.05m)
                {
                    result.TpsAmount = Math.Round(result.SubTotal * 0.05m, 2, MidpointRounding.AwayFromZero);
                    result.TvqAmount = Math.Round(result.SubTotal * 0.09975m, 2, MidpointRounding.AwayFromZero);
                }
            }

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
            if (result.TotalAmount == 0 && result.SubTotal != 0)
            {
                result.TotalAmount = result.SubTotal + result.TaxAmount;
            }
            if (result.TaxAmount == 0 && (result.TpsAmount != 0 || result.TvqAmount != 0))
            {
                result.TaxAmount = result.TpsAmount + result.TvqAmount;
            }
            if (result.DueDate == null && result.InvoiceDate.HasValue)
            {
                result.DueDate = result.InvoiceDate.Value.AddDays(30);
            }
            if (result.IsCreditNote || result.TotalAmount < 0)
            {
                UniversalDocumentClassifier.ApplyCreditNoteNormalization(result, result.ClassificationReason ?? "UBL Credit Note");
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static XElement? FindElement(XContainer container, string localName)
    {
        return container.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<XElement> FindElements(XContainer container, string localName)
    {
        return container.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
    }

    private static decimal ParseDecimal(string? value, decimal defaultValue = 0m)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (decimal.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }
        if (decimal.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }
        return defaultValue;
    }

    private static DateTime? ParseDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;

        string[] formats = ["yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "dd/MM/yyyy"];
        if (DateTime.TryParseExact(dateStr.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt;
        }

        if (DateTime.TryParse(dateStr.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            return dt;
        }

        return null;
    }
}
