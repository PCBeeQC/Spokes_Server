namespace Spokes_Server.Core.Helpers;

using MudBlazor;

public static class StatusColorHelper
{
    /// <summary>
    /// Returns the semantic MudBlazor Color for a given entity or business status.
    /// Case-insensitive with whitespace trimming.
    /// </summary>
    public static Color GetColor(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Color.Default;

        return status.Trim().ToLowerInvariant() switch
        {
            // Positive / Completed / Approved
            "paid" or "fully billed" or "approved" or "accepted" or "quote accepted" or "active" => Color.Success,

            // Informational / In-flight / Invoiced
            "final" or "partially paid" or "partially billed" or "payable" or "submitted" or "quoted" => Color.Info,

            // Cautionary / Awaiting Action
            "pending" or "awaiting approval" or "discovery" or "in progress" => Color.Warning,

            // Error / Negative / Overdue
            "overdue" or "clawback" or "rejected" or "cancelled" or "canceled" or "failed" => Color.Error,

            // Neutral / Draft / Closed
            "draft" or "closed" or "completed" or "project in progress" or "inactive" or "none" => Color.Default,

            _ => Color.Default
        };
    }
}
