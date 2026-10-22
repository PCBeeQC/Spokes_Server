namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Out-of-the-box multilingual and structural classifier for Western accounting documents.
/// Automatically distinguishes actionable Invoices and Credit Notes from Statements of Account,
/// Payment Receipts, and Scanned Image PDFs without requiring manual user keyword configuration.
/// </summary>
public static class UniversalDocumentClassifier
{
    private static readonly Regex StatementTitleRegex = new(
        @"\b(?:" +
        @"statement\s+of\s+account|account\s+statement|monthly\s+statement|customer\s+statement|billing\s+statement|" +
        @"[eé]tat\s+de\s+compte|relev[eé]\s+de\s+compte|sommaire\s+du\s+compte|relev[eé]\s+mensuel|" +
        @"kontoauszug|saldenbest[aä]tigung|offene[\s\-]*posten[\s\-]*liste|kontokorrentauszug|" +
        @"estado\s+de\s+cuenta|extracto\s+de\s+cuenta|" +
        @"estratto\s+conto|riepilogo\s+conto|" +
        @"rekeningoverzicht|openstaande\s+posten|" +
        @"extrato\s+de\s+conta" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AgingBucketRegex = new(
        @"\b(?:" +
        @"1\s*[-–]\s*30\s*(?:days|jours|tage)?|31\s*[-–]\s*60\s*(?:days|jours|tage)?|" +
        @"61\s*[-–]\s*90\s*(?:days|jours|tage)?|91\s*[-–]\s*120\s*(?:days|jours|tage)?|" +
        @"over\s+90\s*(?:days)?|plus\s+de\s+90\s*(?:jours)?|90\s*\+\s*(?:days|jours)|" +
        @"30\s+days\s+past\s+due|60\s+days\s+past\s+due|90\s+days\s+past\s+due|" +
        @"previous\s+balance|balance\s+forward|opening\s+balance|solde\s+ant[eé]rieur|solde\s+report[eé]" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CreditNotePhraseRegex = new(
        @"\b(?:" +
        @"credit\s+note|credit\s+memo(?:randum)?|cn\s*(?:no\.?|#|number)|" +
        @"note\s+de\s+cr[eé]dit|facture\s+d['’]avoir|note\s+d['’]avoir|avoir\s+n[o°º.]|" +
        @"gutschrift|rechnungsr[uü]ckerstattung|korrekturrechnung|" +
        @"nota\s+de\s+cr[eé]dito|nota\s+de\s+abono|factura\s+rectificativa|" +
        @"nota\s+di\s+credito|nota\s+di\s+accredito|" +
        @"creditnota|creditfactuur" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Standalone "AVOIR" only when used as a document title/header on its own or with a document number
    private static readonly Regex StandaloneAvoirHeaderRegex = new(
        @"^\s*avoir(?:\s*(?:n[o°º.]?|#|du|\d[\w\-]*))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PaymentTermsExclusionRegex = new(
        @"\b(?:" +
        @"due\s+(?:upon|on)\s+receipt|payable\s+(?:upon|on)\s+receipt|upon\s+receipt\s+of\s+invoice|" +
        @"payable\s+[aà]\s+r[eé]ception|[aà]\s+la\s+r[eé]ception\s+de\s+la\s+facture|" +
        @"zahlbar\s+sofort\s+nach\s+erhalt|bei\s+erhalt" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PaymentReceiptTitleRegex = new(
        @"\b(?:" +
        @"payment\s+receipt|official\s+receipt|sales\s+receipt|receipt\s+of\s+payment|payment\s+confirmation|remittance\s+advice|" +
        @"re[cç]u\s+de\s+paiement|confirmation\s+de\s+paiement|re[cç]u\s+officiel|avis\s+de\s+paiement|" +
        @"zahlungsbest[aä]tigung|quittung|zahlungsbeleg|" +
        @"recibo\s+de\s+pago|comprobante\s+de\s+pago|" +
        @"ricevuta\s+di\s+pagamento|quietanza\s+di\s+pagamento|" +
        @"betalingsbewijs|kwitantie|" +
        @"comprovativo\s+de\s+pagamento" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CardPaymentSignalRegex = new(
        @"\b(?:" +
        @"auth(?:orization)?\s*(?:code|#)\s*[:#]?\s*[A-Z0-9]{4,}|" +
        @"transaction\s+approved|paiement\s+approuv[eé]|" +
        @"(?:visa|mastercard|amex|american\s+express|interac)\s*[\*xX•\.]{2,}\s*\d{3,4}" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvoiceTitleSignalRegex = new(
        @"\b(?:tax\s+invoice|invoice\s*(?:no\.?|#|number)|facture\s*(?:n[o°º.]?|#)|rechnung\s*(?:nr\.?|nummer)|fattura|factura|factuur)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StoreChargeTermsSignalRegex = new(
        @"\b(?:net\s+\d+|charge\s+account|compte\s+charge|\bcharg\b|balance\s+due|solde\s+d[uû]|montant\s+[aà]\s+payer)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Classifies an extracted PDF document in-place (Statement, Credit Note, Payment Receipt, Scanned Image PDF).
    /// </summary>
    public static void ClassifyDocument(ExtractedInvoiceData data, SpatialTextDocument spatialDoc)
    {
        // 1. Scanned Image PDF check
        if (!spatialDoc.HasSelectableText || string.IsNullOrWhiteSpace(spatialDoc.FullLayoutText))
        {
            data.IsScannedImagePdf = true;
            data.ClassificationReason = "Scanned image PDF (no selectable text layer)";
            data.ExtractionLog.Add("[Classifier] Flagged as scanned image PDF: document contains pages with 0 selectable words");
            return;
        }

        var fullText = spatialDoc.FullLayoutText;
        var headerText = string.Join("\n", spatialDoc.Pages.Take(1).SelectMany(p => p.HeaderLines.Take(18)).Select(l => l.Text));
        if (string.IsNullOrWhiteSpace(headerText))
        {
            headerText = string.Join("\n", spatialDoc.AllLines.Take(18).Select(l => l.Text));
        }

        // 2. Statement of Account check
        var statementMatch = StatementTitleRegex.Match(fullText);
        if (statementMatch.Success)
        {
            data.IsStatement = true;
            data.ClassificationReason = $"Statement of account (matched '{statementMatch.Value.Trim()}')";
            data.ExtractionLog.Add($"[Classifier] Flagged as Statement of Account via phrase '{statementMatch.Value.Trim()}'");
            return;
        }

        var agingMatches = AgingBucketRegex.Matches(fullText)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (agingMatches.Count >= 2 && !InvoiceTitleSignalRegex.IsMatch(headerText))
        {
            data.IsStatement = true;
            data.ClassificationReason = $"Statement of account (detected aging buckets: {string.Join(", ", agingMatches.Take(3))})";
            data.ExtractionLog.Add($"[Classifier] Flagged as Statement of Account via structural aging buckets ({string.Join(", ", agingMatches)})");
            return;
        }

        // 3. Credit Note check
        bool isCreditNote = data.IsCreditNote || data.TotalAmount < 0;
        string? creditSignal = data.TotalAmount < 0 ? "negative total amount" : null;

        if (!isCreditNote)
        {
            var cnMatch = CreditNotePhraseRegex.Match(headerText);
            if (!cnMatch.Success)
            {
                cnMatch = CreditNotePhraseRegex.Match(fullText);
            }

            if (cnMatch.Success)
            {
                isCreditNote = true;
                creditSignal = cnMatch.Value.Trim();
            }
            else
            {
                foreach (var line in spatialDoc.AllLines.Take(15))
                {
                    if (StandaloneAvoirHeaderRegex.IsMatch(line.Text))
                    {
                        isCreditNote = true;
                        creditSignal = line.Text.Trim();
                        break;
                    }
                }
            }
        }

        if (isCreditNote)
        {
            ApplyCreditNoteNormalization(data, creditSignal ?? "credit note indicator");
            return;
        }

        // 4. Payment Receipt check (guarded against "Due upon receipt" and store-charge invoices)
        var textWithoutReceiptTerms = PaymentTermsExclusionRegex.Replace(fullText, " ");
        var headerWithoutReceiptTerms = PaymentTermsExclusionRegex.Replace(headerText, " ");

        var receiptTitleMatch = PaymentReceiptTitleRegex.Match(headerWithoutReceiptTerms);
        if (!receiptTitleMatch.Success)
        {
            receiptTitleMatch = PaymentReceiptTitleRegex.Match(textWithoutReceiptTerms);
        }

        bool hasInvoiceHeader = InvoiceTitleSignalRegex.IsMatch(headerText) || !string.IsNullOrWhiteSpace(data.InvoiceNumber);
        bool hasStoreChargeOrTerms = !string.IsNullOrWhiteSpace(data.PaymentTerms) || StoreChargeTermsSignalRegex.IsMatch(textWithoutReceiptTerms);

        if (receiptTitleMatch.Success && !hasStoreChargeOrTerms)
        {
            data.IsPaymentReceipt = true;
            data.ClassificationReason = $"Payment receipt (matched '{receiptTitleMatch.Value.Trim()}')";
            data.ExtractionLog.Add($"[Classifier] Flagged as Payment Receipt via phrase '{receiptTitleMatch.Value.Trim()}'");
            return;
        }

        var cardSignalMatch = CardPaymentSignalRegex.Match(textWithoutReceiptTerms);
        if (cardSignalMatch.Success && !hasInvoiceHeader && !hasStoreChargeOrTerms)
        {
            data.IsPaymentReceipt = true;
            data.ClassificationReason = $"Payment receipt (card payment '{cardSignalMatch.Value.Trim()}')";
            data.ExtractionLog.Add($"[Classifier] Flagged as Payment Receipt via card payment signal '{cardSignalMatch.Value.Trim()}'");
        }
    }

    /// <summary>
    /// Ensures a Credit Note has `IsCreditNote = true` and negative monetary totals for accounting consistency.
    /// </summary>
    public static void ApplyCreditNoteNormalization(ExtractedInvoiceData data, string reason)
    {
        data.IsCreditNote = true;
        if (string.IsNullOrWhiteSpace(data.ClassificationReason))
        {
            data.ClassificationReason = $"Credit note ({reason})";
        }

        if (data.TotalAmount > 0)
        {
            data.TotalAmount = -Math.Abs(data.TotalAmount);
            data.SubTotal = -Math.Abs(data.SubTotal);
            data.TaxAmount = -Math.Abs(data.TaxAmount);
            data.TpsAmount = -Math.Abs(data.TpsAmount);
            data.TvqAmount = -Math.Abs(data.TvqAmount);
            foreach (var item in data.LineItems)
            {
                if (item.Total > 0)
                {
                    item.Total = -Math.Abs(item.Total);
                }
            }
            data.ExtractionLog.Add($"[Classifier] Normalized Credit Note totals to negative values ({reason})");
        }
        else
        {
            data.ExtractionLog.Add($"[Classifier] Classified as Credit Note ({reason})");
        }
    }

    /// <summary>
    /// Classifies an incoming email at the inbox level, prioritizing actionable attached invoices
    /// over email body/subject phrasing (e.g., overdue reminders with real invoice PDFs attached).
    /// </summary>
    public static (bool IsStatement, bool IsPaymentReceipt, string? ClassificationReason) ClassifyEmail(
        string subject,
        string textBody,
        IReadOnlyList<ExtractedInvoiceData> extractedInvoices)
    {
        // If any attached PDF was confidently parsed as an actionable invoice (or credit note), process it as a bill!
        if (extractedInvoices.Any(i => i.IsConfident && !i.IsStatement && !i.IsPaymentReceipt))
        {
            return (false, false, null);
        }

        // If all extracted attachments were statements
        if (extractedInvoices.Count > 0 && extractedInvoices.All(i => i.IsStatement))
        {
            var reason = extractedInvoices.First().ClassificationReason ?? "Attached PDF classified as Statement of Account";
            return (true, false, reason);
        }

        // If all extracted attachments were payment receipts
        if (extractedInvoices.Count > 0 && extractedInvoices.All(i => i.IsPaymentReceipt))
        {
            var reason = extractedInvoices.First().ClassificationReason ?? "Attached PDF classified as Payment Receipt";
            return (false, true, reason);
        }

        // Inspect subject + email body when no actionable invoice PDF was extracted
        var combined = $"{subject}\n{textBody}";
        var cleaned = PaymentTermsExclusionRegex.Replace(combined, " ");

        var statementMatch = StatementTitleRegex.Match(cleaned);
        if (statementMatch.Success)
        {
            return (true, false, $"Statement of account (matched '{statementMatch.Value.Trim()}' in email)");
        }

        var receiptMatch = PaymentReceiptTitleRegex.Match(cleaned);
        if (receiptMatch.Success)
        {
            return (false, true, $"Payment receipt (matched '{receiptMatch.Value.Trim()}' in email)");
        }

        if (extractedInvoices.Any(i => i.IsScannedImagePdf))
        {
            return (false, false, "Attached PDF is a scanned image without a selectable text layer (manual entry required)");
        }

        return (false, false, null);
    }
}
