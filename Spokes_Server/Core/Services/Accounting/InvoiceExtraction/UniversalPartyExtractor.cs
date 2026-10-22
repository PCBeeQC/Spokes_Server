namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Extracts supplier and buyer entities, contact info, and global business/tax identifiers.
/// </summary>
public static class UniversalPartyExtractor
{
    private static readonly HashSet<string> DefaultInternalCompanyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "poly-robotics", "poly robotics", "polyrobotics"
    };

    private static readonly Regex LegalEntitySuffixRegex = new(
        @"\b(?:inc\.?|llc|l\.l\.c\.|ltd\.?|limited|corp\.?|corporation|gmbh|gmbh\s*&\s*co\.?\s*kg|ag|sarl|s\.a\.r\.l\.|sas|s\.a\.s\.|s\.a\.|b\.v\.|n\.v\.|s\.r\.l\.|s\.l\.|ab|as|aps|oy|lt[eé]e|s\.e\.n\.c\.|plc)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BuyerHeaderLabelRegex = new(
        @"^(?:factur[eé]\s*[aà]|bill\s*to|billed\s*to|sold\s*to|ship\s*to|deliver\s*to|destinataire|client|customer|comptes\s*payables|rechnungsempf[aä]nger)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void ExtractParties(SpatialTextDocument document, string? fileName, ExtractedInvoiceData data, InvoiceExtractionContext? context = null)
    {
        if (document.Pages.Count == 0) return;

        var fullText = document.FullLayoutText;
        var firstPage = document.Pages[0];

        // 1. Supplier Email (from first page)
        var emailMatches = Regex.Matches(fullText, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b");
        foreach (Match match in emailMatches)
        {
            var email = match.Value.Trim();
            // Don't assign internal buyer/forwarder emails as supplier email
            if (!IsInternalBuyerEmail(email, context))
            {
                data.SupplierEmail = email;
                break;
            }
        }

        // 2. Supplier Name from Header Letterhead & Buyer Zone Segmentation (Column-aware for 2-column Seller/Buyer layouts)
        string? headerSupplier = null;
        var headerLines = firstPage.HeaderLines.Take(16).ToList();
        double? buyerZoneLeft = null;
        double? buyerZoneRight = null;
        bool buyerColumnIsLeft = false;
        int buyerZoneRemainingLines = 0;
        int singleColumnBuyerSkipLines = 0;
        var buyerBlockCells = new HashSet<SpatialCell>();

        for (int i = 0; i < headerLines.Count; i++)
        {
            var spatialLine = headerLines[i];
            if (string.IsNullOrWhiteSpace(spatialLine.Text)) continue;

            var cells = spatialLine.GetSpatialCells(15.0);
            if (cells.Count == 0) continue;

            if (singleColumnBuyerSkipLines > 0 && cells.Count == 1)
            {
                buyerBlockCells.Add(cells[0]);
                singleColumnBuyerSkipLines--;
                continue;
            }

            bool decrementedBuyerLine = false;
            for (int cIdx = 0; cIdx < cells.Count; cIdx++)
            {
                var cell = cells[cIdx];
                var line = cell.Text.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Check if this cell starts a buyer block
                if (BuyerHeaderLabelRegex.IsMatch(line))
                {
                    buyerBlockCells.Add(cell);
                    if (cells.Count > 1)
                    {
                        var rightNeighbor = cells.Where(c => c.Left > cell.Right).OrderBy(c => c.Left).FirstOrDefault();
                        buyerZoneLeft = cell.Left - 20.0;
                        buyerZoneRight = rightNeighbor != null
                            ? rightNeighbor.Left - 10.0
                            : Math.Max(cell.Right + 100.0, cell.Left + 200.0);
                        buyerColumnIsLeft = cIdx == 0;
                        buyerZoneRemainingLines = 8;
                    }
                    else
                    {
                        // Single column: skip the buyer address lines directly below
                        singleColumnBuyerSkipLines = 4;
                    }
                    continue;
                }

                // Skip cells that fall horizontally inside an active 2-column buyer address block
                bool isInBuyerColumn = buyerZoneLeft.HasValue && buyerZoneRight.HasValue && buyerZoneRemainingLines > 0 &&
                                       cell.Left >= buyerZoneLeft.Value && cell.Left <= buyerZoneRight.Value &&
                                       (cells.Count == 1 || (buyerColumnIsLeft ? cIdx == 0 : cIdx > 0));

                if (isInBuyerColumn)
                {
                    if (Regex.IsMatch(line, @"^(?:date|num[eé]ro|invoice|facture|rechnung|description|qt[eéy]|item|sous-total|subtotal|total)\b", RegexOptions.IgnoreCase))
                    {
                        buyerZoneRemainingLines = 0;
                    }
                    else
                    {
                        buyerBlockCells.Add(cell);
                        if (!decrementedBuyerLine)
                        {
                            buyerZoneRemainingLines--;
                            decrementedBuyerLine = true;
                        }
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(headerSupplier) && IsValidSupplierNameCandidate(line, context))
                {
                    headerSupplier = line;
                }
            }
        }

        if (!string.IsNullOrEmpty(headerSupplier))
        {
            headerSupplier = ResolveSpacedCompanyName(headerSupplier, fullText);
        }

        // 3. Global & Regional Tax IDs (Scans document while excluding Buyer address block cells)
        var sellerText = BuildSellerScopedText(document, buyerBlockCells);
        ExtractTaxIdentifiers(sellerText, data, context);

        // 4. Filename fallback (e.g. Facture1631_de_Alumax_Usinage.pdf -> Alumax Usinage)
        string? filenameSupplier = null;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fnMatch = Regex.Match(fileName, @"(?:_de_|_from_|-de-|-from-)([A-Za-z0-9_\-\s]+?)(?:\.pdf|$)", RegexOptions.IgnoreCase);
            if (fnMatch.Success)
            {
                filenameSupplier = fnMatch.Groups[1].Value.Replace("_", " ").Replace("-", " ").Trim();
            }
        }

        // 5. Footer Legal Entity fallback (common on German/Swiss/EU invoices where header has logo image and footer has GmbH/AG/SARL)
        string? footerSupplier = null;
        if (string.IsNullOrEmpty(headerSupplier) && string.IsNullOrEmpty(filenameSupplier))
        {
            var footerLines = document.Pages.SelectMany(p => p.FooterLines).ToList();
            foreach (var fLine in footerLines)
            {
                foreach (var cell in fLine.GetSpatialCells(15.0))
                {
                    var candidate = cell.Text.Trim();
                    candidate = Regex.Replace(candidate, @"^(?:si[eè]ge\s*social|registered\s*(?:office|address)|head\s*office|sitz(?:\s*der\s*gesellschaft)?)[\s:]+", "", RegexOptions.IgnoreCase).Trim();
                    if (LegalEntitySuffixRegex.IsMatch(candidate) && IsValidSupplierNameCandidate(candidate, context))
                    {
                        // Extract just the company name before any trailing address comma/pipe
                        var cleanName = candidate.Split([',', '|', '•', '-'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                        if (LegalEntitySuffixRegex.IsMatch(cleanName) && cleanName.Length >= 3)
                        {
                            footerSupplier = cleanName;
                        }
                        else
                        {
                            footerSupplier = candidate;
                        }
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(footerSupplier)) break;
            }
        }

        data.SupplierName = headerSupplier ?? filenameSupplier ?? footerSupplier;

        // 6. Buyer Name
        ExtractBuyer(document, data);
    }

    private static bool IsValidSupplierNameCandidate(string line, InvoiceExtractionContext? context)
    {
        // Skip document titles and standalone document title lines
        if (Regex.IsMatch(line, @"^(?:facture|invoice|tax\s*invoice|bill|rechnung|factura|fattura|factuur|credit\s*note|note\s*de\s*cr[eé]dit|avoir|gutschrift)(?:\s+[0-9]{1,4}[-/\.][0-9]{1,2}[-/\.][0-9]{1,4})?(?:\s+[A-Z0-9\-_]+)?$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        if (BuyerHeaderLabelRegex.IsMatch(line))
        {
            return false;
        }

        // Skip internal company name if placed at top as buyer
        if (IsInternalBuyerName(line, context))
        {
            return false;
        }

        // Skip noise: addresses, emails, phones, web, tax lines, account/order metadata
        if (line.Contains('@') ||
            line.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(line, @"^(?:info|page\s*\d|tps|tvq|tvp|gst|hst|qst|vat|tva|mwst|ust|tel|tél|phone|fax|iban|bic|swift)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(line, @"^(?:account|compte|order|commande|date|time|heure|po#?|p\.o\.|ref|r[eé]f|job|project|projet|terms|termes|due\s*date|invoice\s*date)[\s#:]", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(line, @"\b(?:qc|on|nb|ns|bc|ab|mb|sk|pe|nl)\b\s+[A-Z]\d[A-Z]\s*\d[A-Z]\d", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(line, @"^\d+.*(?:rue|street|st\b|av|ave|avenue|boul|boulevard|blvd|road|rd\b|chemin|local|suite|way|drive|dr\b|lane|ln\b|platz|straße|strasse)", RegexOptions.IgnoreCase))
        {
            return false;
        }

        // Universal company name validity: must have meaningful letters and not be a barcode/scanline/digits string
        int letterCount = line.Count(char.IsLetter);
        int digitCount = line.Count(char.IsDigit);
        if (letterCount < 3 || digitCount > letterCount || line.StartsWith('_') || line.StartsWith('-'))
        {
            return false;
        }

        return line.Length >= 2 && line.Length <= 80;
    }

    private static bool IsInternalBuyerName(string text, InvoiceExtractionContext? context)
    {
        if (DefaultInternalCompanyKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (context?.BuyerNames != null)
        {
            foreach (var buyer in context.BuyerNames)
            {
                if (!string.IsNullOrWhiteSpace(buyer) && buyer.Length >= 3 &&
                    text.Contains(buyer, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsInternalBuyerEmail(string email, InvoiceExtractionContext? context)
    {
        if (DefaultInternalCompanyKeywords.Any(k => email.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
            email.Contains("poly-robotics.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (context?.BuyerEmailDomains != null)
        {
            foreach (var domain in context.BuyerEmailDomains)
            {
                if (!string.IsNullOrWhiteSpace(domain) && email.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string BuildSellerScopedText(SpatialTextDocument document, HashSet<SpatialCell> buyerBlockCells)
    {
        if (buyerBlockCells.Count == 0)
        {
            return document.FullLayoutText;
        }

        var buyerStrings = buyerBlockCells
            .Select(c => c.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var sb = new System.Text.StringBuilder();
        foreach (var page in document.Pages)
        {
            foreach (var line in page.Lines)
            {
                var cells = line.GetSpatialCells(15.0);
                var nonBuyerCells = cells
                    .Where(c => !buyerBlockCells.Any(b => Math.Abs(b.Y - c.Y) < 0.1 && Math.Abs(b.Left - c.Left) < 0.5))
                    .Select(c => c.Text.Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                if (nonBuyerCells.Count > 0)
                {
                    sb.AppendLine(string.Join("   ", nonBuyerCells));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// If a header company name has missing spaces due to tight PDF kerning (e.g. "OcrexLimitedt/aAutoEntry"),
    /// checks whether the exact same character sequence appears with proper spacing elsewhere in the document (such as the legal footer).
    /// </summary>
    private static string ResolveSpacedCompanyName(string candidate, string fullText)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(fullText))
        {
            return candidate;
        }

        var nonSpaceChars = candidate.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (nonSpaceChars.Length < 4 || nonSpaceChars.Length > 80)
        {
            return candidate;
        }

        var patternBuilder = new System.Text.StringBuilder();
        for (int i = 0; i < nonSpaceChars.Length; i++)
        {
            if (i > 0)
            {
                patternBuilder.Append(@"[ \t]*");
            }
            patternBuilder.Append(Regex.Escape(nonSpaceChars[i].ToString()));
        }

        var matches = Regex.Matches(fullText, patternBuilder.ToString(), RegexOptions.IgnoreCase);
        string best = candidate;
        int bestSpaceCount = candidate.Count(char.IsWhiteSpace);

        foreach (Match m in matches)
        {
            var normalized = Regex.Replace(m.Value.Trim(), @"\s+", " ");
            int spaces = normalized.Count(char.IsWhiteSpace);
            if (spaces > bestSpaceCount)
            {
                best = normalized;
                bestSpaceCount = spaces;
            }
        }

        return best;
    }

    private static void ExtractTaxIdentifiers(string text, ExtractedInvoiceData data, InvoiceExtractionContext? context)
    {
        var ids = new List<string>();

        // 1. Canada CRA (GST/HST): 9 digits + RT + optional 4 digits (e.g. 815653118RT0001 or 882929151 RT)
        var craMatch = Regex.Match(text, @"\b([0-9]{9}\s*RT(?:\s*[0-9]{4})?)\b", RegexOptions.IgnoreCase);
        if (craMatch.Success)
        {
            var tpsId = craMatch.Groups[1].Value.Replace(" ", "").ToUpperInvariant();
            if (tpsId.EndsWith("RT", StringComparison.OrdinalIgnoreCase))
            {
                tpsId += "0001";
            }
            AddTaxIdIfNotBuyer(ids, tpsId, context);
        }

        // 2. Revenu Québec (QST/TVQ/TVP): 10 digits + TQ + optional 4 digits (e.g. 1010485891TQ0001)
        var rqMatch = Regex.Match(text, @"\b([0-9]{10}\s*TQ(?:\s*[0-9]{4})?)\b", RegexOptions.IgnoreCase);
        if (rqMatch.Success)
        {
            var tvqId = rqMatch.Groups[1].Value.Replace(" ", "").ToUpperInvariant();
            if (tvqId.EndsWith("TQ", StringComparison.OrdinalIgnoreCase))
            {
                tvqId += "0001";
            }
            AddTaxIdIfNotBuyer(ids, tvqId, context);
        }

        // 3. Swiss UID (e.g. CHE-123.456.789 MWST / TVA / IVA)
        var chMatch = Regex.Match(text, @"\b(CHE[\s\-]?[0-9]{3}[\.\s]?[0-9]{3}[\.\s]?[0-9]{3})(?:\s*(?:MWST|TVA|IVA))?\b", RegexOptions.IgnoreCase);
        if (chMatch.Success)
        {
            var digits = new string(chMatch.Groups[1].Value.Where(char.IsDigit).ToArray());
            if (digits.Length == 9)
            {
                AddTaxIdIfNotBuyer(ids, $"CHE-{digits[..3]}.{digits[3..6]}.{digits[6..9]}", context);
            }
        }

        // 4. European & UK VAT (supports internal spaces/dots/dashes, e.g. FR12345678901, GB 123 4567 89, DE 123 456 789, IE 9501858N)
        var vatMatch = Regex.Match(
            text,
            @"\b(?:vat(?:\s*(?:reg(?:istration)?|no\.?|number|id))?|tva(?:\s*intracom(?:munautaire)?)?|n[oº°\.]*\s*(?:de\s*)?(?:taxe|tva)|num[eé]ro\s*(?:de\s*)?(?:taxe|tva)|id\.?\s*(?:de\s*)?(?:taxe|tva)|tax\s*(?:id|no\.?|number|reg(?:istration)?)|mwst|ust-?id(?:nr\.?)?|steuernummer|cif|nif|p\.?\s*iva|partita\s*iva|btw(?:-?(?:nr|id)\.?)?|moms)[\s#/:.-]*([A-Z]{2}[\s\.\-]?[0-9A-Z]{2,4}(?:[\s\.\-]?[0-9A-Z]{2,5}){1,3})\b",
            RegexOptions.IgnoreCase);
        if (vatMatch.Success)
        {
            var normalizedVat = Regex.Replace(vatMatch.Groups[1].Value, @"[\s\.\-]", "").ToUpperInvariant();
            if (normalizedVat.Length >= 8 && normalizedVat.Length <= 14 && normalizedVat.Any(char.IsDigit))
            {
                AddTaxIdIfNotBuyer(ids, normalizedVat, context);
            }
        }

        // 5. Australian Business Number (ABN: 11 digits, e.g. ABN 51 824 753 556)
        var abnMatch = Regex.Match(text, @"\bABN[\s#:]*([0-9]{2}[\s\-]?[0-9]{3}[\s\-]?[0-9]{3}[\s\-]?[0-9]{3})\b", RegexOptions.IgnoreCase);
        if (abnMatch.Success)
        {
            var normalizedAbn = Regex.Replace(abnMatch.Groups[1].Value, @"[\s\-]", "");
            AddTaxIdIfNotBuyer(ids, normalizedAbn, context);
        }

        // 6. French SIRET (14 digits)
        var siretMatch = Regex.Match(text, @"\bSIRET[\s#:]*([0-9]{3}[\s\.]?[0-9]{3}[\s\.]?[0-9]{3}[\s\.]?[0-9]{5})\b", RegexOptions.IgnoreCase);
        if (siretMatch.Success)
        {
            var normalizedSiret = Regex.Replace(siretMatch.Groups[1].Value, @"[\s\.]", "");
            AddTaxIdIfNotBuyer(ids, normalizedSiret, context);
        }

        // 7. US Employer Identification Number (EIN: XX-XXXXXXX)
        var einMatch = Regex.Match(text, @"\b(?:ein|tax\s*id|fein)[\s#/:]*([0-9]{2}-[0-9]{7})\b", RegexOptions.IgnoreCase);
        if (einMatch.Success)
        {
            AddTaxIdIfNotBuyer(ids, einMatch.Groups[1].Value.Trim(), context);
        }

        if (ids.Count > 0)
        {
            data.SupplierTaxId = string.Join(" / ", ids.Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static void AddTaxIdIfNotBuyer(List<string> ids, string candidate, InvoiceExtractionContext? context)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        if (context?.BuyerTaxIds != null)
        {
            var normCandidate = Regex.Replace(candidate, @"[^A-Za-z0-9]", "");
            foreach (var buyerTaxId in context.BuyerTaxIds)
            {
                var normBuyer = Regex.Replace(buyerTaxId, @"[^A-Za-z0-9]", "");
                if (!string.IsNullOrEmpty(normBuyer) && normCandidate.Equals(normBuyer, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        ids.Add(candidate);
    }

    private static void ExtractBuyer(SpatialTextDocument document, ExtractedInvoiceData data)
    {
        if (document.Pages.Count == 0) return;
        var lines = document.Pages[0].HeaderLines.ToList();

        for (int i = 0; i < lines.Count - 1; i++)
        {
            var l = lines[i].Text;
            if (Regex.IsMatch(l, @"(?:factur[eé]\s*[aà]|bill\s*to|sold\s*to|client|rechnungsempf[aä]nger)[\s:]*$", RegexOptions.IgnoreCase))
            {
                var nextLine = lines[i + 1].Text.Trim();
                if (!string.IsNullOrWhiteSpace(nextLine) && nextLine.Length > 2)
                {
                    data.BuyerName = nextLine;
                    return;
                }
            }
            else if (Regex.IsMatch(l, @"(?:factur[eé]\s*[aà]|bill\s*to|sold\s*to|rechnungsempf[aä]nger)[\s:]*(.+)$", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(l, @"(?:factur[eé]\s*[aà]|bill\s*to|sold\s*to|rechnungsempf[aä]nger)[\s:]*(.+)$", RegexOptions.IgnoreCase);
                var candidate = match.Groups[1].Value.Trim();
                if (candidate.Length > 2 && !candidate.StartsWith("EXPÉDIER", StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith("SHIP", StringComparison.OrdinalIgnoreCase))
                {
                    data.BuyerName = candidate;
                    return;
                }
            }
        }
    }
}
