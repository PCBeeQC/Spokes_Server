namespace Spokes_Server.Core.Models.Projects;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Extensions;
using Spokes_Server.Core.Models.Common;
using Spokes_Server.Core.Services.Accounting;

public class Quote : IDataEntity, IHasDocumentHistory, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Quote other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string ProjectId { get; set; } = string.Empty;

    public string QuoteNumber { get; set; } = string.Empty;
    public string Title { get; set; } = "Main Estimate";
    public DateTime Date { get; set; } = DateTime.Today;

    public string Status { get; set; } = QuoteStatus.Draft;
    public string PdfPath { get; set; } = string.Empty;

    // NEW: Template Override
    public string? TemplateId { get; set; }

    // NEW: Deadline specific to this quote
    public DateOnly? StartDate { get; set; }
    public DateOnly? Deadline { get; set; }

    // Data
    public List<QuoteLaborItem> LaborItems { get; set; } = [];
    public List<QuoteItemRow> MaterialLineItems { get; set; } = [];

    // Backward compatibility for legacy JSON files and existing call sites
    [JsonPropertyName("ItemRows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QuoteItemRow>? LegacyItemRows
    {
        get => null;
        set { if (value != null && MaterialLineItems.Count == 0) MaterialLineItems = value; }
    }

    public List<PaymentTerm> PaymentTerms { get; set; } = [];

    // Monthly Payment Display
    public bool ShowMonthlyPayments { get; set; }
    public int MonthlyPaymentMonths { get; set; } = 12;
    public string MonthlyPaymentDescription { get; set; } = "Monthly payments over {0} months";

    // Financials
    // NEW: Store the tax rate snapshot
    public decimal TaxRate { get; set; }
    public bool ShowTax { get; set; } = true;

    public decimal TotalLabor => LaborItems.Sum(x => x.Total);
    public decimal TotalItems => MaterialLineItems.Where(x => !x.IsClientBought).Sum(x => x.Total);
    public decimal TotalClientBought => MaterialLineItems.Where(x => x.IsClientBought).Sum(x => x.Total);

    // NEW: Calculation Logic
    public decimal SubTotal => Math.Round(TotalLabor + TotalItems, 2, MidpointRounding.AwayFromZero);
    public decimal TaxAmount => ShowTax ? FinancialCalculationEngine.CalculateTax(SubTotal, TaxRate) : 0;
    public decimal GrandTotal => SubTotal + TaxAmount;

    public decimal TotalProjectBudget => GrandTotal + TotalClientBought;

    // NEW: Presentation Data
    public string ScopeOfWork { get; set; } = string.Empty; // Rich text / Long description
    public string MainImageBase64 { get; set; } = string.Empty; // Legacy inline data URI
    public string MainImagePath { get; set; } = string.Empty; // Uploaded /spokesapi/files/... path

    [JsonIgnore]
    public string EffectiveMainImageUrl => !string.IsNullOrWhiteSpace(MainImagePath) ? MainImagePath : MainImageBase64;

    // NEW: Legal
    // This gets copied from the Template when created, but can be edited per project
    public string TermsAndConditions { get; set; } = string.Empty;
    public bool TermsStartOnNewPage { get; set; }

    public bool IsConfidential { get; set; }

    public bool SignatureRequired { get; set; }
    public bool ShowIntermediateSignature { get; set; }

    // NEW: Signatories
    public string ClientRepresentativeName { get; set; } = string.Empty;
    public string CompanyRepresentativeName { get; set; } = string.Empty;

    public List<DocumentHistoryEntry> History { get; set; } = [];

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        if (!string.IsNullOrWhiteSpace(PdfPath)) yield return PdfPath;
        if (!string.IsNullOrWhiteSpace(MainImagePath)) yield return MainImagePath;
    }

    /// <summary>
    /// Creates an independent draft copy of this quote with regenerated identity and child item IDs
    /// and cleared cached PDF reference.
    /// </summary>
    public Quote DuplicateAsDraft(string newQuoteNumber, string? changedBy = null)
    {
        var copy = this.DeepClone();

        copy.Id = Guid.NewGuid().ToString();
        copy.QuoteNumber = newQuoteNumber;
        copy.Date = DateTime.Today;
        copy.Status = QuoteStatus.Draft;
        copy.PdfPath = string.Empty;
        copy.Title = $"{Title} (Copy)";

        foreach (var labor in copy.LaborItems) labor.Id = Guid.NewGuid().ToString();
        foreach (var item in copy.MaterialLineItems) item.Id = Guid.NewGuid().ToString();
        foreach (var term in copy.PaymentTerms) term.Id = Guid.NewGuid().ToString();

        copy.History = [];
        DocumentHistoryHelper.RecordCreated(copy, changedBy, $"Created (Duplicated from #{QuoteNumber})");

        return copy;
    }
}

public class QuoteLaborItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is QuoteLaborItem other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string WorkTypeId { get; set; } = string.Empty;
    public string WorkTypeName { get; set; } = string.Empty;
    public string SubTaskId { get; set; } = string.Empty;
    public string SubTaskName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public decimal Hours { get; set; }
    public decimal Rate { get; set; }
    public decimal Total => Math.Round(Hours * Rate, 2, MidpointRounding.AwayFromZero);
}

public class QuoteItemRow
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is QuoteItemRow other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public bool IsClientBought { get; set; }

    // Cost Management
    public decimal UnitCost { get; set; }
    public decimal MarkupPercentage { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal Total => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public class PaymentTerm
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is PaymentTerm other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Description { get; set; } = string.Empty;
    public decimal Percentage { get; set; }
    public decimal Amount { get; set; }
    public string InvoiceId { get; set; } = string.Empty;
    public bool IsInvoiced => !string.IsNullOrEmpty(InvoiceId);
}



