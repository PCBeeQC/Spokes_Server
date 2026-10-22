using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Notifications.Routing;

public class NotificationRecipientFilter
{
    private readonly ChatReadStateRepository _readStates;
    private readonly ChatMessageRepository _chatMessages;

    protected NotificationRecipientFilter()
    {
        _readStates = null!;
        _chatMessages = null!;
    }

    public NotificationRecipientFilter(
        ChatReadStateRepository readStates,
        ChatMessageRepository chatMessages)
    {
        _readStates = readStates ?? throw new System.ArgumentNullException(nameof(readStates));
        _chatMessages = chatMessages ?? throw new System.ArgumentNullException(nameof(chatMessages));
    }

    public virtual bool ShouldNotifyChat([NotNullWhen(true)] Employee? employee, Employee sender, ChatChannel channel, ChatMessage message)
    {
        if (employee == null || !employee.ChatNotificationsEnabled)
        {
            return false;
        }

        if (employee.BlockedUserIds?.Contains(sender.Id) == true)
        {
            return false;
        }

        var notifLevel = _readStates.GetNotificationLevel(employee.Id, channel.Id, channel.ChannelType.ToString(), channel.IsVoiceChannel);
        if (notifLevel == ChatNotificationLevel.None)
        {
            return false;
        }

        if (notifLevel == ChatNotificationLevel.Mentions)
        {
            var channelIndex = _chatMessages.GetIndexForChannel(channel.Id);
            return IsUserMentioned(employee.Id, employee, message, channelIndex);
        }

        return true;
    }

    public virtual bool ShouldNotifyReaction([NotNullWhen(true)] Employee? originalSender, Employee reactionSender)
    {
        if (originalSender == null || !originalSender.ReactionNotificationsEnabled)
        {
            return false;
        }

        if (originalSender.BlockedUserIds?.Contains(reactionSender.Id) == true)
        {
            return false;
        }

        return true;
    }

    public virtual bool IsUserMentioned(string userId, Employee? employee, ChatMessage message, IReadOnlyList<ChatIndexEntry>? channelIndex = null)
    {
        bool isMentioned = message.MentionedUserIds.Contains(userId) || message.MentionsEveryone;

        if (!isMentioned && employee?.ReplyNotificationsTreatAsMention == true && !string.IsNullOrEmpty(message.ReplyToId))
        {
            channelIndex ??= _chatMessages.GetIndexForChannel(message.ChannelId);
            var parentEntry = channelIndex?.FirstOrDefault(e => e.Id == message.ReplyToId);
            if (parentEntry != null && parentEntry.SenderId == userId)
            {
                isMentioned = true;
            }
        }

        return isMentioned;
    }
}
