using Spokes_Server.Core.Services.Communication.Notifications.InApp;

namespace Spokes_Server.Core.Services.Communication.Notifications.Presentation;

/// <summary>
/// Null fallback notification presenter for Core domain services when no UI presenter is provided.
/// Contains no presentation framework dependencies.
/// </summary>
public class NullUiNotificationPresenter : IUiNotificationPresenter
{
    public static readonly NullUiNotificationPresenter Instance = new();

    public void ShowChatNotification(InAppNotification notification) { }
    public void ShowModerationAlert(string reporterName, string reason) { }
    public void ShowEmailNotification(string fromName, string subject, string snippet) { }
    public void ShowCalendarNotification(CalendarNotificationModel notification) { }
    public bool IsOnChatChannel(string channelId) => false;
    public bool IsOnEmailPage() => false;
}
