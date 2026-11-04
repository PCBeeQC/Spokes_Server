using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.Logging;

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
        var readStates = scope.ServiceProvider.GetService<ChatReadStateRepository>();
        var emailMessages = scope.ServiceProvider.GetService<EmailMessageRepository>();

        foreach (var userId in userIds)
        {
            var employee = employees.GetById(userId);
            if (employee == null)
            {
                // User deleted — discard their queue
                _queue.DequeueAll(userId);
                continue;
            }

            if (!_queue.IsWithinSchedule(employee))
                continue; // Still outside schedule

            // Schedule is now active — lease all due notifications
            var leased = _queue.LeaseDueNotifications(userId, TimeSpan.FromMinutes(2));
            if (leased.Count == 0) continue;

            // Prune items that were already read or expired while queued during quiet hours
            var alreadyHandledIds = new List<Guid>();
            var queued = new List<QueuedNotification>(leased.Count);
            foreach (var item in leased)
            {
                if (IsQueuedItemAlreadyReadOrExpired(item, employee, readStates, emailMessages))
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

    private static bool IsQueuedItemAlreadyReadOrExpired(
        QueuedNotification item,
        Employee employee,
        ChatReadStateRepository? readStates,
        EmailMessageRepository? emailMessages)
    {
        if (string.Equals(item.Category, NotificationCategories.Call, StringComparison.OrdinalIgnoreCase))
        {
            var sentAt = item.SentAtUtc == default ? item.QueuedAtUtc : item.SentAtUtc;
            if (DateTime.UtcNow - sentAt > TimeSpan.FromSeconds(PushPayloadBuilder.CallTtlSeconds))
            {
                return true;
            }
        }

        bool isEmail = string.Equals(item.Category, NotificationCategories.Email, StringComparison.OrdinalIgnoreCase) ||
                       (item.Url != null && item.Url.StartsWith("/email", StringComparison.OrdinalIgnoreCase));
        if (isEmail)
        {
            var sentAt = item.SentAtUtc == default ? item.QueuedAtUtc : item.SentAtUtc;
            if (employee.LastEmailVisitedAt.HasValue && sentAt <= employee.LastEmailVisitedAt.Value.UtcDateTime)
            {
                return true;
            }

            if (emailMessages != null && !string.IsNullOrEmpty(item.Url))
            {
                var segments = item.Url.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 3 && string.Equals(segments[0], "email", StringComparison.OrdinalIgnoreCase))
                {
                    var messageId = segments[2];
                    var email = emailMessages.GetByIdOrLoad(employee.Id, messageId) ?? emailMessages.GetById(messageId);
                    if (email != null && email.IsRead)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        bool isChat = string.Equals(item.Category, NotificationCategories.Chat, StringComparison.OrdinalIgnoreCase) ||
                      (item.Url != null && item.Url.StartsWith("/chat", StringComparison.OrdinalIgnoreCase));
        if (isChat && readStates != null)
        {
            string? channelId = !string.IsNullOrEmpty(item.ThreadId) ? item.ThreadId : null;
            if (string.IsNullOrEmpty(channelId) && !string.IsNullOrEmpty(item.Url))
            {
                var segments = item.Url.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 && string.Equals(segments[0], "chat", StringComparison.OrdinalIgnoreCase))
                {
                    channelId = segments[1];
                }
            }

            if (!string.IsNullOrEmpty(channelId))
            {
                var lastReadAt = readStates.GetLastReadAt(employee.Id, channelId);
                var sentAt = item.SentAtUtc == default ? item.QueuedAtUtc : item.SentAtUtc;
                if (lastReadAt > DateTime.MinValue && sentAt <= lastReadAt)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task FlushDelayedMobileNotificationsAsync()
    {
        var matured = _queue.LeaseMaturedMobilePushes(TimeSpan.FromMinutes(2));
        if (matured.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var webPush = scope.ServiceProvider.GetRequiredService<IWebPushService>();
        var readStates = scope.ServiceProvider.GetRequiredService<ChatReadStateRepository>();
        var presenceState = scope.ServiceProvider.GetService<IPushDecisionService>()
            ?? (IPushDecisionService)scope.ServiceProvider.GetRequiredService<PresenceStateService>();
        var messages = scope.ServiceProvider.GetRequiredService<ChatMessageRepository>();
        var emailMessages = scope.ServiceProvider.GetRequiredService<EmailMessageRepository>();
        var systemLog = scope.ServiceProvider.GetRequiredService<ISystemLogService>();
        var notificationRouting = scope.ServiceProvider.GetRequiredService<INotificationRoutingService>();

        foreach (var item in matured)
        {
            try
            {
                // 1. Is it still unread?
                if (item.Type == "Chat")
                {
                    var lastReadAt = readStates.GetLastReadAt(item.UserId, item.ChannelId);
                    var message = messages.GetByIdOrLoad(item.ChannelId, item.MessageId);

                    if (message == null || message.IsDeleted || message.SentAt <= lastReadAt)
                    {
                        _queue.AcknowledgeMobilePushDelivery(item.Id);
                        continue; // Message was read or deleted
                    }
                }
                else if (item.Type == "Email")
                {
                    var accountId = !string.IsNullOrEmpty(item.ChannelId) && item.ChannelId != "email" ? item.ChannelId : item.UserId;
                    var email = emailMessages.GetByIdOrLoad(accountId, item.MessageId) ?? emailMessages.GetById(item.MessageId);
                    if (email == null || email.IsRead)
                    {
                        _queue.AcknowledgeMobilePushDelivery(item.Id);
                        continue; // Email was read or deleted
                    }
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
