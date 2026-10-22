namespace Spokes_Server.Core.Services.Accounting.Export;

using System;
using System.Collections.Generic;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Core;

public enum SagePayableSourceType
{
    SupplierBill,
    RecurringBill,
    ExpenseReport,
    Commission
}

/// <summary>
/// Unified representation of any payable transaction in Spokes (Supplier/PO Bill, Recurring Bill,
/// Employee Expense Report, or Commission) ready for validation and export to Sage 50 Canadian Edition.
/// </summary>
public class SagePayableExportItem
{
    public string Id { get; set; } = string.Empty;
    public string? ParentEntityId { get; set; }
    public SagePayableSourceType SourceType { get; set; } = SagePayableSourceType.SupplierBill;
    public string TypeBadge => SourceType switch
    {
        SagePayableSourceType.SupplierBill => !string.IsNullOrEmpty(PurchaseOrderNumber) ? "PO Bill" : "Bill",
        SagePayableSourceType.RecurringBill => IsOneTimeRecurring ? "One-Time" : "Recurring",
        SagePayableSourceType.ExpenseReport => "Expense Report",
        SagePayableSourceType.Commission => IsCreditNote ? "Clawback" : "Commission",
        _ => "Bill"
    };
    public bool IsOneTimeRecurring { get; set; }

    public string InvoiceOrReferenceNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string VendorName { get; set; } = string.Empty;
    public string? SupplierId { get; set; }
    public string? PurchaseOrderId { get; set; }
    public string? PurchaseOrderNumber { get; set; }

    public DateTime Date { get; set; } = DateTime.Today;
    public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);

    public decimal SubTotal { get; set; }
    public decimal TpsAmount { get; set; }
    public decimal TvqAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public bool IsTaxExempt { get; set; }
    public bool IsCreditNote { get; set; }
    public bool HasExplicitTaxBreakdown { get; set; } = true;

    public string Status { get; set; } = string.Empty;
    public string? GlAccountCode { get; set; }
    public string? SageItemCode { get; set; }

    public DateTime? ExportedToSageAt { get; set; }
    public string? SageBatchId { get; set; }
    public bool IsExported => ExportedToSageAt.HasValue;

    public List<(string Description, decimal Quantity, decimal RawLineTotal, string? GlAccountOrItemCode)> LineItems { get; set; } = [];

    // Reference to underlying domain object for quick editing in UI
    public object? UnderlyingEntity { get; set; }
    public object? SecondaryEntity { get; set; }
}

public class SageBaselineImportSummary
{
    public bool Success { get; set; }
    public string DetectedRecordType { get; set; } = string.Empty;
    public int CustomersImported { get; set; }
    public int VendorsImported { get; set; }
    public int AccountsImported { get; set; }
    public int ServiceItemsImported { get; set; }
    public int SuppliersSyncedToSpokes { get; set; }
    public int AutoMappedEntities { get; set; }
    public List<string> Messages { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public class SageExportValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public interface ISage50ExportService
{
    // Legacy / Direct Bill Overloads
    SageExportValidationResult ValidateForExport(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, Sage50ExportConfig config);
    string GenerateSage50Imp(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, Sage50ExportConfig config);
    string GenerateSalesInvoicesCsv(IEnumerable<Invoice> invoices, Sage50ExportConfig config);
    string GeneratePurchaseInvoicesCsv(IEnumerable<Bill> bills, Sage50ExportConfig config);
    void MarkAsExported(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills, string batchId);
    void UnmarkExported(IEnumerable<Invoice> invoices, IEnumerable<Bill> bills);

    // Unified Multi-Stream Accounting Export Pipeline
    List<SagePayableExportItem> GetAllPayableExportItems();
    SagePayableExportItem ConvertBillToExportItem(Bill bill, Sage50ExportConfig? config = null);
    SageExportValidationResult ValidateForExport(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables, Sage50ExportConfig config);
    string GenerateSage50Imp(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables, Sage50ExportConfig config);
    byte[] GenerateSage50ImpBytes(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables, Sage50ExportConfig config);
    string GeneratePurchaseInvoicesCsv(IEnumerable<SagePayableExportItem> payables, Sage50ExportConfig config);
    void MarkAsExported(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables, string batchId, bool overwriteExistingBatch = false);
    void UnmarkExported(IEnumerable<Invoice> invoices, IEnumerable<SagePayableExportItem> payables);

    // Client / Vendor Resolution & Sage 50 Baseline Import
    ClientInfo? ResolveClientForInvoice(Invoice inv);
    string ResolveSageCustomerName(ClientInfo? client, Sage50ExportConfig config);
    string ResolveSageVendorName(string? vendorName, PartyContactInfo? contactInfo, Sage50ExportConfig config);
    SageBaselineImportSummary ImportSageBaselineFile(string fileContent, Sage50ExportConfig config, bool syncSuppliersToDatabase = true);
    SageBaselineImportSummary ImportSageBaselineBytes(byte[] fileBytes, Sage50ExportConfig config, bool syncSuppliersToDatabase = true);
    int AutoMapEntitiesToBaseline(Sage50ExportConfig config, bool syncSuppliersToDatabase = true);
}
