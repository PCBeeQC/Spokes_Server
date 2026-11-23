using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Email;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.HR;
using Spokes_Server.Core.Services.Logging;
using Spokes_Server.Core.Services.Storage;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

/// <summary>
/// Background service that periodically flushes queued notifications
/// for users whose schedule window has opened.
/// Runs every 60 seconds.
/// </summary>
public class NotificationQueueBackgroundService : BackgroundService
{
    private readonly NotificationQueueService _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<NotificationQueueBackgroundService> _logger;

    public NotificationQueueBackgroundService(
        NotificationQueueService queue,
        IServiceProvider serviceProvider,
        ILogger<NotificationQueueBackgroundService> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationQueueBackgroundService started");

        _queue.RestoreFromDisk();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _queue.PurgeExpired();
                await FlushDueNotificationsAsync();
                await FlushDelayedMobileNotificationsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error flushing notification queue");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task FlushDueNotificationsAsync()
    {
        var userIds = _queue.GetQueuedUserIds().ToList();
        if (userIds.Count == 0) return;

        // Resolve scoped services
        using var scope = _serviceProvider.CreateScope();
        var employees = scope.ServiceProvider.GetRequiredService<EmployeeRepository>();
        var webPush = scope.ServiceProvider.GetRequiredService<IWebPushService>();
        var validation = LiveNotificationValidationContext.Resolve(scope.ServiceProvider);

        foreach (var userId in userIds)
        {
            var employee = employees.GetById(userId);
            if (employee == null || !employee.IsActive || employee.IsBanned)
            {
                // User deleted, deactivated, or banned — discard all queued items
                _queue.PurgeForUser(userId);
                continue;
            }

            if (!_queue.IsWithinSchedule(employee))
                continue; // Still outside schedule

            // Schedule is now active — lease all due notifications
            var leased = _queue.LeaseDueNotifications(userId, TimeSpan.FromMinutes(2));
            if (leased.Count == 0) continue;

            // Prune items that were already read, expired, deleted, or whose ACL was revoked while queued
            var alreadyHandledIds = new List<Guid>();
            var queued = new List<QueuedNotification>(leased.Count);
            foreach (var item in leased)
            {
                if (IsQueuedItemAlreadyReadExpiredOrRevoked(item, employee, validation))
                {
                    alreadyHandledIds.Add(item.Id);
                }
                else
                {
                    queued.Add(item);
                }
            }

            if (alreadyHandledIds.Count > 0)
            {
                _queue.AcknowledgeDelivery(userId, alreadyHandledIds);
            }

            if (queued.Count == 0)
            {
                _logger.LogDebug("All {Count} queued notifications for user {UserId} were already read during quiet hours", alreadyHandledIds.Count, userId);
                continue;
            }

            _logger.LogInformation("Flushing {Count} queued notifications for user {UserId}", queued.Count, userId);

            if (queued.Count == 1)
            {
                var item = queued.First();
                try
                {
                    await webPush.SendNotificationDirectAsync(item.ToPushNotificationRequest());
                    _queue.AcknowledgeDelivery(userId, new[] { item.Id });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send queued notification to user {UserId}", userId);
                    _queue.RequeueOnFailure(userId, new[] { item.Id }, ex);
                }
            }
            else
            {
                int emailCount = queued.Count(q => q.Url != null && q.Url.StartsWith("/email", StringComparison.OrdinalIgnoreCase));
                int chatCount = queued.Count(q => q.Url != null && q.Url.StartsWith("/chat", StringComparison.OrdinalIgnoreCase));
                int otherCount = queued.Count - emailCount - chatCount;

                List<string> parts = [];
                if (emailCount > 0) parts.Add($"{emailCount} new email{(emailCount > 1 ? "s" : "")}");
                if (chatCount > 0) parts.Add($"{chatCount} new message{(chatCount > 1 ? "s" : "")}");
                if (otherCount > 0) parts.Add($"{otherCount} other notification{(otherCount > 1 ? "s" : "")}");

                string summaryBody = "You have " + string.Join(" and ", parts);
                string summaryTitle = "Quiet hours ended";
                string summaryUrl = "/";

                try
                {
                    await webPush.SendNotificationDirectAsync(new PushNotificationRequest
                    {
                        UserId = userId,
                        Title = summaryTitle,
                        Body = summaryBody,
                        Url = summaryUrl
                    });
                    _queue.AcknowledgeDelivery(userId, queued.Select(q => q.Id));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send summary notification to user {UserId}", userId);
                    _queue.RequeueOnFailure(userId, queued.Select(q => q.Id), ex);
                }
            }
        }
    }

    private sealed record LiveNotificationValidationContext(
        EmployeeRepository? Employees,
        ChatChannelRepository? Channels,
        IChatAuthorizationService? ChatAuth,
        ChatMessageRepository? Messages,
        ChatReadStateRepository? ReadStates,
        EmailMessageRepository? EmailMessages,
        IEmailInboxService? EmailInboxService,
        ReportedMessageRepository? ReportedMessages,
        CalendarEventRepository? CalendarEvents,
        TeamRepository? Teams)
    {
        public static LiveNotificationValidationContext Resolve(IServiceProvider sp) => new(
            sp.GetService<EmployeeRepository>(),
            sp.GetService<ChatChannelRepository>(),
            sp.GetService<IChatAuthorizationService>(),
            sp.GetService<ChatMessageRepository>(),
            sp.GetService<ChatReadStateRepository>(),
            sp.GetService<EmailMessageRepository>(),
            sp.GetService<IEmailInboxService>(),
            sp.GetService<ReportedMessageRepository>(),
            sp.GetService<CalendarEventRepository>(),
            sp.GetService<TeamRepository>());
    }

    private static bool IsQueuedItemAlreadyReadExpiredOrRevoked(
        QueuedNotification item,
        Employee employee,
        LiveNotificationValidationContext validation)
    {
        var sentAt = item.SentAtUtc == default ? item.QueuedAtUtc : item.SentAtUtc;
        return ShouldDropNotificationForLiveState(item.ToPushNotificationRequest(), sentAt, validation, employee);
    }

    private static bool ShouldDropNotificationForLiveState(
        PushNotificationRequest req,
        DateTime sentOrQueuedAtUtc,
        LiveNotificationValidationContext ctx,
        Employee? employee,
        string? delayedType = null,
        string? delayedMessageId = null,
        string? delayedChannelId = null,
        string? delayedAccountId = null)
    {
        if (ctx.Employees != null && (employee == null || !employee.IsActive || employee.IsBanned))
        {
            return true;
        }

        if (employee != null && (!employee.IsActive || employee.IsBanned || !employee.PushNotificationsEnabled))
        {
            return true;
        }

        if (string.Equals(req.Category, NotificationCategories.Call, StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow - sentOrQueuedAtUtc > TimeSpan.FromSeconds(PushPayloadBuilder.CallTtlSeconds))
            {
                return true;
            }
        }

        bool hasDelayedType = !string.IsNullOrEmpty(delayedType);
        bool isCalendar = hasDelayedType
            ? string.Equals(delayedType, "Calendar", StringComparison.OrdinalIgnoreCase)
            : (string.Equals(req.Category, NotificationCategories.Calendar, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(req.SourceEventId));
        if (isCalendar)
        {
            if (employee != null && !CalendarService.HasCalendarViewAccess(employee) && !CalendarService.HasCalendarAdminOverride(employee))
            {
                return true;
            }

            var eventId = ExtractCalendarEventId(req, delayedChannelId, delayedMessageId);
            if (ctx.CalendarEvents != null && !string.IsNullOrEmpty(eventId))
            {
                var evt = ctx.CalendarEvents.GetById(eventId);
                if (evt == null)
                {
                    return true;
                }

                if (employee != null)
                {
                    var teamIds = BuildEmployeeTeamIds(employee, ctx.Teams);
                    if (!CalendarService.CanUserViewEventCore(evt, employee, teamIds))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        bool isModeration = hasDelayedType
            ? string.Equals(delayedType, "Moderation", StringComparison.OrdinalIgnoreCase)
            : (string.Equals(req.Category, "moderation", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(req.SourceReportId));
        if (isModeration)
        {
            if (employee != null && !ModerationAuthorizationHelper.CanModerateAny(employee))
            {
                return true;
            }

            var reportId = ExtractModerationReportId(req, delayedMessageId);
            if (ctx.ReportedMessages != null && !string.IsNullOrEmpty(reportId))
            {
                var report = ctx.ReportedMessages.GetById(reportId);
                if (report == null || report.Status != ReportedMessageStatus.Open)
                {
                    return true;
                }

                if (employee != null)
                {
                    var channel = ModerationAuthorizationHelper.ResolveReportChannel(report, ctx.Channels, ctx.Messages);
                    if (!ModerationAuthorizationHelper.CanViewReport(employee, report, channel, ctx.ChatAuth))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        bool isEmail = hasDelayedType
            ? string.Equals(delayedType, "Email", StringComparison.OrdinalIgnoreCase)
            : (string.Equals(req.Category, NotificationCategories.Email, StringComparison.OrdinalIgnoreCase)
                || (req.Url != null && req.Url.StartsWith("/email", StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(req.SourceEmailMessageId));
        if (isEmail)
        {
            if (employee != null && (!employee.IsActive || employee.IsBanned || employee.IsSuspended || !FileAccessPermissionHelper.HasAssignedPermission(employee, AppPermissions.Email.Use)))
            {
                return true;
            }

            if (employee?.LastEmailVisitedAt.HasValue == true && sentOrQueuedAtUtc <= employee.LastEmailVisitedAt.Value.UtcDateTime)
            {
                return true;
            }

            var messageId = !string.IsNullOrEmpty(req.SourceEmailMessageId)
                ? req.SourceEmailMessageId
                : (!string.IsNullOrEmpty(delayedMessageId) ? delayedMessageId : ExtractUrlSegment(req.Url, 2));
            var accountId = !string.IsNullOrEmpty(req.SourceEmailAccountId)
                ? req.SourceEmailAccountId
                : (!string.IsNullOrEmpty(delayedAccountId)
                    ? delayedAccountId
                    : (!string.IsNullOrEmpty(delayedChannelId) && delayedChannelId != "email" ? delayedChannelId : req.UserId));

            if (ctx.EmailMessages != null && !string.IsNullOrEmpty(messageId))
            {
                var email = (!string.IsNullOrEmpty(accountId) ? ctx.EmailMessages.GetById(accountId, messageId) : null)
                    ?? (employee != null ? ctx.EmailMessages.GetById(employee.Id, messageId) : null)
                    ?? ctx.EmailMessages.GetById(messageId);

                if (email == null || email.IsRead)
                {
                    return true;
                }

                var effectiveAccountId = !string.IsNullOrEmpty(email.AccountId) ? email.AccountId : accountId;
                if (ctx.EmailInboxService != null && employee != null && !string.IsNullOrEmpty(effectiveAccountId)
                    && !ctx.EmailInboxService.CanUserAccessEmailMessage(employee, effectiveAccountId, email.Id))
                {
                    return true;
                }
            }
            else if (!string.IsNullOrEmpty(delayedType))
            {
                return true;
            }

            return false;
        }

        bool isChat = hasDelayedType
            ? string.Equals(delayedType, "Chat", StringComparison.OrdinalIgnoreCase)
            : (string.Equals(req.Category, NotificationCategories.Chat, StringComparison.OrdinalIgnoreCase)
                || (req.Url != null && req.Url.StartsWith("/chat", StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(req.SourceChannelId));
        if (isChat)
        {
            if (employee != null && (!employee.IsActive || employee.IsBanned || !employee.HasEffectivePermission(AppPermissions.Chat.Use, allowSuspended: true)))
            {
                return true;
            }

            var channelId = !string.IsNullOrEmpty(req.SourceChannelId)
                ? req.SourceChannelId
                : (!string.IsNullOrEmpty(delayedChannelId)
                    ? delayedChannelId
                    : (!string.IsNullOrEmpty(req.ThreadId) ? req.ThreadId : ExtractUrlSegment(req.Url, 1)));
            var messageId = !string.IsNullOrEmpty(req.SourceMessageId) ? req.SourceMessageId : delayedMessageId;

            if (ctx.Channels != null && !string.IsNullOrEmpty(channelId))
            {
                var channel = ctx.Channels.GetById(channelId);
                if (channel == null || channel.IsArchived)
                {
                    return true;
                }

                if (employee != null && ctx.ChatAuth != null && !ctx.ChatAuth.CanUserAccessChannel(channel, employee))
                {
                    return true;
                }
            }

            if (ctx.Messages != null && !string.IsNullOrEmpty(channelId) && !string.IsNullOrEmpty(messageId))
            {
                var msg = ctx.Messages.GetByIdOrLoad(channelId, messageId);
                if (msg == null || msg.IsDeleted)
                {
                    return true;
                }

                if (ctx.ReadStates != null)
                {
                    var lastReadAt = ctx.ReadStates.GetLastReadAt(req.UserId, channelId);
                    if (msg.SentAt <= lastReadAt)
                    {
                        return true;
                    }
                }
            }
            else if (ctx.ReadStates != null && !string.IsNullOrEmpty(channelId))
            {
                var lastReadAt = ctx.ReadStates.GetLastReadAt(req.UserId, channelId);
                if (lastReadAt > DateTime.MinValue && sentOrQueuedAtUtc <= lastReadAt)
                {
                    return true;
                }
            }
            else if (!string.IsNullOrEmpty(delayedType) && (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(messageId)))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractUrlSegment(string? url, int index)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > index ? segments[index] : null;
    }

    private static string? ExtractCalendarEventId(PushNotificationRequest req, string? delayedChannelId, string? delayedMessageId)
    {
        if (!string.IsNullOrEmpty(req.SourceEventId)) return req.SourceEventId;
        if (!string.IsNullOrEmpty(delayedChannelId) && !delayedChannelId.StartsWith('/')) return delayedChannelId;
        if (!string.IsNullOrEmpty(delayedMessageId) && !delayedMessageId.StartsWith("calendar_", StringComparison.OrdinalIgnoreCase)) return delayedMessageId;

        var tag = req.Tag ?? req.ThreadId ?? delayedMessageId;
        if (!string.IsNullOrEmpty(tag) && tag.StartsWith("calendar_", StringComparison.OrdinalIgnoreCase))
        {
            var lastUnderscore = tag.LastIndexOf('_');
            if (lastUnderscore > "calendar_".Length)
            {
                return tag.Substring("calendar_".Length, lastUnderscore - "calendar_".Length);
            }
        }

        return null;
    }

    private static string? ExtractModerationReportId(PushNotificationRequest req, string? delayedMessageId)
    {
        if (!string.IsNullOrEmpty(req.SourceReportId)) return req.SourceReportId;
        if (!string.IsNullOrEmpty(delayedMessageId)) return delayedMessageId;

        var tag = req.Tag ?? req.ThreadId;
        if (!string.IsNullOrEmpty(tag) && tag.StartsWith("mod-", StringComparison.OrdinalIgnoreCase))
        {
            return tag.Substring("mod-".Length);
        }

        return null;
    }

    private static HashSet<string> BuildEmployeeTeamIds(Employee employee, TeamRepository? teams)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(employee.TeamId))
        {
            set.Add(employee.TeamId);
        }
        if (teams != null && !string.IsNullOrEmpty(employee.Id))
        {
            foreach (var team in teams.GetAll().Where(t => string.Equals(t.LeaderId, employee.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(team.Id))
                {
                    set.Add(team.Id);
                }
            }
        }
        return set;
    }

    private async Task FlushDelayedMobileNotificationsAsync()
    {
        var matured = _queue.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(2));
        if (matured.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var webPush = scope.ServiceProvider.GetRequiredService<IWebPushService>();
        var presenceState = scope.ServiceProvider.GetService<IPushDecisionService>()
            ?? (IPushDecisionService)scope.ServiceProvider.GetRequiredService<PresenceStateService>();
        var systemLog = scope.ServiceProvider.GetRequiredService<ISystemLogService>();
        var notificationRouting = scope.ServiceProvider.GetRequiredService<INotificationRoutingService>();
        var validation = LiveNotificationValidationContext.Resolve(scope.ServiceProvider);

        foreach (var item in matured)
        {
            try
            {
                var employee = validation.Employees?.GetById(item.UserId);
                var request = item.ToPushNotificationRequest();
                var sentAt = item.SentAtUtc == default ? item.QueuedAtUtc : item.SentAtUtc;

                // 1. Validate live user, existence, unread state, and ACL before releasing delayed push
                if (ShouldDropNotificationForLiveState(
                    request,
                    sentAt,
                    validation,
                    employee,
                    item.Type,
                    item.MessageId,
                    item.ChannelId,
                    item.AccountId))
                {
                    if (validation.Employees != null && (employee == null || !employee.IsActive || employee.IsBanned))
                    {
                        _queue.PurgeForUser(item.UserId);
                    }
                    _queue.AcknowledgeMobilePushDelivery(item.Id);
                    continue;
                }

                // 2. Are they currently actively looking at a desktop connection? 
                if (presenceState.ShouldSuppressMobilePush(item.UserId))
                {
                    // User is actively active on desktop, but has not read or cleared the message.
                    // Do not permanently discard the withheld notification; postpone it so mobile receives it if desktop goes idle.
                    if (_queue.PostponeDelayedMobilePush(item, TimeSpan.FromMinutes(1)))
                    {
                        _logger.LogInformation("Postponed delayed mobile push for active desktop user {UserId}, Type {Type}, Msg {MsgId}", item.UserId, item.Type, item.MessageId);
                    }
                    else
                    {
                        _logger.LogInformation("Delayed mobile push expired for user {UserId}, Type {Type}, Msg {MsgId} after maximum delay window", item.UserId, item.Type, item.MessageId);
                        _queue.AcknowledgeMobilePushDelivery(item.Id);
                    }
                    continue; // They are active on desktop right now, postponed instead of discarding
                }

                // 3. Recalculate badge count fresh (enqueued value may be stale after 1-minute delay)
                var freshBadge = notificationRouting.GetTotalBadgeCount(item.UserId);

                // 4. Send mobile push!
                _logger.LogInformation("Flushing delayed mobile push for user {UserId}, Type {Type}, Msg {MsgId}", item.UserId, item.Type, item.MessageId);
                await webPush.SendNotificationDirectAsync(item.ToPushNotificationRequest(freshBadge));
                _queue.AcknowledgeMobilePushDelivery(item.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process mobile notification queue item for user {UserId}", item.UserId);
                systemLog.LogError("Notifications", $"Failed to process mobile notification queue item for user {item.UserId} (Error occurred before delivery attempt)", ex.ToString());
                _queue.RequeueMobilePushOnFailure(item.Id, ex);
            }
        }
    }
}
