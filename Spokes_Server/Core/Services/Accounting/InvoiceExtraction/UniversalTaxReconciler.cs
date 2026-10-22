namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Reconciles financial amounts and taxes across Western world invoicing standards
/// (VAT, GST, US Sales Tax, Canadian split taxes TPS/TVQ/TVP/HST, discounts, shipping, and zero-tax B2B).
/// </summary>
public static class UniversalTaxReconciler
{
    private const string AmountCapturePattern = @"([-+(]?\s*(?:[$€£]\s*)?(?:\d{1,3}(?:,\d{3})*(?:\.\d{2})|\d{1,3}(?:\.\d{3})*(?:,\d{2})|\d{1,3}(?:['’\u2019]\d{3})*(?:[,\.]\d{2})|\d{1,3}(?:[ \u00A0\u202F]\d{3})*(?:[,\.]\d{2})|\d+[,\.]\d{2}|\d+)\)?)";
    private const string TaxRateAndBaseSkip = @"(?:\s*\([A-Z0-9\-\s\.]+\))?(?:\s*\(?\s*(?:@|\(taxe\)?|rate)?\s*[\d,\.]+\s*%\s*\)?)?(?:\s*\(?\s*(?:sur|on|auf|sobre|base|of|von|over)\s*(?:CAD|USD|EUR|GBP|CHF|\$|€|£)?\s*[\d\s,\.']+(?:\.\d{2}|,\d{2})?\s*(?:CAD|USD|EUR|GBP|CHF|\$|€|£)?\s*\)?)?";

    private static readonly Regex AmountPattern = new(
        @"(?<=[^\d\w]|^)(?:CAD|USD|EUR|GBP|AUD|NZD|CHF|SEK|NOK|DKK|PLN|CZK|US\$|CA\$|C\$|A\$|NZ\$|\$|£|€)?\s*[-+]?(?:\d{1,3}(?:,\d{3})*(?:\.\d{2})|\d{1,3}(?:\.\d{3})*(?:,\d{2})|\d{1,3}(?:['’\u2019]\d{3})*(?:[,\.]\d{2})|\d{1,3}(?:[ \u00A0\u202F]\d{3})*(?:[,\.]\d{2})|\d+[,\.]\d{2}|\d+)\s*(?:CAD|USD|EUR|GBP|AUD|NZD|CHF|SEK|NOK|DKK|PLN|CZK|\$|£|€)?(?:\s*CR\b)?(?=[^\d\w]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CurrencyStripRegex = new(
        @"(?:US\$|CA\$|C\$|A\$|NZ\$|\b(?:CAD|USD|EUR|GBP|AUD|NZD|CHF|SEK|NOK|DKK|PLN|CZK|FR\.|KR\.?|ZŁ|KČ)\b|[$€£])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses numeric amounts supporting multi-locale conventions:
    /// French/Quebec (6 397,10 $), European (1.234,56 €), Swiss (1'234.56 CHF),
    /// Anglo-American ($1,234.56), and credit note CR suffixes (150.25 CR).
    /// </summary>
    public static decimal ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;

        var cleaned = raw.Trim();
        bool hasCrSuffix = Regex.IsMatch(cleaned, @"\bCR\s*$", RegexOptions.IgnoreCase);
        if (hasCrSuffix)
        {
            cleaned = Regex.Replace(cleaned, @"\bCR\s*$", "", RegexOptions.IgnoreCase).Trim();
        }

        cleaned = CurrencyStripRegex.Replace(cleaned, "")
            .Replace("\u00A0", " ")
            .Replace("\u202F", " ")
            .Replace("’", "'")
            .Replace("\u2019", "'")
            .Trim();

        bool isNegative = hasCrSuffix || cleaned.StartsWith('-') || cleaned.EndsWith('-') || (cleaned.StartsWith('(') && cleaned.EndsWith(')'));
        cleaned = cleaned.Trim('-', '+', '(', ')').Trim();

        if (string.IsNullOrWhiteSpace(cleaned)) return 0m;

        // Remove Swiss apostrophe thousands separator (e.g. 1'234.56 or 1'234,56)
        if (cleaned.Contains('\''))
        {
            cleaned = cleaned.Replace("'", "");
        }

        int commaIdx = cleaned.LastIndexOf(',');
        int dotIdx = cleaned.LastIndexOf('.');

        if (commaIdx >= 0 && dotIdx >= 0)
        {
            if (dotIdx > commaIdx)
            {
                // Standard 1,234.56
                cleaned = cleaned.Replace(",", "").Replace(" ", "");
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return isNegative ? -val : val;
            }
            else
            {
                // European 1.234,56
                cleaned = cleaned.Replace(".", "").Replace(" ", "").Replace(',', '.');
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return isNegative ? -val : val;
            }
        }
        else if (commaIdx >= 0)
        {
            int decimalsAfterComma = cleaned.Length - commaIdx - 1;
            if (decimalsAfterComma == 2)
            {
                // French/European comma decimal: 6 397,10 or 123,45
                cleaned = cleaned.Replace(" ", "").Replace(',', '.');
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return isNegative ? -val : val;
            }
            else if (decimalsAfterComma == 3)
            {
                // Thousand separator: 1,234
                cleaned = cleaned.Replace(",", "").Replace(" ", "");
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return isNegative ? -val : val;
            }
            else
            {
                cleaned = cleaned.Replace(" ", "").Replace(',', '.');
                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                    return isNegative ? -val : val;
            }
        }
        else if (dotIdx >= 0)
        {
            cleaned = cleaned.Replace(" ", "");
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                return isNegative ? -val : val;
        }
        else
        {
            cleaned = cleaned.Replace(" ", "");
            if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                return isNegative ? -val : val;
        }

        return 0m;
    }

