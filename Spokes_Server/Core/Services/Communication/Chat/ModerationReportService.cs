using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class ModerationReportService : IModerationReportService
{
    private readonly ReportedMessageRepository _reportedMessages;
    private readonly EmployeeRepository _employees;
    private readonly IChatService _chatService;
    private readonly ChatStateService _chatState;
    private readonly INotificationRoutingService? _notificationRouting;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModerationReportService>? _logger;

    public ModerationReportService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        IChatService chatService,
        ChatStateService? chatState = null,
        INotificationRoutingService? notificationRouting = null,
        TimeProvider? timeProvider = null,
        ILogger<ModerationReportService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(communicationContext.ReportedMessages);
        ArgumentNullException.ThrowIfNull(hrContext.Employees);
        ArgumentNullException.ThrowIfNull(chatService);

        _reportedMessages = communicationContext.ReportedMessages;
        _employees = hrContext.Employees;
        _chatService = chatService;
        _chatState = chatState ?? new ChatStateService();
        _notificationRouting = notificationRouting;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public bool CanModerate(Employee? employee) =>
        employee is { IsActive: true, IsSuspended: false, IsBanned: false } &&
        (employee.IsAdmin || employee.HasPermission(AppPermissions.Chat.Moderator));

    public ModerationReportCounts GetCounts()
    {
        var all = _reportedMessages.GetAll();
        int open = 0;
        int resolved = 0;
        int dismissed = 0;

        foreach (var report in all)
        {
            if (IsOpen(report))
            {
                open++;
            }
            else if (string.Equals(report.Status, ReportedMessageStatus.Dismissed, StringComparison.OrdinalIgnoreCase))
            {
                dismissed++;
            }
            else
            {
                resolved++;
            }
        }

        return new ModerationReportCounts(open, resolved, dismissed);
    }

    public IReadOnlyList<ReportedMessage> GetReports(
        ModerationQueueTab tab = ModerationQueueTab.Open,
        string? historyStatusFilter = null,
        string? searchQuery = null) =>
        GetReportItems(tab, historyStatusFilter, searchQuery)
            .Select(item => item.Report)
            .ToList();

    public IReadOnlyList<ModerationReportItem> GetReportItems(
        ModerationQueueTab tab = ModerationQueueTab.Open,
        string? historyStatusFilter = null,
        string? searchQuery = null,
        string unknownUserLabel = "Unknown",
        Func<string, string>? localize = null)
    {
        IEnumerable<ReportedMessage> query = _reportedMessages.GetAll();

        if (tab == ModerationQueueTab.Open)
        {
            query = query
                .Where(IsOpen)
                .OrderByDescending(r => r.ReportedAt);
        }
        else
        {
            query = query.Where(r => !IsOpen(r));

            var normalizedStatus = historyStatusFilter?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                !string.Equals(normalizedStatus, "all", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => string.Equals(r.Status, normalizedStatus, StringComparison.OrdinalIgnoreCase));
            }

            query = query.OrderByDescending(r => r.ResolvedAt ?? r.ReportedAt);
        }

        var projected = ModerationReportFilterHelper.BuildItems(
            query,
            id => string.IsNullOrWhiteSpace(id) ? null : _employees.GetById(id),
            unknownUserLabel,
            localize,
            sortByReportedAtDescending: false);

        return ModerationReportFilterHelper.Filter(projected, searchQuery);
    }

    public async Task<SpokesResult<ReportedMessage>> SubmitReportAsync(
        ReportedMessage report,
        string reporterEmployeeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (string.IsNullOrWhiteSpace(reporterEmployeeId))
        {
            return SpokesResult<ReportedMessage>.Failure("Reporter user ID is required.", "ACCESS_DENIED");
        }

        var reporter = _employees.GetById(reporterEmployeeId);
        if (reporter is not { IsActive: true, IsSuspended: false, IsBanned: false })
        {
            return SpokesResult<ReportedMessage>.Failure("User is not eligible to submit message reports.", "ACCESS_DENIED");
        }

        if (string.IsNullOrWhiteSpace(report.Id))
        {
            report.Id = Guid.NewGuid().ToString();
        }

        report.ReporterId = reporter.Id;
        report.Status = ReportedMessageStatus.Open;
        report.ReportedAt = _timeProvider.GetUtcNow().UtcDateTime;
        report.ResolutionAction = string.Empty;
        report.ResolvedBy = string.Empty;
        report.ResolvedAt = null;

        _reportedMessages.Save(report);

        if (_notificationRouting != null)
        {
            try
            {
                await _notificationRouting.RouteModerationNotificationAsync(report);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to route moderation notification for report {ReportId}", report.Id);
            }
        }

        _chatState.NotifyModerationAlert(report);
        return SpokesResult<ReportedMessage>.Success(report);
    }

    public SpokesResult<ReportedMessage> DismissReport(string reportId, string moderatorEmployeeId)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out var moderator, out var authError))
        {
            return SpokesResult<ReportedMessage>.Failure(authError!, "ACCESS_DENIED");
        }

        if (string.IsNullOrWhiteSpace(reportId))
        {
            return SpokesResult<ReportedMessage>.Failure("Report ID is required.", "VALIDATION_ERROR");
        }

        var report = _reportedMessages.GetById(reportId);
        if (report == null)
        {
            return SpokesResult<ReportedMessage>.Failure($"Report '{reportId}' not found.", "NOT_FOUND");
        }

        if (!IsOpen(report))
        {
            return SpokesResult<ReportedMessage>.Failure("Report has already been reviewed.", "INVALID_STATE");
        }

        report.Status = ReportedMessageStatus.Dismissed;
        report.ResolutionAction = ReportedMessageResolution.Dismissed;
        report.ResolvedBy = ResolveModeratorDisplayName(moderator);
        report.ResolvedAt = _timeProvider.GetUtcNow().UtcDateTime;

        _reportedMessages.Save(report);
        _chatState.NotifyModerationReportUpdated(report);

        return SpokesResult<ReportedMessage>.Success(report);
    }

    public async Task<SpokesResult<int>> ResolveAndDeleteMessageAsync(
        string reportId,
        string moderatorEmployeeId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out var moderator, out var authError))
        {
            return SpokesResult<int>.Failure(authError!, "ACCESS_DENIED");
        }

        if (string.IsNullOrWhiteSpace(reportId))
        {
            return SpokesResult<int>.Failure("Report ID is required.", "VALIDATION_ERROR");
        }

        var report = _reportedMessages.GetById(reportId);
        if (report == null)
        {
            return SpokesResult<int>.Failure($"Report '{reportId}' not found.", "NOT_FOUND");
        }

        if (!string.IsNullOrWhiteSpace(report.MessageId))
        {
            var deleteResult = await _chatService.AdminDeleteMessageAsync(report.MessageId, moderator.Id, cancellationToken);
            if (!deleteResult.IsSuccess &&
                !string.Equals(deleteResult.Code, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return SpokesResult<int>.Failure(
                    deleteResult.ErrorMessage ?? "Failed to delete message.",
                    deleteResult.Code ?? "DELETE_FAILED");
            }
        }

        var targets = new List<ReportedMessage> { report };
        if (!string.IsNullOrWhiteSpace(report.MessageId))
        {
            var siblingReports = _reportedMessages.GetAll()
                .Where(r => r.Id != report.Id &&
                            IsOpen(r) &&
                            string.Equals(r.MessageId, report.MessageId, StringComparison.Ordinal));
            targets.AddRange(siblingReports);
        }

        var resolvedBy = ResolveModeratorDisplayName(moderator);
        var resolvedAt = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var target in targets)
        {
            target.Status = ReportedMessageStatus.Resolved;
            target.ResolutionAction = ReportedMessageResolution.DeletedMessage;
            target.ResolvedBy = resolvedBy;
            target.ResolvedAt = resolvedAt;
            target.SnapshotContent = ReportedMessageResolution.DeletedContentPlaceholder;
            if (target.Attachments != null)
            {
                target.Attachments.Clear();
            }
            else
            {
                target.Attachments = [];
            }

            _reportedMessages.Save(target);
            _chatState.NotifyModerationReportUpdated(target);
        }

        return SpokesResult<int>.Success(targets.Count);
    }

    public SpokesResult DeleteHistoryReport(string reportId, string moderatorEmployeeId)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out _, out var authError))
        {
            return SpokesResult.Failure(authError!, "ACCESS_DENIED");
        }

        if (string.IsNullOrWhiteSpace(reportId))
        {
            return SpokesResult.Failure("Report ID is required.", "VALIDATION_ERROR");
        }

        var report = _reportedMessages.GetById(reportId);
        if (report == null)
        {
            return SpokesResult.Failure($"Report '{reportId}' not found.", "NOT_FOUND");
        }

        if (IsOpen(report))
        {
            return SpokesResult.Failure("Open reports must be dismissed or resolved before removal.", "INVALID_STATE");
        }

        _reportedMessages.Delete(report.Id);
        _chatState.NotifyModerationReportUpdated(report);

        return SpokesResult.Success();
    }

    public SpokesResult<int> ClearHistory(string moderatorEmployeeId)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out _, out var authError))
        {
            return SpokesResult<int>.Failure(authError!, "ACCESS_DENIED");
        }

        var historyItems = _reportedMessages.GetAll()
            .Where(r => !IsOpen(r))
            .ToList();

        foreach (var item in historyItems)
        {
            _reportedMessages.Delete(item.Id);
        }

        if (historyItems.Count > 0)
        {
            _chatState.NotifyModerationReportUpdated(historyItems[0]);
        }

        return SpokesResult<int>.Success(historyItems.Count);
    }

    private static bool IsOpen(ReportedMessage report) =>
        ModerationReportFilterHelper.IsOpenStatus(report);

    private bool TryGetAuthorizedModerator(string moderatorEmployeeId, out Employee moderator, out string? errorMessage)
    {
        moderator = null!;
        if (string.IsNullOrWhiteSpace(moderatorEmployeeId))
        {
            errorMessage = "Moderator user ID is required.";
            return false;
        }

        var emp = _employees.GetById(moderatorEmployeeId);
        if (!CanModerate(emp))
        {
            _logger?.LogWarning("Moderation action rejected: user {UserId} lacks moderation privileges", moderatorEmployeeId);
            errorMessage = "User is not authorized to perform moderation actions.";
            return false;
        }

        moderator = emp!;
        errorMessage = null;
        return true;
    }

    private static string ResolveModeratorDisplayName(Employee moderator) =>
        !string.IsNullOrWhiteSpace(moderator.FullName) ? moderator.FullName : "Moderator";
}
