using System;
using System.Collections.Generic;

namespace Spokes_Server.Core.Models.Projects;

public static class QuoteStatus
{
    public const string Draft = "Draft";
    public const string Final = "Final";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";

    public static int GetRank(string? status) => status switch
    {
        Draft => 0,
        Final => 1,
        Accepted or Rejected => 2,
        _ => 0
    };

    /// <summary>
    /// Returns the statuses selectable from the given current status.
    /// A Draft quote must be Finalized before it can be Accepted or Rejected.
    /// </summary>
    public static IReadOnlyList<string> GetAllowedTransitions(string? currentStatus) => currentStatus switch
    {
        Draft => [Draft, Final],
        _ => [Draft, Final, Accepted, Rejected]
    };

    /// <summary>
    /// Returns true if transitioning from <paramref name="oldStatus"/> to <paramref name="newStatus"/>
    /// moves backward in the lifecycle or changes a terminal resolution (Accepted &lt;-&gt; Rejected),
    /// requiring a confirmation dialog with a reason.
    /// </summary>
    public static bool RequiresReason(string? oldStatus, string? newStatus)
    {
        if (string.IsNullOrWhiteSpace(oldStatus) || string.IsNullOrWhiteSpace(newStatus)) return false;
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase)) return false;

        int oldRank = GetRank(oldStatus);
        int newRank = GetRank(newStatus);

        if (newRank < oldRank) return true;

        // Switching between Accepted and Rejected
        if (oldRank == 2 && newRank == 2) return true;

        return false;
    }

    /// <summary>
    /// Returns true only when a finalized quote can be unlocked for editing.
    /// Accepted and Rejected quotes are strictly read-only.
    /// </summary>
    public static bool CanUnlockEdit(string? status) =>
        string.Equals(status, Final, StringComparison.OrdinalIgnoreCase);
}
