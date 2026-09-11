using System;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication;

/// <summary>
/// Defines a unified initialization and tracking contract for user-specific notification services.
/// </summary>
public interface IUserNotificationService
{
    /// <summary>
    /// Initializes the notification service for the specified user.
    /// </summary>
    Task InitializeAsync(string userId);

    /// <summary>
    /// Total count of unread notifications.
    /// </summary>
    int TotalUnreadCount { get; }

    /// <summary>
    /// Triggered when the unread notification count changes.
    /// </summary>
    event Action? OnUnreadCountChanged;
}
