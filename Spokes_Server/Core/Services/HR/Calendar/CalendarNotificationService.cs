using System;
using System.Threading.Tasks;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Communication.Notifications.WebPush;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Core.Services.Communication.Presence;

namespace Spokes_Server.Core.Services.HR.Calendar;

/// <summary>
/// Scoped service providing real-time calendar alert notifications within a user session.
/// Plays notification audio and delegates presentation of alerts to IUiNotificationPresenter.
/// </summary>
public class CalendarNotificationService : IAsyncDisposable
{
    private readonly ISoundService _soundService;
    private readonly IUiNotificationPresenter _presenter;
    private readonly CalendarReminderStateService _reminderState;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly UserCircuitContext? _circuitContext;
    private readonly PresenceStateService? _presenceState;
    private readonly IWebPushService? _webPush;
    private readonly EmployeeRepository? _employeeRepo;
    private string? _currentUserId;

    public int TotalUnreadCount => 0;
    public event Action? OnUnreadCountChanged;

    public CalendarNotificationService(
        IUiNotificationPresenter presenter,
        ISoundService soundService,
        CalendarReminderStateService reminderState,
        CompanyProfileRepository companyProfile,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null,
        IWebPushService? webPush = null,
        EmployeeRepository? employeeRepo = null)
    {
        _presenter = presenter ?? NullUiNotificationPresenter.Instance;
        _soundService = soundService;
        _reminderState = reminderState;
        _companyProfile = companyProfile;
        _circuitContext = circuitContext;
        _presenceState = presenceState;
        _webPush = webPush;
        _employeeRepo = employeeRepo;
    }

    private bool ShouldDisplayInAppNotification()
    {
        if (_circuitContext == null) return true;

        // When web push is available on the server but unconfigured, never suppress in-app notifications
        // because no background push notification will ever be delivered.
        if (_webPush != null && !_webPush.IsConfigured)
        {
            return true;
        }

        return _circuitContext.ShouldDisplayInAppNotification(_presenceState);
    }

    public Task InitializeAsync(string userId)
    {
        if (_currentUserId != null) return Task.CompletedTask;

        _currentUserId = userId;
        _reminderState.OnReminderTriggered += HandleReminderTriggered;

        return Task.CompletedTask;
    }

    private void HandleReminderTriggered(string userId, CalendarEvent evt, CalendarReminder reminder)
    {
        if (string.IsNullOrEmpty(_currentUserId) || !string.Equals(userId, _currentUserId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!ShouldDisplayInAppNotification())
            return;

        try
        {
            // 1. Play the calendar notification sound using the recipient user's configured sound choice
            var soundChoice = _employeeRepo?.GetById(userId)?.GetNotificationSound(NotificationCategories.Calendar);
            _ = _soundService.PlayNotificationSoundAsync(NotificationCategories.Calendar, soundChoice);

            // 2. Format timing description and branding
            var timeDesc = FormatTimeDescription(evt, reminder);
            var profile = _companyProfile.Get();
            var serverName = profile?.CompanyName ?? "Spokes";
            var serverIconUrl = !string.IsNullOrEmpty(profile?.IconBase64) || !string.IsNullOrEmpty(profile?.LogoBase64)
                ? $"/spokesapi/Media/Icon?v={profile?.IconVersion ?? 1}"
                : null;

            // 3. Show alert snackbar
            _presenter.ShowCalendarNotification(new CalendarNotificationModel(
                string.IsNullOrWhiteSpace(evt.Title) ? "Untitled Event" : evt.Title,
                timeDesc,
                evt.Location ?? "",
                evt.Id,
                serverName,
                serverIconUrl));

            OnUnreadCountChanged?.Invoke();
        }
        catch
        {
            // Safeguard against any unexpected exception so the reminder dispatch loop continues
        }
    }

    public static string FormatTimeDescription(CalendarEvent evt, CalendarReminder reminder)
    {
        var timeSpanStr = evt.IsAllDay ? "All Day" : $"{evt.Start:t} – {evt.End:t}";
        if (reminder.Value == 0)
        {
            return evt.IsAllDay ? "Today (All Day)" : $"Starts now ({timeSpanStr})";
        }

        var reminderDisplay = reminder.ToDurationString();
        if (evt.IsAllDay)
        {
            return $"Starts in {reminderDisplay} ({evt.Start:MMM d})";
        }

        return $"Starts in {reminderDisplay} ({timeSpanStr})";
    }

    public ValueTask DisposeAsync()
    {
        _reminderState.OnReminderTriggered -= HandleReminderTriggered;
        return ValueTask.CompletedTask;
    }
}