    /// <summary>
    /// Reconciles subtotal, discounts, shipping, taxes (TPS, TVQ/TVP, VAT, Sales Tax), and grand total.
    /// Uses mathematical equation satisfaction to disambiguate and cross-validate totals.
    /// </summary>
    public static void ReconcileTotals(ExtractedInvoiceData data, SpatialTextDocument document)
    {
        var text = document.FullLayoutText;
        const string currPrefix = @"[\s:$€£]*(?:(?:CAD|USD|EUR|GBP|AUD|NZD|CHF|US\$|CA\$)\s*)?";

        // 1. Extract explicit candidate Subtotal (prefer bottom-most match in document)
        decimal candidateSubtotal = MatchLastAmount(
            text,
            @"(?:subtotal|sous-total|sub-total|total\s*partiel|total\s*avant\s*taxes?|montant\s*(?:avant\s*taxes?|ht)|total\s*ht|net\s*amount|nettobetrag|zwischensumme|netto|imponibile|base\s*imponible)" + currPrefix + AmountCapturePattern);

        if (candidateSubtotal == 0)
        {
            var subBox = document.FindValueNearLabel(
                new Regex(@"^(?:subtotal|sous-total|sub-total|total\s*avant\s*taxes?|montant\s*ht|nettobetrag|zwischensumme)$", RegexOptions.IgnoreCase),
                AmountPattern,
                searchBottomUp: true);
            if (!string.IsNullOrWhiteSpace(subBox))
            {
                candidateSubtotal = ParseAmount(subBox);
            }
        }

        // 1b. Extract optional Discount and Shipping/Freight amounts
        decimal candidateDiscount = Math.Abs(MatchLastAmount(
            text,
            @"\b(?:remise|escompte|rabais|discount|rabatt|skonto|descuento|sconto)(?:\s*\d+(?:[,\.]\d+)?\s*%)?" + currPrefix + AmountCapturePattern));

        decimal candidateShipping = MatchLastAmount(
            text,
            @"\b(?:frais\s*de\s*(?:port|livraison|transport|manutention)|livraison|shipping(?:\s*&\s*handling)?|freight|delivery(?:\s*charge)?|versand(?:kosten)?|porto|fracht)" + currPrefix + AmountCapturePattern);

        // 2. Extract explicit Federal/Primary Tax (TPS / GST)
        decimal candidateTps = MatchTaxAmount(
            text,
            @"(?<!inscription\s*(?:à\s*la\s*|a\s*la\s*)?)(?<!n[oº°\.]*\s*(?:d['’]inscription\s*(?:à\s*la\s*|a\s*la\s*)?))\b(?:tps|gst)\b" + TaxRateAndBaseSkip + currPrefix + AmountCapturePattern + @"(?:[ \t]+(?:[$€£]\s*)?" + AmountCapturePattern + @")?",
            candidateSubtotal);

        // 3. Extract explicit Provincial/State Tax (TVQ / TVP / QST / PST)
        decimal candidateTvq = MatchTaxAmount(
            text,
            @"(?<!enregistrement\s*(?:de\s*la\s*)?)(?<!n[oº°\.]*\s*(?:d['’]enregistrement\s*(?:de\s*la\s*)?))\b(?:tvq|tvp|qst|pst)\b" + TaxRateAndBaseSkip + currPrefix + AmountCapturePattern + @"(?:[ \t]+(?:[$€£]\s*)?" + AmountCapturePattern + @")?",
            candidateSubtotal);

        // 4. Single-tax / Harmonized Tax (TVH / HST / VAT / TVA / MwSt / USt / Sales Tax / Total Tax / Tax)
        var generalTaxPattern = @"(?<!(?:avant|before|hors|sans|net\s*de|net\s*of|excl\.?|exkl\.?)\s+)\b(?:tvh|hst|total\s*(?:des\s*taxes?|taxes?)|vat|tva|mwst\.?|ust\.?|mehrwertsteuer|iva|btw|moms|sales\s*tax|tax(?:es?)?|tax\s*amount)\b" + TaxRateAndBaseSkip + currPrefix + AmountCapturePattern + @"(?:[ \t]+(?:[$€£]\s*)?" + AmountCapturePattern + @")?";
        var generalTaxCandidates = MatchAllTaxAmounts(text, generalTaxPattern, candidateSubtotal);
        decimal candidateGeneralTax = generalTaxCandidates.Count > 0 ? generalTaxCandidates[^1] : 0m;

        // 5. Grand Total / Total à payer / Charge on account
        decimal candidateTotal = MatchLastAmount(
            text,
            @"(?<!sous-)(?<!sub-)(?<!total\s+)\b(?:grand\s*total|montant\s*total|total\s*(?:d[uû]|due|facture|à\s*payer|a\s*payer|net|ttc|incl\.?\s*(?:vat|tax)|inkl\.?\s*mwst)?|gesamtbetrag|bruttobetrag|rechnungsbetrag|total\s*general|totale\s*fattura|totaalbedrag|charg(?:e|es?)?|total\s*charg(?:e|es?)?|net\s*charg(?:e|es?)?)\b(?![\s:]*(?:partiel|avant|des?\s*taxes?|de\s*la\s*taxe|ht|netto|excl))" + currPrefix + AmountCapturePattern);

        if (candidateTotal == 0)
        {
            var totalBox = document.FindValueNearLabel(
                new Regex(@"^(?:grand\s*total|montant\s*total|total\s*(?:due|d[uû]|ttc|à\s*payer|a\s*payer)?|amount\s*due|balance\s*due|gesamtbetrag|rechnungsbetrag)$", RegexOptions.IgnoreCase),
                AmountPattern,
                searchBottomUp: true);
            if (!string.IsNullOrWhiteSpace(totalBox))
            {
                candidateTotal = ParseAmount(totalBox);
            }
        }

        // 6. Explicit Prior Payments / Acomptes / Paid Amounts (multi-word phrases or standalone summary labels like Payée / Paid / Bezahlt)
        decimal candidatePaid = Math.Abs(MatchLastAmount(
            text,
            @"(?:\b(?:paiements?(?:\s*re[çc]us?|\s*appliqu[eé]s?)?|montant\s*pay[eé]e?s?|total\s*pay[eé]e?s?|d[eé]j[àa]\s*(?:pay[eé]e?s?|vers[eé]e?s?)|acomptes?|versements?|payments?(?:\s*received|\s*applied)?|amount\s*paid|total\s*paid|paid\s*to\s*date|less\s*(?:previous\s*)?(?:payments?|paid)|prepaid(?:\s*amount)?|anzahlung|bereits\s*gezahlt|pagos?(?:\s*recibidos?)?)|(?:^|\n|\|)\s*(?:pay[eé]e?s?|paid|bezahlt|gezahlt|pagad[oa]s?|pagat[oa]s?|betaald)\b)" + currPrefix + AmountCapturePattern));

        if (candidatePaid == 0)
        {
            var paidBox = document.FindValueNearLabel(
                new Regex(@"^(?:pay[eé]e?s?|montant\s*pay[eé]e?s?|total\s*pay[eé]e?s?|paiements?|paid|amount\s*paid|total\s*paid|payments?|bezahlt|gezahlt|pagad[oa]|pagat[oa]|betaald)$", RegexOptions.IgnoreCase),
                AmountPattern,
                searchBottomUp: true);
            if (!string.IsNullOrWhiteSpace(paidBox))
            {
                candidatePaid = Math.Abs(ParseAmount(paidBox));
            }
        }

        // 7. Explicit Balance Due / Solde à payer (preserves explicit 0.00 balance due)
        bool hasExplicitBalanceDue = TryMatchLastAmount(
            text,
            @"(?:\b(?:solde\s*(?:d[uû]|du|à\s*payer|a\s*payer|actuel|final)|net\s*à\s*payer|net\s*a\s*payer|net\s*payable|reste\s*à\s*payer|reste\s*a\s*payer|montant\s*(?:d[uû]|du)|balance\s*due|amount\s*due|net\s*(?:amount\s*)?due|total\s*due|remaining\s*balance|f[aä]lliger\s*betrag|restbetrag|offener\s*betrag|zu\s*zahlen|saldo\s*(?:pendiente|dovuto|deudor)|importe\s*a\s*pagar|openstaand\s*bedrag|te\s*betalen)|(?:^|\n|\|)\s*(?:solde|balance|saldo)\b)" + currPrefix + AmountCapturePattern,
            allowZero: true,
            out decimal candidateBalanceDue);

        if (!hasExplicitBalanceDue)
        {
            var balBox = document.FindValueNearLabel(
                new Regex(@"^(?:solde(?:\s*d[uû]|\s*à\s*payer|\s*a\s*payer)?|reste\s*à\s*payer|balance(?:\s*due)?|amount\s*due|restbetrag|f[aä]lliger\s*betrag|saldo(?:\s*pendiente|\s*dovuto)?)$", RegexOptions.IgnoreCase),
                AmountPattern,
                searchBottomUp: true);
            if (!string.IsNullOrWhiteSpace(balBox))
            {
                candidateBalanceDue = ParseAmount(balBox);
                hasExplicitBalanceDue = true;
            }
        }

        // Fallback total if candidateTotal was not matched
        if (candidateTotal == 0)
        {
            if (hasExplicitBalanceDue && candidatePaid != 0)
            {
                candidateTotal = candidateBalanceDue + candidatePaid;
            }
            else if (candidateBalanceDue != 0)
            {
                candidateTotal = candidateBalanceDue;
            }
        }

        // Line items sum fallback for subtotal when subtotal was not explicitly labeled
        if (candidateSubtotal == 0 && data.LineItems.Count > 0)
        {
            var lineItemsSum = data.LineItems.Sum(i => i.Total);
            if (lineItemsSum != 0)
            {
                candidateSubtotal = lineItemsSum;
            }
        }

        // 8. Footer Line Scanning: Check for unlabelled or numbered tax line pairs (e.g. A) ... 0.75, B) ... 1.50)
        if (candidateTps == 0 || candidateTvq == 0)
        {
            foreach (var line in document.AllLines.Where(l => l.Y < 300))
            {
                var lineText = line.Text;
                if (candidateTps == 0 && Regex.IsMatch(lineText, @"\b(?:tps|gst)\b", RegexOptions.IgnoreCase))
                {
                    var amt = ExtractLastAmount(lineText);
                    if (amt > 0) candidateTps = amt;
                }

                if (candidateTvq == 0 && Regex.IsMatch(lineText, @"\b(?:tvq|tvp|qst|pst)\b", RegexOptions.IgnoreCase))
                {
                    var amt = ExtractLastAmount(lineText);
                    if (amt > 0) candidateTvq = amt;
                }
            }
        }

        // 9. Mathematical Reconciliation & Universal Equation Solver:
        // EffectiveNet = SubTotal - Discount + Shipping
        // EffectiveNet + Taxes = Total
        decimal effectiveNet = candidateSubtotal - candidateDiscount + candidateShipping;

        // Check if multiple VAT/MwSt/TVA rate lines sum to the exact tax difference
        if (generalTaxCandidates.Count > 1 && candidateTotal > 0 && effectiveNet > 0)
        {
            decimal sumMultiRateTax = generalTaxCandidates.Sum();
            if (Math.Abs((effectiveNet + sumMultiRateTax) - candidateTotal) <= 0.05m)
            {
                candidateGeneralTax = sumMultiRateTax;
            }
        }

        decimal totalTax = 0m;
        if (candidateTps != 0 || candidateTvq != 0)
        {
            totalTax = candidateTps + candidateTvq;
        }
        else if (candidateGeneralTax != 0)
        {
            totalTax = candidateGeneralTax;
        }

        // If candidateTotal and candidateSubtotal exist, check if diff matches candidate tax
        if (candidateTotal > 0 && candidateSubtotal > 0)
        {
            decimal diff = candidateTotal - effectiveNet;
            if (diff >= 0)
            {
                if (totalTax == 0 && diff > 0)
                {
                    totalTax = diff;

                    // If candidate looks like Canadian/Quebec dual rate (5% and 9.975% = 14.975%)
                    if (Math.Abs(effectiveNet * 0.14975m - diff) <= 0.05m)
                    {
                        candidateTps = Math.Round(effectiveNet * 0.05m, 2);
                        candidateTvq = Math.Round(effectiveNet * 0.09975m, 2);
                    }
                }
                else if (totalTax > 0 && Math.Abs(diff - totalTax) <= 0.03m)
                {
                    // Perfectly reconciled
                }
            }
            else if (diff < 0 && totalTax >= 0)
            {
                // Disambiguation: candidateTotal was smaller than SubTotal (e.g. matched "Total à payer" which was only balance due)
                decimal fullExpectedTotal = effectiveNet + totalTax;
                if (candidatePaid > 0 && Math.Abs(fullExpectedTotal - candidatePaid - candidateTotal) <= 0.05m)
                {
                    if (!hasExplicitBalanceDue || candidateBalanceDue == 0)
                    {
                        candidateBalanceDue = candidateTotal;
                        hasExplicitBalanceDue = true;
                    }
                    candidateTotal = fullExpectedTotal;
                }
            }
        }
        else if (candidateTotal == 0 && candidateSubtotal > 0 && totalTax > 0)
        {
            candidateTotal = effectiveNet + totalTax;
        }
        else if (candidateTotal > 0 && candidateSubtotal == 0 && totalTax > 0 && candidateTotal > totalTax)
        {
            candidateSubtotal = candidateTotal - totalTax + candidateDiscount - candidateShipping;
        }
        else if (candidateTotal > 0 && candidateSubtotal == 0 && totalTax == 0)
        {
            // Zero-tax invoice or subtotal equals total
            candidateSubtotal = candidateTotal + candidateDiscount - candidateShipping;
        }

        // 10. Reconcile Payments and Balance Due
        if (hasExplicitBalanceDue)
        {
            if (candidateBalanceDue == 0m && candidateTotal > 0m && candidatePaid == 0m)
            {
                // Explicit zero balance on a non-zero invoice implies full payment
                candidatePaid = candidateTotal;
            }
            else if (candidateBalanceDue > 0m && candidatePaid == 0m && candidateTotal > candidateBalanceDue)
            {
                candidatePaid = candidateTotal - candidateBalanceDue;
            }
            else if (candidateBalanceDue > 0m && candidatePaid > 0m && candidateTotal == 0m)
            {
                candidateTotal = candidatePaid + candidateBalanceDue;
            }
        }
        else
        {
            if (candidatePaid > 0m && candidateTotal >= candidatePaid)
            {
                candidateBalanceDue = candidateTotal - candidatePaid;
            }
            else if (candidatePaid == 0m)
            {
                candidateBalanceDue = candidateTotal;
            }
        }

        data.SubTotal = candidateSubtotal;
        data.DiscountAmount = candidateDiscount;
        data.ShippingAmount = candidateShipping;
        data.TpsAmount = candidateTps;
        data.TvqAmount = candidateTvq;
        data.TaxAmount = totalTax;
        data.TotalAmount = candidateTotal;
        data.PaidAmount = candidatePaid;
        data.BalanceDue = candidateBalanceDue;
    }

