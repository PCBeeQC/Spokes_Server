using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Communication.Presence;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Core.Services.Communication.Notifications;

/// <summary>
/// Scoped service providing real-time calendar alert notifications within a user session.
/// Plays notification audio and displays snackbars when an upcoming event reminder triggers.
/// </summary>
public class CalendarNotificationService : IUserNotificationService, IAsyncDisposable
{
    private readonly ISnackbar _snackbar;
    private readonly NavigationManager _nav;
    private readonly ISoundService _soundService;
    private readonly CalendarReminderStateService _reminderState;
    private readonly CompanyProfileRepository _companyProfile;
    private readonly UserCircuitContext? _circuitContext;
    private readonly PresenceStateService? _presenceState;
    private string? _currentUserId;

    public int TotalUnreadCount => 0;
    public event Action? OnUnreadCountChanged;

    public CalendarNotificationService(
        ISnackbar snackbar,
        NavigationManager nav,
        ISoundService soundService,
        CalendarReminderStateService reminderState,
        CompanyProfileRepository companyProfile,
        UserCircuitContext? circuitContext = null,
        PresenceStateService? presenceState = null)
    {
        _snackbar = snackbar;
        _nav = nav;
        _soundService = soundService;
        _reminderState = reminderState;
        _companyProfile = companyProfile;
        _circuitContext = circuitContext;
        _presenceState = presenceState;
    }

    private bool ShouldDisplayInAppNotification()
    {
        if (_circuitContext == null) return true;
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

        // 1. Play the calendar notification sound
        _ = _soundService.PlayNotificationSoundAsync(NotificationCategories.Calendar);

        // 2. Format timing description and branding
        var timeDesc = FormatTimeDescription(evt, reminder);
        var profile = _companyProfile.Get();
        var serverName = profile?.CompanyName ?? "Spokes";
        var serverIconUrl = !string.IsNullOrEmpty(profile?.IconBase64) || !string.IsNullOrEmpty(profile?.LogoBase64)
            ? $"/spokesapi/Media/Icon?v={profile?.IconVersion ?? 1}"
            : null;

        // 3. Show alert snackbar
        _snackbar.Add<CalendarNotificationContent>(
            new Dictionary<string, object>
            {
                { "Title", string.IsNullOrWhiteSpace(evt.Title) ? "Untitled Event" : evt.Title },
                { "TimeDescription", timeDesc },
                { "Location", evt.Location ?? "" },
                { "EventId", evt.Id },
                { "ServerName", serverName },
                { "ServerIconUrl", serverIconUrl ?? "" }
            },
            Severity.Normal,
            config =>
            {
                config.VisibleStateDuration = 10000;
                config.ShowCloseIcon = true;
                config.HideIcon = true;
                config.SnackbarVariant = Variant.Filled;
                config.OnClick = _ =>
                {
                    _nav.NavigateTo("/planning/calendar");
                    return Task.CompletedTask;
                };
            });

        OnUnreadCountChanged?.Invoke();
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
