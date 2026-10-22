using System;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

public class NotificationRateLimiter
{
    private readonly ChatReadStateRepository _readStates;
    private readonly ChatMessageRepository _chatMessages;

    protected NotificationRateLimiter()
    {
        _readStates = null!;
        _chatMessages = null!;
    }

    public NotificationRateLimiter(
        ChatReadStateRepository readStates,
        ChatMessageRepository chatMessages)
    {
        _readStates = readStates ?? throw new ArgumentNullException(nameof(readStates));
        _chatMessages = chatMessages ?? throw new ArgumentNullException(nameof(chatMessages));
    }

    public virtual bool ShouldSuppressSound(Employee employee, string channelId)
    {
        if (!employee.LimitConsecutiveNotificationSounds)
        {
            return false;
        }

        var lastReadAt = _readStates.GetLastReadAt(employee.Id, channelId);
        if (lastReadAt == DateTime.MinValue)
        {
            return false;
        }

        var unreadMessages = _chatMessages.GetUnreadMessagesSince(channelId, lastReadAt, employee.Id);
        return unreadMessages.Count > employee.ConsecutiveNotificationSoundLimit;
    }
}
