using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Services.Communication.Notifications;

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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

            // Schedule is now active — flush all queued notifications
            var queued = _queue.DequeueAll(userId);
            if (queued.Count == 0) continue;

            _logger.LogInformation("Flushing {Count} queued notifications for user {UserId}", queued.Count, userId);

            if (queued.Count == 1)
            {
                var item = queued.First();
                try
                {
                    await webPush.SendNotificationDirectAsync(userId, item.Title, item.Body, item.Url, item.Icon, item.Tier, item.Tag, item.Actions, "chat", item.ThreadId, item.ServerName, item.ChannelName, item.IsGroupChat, item.Badge);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send queued notification to user {UserId}", userId);
                }
            }
            else
            {
                int emailCount = queued.Count(q => q.Url != null && q.Url.StartsWith("/email", StringComparison.OrdinalIgnoreCase));
                int chatCount = queued.Count(q => q.Url != null && q.Url.StartsWith("/chat", StringComparison.OrdinalIgnoreCase));
                int otherCount = queued.Count - emailCount - chatCount;

                var parts = new List<string>();
                if (emailCount > 0) parts.Add($"{emailCount} new email{(emailCount > 1 ? "s" : "")}");
                if (chatCount > 0) parts.Add($"{chatCount} new message{(chatCount > 1 ? "s" : "")}");
                if (otherCount > 0) parts.Add($"{otherCount} other notification{(otherCount > 1 ? "s" : "")}");

                string summaryBody = "You have " + string.Join(" and ", parts);
                string summaryTitle = "Quiet hours ended";
                string summaryUrl = "/";

                try
                {
                    await webPush.SendNotificationDirectAsync(userId, summaryTitle, summaryBody, summaryUrl, null, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send summary notification to user {UserId}", userId);
                }
            }
        }
    }

    private async Task FlushDelayedMobileNotificationsAsync()
    {
        var matured = _queue.DequeueMaturedMobilePushes();
        if (matured.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var webPush = scope.ServiceProvider.GetRequiredService<IWebPushService>();
        var readStates = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Data.Repositories.Communication.ChatReadStateRepository>();
        var presence = scope.ServiceProvider.GetRequiredService<PresenceStateService>();
        var messages = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Data.Repositories.Communication.ChatMessageRepository>();
        var emailMessages = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Data.Repositories.Communication.EmailMessageRepository>();
        var systemLog = scope.ServiceProvider.GetRequiredService<Spokes_Server.Core.Services.Logging.ISystemLogService>();

        foreach (var item in matured)
        {
            try
            {
                // 1. Is it still unread?
                if (item.Type == "Chat")
                {
                    var lastReadAt = readStates.GetLastReadAt(item.UserId, item.ChannelId);
                    var message = messages.GetById(item.MessageId);

                    if (message == null || message.IsDeleted || message.SentAt <= lastReadAt)
                    {
                        continue; // Message was read or deleted
                    }
                }
                else if (item.Type == "Email")
                {
                    var email = emailMessages.GetByIdOrLoad(item.MessageId, item.UserId);
                    if (email == null || email.IsRead)
                    {
                        continue; // Email was read or deleted
                    }
                }

                // 2. Are they currently actively looking at a desktop connection? 
                if (presence.HasActiveDesktopConnectionForPush(item.UserId) && !presence.IsUserAwayFromDesktopForPush(item.UserId))
                {
                    continue; // They are active on desktop right now, don't buzz the phone
                }

                // 3. Send mobile push!
                _logger.LogInformation("Flushing delayed mobile push for user {UserId}, Type {Type}, Msg {MsgId}", item.UserId, item.Type, item.MessageId);
                await webPush.SendNotificationDirectAsync(item.UserId, item.Title, item.Body, item.Url, item.Icon, Spokes_Server.Core.Models.Core.PresenceTier.MobileOnly, item.Tag, item.Actions, "chat", item.ChannelId, item.ServerName, item.ChannelName, item.IsGroupChat, item.Badge, item.IsSilent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process mobile notification queue item for user {UserId}", item.UserId);
                systemLog.LogError("Notifications", $"Failed to process mobile notification queue item for user {item.UserId} (Error occurred before delivery attempt)", ex.ToString());
            }
        }
    }
}
