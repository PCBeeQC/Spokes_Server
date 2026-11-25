namespace Spokes_Server.Core.Models.Documents;

using Spokes_Server.Core.Data;

public class DocumentTemplate : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is DocumentTemplate other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Name { get; set; } = "Standard English";
    public string LanguageCode { get; set; } = "en";
    public string TaskTranslationLanguage { get; set; } = string.Empty;
    public bool IsDefault { get; set; }

    // --- FINANCIALS (New) ---
    public string CurrencySymbol { get; set; } = "$";
    public bool CurrencySymbolAfter { get; set; }
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    // Banking / Wire Transfer Info
    public string BankName { get; set; } = string.Empty;
    public string BankAccountNumber { get; set; } = string.Empty; // Wire/Routing #
    public string BankSwiftCode { get; set; } = string.Empty;
    public string PaymentInstructions { get; set; } = "";

    // --- LABELS (Existing) ---
    public string Label_Invoice { get; set; } = "INVOICE";
    public string Label_Quote { get; set; } = "QUOTE";
    public string Label_PurchaseOrder { get; set; } = "PURCHASE ORDER";

    public string Label_ItemNumber { get; set; } = "#";
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
    public string Label_Paid { get; set; } = "PAID";
    public string Label_DateReceived { get; set; } = "Date Received";

    public string Label_ClientBoughtTag { get; set; } = " (Client Responsibility)";
    public string Label_ClientBoughtTotal { get; set; } = "Client Bought Items";

    public string Label_PaymentInstructions { get; set; } = "Payment Instructions";
    public string Label_PaymentTerms { get; set; } = "Payment Terms";
    public string Label_BankName { get; set; } = "Bank";
    public string Label_BankAccount { get; set; } = "Account";
    public string Label_SwiftCode { get; set; } = "Swift / Transit";

    /// <summary>
    /// Optional per-template translated labels for company tax registration numbers,
    /// keyed by <see cref="Core.Models.Accounting.SalesTaxRule.Code"/> (e.g. "TPS" -> "No de TPS", "TVQ" -> "No de TVQ").
    /// Falls back to the active jurisdiction's RegistrationNumberLabel when blank.
    /// </summary>
    public Dictionary<string, string> TaxRegistrationLabelsByCode { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Label_ProjectManager { get; set; } = "Project Manager";
    public string Label_ItemLaborTag { get; set; } = "(Labor)";
    public string Label_BlockOfHours { get; set; } = "Block of Hours";

    // NEW: Content Defaults
    public string DefaultTermsAndConditions { get; set; } = "1. Validity: This quote is valid for 30 days.\n2. Payment: 30% deposit required.";
    public string? DefaultTermsAndConditionsId { get; set; } = string.Empty;
    public bool TermsStartOnNewPage { get; set; }
    public string DefaultPaymentTerms { get; set; } = "Net 30 days.";

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

    // PROJECT REPORT SPECIFIC LABELS
    public string Label_ProjectReport { get; set; } = "PROJECT REPORT";
    public string Label_InternalManagementReport { get; set; } = "Internal Management Report";
    public string Label_ClientProjectReport { get; set; } = "Client Project Report";
    public string Label_ReportSubtitle { get; set; } = "Project Status & Financial Report";
    public string Label_ReportDetails { get; set; } = "Report & Project Details";
    public string Label_ReportNumber { get; set; } = "Report #";
    public string Label_ReportPeriod { get; set; } = "Reporting Period";
    public string Label_AsOfDate { get; set; } = "As of";
    public string Label_ProjectInception { get; set; } = "Project Start";
    public string Label_ProjectSchedule { get; set; } = "Project Schedule";
    public string Label_PreparedFor { get; set; } = "Prepared For";
    public string Label_ProjectClient { get; set; } = "Project Client";
    public string Label_ReportAudience { get; set; } = "Audience";
    public string Label_ProjectStatus { get; set; } = "Project Status";
    public string Label_PreTax { get; set; } = "Pre-Tax";
    public string Label_PreTaxSeparateTaxBreakdown { get; set; } = "Pre-Tax + Separate Tax Breakdown";
    public string Label_AmountsExclTax { get; set; } = "Amounts Excl. Tax (Pre-Tax)";
    public string Label_AmountsInclTax { get; set; } = "Amounts Incl. Tax";

    public string Label_ExecutiveSummary { get; set; } = "Executive Summary";
    public string Label_FinancialSummary { get; set; } = "Financial & Profitability Summary";
    public string Label_ProfitabilityStatement { get; set; } = "Project Profitability Statement";
    public string Label_BillingAndCashSummary { get; set; } = "Billing, Taxes & Cash Flow";
    public string Label_QuoteVsActualSummary { get; set; } = "Quote vs. Actual (Hours & Costs)";
    public string Label_OverheadAllocationSummary { get; set; } = "Business Overhead Allocation";
    public string Label_DirectProjectProfit { get; set; } = "Direct Project Profit";
    public string Label_AllocatedOverhead { get; set; } = "Allocated Business Overhead";
    public string Label_AllocatedInternalCosts { get; set; } = "Allocated Internal Costs";
    public string Label_NetProfitAfterOverhead { get; set; } = "Net Profit After Overhead";
    public string Label_FinancialCategory { get; set; } = "Financial Category";
    public string Label_Details { get; set; } = "Details";
    public string Label_LaborCost { get; set; } = "Direct Labor";
    public string Label_InternalCost { get; set; } = "Internal Cost";
    public string Label_LaborBreakdown { get; set; } = "Labor & Time Breakdown";
    public string Label_LaborByTask { get; set; } = "Labor by Task";
    public string Label_LaborByEmployee { get; set; } = "Labor by Employee";
    public string Label_LaborByPeriod { get; set; } = "Periodic Labor Breakdown";
    public string Label_PurchasesBreakdown { get; set; } = "Purchases & Procurement";
    public string Label_MonthlyPurchasesSummary { get; set; } = "Monthly Purchases Summary";
    public string Label_ExpensesBreakdown { get; set; } = "Expenses & Reimbursements";
    public string Label_BillingSummary { get; set; } = "Quotes & Invoicing History";
    public string Label_Quotes { get; set; } = "Quotes";
    public string Label_Invoices { get; set; } = "Invoices";
    public string Label_Item { get; set; } = "Item";
    public string Label_Items { get; set; } = "Items";
    public string Label_CommissionsBreakdown { get; set; } = "Commissions";
    public string Label_ProjectTimeline { get; set; } = "Key Milestones & Notes";

    public string Label_Budgeted { get; set; } = "Budgeted";
    public string Label_Actual { get; set; } = "Actual";
    public string Label_Variance { get; set; } = "Variance";
    public string Label_Usage { get; set; } = "Usage";
    public string Label_Hour { get; set; } = "Hour";
    public string Label_Hours { get; set; } = "Hours";
    public string Label_HourlyRate { get; set; } = "Rate";
    public string Label_Employee { get; set; } = "Employee";
    public string Label_Employees { get; set; } = "Employees";
    public string Label_Week { get; set; } = "Week";
    public string Label_Weeks { get; set; } = "Weeks";
    public string Label_Month { get; set; } = "Month";
    public string Label_Months { get; set; } = "Months";
    public string Label_RunningTotal { get; set; } = "Cumulative";
    public string Label_ExpectedRevenue { get; set; } = "Expected Revenue";
    public string Label_TotalInvoiced { get; set; } = "Total Invoiced";
    public string Label_TotalCost { get; set; } = "Total Cost";
    public string Label_NetProfit { get; set; } = "Net Profit";
    public string Label_NetMargin { get; set; } = "Net Margin";
    public string Label_CashPosition { get; set; } = "Cash Position";

    // FINANCIAL STATEMENTS (SHAREHOLDER / INVESTOR) SPECIFIC LABELS
    public string Label_FinancialStatements { get; set; } = "Financial Statements";
    public string Label_FinancialStatementsSubtitle { get; set; } = "Prepared for Shareholders & Investors";
    public string Label_BasisOfPresentationTitle { get; set; } = "Notice to Reader / Basis of Presentation";
    public string Label_BasisOfPresentationDefault { get; set; } =
        "Prepared on an accrual basis from posted General Ledger records for management, shareholder, and investor review (Compilation / Notice to Reader). Not audited or reviewed by an independent public accountant unless signed off externally.";
    public string Label_IncomeStatementSection { get; set; } = "Statement of Income (Profit & Loss)";
    public string Label_BalanceSheetSection { get; set; } = "Balance Sheet (Statement of Financial Position)";
    public string Label_CashFlowSection { get; set; } = "Statement of Cash Flows (Indirect Method)";
    public string Label_FinancialRatiosSection { get; set; } = "Financial Ratios & Key Metrics";
    public string Label_ManagementNotesSection { get; set; } = "Notes to Financial Statements & Management Discussion";
}
