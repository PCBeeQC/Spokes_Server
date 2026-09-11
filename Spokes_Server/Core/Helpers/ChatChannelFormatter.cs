using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// Centralized utility for resolving ChatChannel display names and channel properties.
/// Ensures consistent naming for 1-to-1 DMs, self-DMs, group chats, and public channels.
/// </summary>
public static class ChatChannelFormatter
{
    public const string DefaultYouSuffix = "(you)";

    /// <summary>
    /// Determines whether the given channel is a direct message to oneself.
    /// </summary>
    public static bool IsSelfDirectMessage(ChatChannel? channel, string? currentUserId)
    {
        if (channel == null || string.IsNullOrEmpty(currentUserId))
            return false;

        if (channel.ChannelType != ChatChannelType.Direct)
            return false;

        var validParticipants = channel.ParticipantIds?.Where(id => !string.IsNullOrEmpty(id)).ToList();
        if (validParticipants == null || validParticipants.Count == 0)
            return false;

        return validParticipants.All(id => id == currentUserId);
    }

    /// <summary>
    /// Gets the display name for a channel relative to the viewing user.
    /// </summary>
    public static string GetDisplayName(
        ChatChannel? channel,
        string? currentUserId,
        Func<string, Employee?> getEmployee,
        string youSuffix = DefaultYouSuffix)
    {
        if (channel == null)
            return "Unnamed Channel";

        if (channel.ChannelType == ChatChannelType.Direct)
        {
            if (channel.ParticipantIds.Count <= 2)
            {
                if (IsSelfDirectMessage(channel, currentUserId))
                {
                    var currentUser = !string.IsNullOrEmpty(currentUserId) ? getEmployee(currentUserId) : null;
                    var name = !string.IsNullOrWhiteSpace(currentUser?.FullName) ? currentUser.FullName : "Unknown User";
                    return $"{name} {youSuffix}".Trim();
                }

                var otherUserId = channel.ParticipantIds.FirstOrDefault(id => id != currentUserId)
                                  ?? channel.ParticipantIds.FirstOrDefault();

                if (otherUserId != null)
                {
                    if (!string.IsNullOrEmpty(currentUserId) && otherUserId == currentUserId)
                    {
                        var currentUser = getEmployee(currentUserId);
                        var name = !string.IsNullOrWhiteSpace(currentUser?.FullName) ? currentUser.FullName : "Unknown User";
                        return $"{name} {youSuffix}".Trim();
                    }

                    var otherUser = getEmployee(otherUserId);
                    return !string.IsNullOrWhiteSpace(otherUser?.FullName) ? otherUser.FullName : "Unknown User";
                }

                return !string.IsNullOrWhiteSpace(channel.Name) && channel.Name != "Direct Message"
                    ? channel.Name
                    : "Unknown User";
            }
            else
            {
                // Legacy group DMs still typed as Direct
                if (!string.IsNullOrEmpty(channel.Name) && channel.Name != "Direct Message")
                    return channel.Name;

                var otherParticipantIds = channel.ParticipantIds
                    .Where(id => string.IsNullOrEmpty(currentUserId) || id != currentUserId)
                    .ToList();

                var names = otherParticipantIds
                    .Select(id => getEmployee(id)?.FirstName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Take(3)
                    .ToList();

                return names.Any() ? string.Join(", ", names) : "Direct Message";
            }
        }

        if (channel.ChannelType == ChatChannelType.Group)
        {
            if (!string.IsNullOrEmpty(channel.Name))
                return channel.Name;

            var otherParticipantIds = channel.ParticipantIds
                .Where(id => string.IsNullOrEmpty(currentUserId) || id != currentUserId)
                .ToList();

            var names = otherParticipantIds
                .Select(id => getEmployee(id)?.FirstName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(3)
                .ToList();

            if (otherParticipantIds.Count > 3)
                return string.Join(", ", names) + $" +{otherParticipantIds.Count - 3}";

            if (names.Any())
                return string.Join(", ", names);

            return "Group Chat";
        }

        return !string.IsNullOrWhiteSpace(channel.Name) ? channel.Name : "Unnamed Channel";
    }

    /// <summary>
    /// Gets the display name for a channel relative to the viewing user using an employee collection.
    /// </summary>
    public static string GetDisplayName(
        ChatChannel? channel,
        string? currentUserId,
        IEnumerable<Employee>? employees,
        string youSuffix = DefaultYouSuffix)
    {
        if (employees == null)
            return GetDisplayName(channel, currentUserId, _ => null, youSuffix);

        return GetDisplayName(channel, currentUserId, id => employees.FirstOrDefault(e => e.Id == id), youSuffix);
    }
}
