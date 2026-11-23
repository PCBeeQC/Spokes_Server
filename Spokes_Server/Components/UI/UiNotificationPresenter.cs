using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Spokes_Server.Components.Shared;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.InApp;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.UI;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Blazor/MudBlazor implementation of IUiNotificationPresenter.
/// Encapsulates Razor component snackbar rendering and navigation checks so that Core domain services do not depend on the UI layer.
/// </summary>
public class UiNotificationPresenter : IUiNotificationPresenter
{
    private readonly ISnackbar _snackbar;
    private readonly NavigationManager _nav;
    private readonly ISoundService _soundService;

    public UiNotificationPresenter(ISnackbar snackbar, NavigationManager nav, ISoundService soundService)
    {
        _snackbar = snackbar;
        _nav = nav;
        _soundService = soundService;
    }

    public bool IsOnChatChannel(string channelId) =>
        _nav.Uri.Contains($"/chat/{channelId}", StringComparison.OrdinalIgnoreCase);

    public bool IsOnEmailPage() =>
        _nav.Uri.Contains("/email", StringComparison.OrdinalIgnoreCase);

    public void ShowChatNotification(InAppNotification notification) =>
        AddComponentSnackbar<ChatNotificationContent>(
            new Dictionary<string, object>
            {
                { "SenderName", notification.SenderName },
                { "Content", notification.Content },
                { "Initials", notification.Initials },
                { "ChannelId", notification.ChannelId },
                { "ChannelName", notification.ChannelName },
                { "AvatarUrl", notification.SenderAvatarUrl! },
                { "IsReaction", notification.IsReaction },
                { "IsGroupChat", notification.IsGroupChat },
                { "ServerName", notification.ServerName! }
            },
            visibleStateDuration: 5000,
            () =>
            {
                _soundService.StopCallRingtoneAsync();
                _nav.NavigateTo($"/chat/{notification.ChannelId}");
            });

    public void ShowModerationAlert(string reporterName, string reason)
    {
        _snackbar.Add(
            $"{reporterName} reported a message for: {reason}",
            Severity.Warning,
            config =>
            {
                config.VisibleStateDuration = 10000;
                config.ShowCloseIcon = true;
                config.Action = "Review";
                config.ActionColor = Color.Warning;
                config.OnClick = _ =>
                {
                    _nav.NavigateTo("/admin/moderation");
                    return Task.CompletedTask;
                };
            });
    }

    public void ShowEmailNotification(string fromName, string subject, string snippet) =>
        AddComponentSnackbar<EmailNotificationContent>(
            new Dictionary<string, object>
            {
                { "SenderName", fromName },
                { "Subject", subject },
                { "Snippet", snippet }
            },
            visibleStateDuration: 5000,
            () => _nav.NavigateTo("/email"));

    public void ShowCalendarNotification(CalendarNotificationModel notification) =>
        AddComponentSnackbar<CalendarNotificationContent>(
            new Dictionary<string, object>
            {
                { "Title", string.IsNullOrWhiteSpace(notification.Title) ? "Untitled Event" : notification.Title },
                { "TimeDescription", notification.TimeDescription },
                { "Location", notification.Location },
                { "EventId", notification.EventId },
                { "ServerName", notification.ServerName },
                { "ServerIconUrl", notification.ServerIconUrl ?? "" }
            },
            visibleStateDuration: 10000,
            () => _nav.NavigateTo("/planning/calendar"));

    private void AddComponentSnackbar<TComponent>(
        Dictionary<string, object> parameters,
        int visibleStateDuration,
        Action onClick)
        where TComponent : IComponent
    {
        _snackbar.Add<TComponent>(
            parameters,
            Severity.Normal,
            config =>
            {
                config.VisibleStateDuration = visibleStateDuration;
                config.ShowCloseIcon = true;
                config.SnackbarVariant = Variant.Filled;
                config.HideIcon = true;
                config.OnClick = _ =>
                {
                    onClick();
                    return Task.CompletedTask;
                };
            });
    }
}
