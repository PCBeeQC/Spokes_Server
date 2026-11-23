using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

public class ModerationReportService : IModerationReportService
{
    private readonly ReportedMessageRepository _reportedMessages;
    private readonly ChatChannelRepository? _channels;
    private readonly ChatMessageRepository? _messages;
    private readonly EmployeeRepository _employees;
    private readonly IChatService _chatService;
    private readonly ChatStateService _chatState;
    private readonly INotificationRoutingService? _notificationRouting;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModerationReportService>? _logger;
    private readonly EncryptionService? _encryptionService;
    private readonly IChatAuthorizationService? _chatAuth;
    private readonly NotificationQueueService? _notificationQueue;
    private readonly IUserPrivacyService? _privacyService;

    public ModerationReportService(
        CommunicationContext communicationContext,
        HrContext hrContext,
        IChatService chatService,
        ChatStateService? chatState = null,
        INotificationRoutingService? notificationRouting = null,
        TimeProvider? timeProvider = null,
        ILogger<ModerationReportService>? logger = null,
        EncryptionService? encryptionService = null,
        IChatAuthorizationService? chatAuth = null,
        NotificationQueueService? notificationQueue = null,
        IUserPrivacyService? privacyService = null)
    {
        ArgumentNullException.ThrowIfNull(communicationContext);
        ArgumentNullException.ThrowIfNull(hrContext);
        ArgumentNullException.ThrowIfNull(communicationContext.ReportedMessages);
        ArgumentNullException.ThrowIfNull(hrContext.Employees);
        ArgumentNullException.ThrowIfNull(chatService);

        _reportedMessages = communicationContext.ReportedMessages;
        _channels = communicationContext.ChatChannels;
        _messages = communicationContext.ChatMessages;
        _employees = hrContext.Employees;
        _chatService = chatService;
        _chatState = chatState ?? new ChatStateService();
        _notificationRouting = notificationRouting;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _encryptionService = encryptionService;
        _chatAuth = chatAuth;
        _notificationQueue = notificationQueue;
        _privacyService = privacyService;
    }

    public bool CanModerate(Employee? employee) =>
        ModerationAuthorizationHelper.CanModerateAny(employee);

