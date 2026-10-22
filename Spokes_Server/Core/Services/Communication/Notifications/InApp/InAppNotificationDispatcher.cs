namespace Spokes_Server.Core.Services.Communication.Notifications.InApp;

/// <summary>
/// Singleton dispatcher that broadcasts in-app notifications to connected user circuits.
/// </summary>
public class InAppNotificationDispatcher : IInAppNotificationDispatcher
{
    public event Action<InAppNotification>? OnNotificationDispatched;

    public void Dispatch(InAppNotification notification)
    {
        OnNotificationDispatched?.Invoke(notification);
    }
}
