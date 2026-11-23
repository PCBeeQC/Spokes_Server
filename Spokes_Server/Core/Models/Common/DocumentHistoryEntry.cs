using System;
using System.Collections.Generic;
using System.Linq;

namespace Spokes_Server.Core.Models.Common;

public static class DocumentHistoryActions
{
    public const string Created = "Created";
    public const string StatusChanged = "StatusChanged";
    public const string StatusReverted = "StatusReverted";
    public const string EditedInFinal = "EditedInFinal";
    public const string Edited = "Edited";
    public const string PaymentRecorded = "PaymentRecorded";
    public const string PaymentUpdated = "PaymentUpdated";
    public const string PaymentDeleted = "PaymentDeleted";
    public const string BalanceWrittenOff = "BalanceWrittenOff";
    public const string WriteOffReversed = "WriteOffReversed";
    public const string RemittanceRecorded = "RemittanceRecorded";
}

public interface IHasDocumentHistory
{
    string Id { get; }
    string Status { get; }
    List<DocumentHistoryEntry> History { get; set; }
}

public class DocumentHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string? OldStatus { get; set; }
    public string? NewStatus { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public static class DocumentHistoryHelper
{
    public static string NormalizeActor(string? user) =>
        string.IsNullOrWhiteSpace(user) ? "System" : user.Trim();

    public static void RecordCreated(IHasDocumentHistory doc, string? user = null, string? summary = null)
    {
        if (doc == null) return;
        doc.History ??= [];
        doc.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = DocumentHistoryActions.Created,
            NewStatus = doc.Status,
            ChangedBy = NormalizeActor(user),
            Summary = string.IsNullOrWhiteSpace(summary) ? "Document created" : summary.Trim()
        });
    }

    public static void RecordStatusChange(
        IHasDocumentHistory doc,
        string? oldStatus,
        string? newStatus,
        string? user = null,
        string? summary = null,
        string? reason = null,
        bool isReverted = false)
    {
        if (doc == null) return;
        doc.History ??= [];
        doc.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = isReverted ? DocumentHistoryActions.StatusReverted : DocumentHistoryActions.StatusChanged,
            OldStatus = oldStatus,
            NewStatus = newStatus ?? doc.Status,
            ChangedBy = NormalizeActor(user),
            Summary = string.IsNullOrWhiteSpace(summary)
                ? $"Status changed from {oldStatus ?? "None"} to {newStatus ?? doc.Status}"
                : summary.Trim(),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });
    }

    public static void RecordPaymentAction(
        IHasDocumentHistory doc,
        string action,
        string? oldStatus,
        string? newStatus,
        string summary,
        string? user = null,
        string? reason = null)
    {
        if (doc == null) return;
        doc.History ??= [];
        doc.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = action,
            OldStatus = oldStatus,
            NewStatus = newStatus ?? doc.Status,
            ChangedBy = NormalizeActor(user),
            Summary = summary,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });
    }

    public static void RecordFieldEdits(
        IHasDocumentHistory doc,
        IReadOnlyList<string> fieldDiffs,
        string? user = null,
        string? reason = null,
        string? oldStatus = null,
        string? newStatus = null)
    {
        if (doc == null || fieldDiffs == null || fieldDiffs.Count == 0) return;
        doc.History ??= [];
        doc.History.Add(new DocumentHistoryEntry
        {
            Timestamp = DateTime.UtcNow,
            Action = DocumentHistoryActions.EditedInFinal,
            OldStatus = oldStatus ?? doc.Status,
            NewStatus = newStatus ?? doc.Status,
            ChangedBy = NormalizeActor(user),
            Summary = string.Join("; ", fieldDiffs),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
        });
    }

    public static void AddDiffIfChanged(List<string> diffs, string label, string? oldValue, string? newValue)
    {
        var a = (oldValue ?? string.Empty).Trim();
        var b = (newValue ?? string.Empty).Trim();
        if (!string.Equals(a, b, StringComparison.Ordinal))
        {
            diffs.Add($"{label}: {(string.IsNullOrEmpty(a) ? "(empty)" : a)} → {(string.IsNullOrEmpty(b) ? "(empty)" : b)}");
        }
    }

    public static void AddCurrencyDiffIfChanged(List<string> diffs, string label, decimal oldVal, decimal newVal)
    {
        if (Math.Round(oldVal, 2) != Math.Round(newVal, 2))
        {
            diffs.Add($"{label}: {oldVal:C2} → {newVal:C2}");
        }
    }

    public static void AddDateDiffIfChanged(List<string> diffs, string label, DateTime? oldDate, DateTime? newDate)
    {
        var a = oldDate?.ToString("yyyy-MM-dd") ?? "(none)";
        var b = newDate?.ToString("yyyy-MM-dd") ?? "(none)";
        if (a != b)
        {
            diffs.Add($"{label}: {a} → {b}");
        }
    }
}