    public ModerationReportCounts GetCounts(string? viewerEmployeeId = null)
    {
        if (!TryGetAuthorizedModerator(viewerEmployeeId, out var viewer, out _))
        {
            return new ModerationReportCounts(0, 0, 0);
        }

        var all = _reportedMessages.GetAll()
            .Where(r => ModerationAuthorizationHelper.CanViewReport(viewer, r, ResolveReportChannel(r), _chatAuth));
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

    public IReadOnlyList<ModerationReportItem> GetReportItems(
        ModerationQueueTab tab = ModerationQueueTab.Open,
        string? historyStatusFilter = null,
        string? searchQuery = null,
        string unknownUserLabel = "Unknown",
        Func<string, string>? localize = null,
        string? viewerEmployeeId = null)
    {
        if (!TryGetAuthorizedModerator(viewerEmployeeId, out var viewer, out _))
        {
            return [];
        }

        IEnumerable<ReportedMessage> query = _reportedMessages.GetAll()
            .Where(r => ModerationAuthorizationHelper.CanViewReport(viewer, r, ResolveReportChannel(r), _chatAuth));

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

        var reportList = query.ToList();
        var canActByReportId = reportList.ToDictionary(
            r => r.Id,
            r => ModerationAuthorizationHelper.CanActOnReport(
                viewer,
                r,
                ResolveReportChannel(r),
                string.IsNullOrWhiteSpace(r.ReportedUserId) ? null : _employees.GetById(r.ReportedUserId),
                _chatAuth),
            StringComparer.Ordinal);

        var displayReports = reportList.Select(r => CloneForDisplay(r, viewer)).ToList();

        var projected = ModerationReportFilterHelper.BuildItems(
            displayReports,
            id => ResolveVisibleEmployeeForViewer(viewer, id),
            unknownUserLabel,
            localize,
            sortByReportedAtDescending: false,
            canActPredicate: (rep, _) => canActByReportId.GetValueOrDefault(rep.Id),
            resolveByName: rep => rep.ResolvedBy);

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
        if (reporter is not { IsActive: true, IsSuspended: false, IsBanned: false } ||
            !reporter.HasPermission(AppPermissions.Chat.Use))
        {
            return SpokesResult<ReportedMessage>.Failure("User is not eligible to submit message reports.", "ACCESS_DENIED");
        }

        if (string.IsNullOrWhiteSpace(report.MessageId))
        {
            return SpokesResult<ReportedMessage>.Failure("Message ID is required.", "VALIDATION_ERROR");
        }

        if (_messages != null && _channels != null)
        {
            var message = _messages.GetById(report.MessageId);
            if (message == null || message.IsDeleted)
            {
                return SpokesResult<ReportedMessage>.Failure("Reported message was not found or has been deleted.", "NOT_FOUND");
            }

            if (string.Equals(message.SenderId, reporter.Id, StringComparison.Ordinal))
            {
                return SpokesResult<ReportedMessage>.Failure("Cannot report your own message.", "VALIDATION_ERROR");
            }

            var channel = _channels.GetById(message.ChannelId);
            if (channel == null)
            {
                return SpokesResult<ReportedMessage>.Failure("Channel for the reported message was not found.", "NOT_FOUND");
            }

            if (_chatAuth != null && !_chatAuth.CanUserAccessChannel(channel, reporter))
            {
                return SpokesResult<ReportedMessage>.Failure("User is not authorized to access this channel.", "ACCESS_DENIED");
            }

            report.ChannelId = channel.Id;
            report.ReportedUserId = message.SenderId;

            if (!message.IsEncrypted)
            {
                report.SnapshotContent = message.Content ?? string.Empty;
                report.Attachments = CloneAttachments(message.Attachments);
            }
            else
            {
                var allowedPaths = new HashSet<string>(
                    (message.Attachments ?? []).Where(a => !string.IsNullOrWhiteSpace(a.FilePath)).Select(a => a.FilePath),
                    StringComparer.Ordinal);
                var validClientAttachments = (report.Attachments ?? [])
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.FilePath) && allowedPaths.Contains(a.FilePath))
                    .ToList();
                report.Attachments = CloneAttachments(validClientAttachments);
            }
        }
        else if (string.Equals(report.ReportedUserId, reporter.Id, StringComparison.Ordinal))
        {
            return SpokesResult<ReportedMessage>.Failure("Cannot report your own message.", "VALIDATION_ERROR");
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
        report.ResolvedByEmployeeId = string.Empty;
        report.ResolvedAt = null;
        report.SnapshotContent = EncryptSnapshotIfNeeded(report.SnapshotContent);

        _reportedMessages.Save(report);
        var displayReport = CloneReportSnapshot(report);

        if (_notificationRouting != null)
        {
            try
            {
                await _notificationRouting.RouteModerationNotificationAsync(displayReport);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to route moderation notification for report {ReportId}", report.Id);
            }
        }

        _chatState.NotifyModerationAlert(displayReport);
        return SpokesResult<ReportedMessage>.Success(displayReport);
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

        var channel = ResolveReportChannel(report);
        var reportedUser = ResolveReportedUser(report);
        if (!ModerationAuthorizationHelper.CanActOnReport(moderator, report, channel, reportedUser, _chatAuth))
        {
            return SpokesResult<ReportedMessage>.Failure("User is not authorized to act on this moderation report.", "ACCESS_DENIED");
        }

        if (!IsOpen(report))
        {
            return SpokesResult<ReportedMessage>.Failure("Report has already been reviewed.", "INVALID_STATE");
        }

        report.Status = ReportedMessageStatus.Dismissed;
        report.ResolutionAction = ReportedMessageResolution.Dismissed;
        report.ResolvedBy = ResolveModeratorDisplayName(moderator);
        report.ResolvedByEmployeeId = moderator.Id;
        report.ResolvedAt = _timeProvider.GetUtcNow().UtcDateTime;
        report.SnapshotContent = ReportedMessageResolution.DeletedContentPlaceholder;
        if (report.Attachments != null)
        {
            report.Attachments.Clear();
        }
        else
        {
            report.Attachments = [];
        }

        _reportedMessages.Save(report);
        _notificationQueue?.PurgeForModerationReport(report.Id);
        var snapshotReport = CloneReportSnapshot(report);
        _chatState.NotifyModerationReportUpdated(snapshotReport);

        return SpokesResult<ReportedMessage>.Success(CloneForDisplay(report, moderator));
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

        var channel = ResolveReportChannel(report);
        var reportedUser = ResolveReportedUser(report);
        if (!ModerationAuthorizationHelper.CanActOnReport(moderator, report, channel, reportedUser, _chatAuth))
        {
            return SpokesResult<int>.Failure("User is not authorized to act on this moderation report.", "ACCESS_DENIED");
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
            target.ResolvedByEmployeeId = moderator.Id;
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
            _notificationQueue?.PurgeForModerationReport(target.Id);
            _chatState.NotifyModerationReportUpdated(target);
        }

        return SpokesResult<int>.Success(targets.Count);
    }

