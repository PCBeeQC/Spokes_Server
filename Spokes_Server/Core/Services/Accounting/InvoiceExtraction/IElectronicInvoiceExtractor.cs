namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System.IO;

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
