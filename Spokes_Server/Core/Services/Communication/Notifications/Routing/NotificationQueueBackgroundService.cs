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
using Spokes_Server.Core.Models.Core;
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
            var queued = _queue.LeaseDueNotifications(userId, TimeSpan.FromMinutes(2));
            if (queued.Count == 0) continue;

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
                    var email = emailMessages.GetByIdOrLoad(item.UserId, item.MessageId);
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