    public SpokesResult DeleteHistoryReport(string reportId, string moderatorEmployeeId)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out var moderator, out var authError))
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

        var channel = ResolveReportChannel(report);
        var reportedUser = ResolveReportedUser(report);
        if (!ModerationAuthorizationHelper.CanActOnReport(moderator, report, channel, reportedUser, _chatAuth))
        {
            return SpokesResult.Failure("User is not authorized to remove this moderation report.", "ACCESS_DENIED");
        }

        if (IsOpen(report))
        {
            return SpokesResult.Failure("Open reports must be dismissed or resolved before removal.", "INVALID_STATE");
        }

        _reportedMessages.Delete(report.Id);
        _notificationQueue?.PurgeForModerationReport(report.Id);
        _chatState.NotifyModerationReportUpdated(report);

        return SpokesResult.Success();
    }

    public SpokesResult<int> ClearHistory(string moderatorEmployeeId)
    {
        if (!TryGetAuthorizedModerator(moderatorEmployeeId, out var moderator, out var authError))
        {
            return SpokesResult<int>.Failure(authError!, "ACCESS_DENIED");
        }

        var historyItems = _reportedMessages.GetAll()
            .Where(r => !IsOpen(r) &&
                        ModerationAuthorizationHelper.CanActOnReport(moderator, r, ResolveReportChannel(r), ResolveReportedUser(r), _chatAuth))
            .ToList();

        foreach (var item in historyItems)
        {
            _reportedMessages.Delete(item.Id);
            _notificationQueue?.PurgeForModerationReport(item.Id);
        }

        if (historyItems.Count > 0)
        {
            _chatState.NotifyModerationReportUpdated(historyItems[0]);
        }

        return SpokesResult<int>.Success(historyItems.Count);
    }

    private ChatChannel? ResolveReportChannel(ReportedMessage report) =>
        ModerationAuthorizationHelper.ResolveReportChannel(report, _channels, _messages);

    private Employee? ResolveReportedUser(ReportedMessage report) =>
        string.IsNullOrWhiteSpace(report.ReportedUserId) ? null : _employees.GetById(report.ReportedUserId);

    private string EncryptSnapshotIfNeeded(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot) ||
            snapshot == ReportedMessageResolution.DeletedContentPlaceholder ||
            _encryptionService == null)
        {
            return snapshot ?? string.Empty;
        }

        if (snapshot.StartsWith("v2:", StringComparison.Ordinal))
        {
            return snapshot;
        }

        return _encryptionService.Encrypt(snapshot);
    }

    private string DecryptSnapshotForDisplay(string? storedSnapshot)
    {
        if (string.IsNullOrEmpty(storedSnapshot))
        {
            return string.Empty;
        }

        if (storedSnapshot == ReportedMessageResolution.DeletedContentPlaceholder)
        {
            return storedSnapshot;
        }

        if (_encryptionService != null && storedSnapshot.StartsWith("v2:", StringComparison.Ordinal))
        {
            try
            {
                return _encryptionService.TryDecrypt(storedSnapshot, out var plain) && plain != null
                    ? plain
                    : string.Empty;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to decrypt moderation report snapshot content.");
                return string.Empty;
            }
        }

        return storedSnapshot;
    }

    private Employee? ResolveVisibleEmployeeForViewer(Employee? viewer, string? targetEmployeeId)
    {
        if (viewer == null || string.IsNullOrWhiteSpace(targetEmployeeId))
        {
            return null;
        }

        var target = _employees.GetById(targetEmployeeId);
        if (target == null)
        {
            return null;
        }

        if (string.Equals(viewer.Id, target.Id, StringComparison.Ordinal))
        {
            return target;
        }

        return _privacyService == null || _privacyService.CanDiscoverUser(viewer, target)
            ? target
            : null;
    }

    private string ResolveVisibleResolverName(Employee? viewer, ReportedMessage source)
    {
        if (string.IsNullOrWhiteSpace(source.ResolvedBy))
        {
            return string.Empty;
        }

        if (string.Equals(source.ResolvedBy, "System", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(source.ResolvedByEmployeeId))
        {
            return source.ResolvedBy;
        }

        if (string.IsNullOrEmpty(source.ResolvedByEmployeeId))
        {
            return "Moderator";
        }

        if (viewer != null && string.Equals(viewer.Id, source.ResolvedByEmployeeId, StringComparison.Ordinal))
        {
            return source.ResolvedBy;
        }

        var resolver = _employees.GetById(source.ResolvedByEmployeeId);
        if (resolver != null &&
            viewer != null &&
            (_privacyService == null || _privacyService.CanDiscoverUser(viewer, resolver)))
        {
            return source.ResolvedBy;
        }

        return "Moderator";
    }

    private ReportedMessage CloneReportSnapshot(ReportedMessage source) => new()
    {
        Id = source.Id,
        MessageId = source.MessageId,
        ChannelId = source.ChannelId,
        ReporterId = source.ReporterId,
        ReportedUserId = source.ReportedUserId,
        Reason = source.Reason,
        ReportedAt = source.ReportedAt,
        Status = source.Status,
        SnapshotContent = DecryptSnapshotForDisplay(source.SnapshotContent),
        Attachments = CloneAttachments(source.Attachments),
        ResolutionAction = source.ResolutionAction,
        ResolvedBy = source.ResolvedBy,
        ResolvedByEmployeeId = source.ResolvedByEmployeeId,
        ResolvedAt = source.ResolvedAt
    };

    private ReportedMessage CloneForDisplay(ReportedMessage source, Employee? viewer)
    {
        var copy = CloneReportSnapshot(source);
        copy.ReporterId = ResolveVisibleEmployeeForViewer(viewer, source.ReporterId)?.Id ?? string.Empty;
        copy.ReportedUserId = ResolveVisibleEmployeeForViewer(viewer, source.ReportedUserId)?.Id ?? string.Empty;
        copy.ResolvedBy = ResolveVisibleResolverName(viewer, source);
        copy.ResolvedByEmployeeId = !string.IsNullOrEmpty(source.ResolvedByEmployeeId) &&
                                    ResolveVisibleEmployeeForViewer(viewer, source.ResolvedByEmployeeId) != null
            ? source.ResolvedByEmployeeId
            : string.Empty;
        return copy;
    }

    private static List<ChatAttachment> CloneAttachments(IEnumerable<ChatAttachment>? attachments)
    {
        if (attachments == null)
        {
            return [];
        }

        return attachments
            .Where(a => a != null)
            .Select(a => new ChatAttachment
            {
                Id = a.Id,
                FileName = a.FileName,
                FilePath = a.FilePath,
                ContentType = a.ContentType,
                FileSizeBytes = a.FileSizeBytes,
                KeyEpoch = a.KeyEpoch,
                HasServerThumbnail = a.HasServerThumbnail,
                ImageWidth = a.ImageWidth,
                ImageHeight = a.ImageHeight
            })
            .ToList();
    }

    private static bool IsOpen(ReportedMessage report) =>
        ModerationReportFilterHelper.IsOpenStatus(report);

    private bool TryGetAuthorizedModerator(string? moderatorEmployeeId, out Employee moderator, out string? errorMessage)
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
