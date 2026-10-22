using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Authoritative domain service for evaluating chat channel access, announcement posting permissions,
/// message modifications, and message deletions across hubs, services, and UI components.
/// </summary>
public interface IChatAuthorizationService : IChatChannelAccessService
{
    /// <summary>
    /// Evaluates if an employee currently has read/view access to a channel.
    /// </summary>
    bool CanUserAccessChannel(ChatChannel? channel, Employee? employee);

    /// <summary>
    /// Evaluates if an employee currently has read/view access to a channel by ID.
    /// </summary>
    bool CanUserAccessChannel(string channelId, string userId);

    /// <summary>
    /// Evaluates if an employee has posting rights to a channel (including announcement rules).
    /// </summary>
    bool CanUserPostToChannel(ChatChannel? channel, Employee? employee);

    /// <summary>
    /// Evaluates if an employee can modify (edit) an existing message.
    /// Requires that the employee is the original sender, currently has active channel access,
    /// and has posting rights in the channel.
    /// </summary>
    bool CanUserModifyMessage(ChatMessage? message, ChatChannel? channel, Employee? employee);

    /// <summary>
    /// Evaluates if an employee can delete an existing message.
    /// Requires that the employee currently has active channel access, and is either
    /// the original sender or holds administrative/moderator permissions.
    /// </summary>
    bool CanUserDeleteMessage(ChatMessage? message, ChatChannel? channel, Employee? employee);

    /// <summary>
    /// Resolves all project IDs accessible by the employee (public, directly allowed, or via allowed teams).
    /// </summary>
    List<string> GetUserProjectIds(Employee? employee);

    /// <summary>
    /// Resolves all team IDs associated with the employee (direct membership or team leadership).
    /// </summary>
    List<string> GetUserTeamIds(Employee? employee);

    /// <summary>
    /// Evaluates if an employee has rights to manage channel properties, permissions, and settings.
    /// If the channel is encrypted and isSessionUnlocked is false, returns false.
    /// </summary>
    bool CanUserManageChannel(ChatChannel? channel, Employee? employee, bool isSessionUnlocked = true);

    /// <summary>
    /// Evaluates if an employee has rights to permanently delete a channel.
    /// If the channel is encrypted and isSessionUnlocked is false, returns false.
    /// </summary>
    bool CanUserDeleteChannel(ChatChannel? channel, Employee? employee, bool isSessionUnlocked = true);
}
