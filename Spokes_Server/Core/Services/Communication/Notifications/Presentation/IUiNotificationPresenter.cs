using Spokes_Server.Core.Services.Communication.Notifications.InApp;

namespace Spokes_Server.Core.Services.Communication.Notifications.Presentation;

/// <summary>
/// Abstraction for presenting UI notifications (e.g. snackbars) to isolate Core domain services from Blazor presentation components.
/// </summary>
public interface IUiNotificationPresenter
{
    void ShowChatNotification(InAppNotification notification);
    void ShowChatNotification(string senderName, string truncatedContent, string initials, string channelId, string channelName);
    void ShowModerationAlert(string reporterName, string reason);
    void ShowEmailNotification(string fromName, string subject, string snippet);
    void ShowCalendarNotification(CalendarNotificationModel notification);
    bool IsOnChatChannel(string channelId);
    bool IsOnEmailPage();
}
