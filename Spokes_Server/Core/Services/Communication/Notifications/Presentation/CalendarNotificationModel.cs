namespace Spokes_Server.Core.Services.Communication.Notifications.Presentation;

/// <summary>
/// Encapsulates parameters for displaying a calendar notification in the UI.
/// </summary>
public record CalendarNotificationModel(
    string Title,
    string TimeDescription,
    string Location,
    string EventId,
    string ServerName,
    string? ServerIconUrl = null);
