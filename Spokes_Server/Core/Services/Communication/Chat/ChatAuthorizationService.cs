using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Authoritative domain service for evaluating chat channel access, announcement posting permissions,
/// message modifications, and message deletions.
/// Centralizes all channel security logic to prevent logic fragmentation across ChatHub, ChatService, and UI components.
/// </summary>
public class ChatAuthorizationService : IChatAuthorizationService
{
    private readonly ChatChannelRepository? _channels;
    private readonly ProjectRepository? _projects;
    private readonly TeamRepository? _teams;
    private readonly EmployeeRepository? _employees;

    public ChatAuthorizationService(
        ChatChannelRepository? channels = null,
        ProjectRepository? projects = null,
        TeamRepository? teams = null,
        EmployeeRepository? employees = null)
    {
        _channels = channels;
        _projects = projects;
        _teams = teams;
        _employees = employees;
    }

    public List<string> GetUserTeamIds(Employee employee)
    {
        if (employee == null) return [];

        var teamIds = new List<string>();
        if (!string.IsNullOrEmpty(employee.TeamId))
        {
            teamIds.Add(employee.TeamId);
        }

        if (_teams != null)
        {
            var ledTeams = _teams.GetAll()
                .Where(t => t.LeaderId == employee.Id)
                .Select(t => t.Id);

            foreach (var id in ledTeams)
            {
                if (!teamIds.Contains(id))
                {
                    teamIds.Add(id);
                }
            }
        }

        return teamIds;
    }

    public List<string> GetUserProjectIds(Employee employee)
    {
        if (employee == null || _projects == null) return [];

        var allProjects = _projects.GetAll();
        var userProjectIds = new List<string>();
        var userTeamIds = GetUserTeamIds(employee);

        foreach (var p in allProjects)
        {
            if (employee.IsAdmin || p.AccessPolicy == "Public")
            {
                userProjectIds.Add(p.Id);
            }
            else
            {
                bool isAllowed = p.AllowedUserIds != null && p.AllowedUserIds.Contains(employee.Id);
                if (!isAllowed && p.AllowedTeamIds != null && userTeamIds.Any(tid => p.AllowedTeamIds.Contains(tid)))
                {
                    isAllowed = true;
                }
                if (isAllowed)
                {
                    userProjectIds.Add(p.Id);
                }
            }
        }

        return userProjectIds;
    }

    public bool CanUserAccessChannel(ChatChannel channel, Employee employee)
    {
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        // Direct messages and private groups strictly require participation - no admin eavesdropping
        if (channel.ChannelType == ChatChannelType.Direct || channel.ChannelType == ChatChannelType.Group)
        {
            return (channel.ParticipantIds != null && channel.ParticipantIds.Contains(employee.Id)) ||
                   channel.CreatedById == employee.Id;
        }

        if (employee.IsAdmin) return true;

        var userTeamIds = GetUserTeamIds(employee);

        switch (channel.ChannelType)
        {
            case ChatChannelType.General:
                if (channel.IsDefaultGeneral) return true;
                bool isRestricted = (channel.ParticipantIds != null && channel.ParticipantIds.Count > 0) ||
                                   (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Count > 0);
                if (!isRestricted) return true;
                if (channel.ParticipantIds != null && channel.ParticipantIds.Contains(employee.Id)) return true;
                if (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Any(t => userTeamIds.Contains(t))) return true;
                return false;

            case ChatChannelType.Team:
                if (!string.IsNullOrEmpty(channel.LinkedEntityId) && userTeamIds.Contains(channel.LinkedEntityId))
                {
                    return true;
                }
                if (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Any(t => userTeamIds.Contains(t)))
                {
                    return true;
                }
                return false;

            case ChatChannelType.Project:
                if (string.IsNullOrEmpty(channel.LinkedEntityId) || _projects == null) return false;
                var project = _projects.GetById(channel.LinkedEntityId);
                if (project == null) return false;

                if (project.AccessPolicy == "Public") return true;
                if (project.AllowedUserIds != null && project.AllowedUserIds.Contains(employee.Id)) return true;
                if (project.AllowedTeamIds != null && userTeamIds.Any(t => project.AllowedTeamIds.Contains(t))) return true;
                return false;

            default:
                return false;
        }
    }

    public bool CanUserAccessChannel(string channelId, string userId)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(userId) || _channels == null || _employees == null) return false;

        var channel = _channels.GetById(channelId);
        if (channel == null) return false;

        var employee = _employees.GetById(userId);
        if (employee == null) return false;

        return CanUserAccessChannel(channel, employee);
    }

    public bool CanUserPostToChannel(ChatChannel channel, Employee employee)
    {
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        if (!channel.SupportsAnnouncements || !channel.IsAnnouncementOnly)
        {
            return true;
        }

        if (employee.IsAdmin) return true;

        if (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(employee.Id))
        {
            return true;
        }

        var userTeamIds = GetUserTeamIds(employee);
        if (channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Any(t => userTeamIds.Contains(t)))
        {
            return true;
        }

        if (channel.ChannelType == ChatChannelType.Group && channel.CreatedById == employee.Id)
        {
            return true;
        }

        return false;
    }

    public bool CanUserModifyMessage(ChatMessage message, ChatChannel channel, Employee employee)
    {
        if (message == null || channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        // Only the original author can edit message content
        if (message.SenderId != employee.Id)
        {
            return false;
        }

        // Caller must hold active access to the channel
        if (!CanUserAccessChannel(channel, employee))
        {
            return false;
        }

        // Caller must have post access (not blocked by announcement-only rules)
        if (!CanUserPostToChannel(channel, employee))
        {
            return false;
        }

        return true;
    }

    public bool CanUserDeleteMessage(ChatMessage message, ChatChannel channel, Employee employee)
    {
        if (message == null || channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        // Caller must currently have access to the channel
        if (!CanUserAccessChannel(channel, employee))
        {
            return false;
        }

        // Author can delete their own message
        if (message.SenderId == employee.Id)
        {
            return true;
        }

        // Administrator or Chat Moderator can delete inappropriate messages
        if (employee.IsAdmin || employee.HasPermission(AppPermissions.Chat.Moderator))
        {
            return true;
        }

        return false;
    }
}
