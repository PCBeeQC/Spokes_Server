namespace Spokes_Server.Core.Services.Accounting;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Services.Accounting.InvoiceExtraction;

public record MatchingPoResult(PurchaseOrder? MatchedPo, string Confidence, string Reason)
{
    public static MatchingPoResult None => new(null, "None", "No matching Purchase Order found");
}

/// <summary>
/// Intelligent matching engine that pairs incoming extracted bills with open Purchase Orders.
/// </summary>
public class SmartPoMatchingEngine
{
    private readonly Database _db;

    public SmartPoMatchingEngine(Database db)
    {
        _db = db;
    }

    /// <summary>
    /// Evaluates open purchase orders to find the best match for the extracted invoice data.
    /// </summary>
    public MatchingPoResult FindMatch(ExtractedInvoiceData data, string? matchedSupplierId = null)
    {
        if (data == null) return MatchingPoResult.None;

        var openPos = _db.PurchaseOrders.GetAll()
            .Where(po => po.Status != PurchaseOrderStatus.FullyBilled &&
                         po.Status != PurchaseOrderStatus.Closed &&
                         po.Status != PurchaseOrderStatus.Void &&
                         po.Status != PurchaseOrderStatus.Draft)
            .ToList();

        if (openPos.Count == 0) return MatchingPoResult.None;

        // --- Rule 1: Exact PO Number Match (High Confidence) ---
        if (!string.IsNullOrWhiteSpace(data.PurchaseOrderReference))
        {
            var cleanRef = data.PurchaseOrderReference.Trim();
            var exactMatch = openPos.FirstOrDefault(po =>
                po.PoNumber.Equals(cleanRef, StringComparison.OrdinalIgnoreCase) ||
                po.PoNumber.EndsWith(cleanRef, StringComparison.OrdinalIgnoreCase) ||
                cleanRef.EndsWith(po.PoNumber, StringComparison.OrdinalIgnoreCase));

            if (exactMatch != null)
            {
                return new MatchingPoResult(exactMatch, "High", $"Matched to PO {exactMatch.PoNumber} via explicit invoice order reference");
            }
        }

        // Check if invoice number or text contains any PO number
        if (!string.IsNullOrWhiteSpace(data.InvoiceNumber))
        {
            var poByInv = openPos.FirstOrDefault(po => data.InvoiceNumber.Contains(po.PoNumber, StringComparison.OrdinalIgnoreCase));
            if (poByInv != null)
            {
                return new MatchingPoResult(poByInv, "High", $"Matched to PO {poByInv.PoNumber} referenced in invoice number");
            }
        }

        // Filter open POs by supplier if known
        string? matchedSupplierName = null;
        if (!string.IsNullOrEmpty(matchedSupplierId))
        {
            matchedSupplierName = _db.Suppliers.GetById(matchedSupplierId)?.Name;
        }

        var candidateSupplier = !string.IsNullOrWhiteSpace(matchedSupplierName) ? matchedSupplierName : data.SupplierName;

        var supplierPos = openPos.Where(po =>
            !string.IsNullOrWhiteSpace(candidateSupplier) &&
            (po.Supplier.BusinessName.Contains(candidateSupplier, StringComparison.OrdinalIgnoreCase) ||
             candidateSupplier.Contains(po.Supplier.BusinessName, StringComparison.OrdinalIgnoreCase))
        ).ToList();

        // --- Rule 2: Supplier + Exact or Close Amount Match (Medium/High Confidence) ---
        if (supplierPos.Count > 0 && data.TotalAmount > 0)
        {
            // Exact total match
            var amountMatch = supplierPos.FirstOrDefault(po => Math.Abs(po.GrandTotal - data.TotalAmount) <= 0.05m);
            if (amountMatch != null)
            {
                return new MatchingPoResult(amountMatch, "High", $"Matched to PO {amountMatch.PoNumber} via matching supplier and exact total ({amountMatch.GrandTotal:C2})");
            }

            // Subtotal match (if tax was added or differs)
            if (data.SubTotal > 0)
            {
                var subtotalMatch = supplierPos.FirstOrDefault(po => Math.Abs(po.SubTotal - data.SubTotal) <= 0.05m);
                if (subtotalMatch != null)
                {
                    return new MatchingPoResult(subtotalMatch, "Medium", $"Matched to PO {subtotalMatch.PoNumber} via matching supplier and subtotal ({subtotalMatch.SubTotal:C2})");
                }
            }

            // Open balance match (if PO was partially billed)
            var balanceMatch = supplierPos.FirstOrDefault(po =>
                po.TotalBilled > 0 && Math.Abs((po.GrandTotal - po.TotalBilled) - data.TotalAmount) <= 0.05m);
            if (balanceMatch != null)
            {
                return new MatchingPoResult(balanceMatch, "Medium", $"Matched to PO {balanceMatch.PoNumber} via remaining unbilled balance ({balanceMatch.GrandTotal - balanceMatch.TotalBilled:C2})");
            }

            // Tolerance match within $1.00 for rounding / small variance
            var closeMatch = supplierPos.FirstOrDefault(po => Math.Abs(po.GrandTotal - data.TotalAmount) <= 1.00m);
            if (closeMatch != null)
            {
                return new MatchingPoResult(closeMatch, "Medium", $"Matched to PO {closeMatch.PoNumber} via supplier and close amount match");
            }
        }

        // --- Rule 3: Single open PO for this supplier (Low/Medium Confidence) ---
        if (supplierPos.Count == 1)
        {
            var singlePo = supplierPos[0];
            return new MatchingPoResult(singlePo, "Low", $"Associated with PO {singlePo.PoNumber} (sole open PO for this supplier)");
        }

        return MatchingPoResult.None;
    }
}
