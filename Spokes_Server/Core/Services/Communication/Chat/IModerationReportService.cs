using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

public enum ModerationQueueTab
{
    Open,
    History
}

public sealed record ModerationReportCounts(int Open, int Resolved, int Dismissed)
{
    public int History => Resolved + Dismissed;
    public int Total => Open + History;
}

/// <summary>
/// Manages the lifecycle, querying, resolution, and history cleanup of reported chat messages.
/// </summary>
public interface IModerationReportService
{
    bool CanModerate(Employee? employee);

    ModerationReportCounts GetCounts();

    IReadOnlyList<ReportedMessage> GetReports(
        ModerationQueueTab tab = ModerationQueueTab.Open,
        string? historyStatusFilter = null,
        string? searchQuery = null);

    IReadOnlyList<ModerationReportItem> GetReportItems(
        ModerationQueueTab tab = ModerationQueueTab.Open,
        string? historyStatusFilter = null,
        string? searchQuery = null,
        string unknownUserLabel = "Unknown",
        Func<string, string>? localize = null);

    Task<SpokesResult<ReportedMessage>> SubmitReportAsync(
        ReportedMessage report,
        string reporterEmployeeId,
        CancellationToken cancellationToken = default);

    SpokesResult<ReportedMessage> DismissReport(string reportId, string moderatorEmployeeId);

    Task<SpokesResult<int>> ResolveAndDeleteMessageAsync(
        string reportId,
        string moderatorEmployeeId,
        CancellationToken cancellationToken = default);

    SpokesResult DeleteHistoryReport(string reportId, string moderatorEmployeeId);

    SpokesResult<int> ClearHistory(string moderatorEmployeeId);
}
