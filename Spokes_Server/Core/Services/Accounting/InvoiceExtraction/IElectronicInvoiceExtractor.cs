namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Common interface for electronic invoice XML extractors (Factur-X/CII, UBL).
/// </summary>
public interface IElectronicInvoiceExtractor
{
    /// <summary>
    /// Checks if this extractor can parse the given XML content.
    /// </summary>
    bool CanExtract(string xmlContent);

    /// <summary>
    /// Extracts structured invoice data from the XML string.
    /// </summary>
    ExtractedInvoiceData? Extract(string xmlContent);
}

public abstract class ElectronicInvoiceExtractorBase : IElectronicInvoiceExtractor
{
    public abstract bool CanExtract(string xmlContent);
    public abstract ExtractedInvoiceData? Extract(string xmlContent);

    protected static XElement? FindElement(XContainer container, string localName) =>
        container.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    protected static IEnumerable<XElement> FindElements(XContainer container, string localName) =>
        container.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    protected static decimal ParseDecimal(string? value, decimal defaultValue = 0m)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        var trimmed = value.Trim();
        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
            return result;
        if (decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.CurrentCulture, out result))
            return result;
        return defaultValue;
    }

    protected static DateTime? ParseDate(string? dateStr, string[] formats)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return null;
        var trimmed = dateStr.Trim();
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;
        return null;
    }

    protected static void NormalizeBalanceAmounts(ExtractedInvoiceData result)
    {
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

    protected static void ApplyCanadianTaxBreakdownFallback(ExtractedInvoiceData result)
    {
        if (result.TpsAmount == 0 && result.TvqAmount == 0 && result.TaxAmount > 0 && result.SubTotal > 0
            && Math.Abs(result.TaxAmount - (result.SubTotal * 0.14975m)) <= 0.05m)
        {
            result.TpsAmount = Math.Round(result.SubTotal * 0.05m, 2, MidpointRounding.AwayFromZero);
            result.TvqAmount = Math.Round(result.SubTotal * 0.09975m, 2, MidpointRounding.AwayFromZero);
        }
    }

    protected static void ApplyFallbackTotalsAndCreditNote(ExtractedInvoiceData result, string defaultCreditNoteReason)
    {
        if (result.TotalAmount == 0 && result.SubTotal != 0)
            result.TotalAmount = result.SubTotal + result.TaxAmount;
        if (result.TaxAmount == 0 && (result.TpsAmount != 0 || result.TvqAmount != 0))
            result.TaxAmount = result.TpsAmount + result.TvqAmount;
        if (result.DueDate == null && result.InvoiceDate.HasValue)
            result.DueDate = result.InvoiceDate.Value.AddDays(30);
        if (result.IsCreditNote || result.TotalAmount < 0)
            UniversalDocumentClassifier.ApplyCreditNoteNormalization(result, result.ClassificationReason ?? defaultCreditNoteReason);
    }
}
