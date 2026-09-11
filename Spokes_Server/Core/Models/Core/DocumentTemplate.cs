namespace Spokes_Server.Core.Models.Core;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class DocumentTemplate : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (DocumentTemplate)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = "Standard English";
    public string LanguageCode { get; set; } = "en";
    public string TaskTranslationLanguage { get; set; } = string.Empty;
    public bool IsDefault { get; set; } = false;

    // --- FINANCIALS (New) ---
    public decimal TaxRate { get; set; } = 0.14975m; // Default to QC, but editable per template
    public string CurrencySymbol { get; set; } = "$";
    public bool CurrencySymbolAfter { get; set; } = false;
    public string DateFormat { get; set; } = "MM/dd/yyyy";

    // Banking / Wire Transfer Info
    public string BankName { get; set; } = string.Empty;
    public string BankAccountNumber { get; set; } = string.Empty; // Wire/Routing #
    public string BankSwiftCode { get; set; } = string.Empty;
    public string TaxId1 { get; set; } = string.Empty; // e.g. GST Number
    public string TaxId2 { get; set; } = string.Empty; // e.g. QST Number
    public string PaymentInstructions { get; set; } = "";

    // --- LABELS (Existing) ---
    public string Label_Invoice { get; set; } = "INVOICE";
    public string Label_Quote { get; set; } = "QUOTE";
    public string Label_PurchaseOrder { get; set; } = "PURCHASE ORDER";

    public string Label_Description { get; set; } = "Description";
    public string Label_Quantity { get; set; } = "Qty";
    public string Label_Price { get; set; } = "Price";
    public string Label_Total { get; set; } = "Total";

    public string Label_Subtotal { get; set; } = "Subtotal";
    public string Label_Tax { get; set; } = "Tax";
    public string Label_GrandTotal { get; set; } = "Total Due"; // Used for Invoice
    public string Label_QuoteTotal { get; set; } = "Total Budget"; // Used for Quote
    public string Label_BalanceDue { get; set; } = "Balance Due";
    public string Label_AmountPaid { get; set; } = "Amount Paid";

    public string Label_ClientBoughtTag { get; set; } = " (Client Responsibility)";
    public string Label_ClientBoughtTotal { get; set; } = "Client Bought Items";

    public string Label_PaymentInstructions { get; set; } = "Payment Instructions";
    public string Label_BankName { get; set; } = "Bank";
    public string Label_BankAccount { get; set; } = "Account";
    public string Label_SwiftCode { get; set; } = "Swift / Transit";

    public string Label_TaxId1 { get; set; } = "Tax ID 1"; // e.g. "GST:"
    public string Label_TaxId2 { get; set; } = "Tax ID 2"; // e.g. "QST:"
    public string Label_ProjectManager { get; set; } = "Project Manager";
    public string Label_ItemLaborTag { get; set; } = "(Labor)";
    public string Label_BlockOfHours { get; set; } = "Block of Hours";

    // NEW: Content Defaults
    public string DefaultTermsAndConditions { get; set; } = "1. Validity: This quote is valid for 30 days.\n2. Payment: 30% deposit required.";

    // NEW: Proposal Labels
    public string Label_ScopeOfWork { get; set; } = "Scope of Work";
    public string Label_TermsAndConditions { get; set; } = "Terms & Conditions";
    public string Label_Acceptance { get; set; } = "Acceptance";
    public string Label_Signature { get; set; } = "Authorized Signature";
    public string Label_DateSigned { get; set; } = "Date";

    // QUOTE SPECIFIC
    public string Label_QuoteTo { get; set; } = "Quote For"; // Distinct from "Bill To"
    public string Label_PresentedBy { get; set; } = "Presented By";
    public string Label_ManagerEmail { get; set; } = "Email";
    public string Label_AcceptanceText { get; set; } = "By signing below, the client accepts this proposal and the terms and conditions outlined above.";

    // CONFIDENTIALITY
    public string Label_Confidential { get; set; } = "CONFIDENTIAL";
    public string Label_ConfidentialText { get; set; } = "This document contains proprietary information.";

    // SIGNATURES
    public string Label_SignatureClient { get; set; } = "Client Signature";
    public string Label_SignatureCompany { get; set; } = "Authorized Signature (Spokes)";

    public string Label_PricingBreakdown { get; set; } = "Pricing Breakdown";
    public string Label_PaymentSchedule { get; set; } = "Payment Schedule";
    public string DefaultCostPlusTerms { get; set; } = "**Time & Materials:** This project will be billed monthly based on actual hours worked and expenses incurred.";

    // PO SPECIFIC LABELS
    public string Label_Vendor { get; set; } = "VENDOR";
    public string Label_IssuedBy { get; set; } = "Issued By";
    public string Label_PoTotal { get; set; } = "Total"; // Distinct from "Total Due" on Invoice
    public string Label_SupplierRef { get; set; } = "Supplier Quote Ref";

    // NEW: Branding Colors
    public string ColorPrimary { get; set; } = "#594AE2"; // Default Purple
    public string ColorSecondary { get; set; } = "#1E88E5"; // Default Blue

    // Meta
    public string Label_Date { get; set; } = "Date";
    public string Label_DueDate { get; set; } = "Due Date";
    public string Label_BillTo { get; set; } = "Bill To";
    public string Label_ShipTo { get; set; } = "Ship To";
    public string Label_ProjectNumber { get; set; } = "Project #";
}



