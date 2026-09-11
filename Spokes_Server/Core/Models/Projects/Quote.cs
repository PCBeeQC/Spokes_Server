namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class Quote : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Quote)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;

    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = "Main Estimate";
    public DateTime Date { get; set; } = DateTime.Today;

    public string Status { get; set; } = "Draft";
    public string PdfPath { get; set; } = string.Empty;

    // NEW: Template Override
    public string? TemplateId { get; set; }

    // NEW: Deadline specific to this quote
    public DateOnly? StartDate { get; set; }
    public DateOnly? Deadline { get; set; }

    // Data
    public List<QuoteLaborItem> LaborItems { get; set; } = new();
    public List<QuoteItemRow> ItemRows { get; set; } = new();
    public List<PaymentTerm> PaymentTerms { get; set; } = new();

    // Monthly Payment Display
    public bool ShowMonthlyPayments { get; set; } = false;
    public int MonthlyPaymentMonths { get; set; } = 12;
    public string MonthlyPaymentDescription { get; set; } = "Monthly payments over {0} months";

    // Financials
    // NEW: Store the tax rate snapshot
    public decimal TaxRate { get; set; } = 0.0m;
    public bool ShowTax { get; set; } = true;

    public decimal TotalLabor => LaborItems.Sum(x => x.Total);
    public decimal TotalItems => ItemRows.Where(x => !x.IsClientBought).Sum(x => x.Total);
    public decimal TotalClientBought => ItemRows.Where(x => x.IsClientBought).Sum(x => x.Total);

    // NEW: Calculation Logic
    public decimal SubTotal => TotalLabor + TotalItems;
    public decimal TaxAmount => ShowTax ? SubTotal * TaxRate : 0;
    public decimal GrandTotal => SubTotal + TaxAmount;

    public decimal TotalProjectBudget => GrandTotal + TotalClientBought;

    // NEW: Presentation Data
    public string ScopeOfWork { get; set; } = string.Empty; // Rich text / Long description
    public string MainImageBase64 { get; set; } = string.Empty; // The "Rendering"

    // NEW: Legal
    // This gets copied from the Template when created, but can be edited per project
    public string TermsAndConditions { get; set; } = string.Empty;

    public bool IsConfidential { get; set; } = false;

    public bool SignatureRequired { get; set; } = false;
    public bool ShowIntermediateSignature { get; set; } = false;

    // NEW: Signatories
    public string ClientRepresentativeName { get; set; } = string.Empty;
    public string CompanyRepresentativeName { get; set; } = string.Empty;
}

public class QuoteLaborItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (QuoteLaborItem)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string WorkTypeId { get; set; } = string.Empty;
    public string WorkTypeName { get; set; } = string.Empty;
    public string SubTaskId { get; set; } = string.Empty;
    public string SubTaskName { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public decimal Rate { get; set; }
    public decimal Total => Hours * Rate;
}

public class QuoteItemRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (QuoteItemRow)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public bool IsClientBought { get; set; } = false;

    // Cost Management
    public decimal UnitCost { get; set; }
    public decimal MarkupPercentage { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal Total => Quantity * UnitPrice;
}

public class PaymentTerm
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (PaymentTerm)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public decimal Percentage { get; set; }
    public decimal Amount { get; set; }
    public string InvoiceId { get; set; } = string.Empty;
    public bool IsInvoiced => !string.IsNullOrEmpty(InvoiceId);
}



