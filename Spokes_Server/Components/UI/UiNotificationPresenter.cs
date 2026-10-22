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

    public void ShowChatNotification(InAppNotification notification)
    {
        _snackbar.Add<ChatNotificationContent>(
            new Dictionary<string, object?>
            {
                { "SenderName", notification.SenderName },
                { "Content", notification.Content },
                { "Initials", notification.Initials },
                { "ChannelId", notification.ChannelId },
                { "ChannelName", notification.ChannelName },
                { "AvatarUrl", notification.SenderAvatarUrl },
                { "IsReaction", notification.IsReaction },
                { "IsGroupChat", notification.IsGroupChat },
                { "ServerName", notification.ServerName }
            }!,
            Severity.Normal,
            config =>
            {
                config.VisibleStateDuration = 5000;
                config.ShowCloseIcon = true;
                config.SnackbarVariant = Variant.Filled;
                config.HideIcon = true;
                config.OnClick = _ =>
                {
                    _soundService.StopCallRingtoneAsync();
                    _nav.NavigateTo($"/chat/{notification.ChannelId}");
                    return Task.CompletedTask;
                };
            });
    }

    public void ShowChatNotification(string senderName, string truncatedContent, string initials, string channelId, string channelName) =>
        ShowChatNotification(new InAppNotification
        {
            RecipientUserId = string.Empty,
            Title = senderName,
            Content = truncatedContent,
            SenderName = senderName,
            Initials = initials,
            ChannelId = channelId,
            ChannelName = channelName
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

    public void ShowEmailNotification(string fromName, string subject, string snippet)
    {
        _snackbar.Add<EmailNotificationContent>(
            new Dictionary<string, object>
            {
                { "SenderName", fromName },
                { "Subject", subject },
                { "Snippet", snippet }
            },
            Severity.Normal,
            config =>
            {
                config.VisibleStateDuration = 5000;
                config.ShowCloseIcon = true;
                config.SnackbarVariant = Variant.Filled;
                config.HideIcon = true;
                config.OnClick = _ =>
                {
                    _nav.NavigateTo("/email");
                    return Task.CompletedTask;
                };
            });
    }

    public void ShowCalendarNotification(CalendarNotificationModel notification)
    {
        _snackbar.Add<CalendarNotificationContent>(
            new Dictionary<string, object>
            {
                { "Title", string.IsNullOrWhiteSpace(notification.Title) ? "Untitled Event" : notification.Title },
                { "TimeDescription", notification.TimeDescription },
                { "Location", notification.Location },
                { "EventId", notification.EventId },
                { "ServerName", notification.ServerName },
                { "ServerIconUrl", notification.ServerIconUrl ?? "" }
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
    }
}
