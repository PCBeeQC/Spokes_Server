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
    private static readonly string[] StatementFileNameTokens =
    [
        "statement", "releve", "relevé", "etat_de_compte", "etat-de-compte",
        "kontoauszug", "extracto", "sommaire"
    ];

    private static readonly Regex StatementTitleRegex = new(
        @"\b(?:" +
        @"statement\s+of\s+account|account\s+statement|monthly\s+statement|customer\s+statement|billing\s+statement|" +
        @"account\s+summary|statement\s+summary|statement\s+date|statement\s+period|aging\s+summary|" +
        @"current\s+30\s+60\s+90|balance\s+forward|previous\s+balance|" +
        @"[eé]tat\s+de\s+compte|relev[eé]\s+de\s+compte|sommaire\s+(?:du|de)\s+compte|rapport\s+de\s+compte|relev[eé]\s+mensuel|date\s+de\s+relev[eé]|" +
        @"kontoauszug|rechnungsauszug|saldenbest[aä]tigung|offene[\s\-]*posten(?:[\s\-]*liste)?|kontokorrentauszug|" +
        @"estado\s+de\s+cuenta|extracto\s+de\s+cuenta|resumen\s+de\s+cuenta|" +
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

    private const string IncomingRemittancePattern =
        @"remittance\s+advice|payment\s+advice|avis\s+de\s+paiement|avis\s+de\s+remise|avis\s+de\s+virement|avis\s+de\s+d[eé]p[oô]t|bordereau\s+de\s+paiement|" +
        @"interac\s*e[\-\s]?transfer|virement\s+interac|" +
        @"direct\s+deposit\s+(?:notice|advice|notification|confirmation)|d[eé]p[oô]t\s+direct|" +
        @"(?:eft|ach|wire)\s+(?:payment\s+)?(?:advice|remittance|confirmation|notification)|" +
        @"payment\s+received|paiement\s+re[cç]u|" +
        @"automatically\s+deposited|d[eé]p[oô]t\s+automatique|" +
        @"deposited\s+(?:in|into)\s+your\s+(?:bank\s+)?account|d[eé]pos[eé]\s+dans\s+votre\s+compte|" +
        @"(?:has\s+sent\s+you|sent\s+you|vous\s+a\s+envoy[eé])\s+(?:money|funds|a\s+payment|an?\s+interac|un\s+virement|un\s+paiement)|" +
        @"(?:you(?:'ve|\s+have)?|vous\s+avez)\s+(?:received|re[cç]u)\s+(?:a\s+|an\s+|un\s+|le\s+)?(?:payment|paiement|virement|d[eé]p[oô]t|transfer)";

    private static readonly Regex PaymentReceiptTitleRegex = new(
        @"\b(?:" +
        @"payment\s+receipt|official\s+receipt|sales\s+receipt|receipt\s+of\s+payment|payment\s+confirmation|" +
        @"re[cç]u\s+de\s+paiement|confirmation\s+de\s+paiement|re[cç]u\s+officiel|avis\s+de\s+paiement|" +
        IncomingRemittancePattern + @"|" +
        @"zahlungsbest[aä]tigung|quittung|zahlungsbeleg|" +
        @"recibo\s+de\s+pago|comprobante\s+de\s+pago|" +
        @"ricevuta\s+di\s+pagamento|quietanza\s+di\s+pagamento|" +
        @"betalingsbewijs|kwitantie|" +
        @"comprovativo\s+de\s+pagamento" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IncomingRemittanceOrDepositRegex = new(
        @"\b(?:" + IncomingRemittancePattern + @")\b",
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
    /// Checks whether a filename matches any known statement-of-account token across supported languages.
    /// </summary>
    public static bool TryMatchStatementFileName(string? fileName, out string? matchedToken)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var token in StatementFileNameTokens)
            {
                if (fileName.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    matchedToken = token;
                    return true;
                }
            }
        }

        matchedToken = null;
        return false;
    }

    /// <summary>
    /// Classifies an extracted PDF document in-place (Statement, Credit Note, Payment Receipt, Scanned Image PDF).
    /// </summary>
    public static void ClassifyDocument(ExtractedInvoiceData data, SpatialTextDocument spatialDoc, string? fileName = null)
    {
        // 1. Scanned Image PDF check
        if (!spatialDoc.HasSelectableText ||
            (string.IsNullOrWhiteSpace(spatialDoc.FullLayoutText) && string.IsNullOrWhiteSpace(spatialDoc.RawFullText)))
        {
            data.IsScannedImagePdf = true;
            data.ClassificationReason = "Scanned image PDF (no selectable text layer)";
            return;
        }

        var fullText = !string.IsNullOrWhiteSpace(spatialDoc.FullLayoutText)
            ? spatialDoc.FullLayoutText
            : spatialDoc.RawFullText;
        var headerText = string.Join("\n", spatialDoc.Pages.Take(1).SelectMany(p => p.HeaderLines.Take(18)).Select(l => l.Text));
        if (string.IsNullOrWhiteSpace(headerText))
        {
            headerText = string.Join("\n", spatialDoc.AllLines.Take(18).Select(l => l.Text));
        }

        // 2. Statement of Account check (filename tokens and body phrases before Credit Note / Payment Receipt)
        if (TryMatchStatementFileName(fileName, out var matchedFileToken))
        {
            data.IsAccountStatement = true;
            data.ClassificationReason = $"Statement of account (filename matched '{matchedFileToken}')";
            return;
        }

        var statementSearchText = string.IsNullOrWhiteSpace(spatialDoc.RawFullText) ||
                                  string.Equals(spatialDoc.RawFullText, fullText, StringComparison.Ordinal)
            ? fullText
            : $"{fullText}\n{spatialDoc.RawFullText}";

        var statementMatch = StatementTitleRegex.Match(statementSearchText);
        if (statementMatch.Success)
        {
            data.IsAccountStatement = true;
            data.ClassificationReason = $"Statement of account (matched '{statementMatch.Value.Trim()}')";
            return;
        }

        var agingMatches = AgingBucketRegex.Matches(statementSearchText)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (agingMatches.Count >= 2 && !InvoiceTitleSignalRegex.IsMatch(headerText))
        {
            data.IsAccountStatement = true;
            data.ClassificationReason = $"Statement of account (detected aging buckets: {string.Join(", ", agingMatches.Take(3))})";
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

        var receiptInHeaderMatch = PaymentReceiptTitleRegex.Match(headerWithoutReceiptTerms);
        var receiptTitleMatch = receiptInHeaderMatch.Success
            ? receiptInHeaderMatch
            : PaymentReceiptTitleRegex.Match(textWithoutReceiptTerms);

        bool hasInvoiceHeader = InvoiceTitleSignalRegex.IsMatch(headerText) || !string.IsNullOrWhiteSpace(data.InvoiceNumber);
        bool hasStoreChargeOrTerms = !string.IsNullOrWhiteSpace(data.PaymentTerms) || StoreChargeTermsSignalRegex.IsMatch(textWithoutReceiptTerms);
        bool strongIncomingRemittance = IncomingRemittanceOrDepositRegex.IsMatch(textWithoutReceiptTerms);
        bool settledWithZeroBalance = data.PaidAmount > 0m && data.BalanceDue <= 0.01m;
        bool suppressForUnpaidTerms = hasStoreChargeOrTerms && !receiptInHeaderMatch.Success && !strongIncomingRemittance && !settledWithZeroBalance;

        if (receiptTitleMatch.Success && !suppressForUnpaidTerms)
        {
            data.IsPaymentReceipt = true;
            data.ClassificationReason = $"Payment receipt (matched '{receiptTitleMatch.Value.Trim()}')";
            return;
        }

        var cardSignalMatch = CardPaymentSignalRegex.Match(textWithoutReceiptTerms);
        if (cardSignalMatch.Success && !hasInvoiceHeader && (!hasStoreChargeOrTerms || settledWithZeroBalance))
        {
            data.IsPaymentReceipt = true;
            data.ClassificationReason = $"Payment receipt (card payment '{cardSignalMatch.Value.Trim()}')";
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
        var combined = $"{subject}\n{textBody}";
        var cleaned = PaymentTermsExclusionRegex.Replace(combined, " ");

        // Explicit incoming remittance / bank deposit / Interac e-Transfer emails to our company must be treated as payment receipts even if an invoice PDF is attached as reference
        var strongRemittanceMatch = IncomingRemittanceOrDepositRegex.Match(cleaned);
        if (strongRemittanceMatch.Success)
        {
            return (false, true, $"Payment receipt / incoming remittance (matched '{strongRemittanceMatch.Value.Trim()}' in email)");
        }

        // If any attached PDF was confidently parsed as an actionable invoice (or credit note), process it as a bill!
        if (extractedInvoices.Any(i => i.IsConfident && !i.IsAccountStatement && !i.IsPaymentReceipt))
        {
            return (false, false, null);
        }

        // If all extracted attachments were statements
        if (extractedInvoices.Count > 0 && extractedInvoices.All(i => i.IsAccountStatement))
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
