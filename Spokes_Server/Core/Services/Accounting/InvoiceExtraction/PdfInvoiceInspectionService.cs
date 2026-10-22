namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

/// <summary>
/// Inspects incoming PDF and XML files to automatically extract structured invoice data.
/// Priority:
/// 1. Factur-X / ZUGFeRD CII embedded XML (100% deterministic electronic invoice)
/// 2. OASIS UBL embedded XML (100% deterministic electronic invoice)
/// 3. Universal heuristic spatial text parsing (supporting Western world formats: VAT, GST, US Sales Tax, Canadian split taxes)
/// </summary>
public class PdfInvoiceInspectionService
{
    private readonly FacturXInvoiceExtractor _facturXExtractor = new();
    private readonly UblInvoiceExtractor _ublExtractor = new();
    private readonly ILogger<PdfInvoiceInspectionService>? _logger;

    public PdfInvoiceInspectionService(ILogger<PdfInvoiceInspectionService>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Inspects a file from stream and filename, returning extracted invoice data if recognized.
    /// </summary>
    public ExtractedInvoiceData? Inspect(Stream stream, string fileName, InvoiceExtractionContext? context = null)
    {
        if (stream == null || !stream.CanRead) return null;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        // 1. Direct XML file upload
        if (ext == ".xml")
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var xml = reader.ReadToEnd();
                return ExtractFromXml(xml);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse direct XML invoice {FileName}", fileName);
                return null;
            }
        }

        // 2. PDF file inspection
        if (ext == ".pdf")
        {
            return InspectPdf(stream, fileName, context);
        }

