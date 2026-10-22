namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Universal line items table parser handling single/dual quantity columns, Qty-first tables,
/// service/hourly tables, unit-of-measure tokens, item codes, and multi-page continuation
/// across Western billing formats.
/// </summary>
public static class UniversalLineItemTableParser
{
    private const string AmountPattern = @"-?\d{1,3}(?:[ \u00A0\u202F\.',]\d{3})*(?:[,\.]\d{2,4})";
    private const string TotalPattern = @"-?\d{1,3}(?:[ \u00A0\u202F\.',]\d{3})*(?:[,\.]\d{2})";
    private const string UomPattern = @"(?:ea|each|pc|pcs|stk|st\.?|hrs?|hours?|heures?|std\.?|h|u|un|unit[eé]s?|kg|g|lb|lbs|m|ft|l|gal|days?|jours?|tage)";

    // Regex for Dual Quantity (Comm. / Fact. or Ordered / Shipped): e.g. "54142038 Button socket cap 16 16 0.4400 7.04 AB"
    private static readonly Regex DualQtyLineRegex = new(
        $@"^(?:(?<itemCode>[A-Za-z0-9\-_]{{2,20}})\s+)?(?<desc>.+?)\s+(?<orderedQty>\d+(?:[,\.]\d+)?)\s+(?<qty>\d+(?:[,\.]\d+)?)(?:\s+{UomPattern})?\s+(?:[$€£]\s*)?(?<price>{AmountPattern})\s+(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s+[A-Za-z]{{1,4}})?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Single Quantity: e.g. "9002283-000 Plaque de transition 1 310,00 310,00" or "Usinage 4 309,9825 TPS/TVQ QC - 1 239,93"
    private static readonly Regex SingleQtyLineRegex = new(
        $@"^(?<desc>.+?)\s+(?<qty>\d+(?:[,\.]\d+)?)(?:\s+{UomPattern})?\s+(?:[$€£]\s*)?(?<price>{AmountPattern})\s+.*?(?:\s+|^)(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s*(?:[$€£]|EUR|USD|CAD|GBP|CHF|[A-Za-z]{{1,4}}))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Single Quantity with trailing numbers: e.g. "6016-49379 3.8 L F75CC2 SHER-KEM 1 99.20 99.20"
    private static readonly Regex SingleQtyTrailingRegex = new(
        $@"^(?<desc>.+)\s+(?<qty>\d+(?:[,\.]\d+)?)(?:\s+{UomPattern})?\s+(?:[$€£]\s*)?(?<price>{AmountPattern})\s+(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s*(?:[$€£]|EUR|USD|CAD|GBP|CHF|[A-Za-z]{{1,4}}))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Quantity-First rows: e.g. "2 EA WIDGET-A Industrial Valve 45.00 90.00" or "16 54142038 Button socket cap screw 0.44 7.04"
    private static readonly Regex QtyFirstDirectRegex = new(
        $@"^(?<qty>\d+(?:[,\.]\d+)?)(?:\s+{UomPattern})?\s+(?:(?<itemCode>[A-Za-z0-9\-_]{{3,20}})\s+)?(?<desc>[A-Za-zÀ-ÿ0-9].+?)\s+(?:[$€£]\s*)?(?<price>{AmountPattern})\s+(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s*(?:[$€£]|EUR|USD|CAD|GBP|CHF|[A-Za-z]{{1,4}}))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Position + Quantity-First rows: e.g. "1 10.5 hrs Consulting Services 150.00 1,575.00"
    private static readonly Regex PosAndQtyFirstRegex = new(
        $@"^(?<pos>\d{{1,3}})\s+(?<qty>\d+(?:[,\.]\d+)?)(?:\s+{UomPattern})?\s+(?:(?<itemCode>[A-Za-z0-9\-_]{{3,20}})\s+)?(?<desc>[A-Za-zÀ-ÿ0-9].+?)\s+(?:[$€£]\s*)?(?<price>{AmountPattern})\s+(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s*(?:[$€£]|EUR|USD|CAD|GBP|CHF|[A-Za-z]{{1,4}}))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Amount-Only service/subscription rows (when table header has Description + Amount without Qty/UnitPrice)
    private static readonly Regex AmountOnlyLineRegex = new(
        $@"^(?<desc>[A-Za-zÀ-ÿ].+?)\s+(?:[$€£]\s*)?(?<total>{TotalPattern})(?:\s*(?:[$€£]|EUR|USD|CAD|GBP|CHF))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SummaryStopRegex = new(
        @"^(?:sous-total|subtotal|sub-total|nettobetrag|zwischensumme|total\b|montant\s*total|gesamtbetrag|solde|balance\s*due|amount\s*due|paiement|information\s*pour|copie|page\s*\d|remise\b|discount\b|rabatt\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DescColKeywordRegex = new(
        @"\b(?:activit[eé]s?|description|items?|articles?|pos(?:ition)?|bezeichnung|services?|d[eé]signation|leistungen|concepto|descrizione|omschrijving|produits?|products?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QtyColKeywordRegex = new(
        @"\b(?:qt[eéy]\.?|quantity|menge|anzahl|comm\.?|fact\.?|ordered|shipped|hours?|hrs\.?|heures?|std\.?|stunden|cant\.?|cantidad|q\.?t[aà]|aantal)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PriceOrTotalColKeywordRegex = new(
        @"\b(?:taux|prix|price|preis|einzelpreis|gesamtpreis|rate|unit|total|value|valeur|montant|amount|betrag|importe|importo|bedrag|devise|currency|w[aä]hrung|valuta|cad|usd|eur|gbp|chf|aud|nzd)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<ExtractedInvoiceLineItem> ExtractLineItems(SpatialTextDocument document)
    {
        var lineItems = new List<ExtractedInvoiceLineItem>();
        if (document.Pages.Count == 0) return lineItems;

        var allLines = document.AllLines.ToList();

        int tableHeaderIdx = -1;
        bool isDualQtyTable = false;
        bool isQtyFirstTable = false;
        bool isAmountOnlyTable = false;

        for (int i = 0; i < allLines.Count; i++)
        {
            if (TryDetectTableHeader(allLines[i], out isDualQtyTable, out isQtyFirstTable, out isAmountOnlyTable))
            {
                tableHeaderIdx = i;
                break;
            }
        }

        if (tableHeaderIdx < 0) return lineItems;

        for (int i = tableHeaderIdx + 1; i < allLines.Count; i++)
        {
            var lineText = allLines[i].Text.Trim();
            if (string.IsNullOrWhiteSpace(lineText)) continue;

            // Skip repeated table headers on multi-page continuations
            if (TryDetectTableHeader(allLines[i], out _, out _, out _))
            {
                continue;
            }

            if (SummaryStopRegex.IsMatch(lineText))
            {
                break;
            }

            var parsedItem = TryMatchLineItem(lineText, isDualQtyTable, isQtyFirstTable, isAmountOnlyTable);
            if (parsedItem == null)
            {
                continue;
            }

            // Multi-line description lookahead
            int nextIdx = i + 1;
            while (nextIdx < allLines.Count)
            {
                var nextLine = allLines[nextIdx].Text.Trim();
                if (string.IsNullOrWhiteSpace(nextLine) ||
                    SummaryStopRegex.IsMatch(nextLine) ||
                    TryDetectTableHeader(allLines[nextIdx], out _, out _, out _) ||
                    TryMatchLineItem(nextLine, isDualQtyTable, isQtyFirstTable, isAmountOnlyTable) != null ||
                    Regex.IsMatch(nextLine, @"^(?:tps|tvq|tvp|gst|hst|qst|vat|tva|mwst|ust|inscri|shipping|livraison|port\b)", RegexOptions.IgnoreCase))
                {
                    break;
                }

                if (!Regex.IsMatch(nextLine, @"^\d+([,\.]\d+)?%?$") && nextLine.Length > 2 && nextLine.Length < 100)
                {
                    parsedItem.Description += " " + nextLine;
                    i = nextIdx;
                }
                else
                {
                    break;
                }
                nextIdx++;
            }

            lineItems.Add(parsedItem);
        }

        return lineItems;
    }

    private static bool TryDetectTableHeader(SpatialLine spatialLine, out bool isDualQty, out bool isQtyFirst, out bool isAmountOnly)
    {
        isDualQty = false;
        isQtyFirst = false;
        isAmountOnly = false;

        var lineText = spatialLine.Text;
        var descMatch = DescColKeywordRegex.Match(lineText);
        var priceMatch = PriceOrTotalColKeywordRegex.Match(lineText);
        if (!descMatch.Success || !priceMatch.Success)
        {
            return false;
        }

        var qtyMatch = QtyColKeywordRegex.Match(lineText);
        if (qtyMatch.Success)
        {
            isQtyFirst = qtyMatch.Index < descMatch.Index;
            if (Regex.IsMatch(lineText, @"\b(?:comm|command[eé]|ordered)\b", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(lineText, @"\b(?:fact|factur[eé]|shipped|livr[eé])\b", RegexOptions.IgnoreCase))
            {
                isDualQty = true;
            }
            return true;
        }

        // Service/subscription table header with only Description and Amount/Total/Currency
        var cells = spatialLine.GetSpatialCells(12.0);
        if (cells.Count is >= 2 and <= 4 && descMatch.Index < priceMatch.Index)
        {
            isAmountOnly = true;
            return true;
        }

        return false;
    }

    private static ExtractedInvoiceLineItem? TryMatchLineItem(
        string lineText,
        bool isDualQtyTable,
        bool isQtyFirstTable,
        bool isAmountOnlyTable)
    {
        if (isDualQtyTable)
        {
            var dualMatch = DualQtyLineRegex.Match(lineText);
            if (dualMatch.Success)
            {
                return BuildItemFromMatch(dualMatch);
            }
        }

        if (isQtyFirstTable)
        {
            var qfItem = TryMatchQtyFirst(lineText, requireMathValidation: false);
            if (qfItem != null)
            {
                return qfItem;
            }
        }

        var singleMatch = SingleQtyLineRegex.Match(lineText);
        var trailingMatch = SingleQtyTrailingRegex.Match(lineText);

        if (singleMatch.Success && trailingMatch.Success)
        {
            var singleItem = BuildItemFromMatch(singleMatch);
            var trailingItem = BuildItemFromMatch(trailingMatch);

            bool singleMathOk = Math.Abs((singleItem.Quantity * singleItem.UnitPrice) - singleItem.Total) <= 0.05m;
            bool trailingMathOk = Math.Abs((trailingItem.Quantity * trailingItem.UnitPrice) - trailingItem.Total) <= 0.05m;

            // Prefer trailingMatch when description contains embedded numbers and trailingMatch satisfies Qty * Price = Total
            if (trailingMathOk && !singleMathOk)
            {
                return trailingItem;
            }
            return singleItem;
        }

        if (singleMatch.Success)
        {
            return BuildItemFromMatch(singleMatch);
        }

        if (trailingMatch.Success)
        {
            return BuildItemFromMatch(trailingMatch);
        }

        if (!isQtyFirstTable)
        {
            var qfFallback = TryMatchQtyFirst(lineText, requireMathValidation: true);
            if (qfFallback != null)
            {
                return qfFallback;
            }
        }

        if (isAmountOnlyTable)
        {
            var amtMatch = AmountOnlyLineRegex.Match(lineText);
            if (amtMatch.Success && !SummaryStopRegex.IsMatch(lineText))
            {
                var total = UniversalTaxReconciler.ParseAmount(amtMatch.Groups["total"].Value);
                if (total != 0)
                {
                    return new ExtractedInvoiceLineItem
                    {
                        Description = amtMatch.Groups["desc"].Value.Trim(),
                        Quantity = 1m,
                        UnitPrice = total,
                        Total = total
                    };
                }
            }
        }

        return null;
    }

    private static ExtractedInvoiceLineItem? TryMatchQtyFirst(string lineText, bool requireMathValidation)
    {
        var directMatch = QtyFirstDirectRegex.Match(lineText);
        var posMatch = PosAndQtyFirstRegex.Match(lineText);

        if (directMatch.Success && posMatch.Success)
        {
            var directItem = BuildItemFromMatch(directMatch);
            var posItem = BuildItemFromMatch(posMatch);

            bool directMathOk = Math.Abs((directItem.Quantity * directItem.UnitPrice) - directItem.Total) <= 0.05m;
            bool posMathOk = Math.Abs((posItem.Quantity * posItem.UnitPrice) - posItem.Total) <= 0.05m;

            if (posMathOk && !directMathOk)
            {
                return posItem;
            }
            if (directMathOk || !requireMathValidation)
            {
                return directItem;
            }
            return null;
        }

        if (directMatch.Success)
        {
            var item = BuildItemFromMatch(directMatch);
            if (!requireMathValidation || Math.Abs((item.Quantity * item.UnitPrice) - item.Total) <= 0.05m)
            {
                return item;
            }
        }

        if (posMatch.Success)
        {
            var item = BuildItemFromMatch(posMatch);
            if (!requireMathValidation || Math.Abs((item.Quantity * item.UnitPrice) - item.Total) <= 0.05m)
            {
                return item;
            }
        }

        return null;
    }

    private static ExtractedInvoiceLineItem BuildItemFromMatch(Match match)
    {
        var desc = match.Groups["desc"].Value.Trim();
        var itemCode = match.Groups["itemCode"].Success ? match.Groups["itemCode"].Value.Trim() : null;

        // If itemCode was not split by group, check if desc starts with an obvious SKU code
        if (string.IsNullOrEmpty(itemCode))
        {
            var skuSplit = Regex.Match(desc, @"^([0-9A-Za-z]{2,5}-[0-9A-Za-z\-]{2,15})\s+(.+)$");
            if (skuSplit.Success && skuSplit.Groups[1].Value.Any(char.IsDigit))
            {
                itemCode = skuSplit.Groups[1].Value;
            }
        }

        var qty = UniversalTaxReconciler.ParseAmount(match.Groups["qty"].Value);
        var price = UniversalTaxReconciler.ParseAmount(match.Groups["price"].Value);
        var total = UniversalTaxReconciler.ParseAmount(match.Groups["total"].Value);

        return new ExtractedInvoiceLineItem
        {
            ItemCode = itemCode,
            Description = desc,
            Quantity = qty > 0 ? qty : 1m,
            UnitPrice = price,
            Total = total
        };
    }
}
