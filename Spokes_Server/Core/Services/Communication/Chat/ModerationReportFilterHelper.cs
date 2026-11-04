using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Read-model projection of a <see cref="ReportedMessage"/> with pre-resolved reporter/reported-user
/// identities and localized display labels for Moderation Dashboard rendering and search filtering.
/// </summary>
public sealed class ModerationReportItem
{
    public required ReportedMessage Report { get; init; }
    public Employee? Reporter { get; init; }
    public required string ReporterName { get; init; }
    public Employee? ReportedUser { get; init; }
    public required string ReportedUserName { get; init; }
    public required string LocalizedReason { get; init; }
    public required string LocalizedStatus { get; init; }
    public required string LocalizedResolutionAction { get; init; }
    public required string LocalizedSnapshotContent { get; init; }
    public bool IsOpen { get; init; }

    public bool CanModerateReportedUser =>
        ReportedUser is { IsSystem: false } &&
        !ReportedUser.IsAdmin &&
        !ReportedUser.HasPermission(AppPermissions.Chat.Moderator);
}

/// <summary>
/// Projects normalized <see cref="ReportedMessage"/> entities into <see cref="ModerationReportItem"/>
/// instances with memoized employee resolution and filters reports across visible moderation attributes.
/// </summary>
public static class ModerationReportFilterHelper
{
    public static bool IsOpenStatus(ReportedMessage report) =>
        string.IsNullOrWhiteSpace(report.Status) ||
        string.Equals(report.Status, ReportedMessageStatus.Open, StringComparison.OrdinalIgnoreCase);

    public static List<ModerationReportItem> BuildItems(
        IEnumerable<ReportedMessage> reports,
        Func<string?, Employee?> resolveEmployee,
        string unknownUserLabel = "Unknown",
        Func<string, string>? localize = null,
        bool sortByReportedAtDescending = true)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(resolveEmployee);

        var fallbackUserLabel = string.IsNullOrWhiteSpace(unknownUserLabel) ? "Unknown" : unknownUserLabel;
        var employeeCache = new Dictionary<string, Employee?>(StringComparer.Ordinal);
        var ordered = sortByReportedAtDescending
            ? reports.OrderByDescending(r => r.ReportedAt)
            : reports;

        var items = new List<ModerationReportItem>();
        foreach (var report in ordered)
        {
            if (report == null) continue;

            var reporter = ResolveEmployeeCached(report.ReporterId, employeeCache, resolveEmployee);
            var reportedUser = ResolveEmployeeCached(report.ReportedUserId, employeeCache, resolveEmployee);
            var isOpen = IsOpenStatus(report);
            var isDismissed = !isOpen &&
                              string.Equals(report.Status, ReportedMessageStatus.Dismissed, StringComparison.OrdinalIgnoreCase);

            var statusKey = isOpen
                ? ReportedMessageStatus.Open
                : (isDismissed ? ReportedMessageStatus.Dismissed : ReportedMessageStatus.Resolved);

            var localizedResolutionAction = report.ResolutionAction switch
            {
                ReportedMessageResolution.Dismissed => LocalizeOrFallback(ReportedMessageResolution.Dismissed, localize),
                ReportedMessageResolution.DeletedMessage => LocalizeOrFallback(ReportedMessageResolution.DeletedMessage, localize),
                _ => report.ResolutionAction ?? string.Empty
            };

            var localizedSnapshot = report.SnapshotContent == ReportedMessageResolution.DeletedContentPlaceholder
                ? LocalizeOrFallback(ReportedMessageResolution.DeletedContentPlaceholder, localize)
                : (report.SnapshotContent ?? string.Empty);

            items.Add(new ModerationReportItem
            {
                Report = report,
                Reporter = reporter,
                ReporterName = ResolveDisplayName(reporter, fallbackUserLabel),
                ReportedUser = reportedUser,
                ReportedUserName = ResolveDisplayName(reportedUser, fallbackUserLabel),
                LocalizedReason = LocalizeOrFallback(report.Reason, localize),
                LocalizedStatus = LocalizeOrFallback(statusKey, localize),
                LocalizedResolutionAction = localizedResolutionAction,
                LocalizedSnapshotContent = localizedSnapshot,
                IsOpen = isOpen
            });
        }

        return items;
    }

    public static List<ModerationReportItem> Filter(
        IEnumerable<ModerationReportItem> items,
        string? searchQuery)
    {
        ArgumentNullException.ThrowIfNull(items);

        var trimmed = searchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return items.ToList();
        }

        return items.Where(item => Matches(item, trimmed)).ToList();
    }

    public static bool Matches(ModerationReportItem? item, string? searchQuery)
    {
        if (item == null) return false;

        var trimmed = searchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        // 1. Reporter & Reported User display names (excludes private Email fields to preserve IsEmailPublic privacy)
        if (ContainsIgnoreCase(item.ReporterName, trimmed) ||
            ContainsIgnoreCase(item.ReportedUserName, trimmed))
        {
            return true;
        }

        // 2. Canonical & Localized Reason
        if (ContainsIgnoreCase(item.Report.Reason, trimmed) ||
            ContainsIgnoreCase(item.LocalizedReason, trimmed))
        {
            return true;
        }

        // 3. Message Snapshot Content (canonical & localized placeholder)
        if (ContainsIgnoreCase(item.Report.SnapshotContent, trimmed) ||
            ContainsIgnoreCase(item.LocalizedSnapshotContent, trimmed))
        {
            return true;
        }

        // 4. Attachment Filenames
        if (item.Report.Attachments is { Count: > 0 } &&
            item.Report.Attachments.Any(att => att != null && ContainsIgnoreCase(att.FileName, trimmed)))
        {
            return true;
        }

        // 5. Status & Resolution Metadata (canonical & localized)
        if (ContainsIgnoreCase(item.Report.Status, trimmed) ||
            ContainsIgnoreCase(item.LocalizedStatus, trimmed) ||
            ContainsIgnoreCase(item.Report.ResolvedBy, trimmed) ||
            ContainsIgnoreCase(item.Report.ResolutionAction, trimmed) ||
            ContainsIgnoreCase(item.LocalizedResolutionAction, trimmed))
        {
            return true;
        }

        return false;
    }

    private static Employee? ResolveEmployeeCached(
        string? employeeId,
        Dictionary<string, Employee?> cache,
        Func<string?, Employee?> resolveEmployee)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
        {
            return null;
        }

        if (!cache.TryGetValue(employeeId, out var employee))
        {
            employee = resolveEmployee(employeeId);
            cache[employeeId] = employee;
        }

        return employee;
    }

    private static string ResolveDisplayName(Employee? employee, string fallbackLabel) =>
        !string.IsNullOrWhiteSpace(employee?.FullName)
            ? employee.FullName
            : fallbackLabel;

    private static string LocalizeOrFallback(string? rawValue, Func<string, string>? localize)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return string.Empty;
        }

        if (localize == null)
        {
            return rawValue;
        }

        var localized = localize(rawValue);
        return string.IsNullOrWhiteSpace(localized) ? rawValue : localized;
    }

    private static bool ContainsIgnoreCase(string? value, string query) =>
        !string.IsNullOrEmpty(value) &&
        value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
