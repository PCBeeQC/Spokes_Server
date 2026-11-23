using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat.Formatters;

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
    /// Resolves the target participant ID for a 1-on-1 direct message (including self-DMs).
    /// Returns null for non-DM channels, empty participant lists, or multi-participant group DMs.
    /// </summary>
    public static string? GetDirectMessageParticipantId(ChatChannel? channel, string? currentUserId)
    {
        if (channel == null || channel.ChannelType != ChatChannelType.Direct)
            return null;

        if (channel.ParticipantIds == null || channel.ParticipantIds.Count == 0 || channel.ParticipantIds.Count > 2)
            return null;

        if (IsSelfDirectMessage(channel, currentUserId))
            return currentUserId;

        return channel.ParticipantIds.FirstOrDefault(id => !string.IsNullOrEmpty(id) && id != currentUserId)
               ?? channel.ParticipantIds.FirstOrDefault(id => !string.IsNullOrEmpty(id));
    }

    /// <summary>
    /// Resolves the target <see cref="Employee"/> for a 1-on-1 direct message (including self-DMs).
    /// </summary>
    public static Employee? GetDirectMessageParticipant(
        ChatChannel? channel,
        string? currentUserId,
        IEnumerable<Employee>? employees)
    {
        var participantId = GetDirectMessageParticipantId(channel, currentUserId);
        if (string.IsNullOrEmpty(participantId) || employees == null)
            return null;

        return employees.FirstOrDefault(e => e.Id == participantId);
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

                var otherUserId = GetDirectMessageParticipantId(channel, currentUserId);

                if (otherUserId != null)
                {
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

                return FormatGroupParticipantNames(channel, currentUserId, getEmployee, "Direct Message", showOverflowCount: false);
            }
        }

        if (channel.ChannelType == ChatChannelType.Group)
        {
            if (!string.IsNullOrEmpty(channel.Name))
                return channel.Name;

            return FormatGroupParticipantNames(channel, currentUserId, getEmployee, "Group Chat", showOverflowCount: true);
        }

        return !string.IsNullOrWhiteSpace(channel.Name) ? channel.Name : "Unnamed Channel";
    }

    private static string FormatGroupParticipantNames(
        ChatChannel channel,
        string? currentUserId,
        Func<string, Employee?> getEmployee,
        string fallbackName,
        bool showOverflowCount)
    {
        var otherParticipantIds = channel.ParticipantIds
            .Where(id => string.IsNullOrEmpty(currentUserId) || id != currentUserId)
            .ToList();

        var names = otherParticipantIds
            .Select(id => getEmployee(id)?.FirstName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(3)
            .ToList();

        if (showOverflowCount && otherParticipantIds.Count > 3)
            return string.Join(", ", names) + $" +{otherParticipantIds.Count - 3}";

        return names.Count > 0 ? string.Join(", ", names) : fallbackName;
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