    private static decimal MatchLastAmount(string text, string pattern)
    {
        return TryMatchLastAmount(text, pattern, allowZero: false, out var val) ? val : 0m;
    }

    private static bool TryMatchLastAmount(string text, string pattern, bool allowZero, out decimal amount)
    {
        amount = 0m;
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var raw = matches[i].Groups[1].Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var val = ParseAmount(raw);
            if (val != 0 || (allowZero && Regex.IsMatch(raw, @"\d")))
            {
                amount = val;
                return true;
            }
        }
        return false;
    }

    private static decimal MatchTaxAmount(string text, string pattern, decimal candidateSubtotal)
    {
        var list = MatchAllTaxAmounts(text, pattern, candidateSubtotal);
        return list.Count > 0 ? list[^1] : 0m;
    }

    private static List<decimal> MatchAllTaxAmounts(string text, string pattern, decimal candidateSubtotal)
    {
        var results = new List<decimal>();
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var firstVal = ParseAmount(match.Groups[1].Value);
            if (match.Groups.Count > 2 && match.Groups[2].Success)
            {
                var secondVal = ParseAmount(match.Groups[2].Value);
                // If two numbers appear on the same tax line and the first equals the taxable base (SubTotal), take the second (actual tax amount)
                if (secondVal > 0 && candidateSubtotal > 0 && Math.Abs(firstVal - candidateSubtotal) <= 0.05m && secondVal < firstVal)
                {
                    results.Add(secondVal);
                    continue;
                }
            }
            if (firstVal != 0)
            {
                results.Add(firstVal);
            }
        }
        return results;
    }

    private static decimal ExtractLastAmount(string line)
    {
        var matches = AmountPattern.Matches(line);
        if (matches.Count > 0)
        {
            return ParseAmount(matches[^1].Value);
        }
        return 0m;
    }
}
