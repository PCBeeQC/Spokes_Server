namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Extracts invoice numbers, dates, payment terms, and purchase order references across Western languages.
/// </summary>
public static class UniversalMetadataExtractor
{
    private static readonly string[] DmyDateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
        "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
        "d/M/yyyy", "d-M-yyyy", "d.M.yyyy",
        "MM/dd/yyyy", "MM-dd-yyyy", "M/d/yyyy",
        "dd/MM/yy", "dd-MM-yy", "MM/dd/yy"
    ];

    private static readonly string[] MdyDateFormats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd",
        "MM/dd/yyyy", "MM-dd-yyyy", "M/d/yyyy", "M-d-yyyy",
        "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
        "d/M/yyyy", "d-M-yyyy", "d.M.yyyy",
        "MM/dd/yy", "MM-dd-yy", "dd/MM/yy", "dd-MM-yy"
    ];

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["january"] = 1, ["janvier"] = 1, ["janv"] = 1, ["januar"] = 1, ["jän"] = 1, ["jänner"] = 1, ["ene"] = 1, ["enero"] = 1, ["gen"] = 1, ["gennaio"] = 1, ["januari"] = 1, ["janeiro"] = 1,
        ["feb"] = 2, ["february"] = 2, ["fév"] = 2, ["fev"] = 2, ["févr"] = 2, ["fevr"] = 2, ["février"] = 2, ["fevrier"] = 2, ["februar"] = 2, ["febrero"] = 2, ["febbraio"] = 2, ["februari"] = 2, ["fevereiro"] = 2,
        ["mar"] = 3, ["march"] = 3, ["mars"] = 3, ["mär"] = 3, ["märz"] = 3, ["maerz"] = 3, ["marzo"] = 3, ["maart"] = 3, ["março"] = 3, ["marco"] = 3,
        ["apr"] = 4, ["april"] = 4, ["avr"] = 4, ["avril"] = 4, ["abr"] = 4, ["abril"] = 4, ["aprile"] = 4,
        ["may"] = 5, ["mai"] = 5, ["mayo"] = 5, ["mag"] = 5, ["maggio"] = 5, ["mei"] = 5, ["maio"] = 5,
        ["jun"] = 6, ["june"] = 6, ["juin"] = 6, ["juni"] = 6, ["junio"] = 6, ["giu"] = 6, ["giugno"] = 6, ["junho"] = 6,
        ["jul"] = 7, ["july"] = 7, ["juil"] = 7, ["juillet"] = 7, ["juli"] = 7, ["julio"] = 7, ["lug"] = 7, ["luglio"] = 7, ["julho"] = 7,
        ["aug"] = 8, ["august"] = 8, ["aoû"] = 8, ["aou"] = 8, ["août"] = 8, ["aout"] = 8, ["ago"] = 8, ["agosto"] = 8, ["augustus"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9, ["septembre"] = 9, ["set"] = 9, ["setiembre"] = 9, ["settembre"] = 9, ["setembro"] = 9,
        ["oct"] = 10, ["october"] = 10, ["octobre"] = 10, ["okt"] = 10, ["oktober"] = 10, ["octubre"] = 10, ["ott"] = 10, ["ottobre"] = 10, ["out"] = 10, ["outubro"] = 10,
        ["nov"] = 11, ["november"] = 11, ["novembre"] = 11, ["noviembre"] = 11, ["novembro"] = 11,
        ["dec"] = 12, ["december"] = 12, ["déc"] = 12, ["décembre"] = 12, ["decembre"] = 12, ["dez"] = 12, ["dezember"] = 12, ["dic"] = 12, ["diciembre"] = 12, ["dicembre"] = 12, ["dezembro"] = 12
    };

    private static readonly HashSet<string> InvalidInvoiceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATE", "NUMBER", "NUMERO", "NUMÉRO", "TOTAL", "PAGE", "AMOUNT", "MONTANT",
        "DUE", "TAX", "TVA", "TPS", "TVQ", "GST", "HST", "VAT", "MWST", "UST",
        "INVOICE", "FACTURE", "RECHNUNG", "FACTURA", "FATTURA", "FACTUUR",
        "ACCOUNT", "COMPTE", "CLIENT", "CUSTOMER", "DESCRIPTION", "QTY", "QUANTITY",
        "PRICE", "PRIX", "TERMS", "TERMES", "ORDER", "COMMANDE", "BILL", "RECEIPT",
        "CREDIT", "NOTE", "AVOIR", "GUTSCHRIFT", "SOLDE", "BALANCE"
    };

    private const string DateTokenPattern =
        @"(?:[0-9]{4}[-/\.][0-9]{1,2}[-/\.][0-9]{1,2}|[0-9]{1,2}[-/\.][0-9]{1,2}[-/\.][0-9]{2,4}|" +
        @"[0-9]{1,2}(?:st|nd|rd|th|er)?[\s\.\-]+(?:de\s+)?[A-Za-zÀ-ÿ]{3,12}\.?[\s,\-]+(?:de\s+)?[0-9]{4}|" +
        @"[A-Za-zÀ-ÿ]{3,12}\.?\s+[0-9]{1,2}(?:st|nd|rd|th)?,?\s+[0-9]{4})";

    private static readonly Regex InvoiceNumberLabelRegex = new(
        @"^(?:\bn[oº°\.]*\s*(?:de\s*)?facture|facture\s*n[oº°\.]*|num[eé]ro\s*(?:de\s*)?facture|invoice\s*n[oº°\.]*|invoice\s*number|invoice\s*#|inv\s*#|facture\s*#|rechnungs-?nr\.?|rechnungsnummer|factura\s*n[oº°\.]*|fattura\s*n[oº°\.]*|factuurnummer|credit\s*note\s*(?:no\.?|#)|note\s*de\s*cr[eé]dit\s*(?:no\.?|#)|gutschrift-?nr\.?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvoiceNumberValueValidator = new(
        @"^[A-Z0-9][A-Z0-9\-_/]{1,28}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractInvoiceNumber(SpatialTextDocument document, string? fileName)
    {
        var layoutText = document.FullLayoutText;
        var rawText = document.RawFullText;

        // 1. Header Band Column Layout: e.g. "Facture 31/08/2026 359504"
        if (document.Pages.Count > 0)
        {
            foreach (var line in document.Pages[0].HeaderLines.Take(8))
            {
                var lineText = line.Text.Trim();
                if (Regex.IsMatch(lineText, @"^(?:facture|invoice|bill|rechnung|credit\s*note|avoir|gutschrift)\b", RegexOptions.IgnoreCase))
                {
                    // Pattern: DocumentType <Date> <CandidateNumber>
                    var bandMatch = Regex.Match(
                        lineText,
                        @"^(?:facture|invoice|bill|rechnung|credit\s*note|avoir|gutschrift)\s+[0-9]{1,4}[-/\.][0-9]{1,2}[-/\.][0-9]{1,4}\s+([A-Z0-9\-_]{2,20})$",
                        RegexOptions.IgnoreCase);

                    if (bandMatch.Success)
                    {
                        var candidate = CleanCandidateInvoiceNumber(bandMatch.Groups[1].Value);
                        if (IsValidInvoiceNumberCandidate(candidate))
                        {
                            return candidate;
                        }
                    }

                    // Check column cells if clustered by spatial gaps
                    var cells = line.GetColumnCells();
                    if (cells.Count >= 2)
                    {
                        foreach (var cell in cells.Skip(1))
                        {
                            var trimmedCell = CleanCandidateInvoiceNumber(cell.Trim());
                            if (IsValidInvoiceNumberCandidate(trimmedCell) && Regex.IsMatch(trimmedCell, @"^[A-Z0-9\-_]{3,20}$", RegexOptions.IgnoreCase))
                            {
                                return trimmedCell;
                            }
                        }
                    }
                }
            }
        }

        // 2. Explicit labeled invoice number patterns (Multilingual: EN, FR, DE, ES, IT, NL)
        var explicitPatterns = new[]
        {
            @"(?:\bn[oº°\.]*\s*(?:de\s*)?facture|facture\s*n[oº°\.]*|num[eé]ro\s*(?:de\s*)?facture|invoice\s*n[oº°\.]*|invoice\s*number|invoice\s*#|inv\s*#|facture\s*#|rechnungs-?nr\.?|rechnungsnummer|factura\s*n[oº°\.]*|fattura\s*n[oº°\.]*|factuurnummer|credit\s*note\s*(?:no\.?|#|number)|note\s*de\s*cr[eé]dit\s*(?:no\.?|#)|gutschrift-?nr\.?)[\s:]*([A-Z0-9\-_/]{1,30})",
            @"(?:invoice|facture|rechnung|factura|fattura|factuur|credit\s*note|gutschrift)[\s#:]+(?:no\.?|num[eé]ro|nr\.?|#|:)[:\s]*([A-Z0-9\-_/]{2,30})",
            @"(?<!page\s*)(?<!tel\s*)(?<!phone\s*)(?<!fax\s*)(?<!po\s*)(?<!p\.o\.\s*)\b(?:no\.?|num[eé]ro|#)[\s:]+([A-Z0-9\-_]{3,25})\b",
            @"(?:invoice|facture)([0-9]{3,15})"
        };

        foreach (var pattern in explicitPatterns)
        {
            foreach (Match match in Regex.Matches(layoutText, pattern, RegexOptions.IgnoreCase))
            {
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    var candidate = CleanCandidateInvoiceNumber(match.Groups[1].Value);
                    if (IsValidInvoiceNumberCandidate(candidate)) return candidate;
                }
            }

            foreach (Match match in Regex.Matches(rawText, pattern, RegexOptions.IgnoreCase))
            {
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    var candidate = CleanCandidateInvoiceNumber(match.Groups[1].Value);
                    if (IsValidInvoiceNumberCandidate(candidate)) return candidate;
                }
            }
        }

        // 3. 2D Spatial Box Lookup (label on one line, invoice number in cell below)
        var boxCandidate = document.FindValueNearLabel(InvoiceNumberLabelRegex, InvoiceNumberValueValidator);
        if (!string.IsNullOrWhiteSpace(boxCandidate))
        {
            var cleaned = CleanCandidateInvoiceNumber(boxCandidate);
            if (IsValidInvoiceNumberCandidate(cleaned))
            {
                return cleaned;
            }
        }

        // 4. Filename patterns
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            // Standard full prefixes: Facture1631_... or Invoice-1234
            var fnMatch = Regex.Match(fileName, @"(?:facture|invoice|inv|rechnung|factura|fattura)[-_#\s]*([0-9A-Za-z\-]+)", RegexOptions.IgnoreCase);
            if (fnMatch.Success && fnMatch.Groups[1].Value.Length >= 2)
            {
                var candidate = fnMatch.Groups[1].Value.Trim();
                if (IsValidInvoiceNumberCandidate(candidate))
                {
                    return candidate;
                }
            }

            // Standard shorthand: F-359504.pdf or F359504.pdf
            var shortMatch = Regex.Match(fileName, @"^[Ff][-_#\s]*([0-9]{3,12})(?:\.pdf|$)", RegexOptions.IgnoreCase);
            if (shortMatch.Success)
            {
                return shortMatch.Groups[1].Value.Trim();
            }
        }

        return null;
    }

    public static void ExtractDates(SpatialTextDocument document, ExtractedInvoiceData data)
    {
        var combinedText = document.FullLayoutText;
        bool preferMonthFirst = ShouldPreferMonthFirst(combinedText);

        // 1. Explicit Invoice Date (inline)
        var dateMatch = Regex.Match(
            combinedText,
            $@"(?<!échéance\s*)(?<!echeance\s*)(?<!due\s*)\b(?:date\s*(?:de\s*facturation|de\s*facture|d['’]émission|d['’]emission|du\s*document)?|invoice\s*date|issue\s*date|rechnungsdatum|datum|fecha(?:\s*de\s*emisi[oó]n)?|data(?:\s*fattura)?|factuurdatum|billed\s*on)\b[\s:]*({DateTokenPattern})",
            RegexOptions.IgnoreCase);

        if (dateMatch.Success && TryParseDate(dateMatch.Groups[1].Value, out var invDate, preferMonthFirst))
        {
            data.InvoiceDate = invDate;
        }

        // 1b. 2D Boxed Invoice Date lookup (when label is on line above value)
        if (!data.InvoiceDate.HasValue)
        {
            var dateLabelRegex = new Regex(@"^(?:date\s*(?:de\s*facturation|de\s*facture)?|invoice\s*date|issue\s*date|rechnungsdatum|factuurdatum|data\s*fattura|date)$", RegexOptions.IgnoreCase);
            var dateValValidator = new Regex(DateTokenPattern, RegexOptions.IgnoreCase);
            var boxDateStr = document.FindValueNearLabel(dateLabelRegex, dateValValidator);
            if (!string.IsNullOrWhiteSpace(boxDateStr) && TryParseDate(boxDateStr, out var boxInvDate, preferMonthFirst))
            {
                data.InvoiceDate = boxInvDate;
            }
        }

        // 2. Unlabeled date in top header band (e.g. "Facture 31/08/2026 359504")
        if (!data.InvoiceDate.HasValue && document.Pages.Count > 0)
        {
            foreach (var line in document.Pages[0].HeaderLines.Take(10))
            {
                var match = Regex.Match(line.Text, $@"\b({DateTokenPattern})\b", RegexOptions.IgnoreCase);
                if (match.Success && TryParseDate(match.Groups[1].Value, out var headerDate, preferMonthFirst))
                {
                    data.InvoiceDate = headerDate;
                    break;
                }
            }
        }

        // 3. Explicit Due Date (inline or 2D box)
        var dueMatch = Regex.Match(
            combinedText,
            $@"\b(?:échéance|echeance|date\s*d['’]échéance|date\s*d['’]echeance|date\s*limite|due\s*date|fälligkeitsdatum|faelligkeitsdatum|f[aä]llig\s*(?:am|bis)|fecha\s*de\s*vencimiento|scadenza|vervaldatum|payable\s*by|pay\s*by|payment\s*due)\b[\s:]*({DateTokenPattern})",
            RegexOptions.IgnoreCase);

        if (dueMatch.Success && TryParseDate(dueMatch.Groups[1].Value, out var dueDate, preferMonthFirst))
        {
            data.DueDate = dueDate;
        }
        else
        {
            var dueLabelRegex = new Regex(@"^(?:échéance|echeance|date\s*d['’]échéance|date\s*d['’]echeance|due\s*date|fälligkeitsdatum|f[aä]llig\s*(?:am|bis)|vervaldatum|scadenza|payment\s*due)$", RegexOptions.IgnoreCase);
            var dateValValidator = new Regex(DateTokenPattern, RegexOptions.IgnoreCase);
            var boxDueStr = document.FindValueNearLabel(dueLabelRegex, dateValValidator);
            if (!string.IsNullOrWhiteSpace(boxDueStr) && TryParseDate(boxDueStr, out var boxDueDate, preferMonthFirst))
            {
                data.DueDate = boxDueDate;
            }
        }

        // 4. Extract Payment Terms & compute Due Date when not explicitly provided
        ExtractPaymentTerms(combinedText, data);

        // 5. Cross-check DueDate >= InvoiceDate for ambiguous numeric dates (e.g. 04/05/2026 vs 05/04/2026)
        if (data.InvoiceDate.HasValue && data.DueDate.HasValue && data.DueDate.Value < data.InvoiceDate.Value)
        {
            var inv = data.InvoiceDate.Value;
            if (inv.Day <= 12)
            {
                try
                {
                    var swappedInv = new DateTime(inv.Year, inv.Day, inv.Month);
                    if (data.DueDate.Value >= swappedInv)
                    {
                        data.InvoiceDate = swappedInv;
                    }
                }
                catch
                {
                    // Ignore invalid calendar swap
                }
            }
        }
    }

    private static void ExtractPaymentTerms(string combinedText, ExtractedInvoiceData data)
    {
        // Check "Due upon receipt" / "Payable à réception"
        var uponReceiptMatch = Regex.Match(
            combinedText,
            @"\b(due\s+(?:upon|on)\s+receipt|payable\s+(?:upon|on)\s+receipt|payable\s+[aà]\s+r[eé]ception|sofort\s+f[aä]llig|zahlbar\s+sofort)\b",
            RegexOptions.IgnoreCase);

        if (uponReceiptMatch.Success)
        {
            data.PaymentTerms ??= uponReceiptMatch.Groups[1].Value.Trim();
            if (!data.DueDate.HasValue && data.InvoiceDate.HasValue)
            {
                data.DueDate = data.InvoiceDate.Value;
            }
            return;
        }

        // Check early-payment discount + Net days: e.g. "2/10 Net 30", "Net 30", "Net 15 jours", "30 days net", "30 Tage netto", "Zahlbar innerhalb 14 Tagen"
        var netTermsMatch = Regex.Match(
            combinedText,
            @"(?:modalités|modalites|termes|terms|conditions|payment\s*terms|zahlungsbedingungen)?[\s:]*((?:\d{1,2}\s*%\s*\d{1,2}\s*(?:days\s*)?|\d{1,2}/\d{1,2}\s+)?\bNet\s*(\d{1,3})(?:\s*(?:days|jours|tage))?|\b(\d{1,3})\s*(?:days|jours|tage)\s+net(?:to)?\b|zahlbar\s+innerhalb\s+(?:von\s+)?(\d{1,3})\s*tagen?)",
            RegexOptions.IgnoreCase);

        if (netTermsMatch.Success)
        {
            data.PaymentTerms ??= netTermsMatch.Groups[1].Value.Trim();
            var daysStr = !string.IsNullOrEmpty(netTermsMatch.Groups[2].Value) ? netTermsMatch.Groups[2].Value
                        : !string.IsNullOrEmpty(netTermsMatch.Groups[3].Value) ? netTermsMatch.Groups[3].Value
                        : netTermsMatch.Groups[4].Value;

            if (!data.DueDate.HasValue && data.InvoiceDate.HasValue && int.TryParse(daysStr, out var netDays))
            {
                data.DueDate = data.InvoiceDate.Value.AddDays(netDays);
                return;
            }
        }
    }

    public static string? ExtractPurchaseOrder(SpatialTextDocument document)
    {
        var text = document.FullLayoutText;

        // Exclude P.O. Box / PO Box / Case Postale / Postfach
        var poMatches = Regex.Matches(
            text,
            @"(?:\bp\.?o\.?(?!\s*box\b)\s*(?:no\.?|number|#)?|\bpurchase\s*order(?:\s*(?:no\.?|number|#))?|\bbon\s*de\s*commande|\bbestellnummer|\border\s*(?:no\.?|number|#)|\bvotre\s*r[eé]f[eé]rence)[\s#:]+([A-Z0-9\-_]{3,25})\b",
            RegexOptions.IgnoreCase);

        foreach (Match poMatch in poMatches)
        {
            var candidate = poMatch.Groups[1].Value.Trim();
            if (IsValidPoCandidate(candidate))
            {
                return candidate;
            }
        }

        // 2D Spatial Box Lookup for PO
        var poLabelRegex = new Regex(@"^(?:p\.?o\.?(?!\s*box\b)\s*(?:no\.?|number|#)?|purchase\s*order|bon\s*de\s*commande|bestellnummer|customer\s*po)$", RegexOptions.IgnoreCase);
        var poValValidator = new Regex(@"^[A-Z0-9\-_]{3,25}$", RegexOptions.IgnoreCase);
        var boxPo = document.FindValueNearLabel(poLabelRegex, poValValidator);
        if (!string.IsNullOrWhiteSpace(boxPo) && IsValidPoCandidate(boxPo))
        {
            return boxPo;
        }

        return null;
    }

    private static bool IsValidPoCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (candidate.Equals("BOX", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
            InvalidInvoiceTokens.Contains(candidate))
        {
            return false;
        }
        return !IsDateString(candidate) && candidate.Any(char.IsDigit);
    }

    private static bool IsValidInvoiceNumberCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2) return false;
        if (InvalidInvoiceTokens.Contains(candidate)) return false;
        if (IsDateString(candidate)) return false;
        if (Regex.IsMatch(candidate, @"^\d{1,2}$")) return false;
        return candidate.Any(char.IsDigit);
    }

    private static bool ShouldPreferMonthFirst(string text)
    {
        // Scan all numeric dates XX/YY/ZZZZ in the document for unambiguous (> 12) day/month positions
        var matches = Regex.Matches(text, @"\b(\d{1,2})[-/\.](\d{1,2})[-/\.](\d{2,4})\b");
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int first) && int.TryParse(m.Groups[2].Value, out int second))
            {
                if (first > 12 && first <= 31 && second <= 12)
                {
                    return false; // Unambiguous DD/MM/YYYY
                }
                if (second > 12 && second <= 31 && first <= 12)
                {
                    return true; // Unambiguous MM/DD/YYYY
                }
            }
        }

        // Check locale/country signals
        bool hasUsSignal = Regex.IsMatch(text, @"\b(?:EIN\b|USD\b|US\$|\b(?:AL|AK|AZ|AR|CA|CO|CT|DE|FL|GA|HI|ID|IL|IN|IA|KS|KY|LA|ME|MD|MA|MI|MN|MS|MO|MT|NE|NV|NH|NJ|NM|NY|NC|ND|OH|OK|OR|PA|RI|SC|SD|TN|TX|UT|VT|VA|WA|WV|WI|WY)\s+\d{5}(?:-\d{4})?\b)");
        bool hasNonUsSignal = Regex.IsMatch(text, @"\b(?:EUR|GBP|CHF|CAD|AUD|NZD|TPS|TVQ|GST|HST|QST|VAT|TVA|MWST|UST|SIRET|ABN)\b", RegexOptions.IgnoreCase);

        return hasUsSignal && !hasNonUsSignal;
    }

    private static bool IsDateString(string candidate)
    {
        return TryParseDate(candidate, out _);
    }

    public static bool TryParseDate(string raw, out DateTime date, bool preferMonthFirst = false)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var cleaned = raw.Trim();

        // 1. Written month formats (EN, FR, DE, ES, IT, NL, PT)
        // Pattern A: "15 Oct 2026", "15. Oktober 2026", "15 de octubre de 2026", "1er septembre 2026"
        var dmyWritten = Regex.Match(cleaned, @"^(\d{1,2})(?:st|nd|rd|th|er)?[\s\.\-]+(?:de\s+)?([A-Za-zÀ-ÿ]{3,12})\.?[\s,\-]+(?:de\s+)?(\d{4})$", RegexOptions.IgnoreCase);
        if (dmyWritten.Success &&
            int.TryParse(dmyWritten.Groups[1].Value, out int day1) &&
            TryResolveMonth(dmyWritten.Groups[2].Value, out int month1) &&
            int.TryParse(dmyWritten.Groups[3].Value, out int year1) &&
            day1 >= 1 && day1 <= 31)
        {
            try
            {
                date = new DateTime(year1, month1, day1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Pattern B: "Oct 15, 2026", "October 15 2026", "Oct. 15th, 2026"
        var mdyWritten = Regex.Match(cleaned, @"^([A-Za-zÀ-ÿ]{3,12})\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})$", RegexOptions.IgnoreCase);
        if (mdyWritten.Success &&
            TryResolveMonth(mdyWritten.Groups[1].Value, out int month2) &&
            int.TryParse(mdyWritten.Groups[2].Value, out int day2) &&
            int.TryParse(mdyWritten.Groups[3].Value, out int year2) &&
            day2 >= 1 && day2 <= 31)
        {
            try
            {
                date = new DateTime(year2, month2, day2);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 2. Numeric date formats
        var formats = preferMonthFirst ? MdyDateFormats : DmyDateFormats;
        if (DateTime.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        return DateTime.TryParse(cleaned, CultureInfo.GetCultureInfo("fr-CA"), DateTimeStyles.None, out date) ||
               DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryResolveMonth(string token, out int month)
    {
        var key = token.TrimEnd('.');
        if (MonthNames.TryGetValue(key, out month))
        {
            return true;
        }
        if (key.Length >= 3 && MonthNames.TryGetValue(key[..3], out month))
        {
            return true;
        }
        month = 0;
        return false;
    }

    private static string CleanCandidateInvoiceNumber(string rawCandidate)
    {
        var trimmed = rawCandidate.Trim();
        // If candidate has digits immediately followed by an alphabetic word block of length >= 4 (without separator),
        // e.g. "42629130040726SHERWIN-WILLIAMS" or "123456ACME", separate the numeric invoice number from the merged word.
        var match = Regex.Match(trimmed, @"^(\d{3,25})([A-Z]{3,}.*)$");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }
        return trimmed;
    }
}
