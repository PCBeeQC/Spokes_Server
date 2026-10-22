namespace Spokes_Server.Core.Services.Communication.Notifications.InApp;

/// <summary>
/// Bridge interface to dispatch in-app notifications from singleton routing services to scoped Blazor circuits.
/// </summary>
public interface IInAppNotificationDispatcher
{
    event Action<InAppNotification>? OnNotificationDispatched;
    void Dispatch(InAppNotification notification);
}
