using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate.Contexts;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;

namespace Spokes_Server.Core.Services.HR.Calendar;

/// <summary>
/// Hosted background service that periodically checks for upcoming calendar events with due reminders,
/// dispatching audio alerts, snackbars, and push notifications to eligible participants.
/// </summary>
public class CalendarReminderBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CalendarReminderStateService _reminderState;
    private readonly ILogger<CalendarReminderBackgroundService> _logger;
    private DateTime _lastCleanupUtc = DateTime.UtcNow;

    public CalendarReminderBackgroundService(
        IServiceProvider serviceProvider,
        CalendarReminderStateService reminderState,
        ILogger<CalendarReminderBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _reminderState = reminderState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CalendarReminderBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckDueRemindersAsync();

                if (DateTime.UtcNow - _lastCleanupUtc > TimeSpan.FromHours(1))
                {
                    _reminderState.CleanupOldEntries(TimeSpan.FromHours(24));
                    _lastCleanupUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing calendar reminders");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal Task CheckDueRemindersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var hrContext = scope.ServiceProvider.GetRequiredService<HrContext>();
        var companyProfileRepo = scope.ServiceProvider.GetRequiredService<CompanyProfileRepository>();
        var webPush = scope.ServiceProvider.GetService<IWebPushService>();
        var pushDecision = scope.ServiceProvider.GetService<IPushDecisionService>();

        var notificationQueue = scope.ServiceProvider.GetService<NotificationQueueService>();

        var now = GetCurrentCalendarTime(companyProfileRepo);
        var minDate = now.Date.AddDays(-1);
        var maxDate = now.Date.AddDays(60);

        var events = hrContext.CalendarEvents.GetAll()
            .Where(e => e.End >= minDate && e.Start <= maxDate)
            .ToList();

        if (events.Count == 0) return Task.CompletedTask;

        var employees = hrContext.Employees.GetAll()
            .Where(e => e.IsActive)
            .ToDictionary(e => e.Id, e => e);

        foreach (var evt in events)
        {
            var recipientIds = ResolveRecipients(evt, employees.Values);

            foreach (var userId in recipientIds)
            {
                if (!employees.TryGetValue(userId, out var employee))
                    continue;

                var reminders = evt.GetEffectiveRemindersForUser(userId);
                if (reminders.Count == 0) continue;

                foreach (var reminder in reminders)
                {
                    ProcessDueReminder(evt, reminder, userId, employee, companyProfileRepo, webPush, pushDecision, notificationQueue, now);
                }
            }
        }

        return Task.CompletedTask;
    }

    private void ProcessDueReminder(
        CalendarEvent evt,
        CalendarReminder reminder,
        string userId,
        Employee employee,
        CompanyProfileRepository companyProfileRepo,
        IWebPushService? webPush,
        IPushDecisionService? pushDecision,
        NotificationQueueService? notificationQueue,
        DateTime now)
    {
        var triggerTime = CalculateTriggerTime(evt, reminder);
        if (triggerTime > now || triggerTime <= now.AddMinutes(-30))
            return;

        if (!_reminderState.TryMarkDispatched(evt.Id, reminder.Id, userId, evt.Start.Ticks))
            return;

        _logger.LogInformation("Triggering calendar reminder {ReminderId} for user {UserId} on event {EventId} ('{Title}')",
            reminder.Id, userId, evt.Id, evt.Title);

        _reminderState.TriggerReminder(userId, evt, reminder);

        DispatchWebPushReminder(evt, reminder, userId, employee, companyProfileRepo, webPush, pushDecision, notificationQueue);
    }

    private void DispatchWebPushReminder(
        CalendarEvent evt,
        CalendarReminder reminder,
        string userId,
        Employee employee,
        CompanyProfileRepository companyProfileRepo,
        IWebPushService? webPush,
        IPushDecisionService? pushDecision,
        NotificationQueueService? notificationQueue = null)
    {
        if (webPush == null || !webPush.IsConfigured)
            return;

        try
        {
            var tier = pushDecision?.DetermineNotificationTier(userId) ?? PresenceTier.All;
            if (tier == PresenceTier.None)
                return;

            var companyProfile = companyProfileRepo.Get();
            var serverName = companyProfile?.CompanyName ?? "Spokes";
            var reminderTag = $"calendar_{evt.Id}_{reminder.Id}";
            var body = FormatReminderTimeDescription(evt, reminder);

            var userSoundChoice = employee.GetNotificationSound(NotificationCategories.Calendar);
            var sound = NotificationSoundCatalog.GetEffectivePushSound(NotificationCategories.Calendar, userSoundChoice);
            var calendarIconUrl = "/icons/calendar-notification.png";

            var actions = new object[]
            {
                new { action = "open", title = "View Calendar" },
                new { action = "close", title = "Dismiss" }
            };

            var request = new PushNotificationRequest
            {
                UserId = userId,
                Title = string.IsNullOrWhiteSpace(evt.Title) ? "Calendar Reminder" : evt.Title,
                Body = body,
                Url = "/planning/calendar",
                Icon = calendarIconUrl,
                Tier = tier,
                Tag = reminderTag,
                Actions = actions,
                Category = NotificationCategories.Calendar,
                ThreadId = reminderTag,
                ServerName = serverName,
                Sound = sound
            };

            SafeDispatchNotificationAsync(() => webPush.SendNotificationDirectAsync(request), "calendar reminder web push");

            if (tier == PresenceTier.DesktopOnly && notificationQueue != null)
            {
                notificationQueue.EnqueueDelayedMobilePush(new DelayedMobileNotification(request, reminderTag, "/planning/calendar", "Calendar", TimeSpan.FromMinutes(1)));
            }
        }
        catch (Exception pushEx)
        {
            _logger.LogDebug("Failed to dispatch web push for calendar reminder: {Message}", pushEx.Message);
        }
    }

    private static HashSet<string> ResolveRecipients(CalendarEvent evt, IEnumerable<Employee> activeEmployees)
    {
        var recipientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(evt.EmployeeId))
        {
            recipientIds.Add(evt.EmployeeId);
        }

        if (evt.Attendees != null)
        {
            foreach (var attId in evt.Attendees)
            {
                if (!string.IsNullOrEmpty(attId))
                    recipientIds.Add(attId);
            }
        }

        if (evt.InvitedTeamIds != null && evt.InvitedTeamIds.Count > 0)
        {
            var invitedTeams = new HashSet<string>(evt.InvitedTeamIds, StringComparer.OrdinalIgnoreCase);
            foreach (var emp in activeEmployees)
            {
                if (!string.IsNullOrEmpty(emp.TeamId) && invitedTeams.Contains(emp.TeamId))
                {
                    recipientIds.Add(emp.Id);
                }
            }
        }

        if (evt.IsCompanyWide)
        {
            foreach (var emp in activeEmployees)
            {
                recipientIds.Add(emp.Id);
            }
        }

        return recipientIds;
    }

    public static string FormatReminderTimeDescription(CalendarEvent evt, CalendarReminder reminder)
    {
        string timingStr;
        if (reminder.Value == 0)
        {
            timingStr = evt.IsAllDay ? "Today (All Day)" : $"Starts now ({evt.Start:t} – {evt.End:t})";
        }
        else
        {
            var reminderDisplay = reminder.ToDurationString();
            if (evt.IsAllDay)
            {
                timingStr = $"Starts in {reminderDisplay} ({evt.Start:MMM d})";
            }
            else
            {
                timingStr = $"Starts in {reminderDisplay} ({evt.Start:t} – {evt.End:t})";
            }
        }

        if (!string.IsNullOrWhiteSpace(evt.Location))
        {
            timingStr += $" • 📍 {evt.Location.Trim()}";
        }

        return timingStr;
    }

    public static DateTime CalculateTriggerTime(CalendarEvent evt, CalendarReminder reminder)
    {
        var offset = reminder.ToTimeSpan();
        if (evt.IsAllDay)
        {
            // For all-day events, reference 9:00 AM on the start date
            var baseTime = evt.Start.Date.AddHours(9);
            return baseTime - offset;
        }

        return evt.Start - offset;
    }

    private static DateTime GetCurrentCalendarTime(CompanyProfileRepository companyProfileRepo)
    {
        return TimeZoneHelper.GetCompanyTime(companyProfileRepo);
    }

    internal Task SafeDispatchNotificationAsync(Func<Task> routeAction, string operation)
    {
        return Task.Run(async () =>
        {
            try
            {
                await routeAction();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch {Operation} in background", operation);
            }
        });
    }
}
