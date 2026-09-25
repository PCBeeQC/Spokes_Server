using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications;

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

    private async Task CheckDueRemindersAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Database>();
        var webPush = scope.ServiceProvider.GetService<IWebPushService>();

        var now = GetCurrentCalendarTime(db.CompanyProfile);
        var minDate = now.Date.AddDays(-1);
        var maxDate = now.Date.AddDays(60);

        var events = db.CalendarEvents.GetAll()
            .Where(e => e.End >= minDate && e.Start <= maxDate)
            .ToList();

        if (events.Count == 0) return;

        var employees = db.Employees.GetAll()
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
                    var triggerTime = CalculateTriggerTime(evt, reminder);

                    // If reminder trigger time is right now (within a 10-minute grace window for server restarts/delays)
                    if (triggerTime <= now && triggerTime > now.AddMinutes(-10))
                    {
                        if (_reminderState.TryMarkDispatched(evt.Id, reminder.Id, userId, evt.Start.Ticks))
                        {
                            _logger.LogInformation("Triggering calendar reminder {ReminderId} for user {UserId} on event {EventId} ('{Title}')",
                                reminder.Id, userId, evt.Id, evt.Title);

                            _reminderState.TriggerReminder(userId, evt, reminder);

                            // Optional web push if user is away/offline
                            if (webPush != null && webPush.IsConfigured)
                            {
                                try
                                {
                                    var tier = webPush.DetermineNotificationTier(userId);
                                    if (tier != PresenceTier.None)
                                    {
                                        var companyProfile = db.CompanyProfile.Get();
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

                                        _ = webPush.SendNotificationDirectAsync(
                                            userId: userId,
                                            title: string.IsNullOrWhiteSpace(evt.Title) ? "Calendar Reminder" : evt.Title,
                                            body: body,
                                            url: "/planning/calendar",
                                            icon: calendarIconUrl,
                                            tier: tier,
                                            tag: reminderTag,
                                            actions: actions,
                                            category: NotificationCategories.Calendar,
                                            threadId: reminderTag,
                                            serverName: serverName,
                                            sound: sound);
                                    }
                                }
                                catch (Exception pushEx)
                                {
                                    _logger.LogDebug("Failed to dispatch web push for calendar reminder: {Message}", pushEx.Message);
                                }
                            }
                        }
                    }
                }
            }
        }

        await Task.CompletedTask;
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
        var tzId = companyProfileRepo.Get()?.TimeZoneId;
        if (!string.IsNullOrWhiteSpace(tzId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                // Fallback to local server time
            }
        }
        return DateTime.Now;
    }
}
