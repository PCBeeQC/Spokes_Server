using System;
using System.Collections.Generic;

namespace Spokes_Server.Core.Models.Accounting;

/// <summary>
/// Authoritative domain lifecycle statuses for Invoices.
/// </summary>
public static class InvoiceStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
    public const string Paid = "Paid";
    public const string WrittenOff = "Written Off";
    public const string Partial = "Partial";
    public const string Sent = "Sent";
    public const string Void = "Void";

    public static bool IsSettled(string? status) =>
        string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, WrittenOff, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true only when the invoice is finalized/posted and participates in Accounting,
    /// General Ledger, Tax, Project financial totals, and billing allocations.
    /// Explicitly excludes Draft, Void, and unknown/empty statuses.
    /// </summary>
    public static bool IsPosted(string? status) =>
        string.Equals(status, Final, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Sent, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Partial, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Paid, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, WrittenOff, StringComparison.OrdinalIgnoreCase);

    public static int GetRank(string? status) => status switch
    {
        Draft => 0,
        Final or Sent => 1,
        Partial => 2,
        Paid or WrittenOff => 3,
        Void => 4,
        _ => 0
    };

    /// <summary>
    /// Returns the statuses selectable in the manual status dropdown.
    /// Paid, Written Off, and Partial are strictly conditional on recorded payments/write-offs, so the dropdown only controls Draft &lt;-&gt; Final.
    /// </summary>
    public static IReadOnlyList<string> GetDropdownStatuses(string? currentStatus, bool hasPayments)
    {
        if (hasPayments || currentStatus is Paid or WrittenOff or Partial or Void)
        {
            return [currentStatus ?? Final];
        }

        return [Draft, Final];
    }

    /// <summary>
    /// Returns true if transitioning from <paramref name="oldStatus"/> to <paramref name="newStatus"/>
    /// moves backward in the lifecycle (e.g., Final -&gt; Draft, Paid -&gt; Final/Partial), requiring a mandatory reason.
    /// </summary>
    public static bool RequiresReason(string? oldStatus, string? newStatus)
    {
        if (string.IsNullOrWhiteSpace(oldStatus) || string.IsNullOrWhiteSpace(newStatus)) return false;
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase)) return false;

        return GetRank(newStatus) < GetRank(oldStatus);
    }

    /// <summary>
    /// Returns true only when a finalized invoice (without payments) can be unlocked for editing.
    /// Paid, Partial, and Void invoices are strictly read-only.
    /// </summary>
    public static bool CanUnlockEdit(string? status) =>
        string.Equals(status, Final, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, Sent, StringComparison.OrdinalIgnoreCase);
}
