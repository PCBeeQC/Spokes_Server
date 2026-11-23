namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Core;

/// <summary>
/// Source format from which the invoice data was extracted.
/// </summary>
public enum InvoiceExtractionSource
{
    FacturX_CII,
    UBL,
    PdfTextHeuristic
}

/// <summary>
/// Normalized data extracted from an electronic invoice (Factur-X, UBL, or text PDF).
/// </summary>
public class ExtractedInvoiceData
{
    public InvoiceExtractionSource Source { get; set; }
    public string? RawDocumentType { get; set; }
    public bool IsAccountStatement { get; set; }
    public bool IsCreditNote { get; set; }
    public bool IsPaymentReceipt { get; set; }
    public bool IsScannedImagePdf { get; set; }
    public string? ClassificationReason { get; set; }

    // Supplier / Seller Info
    public string? SupplierName { get; set; }
    public string? SupplierTaxId { get; set; } // TPS / TVQ or Tax Registration
    public string? SupplierEmail { get; set; }

    // Buyer / Customer Info
    public string? BuyerName { get; set; }

    // References
    public string? InvoiceNumber { get; set; }
    public string? PurchaseOrderReference { get; set; } // e.g. PO-2401-002
    public string? PaymentTerms { get; set; }

    // Dates
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }

    // Financial Totals
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TpsAmount { get; set; }
    public decimal TvqAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public string Currency { get; set; } = "CAD";
    public bool HasPaymentDetected => PaidAmount > 0;

    // Line Items
    public List<ExtractedInvoiceLineItem> LineItems { get; set; } = [];

    // Confidence
    public bool IsConfident => !string.IsNullOrWhiteSpace(InvoiceNumber) && TotalAmount != 0;
}

public class ExtractedInvoiceLineItem
{
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public string? ItemCode { get; set; }
}

/// <summary>
/// Context passed into the invoice extraction pipeline to identify the internal buyer organization
/// and default locale settings without hardcoding company names.
/// </summary>
public class InvoiceExtractionContext
{
    private const string DefaultPlaceholderCompanyName = "My Spokes";

    private static readonly Regex TrailingLegalSuffixRegex = new(
        @"(?:[\s,\.]+(?:inc\.?|llc|l\.l\.c\.|ltd\.?|limited|corp\.?|corporation|gmbh|gmbh\s*&\s*co\.?\s*kg|ag|sarl|s\.a\.r\.l\.|sas|s\.a\.s\.|s\.a\.|b\.v\.|n\.v\.|s\.r\.l\.|s\.l\.|ab|as|aps|oy|lt[eé]e|s\.e\.n\.c\.|plc))+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string BuyerCompanyName { get; set; } = string.Empty;
    public List<string> BuyerAliases { get; set; } = [];
    public List<string> BuyerEmailDomains { get; set; } = [];
    public List<string> BuyerTaxIds { get; set; } = [];
    public string DefaultCurrency { get; set; } = "CAD";

    public IEnumerable<string> BuyerNames
    {
        get
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seeds = string.IsNullOrWhiteSpace(BuyerCompanyName)
                ? BuyerAliases
                : new[] { BuyerCompanyName }.Concat(BuyerAliases);

            foreach (var raw in seeds)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var trimmed = raw.Trim();
                results.Add(trimmed);

                var baseName = TrailingLegalSuffixRegex.Replace(trimmed, string.Empty).Trim();
                if (baseName.Length >= 4)
                {
                    results.Add(baseName);
                    results.Add(baseName.Replace('-', ' '));
                    results.Add(Regex.Replace(baseName, @"\s+", "-"));
                    var compact = Regex.Replace(baseName, @"[\s\-]+", string.Empty);
                    if (compact.Length >= 4)
                    {
                        results.Add(compact);
                    }
                }
            }

            return results;
        }
    }

    public IEnumerable<string> AllBuyerEmailDomains
    {
        get
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in BuyerEmailDomains)
            {
                var normalized = CompanyProfile.ExtractDomainFromEmail(raw);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    results.Add(normalized);
                }
            }

            return results;
        }
    }

    public static InvoiceExtractionContext FromCompanyProfile(
        CompanyProfile? profile,
        CompanyBillInboxSettings? billSettings = null)
    {
        var defaultCurrency = !string.IsNullOrWhiteSpace(billSettings?.DefaultCurrency)
            ? billSettings.DefaultCurrency
            : (profile?.SupportedCurrencies?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "CAD");

        var ctx = new InvoiceExtractionContext
        {
            DefaultCurrency = defaultCurrency
        };

        if (profile != null)
        {
            var companyName = profile.CompanyName?.Trim();
            if (!string.IsNullOrWhiteSpace(companyName) &&
                !string.Equals(companyName, DefaultPlaceholderCompanyName, StringComparison.OrdinalIgnoreCase))
            {
                ctx.BuyerCompanyName = companyName;
            }

            foreach (var domain in profile.GetInternalDomains(billSettings?.EmailAddress))
            {
                ctx.BuyerEmailDomains.Add(domain);
            }
        }
        else
        {
            var billDomain = CompanyProfile.ExtractDomainFromEmail(billSettings?.EmailAddress);
            if (!string.IsNullOrWhiteSpace(billDomain) && !EmailProviderPresets.IsPublicMailboxDomain(billDomain))
            {
                ctx.BuyerEmailDomains.Add(billDomain);
            }
        }

        return ctx;
    }
}
