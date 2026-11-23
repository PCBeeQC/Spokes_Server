using System;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Shared authorization boundary helper for chat moderation visibility, target hierarchy,
/// and notification routing across Global Admins and delegated <see cref="AppPermissions.Chat.Moderator"/> users.
/// </summary>
public static class ModerationAuthorizationHelper
{
    public static bool CanModerateAny(Employee? moderator) =>
        moderator is { IsActive: true, IsSuspended: false, IsBanned: false } &&
        (moderator.IsAdmin || moderator.HasPermission(AppPermissions.Chat.Moderator));

    public static ChatChannel? ResolveReportChannel(
        ReportedMessage? report,
        ChatChannelRepository? channels,
        ChatMessageRepository? messages)
    {
        if (report == null || channels == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(report.ChannelId))
        {
            var directChannel = channels.GetById(report.ChannelId);
            if (directChannel != null)
            {
                return directChannel;
            }
        }

        if (!string.IsNullOrWhiteSpace(report.MessageId) && messages != null)
        {
            var message = messages.GetById(report.MessageId);
            if (!string.IsNullOrWhiteSpace(message?.ChannelId))
            {
                return channels.GetById(message.ChannelId);
            }
        }

        return null;
    }

    public static bool CanViewReport(
        Employee? moderator,
        ReportedMessage? report,
        ChatChannel? channel,
        IChatAuthorizationService? chatAuth) =>
        CanViewReport(
            moderator,
            report,
            channel,
            chatAuth != null ? chatAuth.CanUserAccessChannel : null);

    public static bool CanViewReport(
        Employee? moderator,
        ReportedMessage? report,
        ChatChannel? channel,
        IChatChannelAccessService? channelAccess) =>
        CanViewReport(
            moderator,
            report,
            channel,
            channelAccess switch
            {
                IChatAuthorizationService auth => auth.CanUserAccessChannel,
                not null => (ch, user) => channelAccess.GetChannelsForUser(user.Id).Any(c => string.Equals(c.Id, ch.Id, StringComparison.Ordinal)),
                null => null
            });

    public static bool CanViewReport(
        Employee? moderator,
        ReportedMessage? report,
        ChatChannel? channel,
        Func<ChatChannel, Employee, bool>? canAccessChannel)
    {
        if (!CanModerateAny(moderator) || report == null)
        {
            return false;
        }

        if (moderator!.IsAdmin)
        {
            return true;
        }

        if (!moderator.HasPermission(AppPermissions.Chat.Use))
        {
            return false;
        }

        if (channel == null ||
            string.Equals(channel.ChannelType, ChatChannelType.Direct, StringComparison.Ordinal) ||
            string.Equals(channel.ChannelType, ChatChannelType.Group, StringComparison.Ordinal))
        {
            return false;
        }

        return canAccessChannel != null && canAccessChannel(channel, moderator);
    }

    public static bool CanActOnReport(
        Employee? moderator,
        ReportedMessage? report,
        ChatChannel? channel,
        Employee? reportedUser,
        IChatAuthorizationService? chatAuth)
    {
        if (!CanViewReport(moderator, report, channel, chatAuth))
        {
            return false;
        }

        if (moderator!.IsAdmin)
        {
            return true;
        }

        if (string.Equals(moderator.Id, report!.ReportedUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return reportedUser != null && UserService.CanModerateUserTarget(moderator, reportedUser);
    }
}