        return null;
    }

    /// <summary>
    /// Inspects a PDF file on disk.
    /// </summary>
    public ExtractedInvoiceData? InspectFile(string filePath, InvoiceExtractionContext? context = null)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            using var stream = File.OpenRead(filePath);
            return Inspect(stream, Path.GetFileName(filePath), context);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to inspect file at {Path}", filePath);
            return null;
        }
    }

    private ExtractedInvoiceData? InspectPdf(Stream stream, string fileName, InvoiceExtractionContext? context = null)
    {
        try
        {
            if (stream.CanSeek) stream.Position = 0;

            using var document = PdfDocument.Open(stream);

            // Tier 1: Check for embedded files (Factur-X / ZUGFeRD / UBL PDF/A-3)
            var embeddedData = CheckEmbeddedFiles(document);
            if (embeddedData != null)
            {
                _logger?.LogInformation("Successfully extracted e-invoice data from embedded XML in {FileName}", fileName);
                return embeddedData;
            }

            // Tier 2: Universal spatial layout heuristic extraction
            var heuristicData = ExtractTextHeuristics(document, fileName, context);
            if (heuristicData != null && heuristicData.IsConfident)
            {
                _logger?.LogInformation("Successfully extracted invoice data via text heuristics from {FileName}", fileName);
                return heuristicData;
            }

            return heuristicData;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to inspect PDF {FileName}", fileName);
            return null;
        }
    }

    private ExtractedInvoiceData? CheckEmbeddedFiles(PdfDocument document)
    {
        try
        {
            if (document.Advanced.TryGetEmbeddedFiles(out var embeddedFiles))
            {
                foreach (var file in embeddedFiles)
                {
                    var name = file.Name.ToLowerInvariant();
                    if (name.EndsWith(".xml") || name.Contains("factur-x") || name.Contains("zugferd") || name.Contains("invoice") || name.Contains("xrechnung"))
                    {
                        var xml = Encoding.UTF8.GetString(file.Bytes.ToArray());
                        var extracted = ExtractFromXml(xml);
                        if (extracted != null)
                        {
                            return extracted;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error reading embedded files from PDF");
        }

        return null;
    }

    private ExtractedInvoiceData? ExtractFromXml(string xml)
    {
        if (_facturXExtractor.CanExtract(xml))
        {
            var data = _facturXExtractor.Extract(xml);
            if (data != null) return data;
        }

        if (_ublExtractor.CanExtract(xml))
        {
            var data = _ublExtractor.Extract(xml);
            if (data != null) return data;
        }

        return null;
    }

    private static ExtractedInvoiceData? ExtractTextHeuristics(PdfDocument document, string? fileName = null, InvoiceExtractionContext? context = null)
    {
        var spatialDoc = SpatialTextDocument.FromPdf(document);
        var data = new ExtractedInvoiceData
        {
            Source = InvoiceExtractionSource.PdfTextHeuristic,
            RawDocumentType = "PDF-Text"
        };

        if (!spatialDoc.HasSelectableText || (string.IsNullOrWhiteSpace(spatialDoc.FullLayoutText) && string.IsNullOrWhiteSpace(spatialDoc.RawFullText)))
        {
            if (document.NumberOfPages > 0)
            {
                UniversalDocumentClassifier.ClassifyDocument(data, spatialDoc);
                data.RawDocumentType = "Scanned-PDF";
                return data;
            }
            return null;
        }

        // 1. Supplier, Buyer, and Global Tax Identifiers (CRA, RQ, VAT, UID, ABN, SIRET, EIN, etc.)
        UniversalPartyExtractor.ExtractParties(spatialDoc, fileName, data, context);

        // 2. Invoice Number
        data.InvoiceNumber = UniversalMetadataExtractor.ExtractInvoiceNumber(spatialDoc, fileName);

        // 3. Purchase Order Reference
        data.PurchaseOrderReference = UniversalMetadataExtractor.ExtractPurchaseOrder(spatialDoc);

        // 4. Dates & Payment Terms
        UniversalMetadataExtractor.ExtractDates(spatialDoc, data);

        // 5. Currency Identification (Expanded Western Currencies + Locale Disambiguation)
        var combinedText = spatialDoc.FullLayoutText + "\n" + spatialDoc.RawFullText;
        data.Currency = DetectCurrency(combinedText, data, context);

        // 6. Line Items Table (single or dual quantity, Qty-first, UoM, item codes, trailing tax flags)
        data.LineItems = UniversalLineItemTableParser.ExtractLineItems(spatialDoc);

        // 7. Universal Financial Reconciler (SubTotal - Discount + Shipping + Taxes = Total)
        UniversalTaxReconciler.ReconcileTotals(data, spatialDoc);

        // 8. Universal Document Classification (Statement, Credit Note, Payment Receipt)
        UniversalDocumentClassifier.ClassifyDocument(data, spatialDoc);

        // Check for Account Statement filename/body signatures
        if (!data.IsAccountStatement && IsStatementDocument(spatialDoc, fileName))
        {
            data.IsAccountStatement = true;
            data.RawDocumentType = "Statement";
            data.ClassificationReason ??= "Statement of account";
        }
        else if (data.IsAccountStatement)
        {
            data.RawDocumentType = "Statement";
        }
        else if (data.IsCreditNote)
        {
            data.RawDocumentType = "CreditNote";
        }
        else if (data.IsPaymentReceipt)
        {
            data.RawDocumentType = "PaymentReceipt";
        }

        // Return extracted data if a monetary total or document classification was identified
        return (data.TotalAmount != 0 || data.IsStatement || data.IsPaymentReceipt || data.IsScannedImagePdf) ? data : null;
    }

    private static string DetectCurrency(string combinedText, ExtractedInvoiceData data, InvoiceExtractionContext? context)
    {
        // 1. Explicit ISO 4217 Currency Codes
        var currMatch = Regex.Match(combinedText, @"\b(CAD|USD|EUR|GBP|AUD|NZD|CHF|SEK|NOK|DKK|PLN|CZK)\b", RegexOptions.IgnoreCase);
        if (currMatch.Success)
        {
            return currMatch.Groups[1].Value.ToUpperInvariant();
        }

        // 2. Explicit Country-Prefixed Dollar Symbols
        if (Regex.IsMatch(combinedText, @"\bUS\$", RegexOptions.IgnoreCase)) return "USD";
        if (Regex.IsMatch(combinedText, @"\b(?:CA|C)\$", RegexOptions.IgnoreCase)) return "CAD";
        if (Regex.IsMatch(combinedText, @"\b(?:AU|A)\$", RegexOptions.IgnoreCase)) return "AUD";
        if (Regex.IsMatch(combinedText, @"\bNZ\$", RegexOptions.IgnoreCase)) return "NZD";

        // 3. Distinct Currency Symbols
        if (combinedText.Contains('€')) return "EUR";
        if (combinedText.Contains('£')) return "GBP";
        if (combinedText.Contains("zł", StringComparison.OrdinalIgnoreCase)) return "PLN";
        if (combinedText.Contains("Kč", StringComparison.OrdinalIgnoreCase)) return "CZK";

        // 4. Disambiguate bare '$' using tax IDs and country signals
        if (combinedText.Contains('$'))
        {
            if (Regex.IsMatch(combinedText, @"\b(?:TPS|TVQ|GST|HST|QST)\b", RegexOptions.IgnoreCase) ||
                (data.SupplierTaxId != null && (data.SupplierTaxId.Contains("RT") || data.SupplierTaxId.Contains("TQ"))))
            {
                return "CAD";
            }
            if (Regex.IsMatch(combinedText, @"\bABN\b", RegexOptions.IgnoreCase))
            {
                return "AUD";
            }
            if (Regex.IsMatch(combinedText, @"\b(?:EIN|FEIN)\b", RegexOptions.IgnoreCase) ||
                (data.SupplierTaxId != null && Regex.IsMatch(data.SupplierTaxId, @"^\d{2}-\d{7}$")))
            {
                return "USD";
            }
        }

        // 5. Infer from European / Swiss / UK tax IDs if no symbol was printed
        if (!string.IsNullOrEmpty(data.SupplierTaxId))
        {
            if (data.SupplierTaxId.StartsWith("CHE-", StringComparison.OrdinalIgnoreCase)) return "CHF";
            if (data.SupplierTaxId.StartsWith("GB", StringComparison.OrdinalIgnoreCase)) return "GBP";
            if (Regex.IsMatch(data.SupplierTaxId, @"^(?:FR|DE|ES|IT|NL|BE|AT|PT|IE|FI|LU)")) return "EUR";
        }

        return context?.DefaultCurrency ?? "CAD";
    }

    private static bool IsStatementDocument(SpatialTextDocument spatialDoc, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fnLower = fileName.ToLowerInvariant();
            if (fnLower.Contains("statement") || fnLower.Contains("releve") || fnLower.Contains("relevé") ||
                fnLower.Contains("etat_de_compte") || fnLower.Contains("etat-de-compte") || fnLower.Contains("kontoauszug") ||
                fnLower.Contains("extracto") || fnLower.Contains("sommaire"))
            {
                return true;
            }
        }

        var text = (spatialDoc.FullLayoutText + "\n" + spatialDoc.RawFullText).ToLowerInvariant();

        string[] statementKeywords =
        [
            "statement of account", "account statement", "monthly statement", "billing statement",
            "account summary", "statement summary", "statement date", "statement period",
            "aging summary", "current 30 60 90", "balance forward", "previous balance",
            "relevé de compte", "releve de compte", "état de compte", "etat de compte",
            "sommaire de compte", "rapport de compte", "date de relevé", "date de releve",
            "kontoauszug", "rechnungsauszug", "offene posten",
            "extracto de cuenta", "estado de cuenta", "resumen de cuenta"
        ];

        return statementKeywords.Any(k => text.Contains(k));
    }

    /// <summary>
    /// Legacy helper method exposed for backwards compatibility.
    /// </summary>
    public static decimal ParseAmount(string? raw) => UniversalTaxReconciler.ParseAmount(raw);
}
