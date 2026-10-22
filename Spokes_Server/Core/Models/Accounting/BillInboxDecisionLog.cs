namespace Spokes_Server.Core.Models.Accounting;

using System;
using System.Collections.Generic;

/// <summary>
/// Status of an individual step in the email ingestion decision tree.
/// </summary>
public enum DecisionStepStatus
{
    Success,
    Warning,
    Skipped,
    Info
}

/// <summary>
/// Records an individual step and evaluation outcome in the bill ingestion decision tree.
/// </summary>
public class DecisionLogStep
{
    public string StepNumber { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public DecisionStepStatus Status { get; set; } = DecisionStepStatus.Success;
    public string Outcome { get; set; } = string.Empty;
    public List<string> Details { get; set; } = [];
}

/// <summary>
/// Details of how an individual bill field was populated and what source extracted it.
/// </summary>
public class DecisionFieldSource
{
    public string FieldName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ExtractionMethod { get; set; } = string.Empty;
}

/// <summary>
/// Records the specific incoming attachment/extraction that matched an existing bill in Spokes.
/// </summary>
public class DuplicateBillMatchSummary
{
    public string ExistingBillId { get; set; } = string.Empty;
    public string AttachmentFileName { get; set; } = string.Empty;
    public string AttachmentPath { get; set; } = string.Empty;
    public string? ExtractedInvoiceNumber { get; set; }
    public decimal? ExtractedAmount { get; set; }
    public string? ExtractedCurrency { get; set; }
    public DateTime? ExtractedInvoiceDate { get; set; }
    public string MatchReason { get; set; } = string.Empty;
    public ExistingBillMatchMethod MatchMethod { get; set; }
    public bool IsPossibleDuplicate { get; set; }
}

/// <summary>
/// Complete decision tree and extraction diagnostics log for a billing email.
/// Provides transparency into why an email was processed or rejected, and how each field was populated.
/// </summary>
public class BillInboxDecisionLog
{
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
    public string FinalStatus { get; set; } = string.Empty;
    public string SummaryReason { get; set; } = string.Empty;
    public List<DecisionLogStep> Steps { get; set; } = [];
    public List<DecisionFieldSource> FieldSources { get; set; } = [];
    public List<DuplicateBillMatchSummary> DuplicateMatches { get; set; } = [];
}

