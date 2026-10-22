namespace Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

using System;
using System.Collections.Generic;

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
    public bool IsStatement { get => IsAccountStatement; set => IsAccountStatement = value; }
    public bool IsCreditNote { get; set; }
    public bool IsPaymentReceipt { get; set; }
    public bool IsScannedImagePdf { get; set; }
    public string? ClassificationReason { get; set; }
    public List<string> StatementReferencedInvoices { get; set; } = [];
    public List<string> ExtractionLog { get; set; } = [];

    // Supplier / Seller Info
    public string? SupplierName { get; set; }
    public string? SupplierTaxId { get; set; } // TPS / TVQ or Tax Registration
    public string? SupplierEmail { get; set; }

    // Buyer / Customer Info (e.g. Poly-Robotics)
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
    public string BuyerCompanyName { get; set; } = "Poly-Robotics";
    public List<string> BuyerAliases { get; set; } = ["poly-robotics", "poly robotics", "polyrobotics"];
    public string BuyerEmailDomain { get; set; } = "poly-robotics.com";
    public List<string> BuyerEmailDomains { get; set; } = ["poly-robotics.com"];
    public List<string> BuyerTaxIds { get; set; } = [];
    public string DefaultCurrency { get; set; } = "CAD";

    public IEnumerable<string> BuyerNames =>
        string.IsNullOrWhiteSpace(BuyerCompanyName)
            ? BuyerAliases
            : new[] { BuyerCompanyName }.Concat(BuyerAliases);
}
