using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Constants;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Security;

namespace Spokes_Server.Core.Services.Communication.Chat;

/// <summary>
/// Authoritative domain service for evaluating chat channel access, announcement posting permissions,
/// message modifications, and message deletions.
/// Centralizes all channel security logic to prevent logic fragmentation across ChatHub, ChatService, and UI components.
/// </summary>
public class ChatAuthorizationService : IChatAuthorizationService, IChatChannelAccessService
{
    private readonly ChatChannelRepository _channels;
    private readonly ProjectRepository _projects;
    private readonly TeamRepository _teams;
    private readonly EmployeeRepository _employees;
    private readonly IUserPrivacyService? _privacyService;

    public ChatAuthorizationService(
        ChatChannelRepository channels,
        ProjectRepository projects,
        TeamRepository teams,
        EmployeeRepository employees,
        IUserPrivacyService? privacyService = null)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _employees = employees ?? throw new ArgumentNullException(nameof(employees));
        _privacyService = privacyService;
    }

    public List<string> GetUserTeamIds(Employee? employee)
    {
        if (employee == null) return [];

        var teamIds = new List<string>();
        if (!string.IsNullOrEmpty(employee.TeamId))
        {
            teamIds.Add(employee.TeamId);
        }

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

        return teamIds;
    }

    public List<string> GetUserProjectIds(Employee? employee)
    {
        if (employee == null) return [];

        var allProjects = _projects.GetAll();
        var userProjectIds = new List<string>();
        var userTeamIds = GetUserTeamIds(employee);

        foreach (var p in allProjects)
        {
            if (ProjectAccessHelper.CanAccess(employee, p, userTeamIds))
            {
                userProjectIds.Add(p.Id);
            }
        }

        return userProjectIds;
    }

    public bool CanUserAccessChannel(ChatChannel? channel, Employee? employee)
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

        // Public channels are governed by server-wide privacy policy (PublicChannelPolicy)
        if (channel.IsPublicChannel)
        {
            if (_privacyService != null && !_privacyService.CanAccessPublicChannel(channel, employee))
            {
                return false;
            }
        }

        if (employee.IsAdmin) return true;

        var userTeamIds = GetUserTeamIds(employee);

        switch (channel.ChannelType)
        {
            case ChatChannelType.General:
                if (channel.IsPublicChannel) return true;
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
                if (string.IsNullOrEmpty(channel.LinkedEntityId)) return false;
                var project = _projects.GetById(channel.LinkedEntityId);
                if (project == null) return false;

                return ProjectAccessHelper.CanAccess(employee, project, userTeamIds);

            default:
                return false;
        }
    }

    public bool CanUserAccessChannel(string channelId, string userId)
    {
        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(userId)) return false;

        var channel = _channels.GetById(channelId);
        if (channel == null) return false;

        var employee = _employees.GetById(userId);
        if (employee == null) return false;

        return CanUserAccessChannel(channel, employee);
    }

    public bool CanUserPostToChannel(ChatChannel? channel, Employee? employee)
    {
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        bool treatedAsAnnouncement = channel.IsAnnouncementOnly ||
            (channel.IsPublicChannel && _privacyService?.GetPrivacyConfig().PublicChannels == PublicChannelPolicy.AnnouncementOnly);

        if (!channel.SupportsAnnouncements || !treatedAsAnnouncement)
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

    public bool CanUserModifyMessage(ChatMessage? message, ChatChannel? channel, Employee? employee)
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

    public bool CanUserDeleteMessage(ChatMessage? message, ChatChannel? channel, Employee? employee)
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

    public List<ChatChannel> GetChannelsForUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return [];
        var employee = _employees.GetById(userId);
        if (employee == null) return [];

        var userProjectIds = GetUserProjectIds(employee);
        var userTeamIds = GetUserTeamIds(employee);

        var channels = _channels.GetChannelsForUser(userId, userProjectIds, userTeamIds, employee.IsAdmin);

        if (!employee.HasPermission(AppPermissions.Chat.ViewArchive))
        {
            channels = channels.Where(c => !c.IsArchived).ToList();
        }

        if (_privacyService != null)
        {
            channels = channels.Where(c => _privacyService.CanAccessPublicChannel(c, employee)).ToList();
        }

        return channels;
    }

    public List<string> GetUsersForChannel(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return [];
        var channel = _channels.GetById(channelId);
        if (channel == null) return [];

        var validUserIds = new List<string>();
        var allActiveEmployees = _employees.GetAll().Where(e => e.IsActive).ToList();

        foreach (var emp in allActiveEmployees)
        {
            if (CanUserAccessChannel(channel, emp))
            {
                validUserIds.Add(emp.Id);
            }
        }

        return validUserIds;
    }

    public bool CanUserManageChannel(ChatChannel? channel, Employee? employee, bool isSessionUnlocked = true)
    {
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        if (channel.IsEncrypted && !isSessionUnlocked)
        {
            return false;
        }

        if (employee.IsAdmin)
        {
            return true;
        }

        if (channel.ChannelType == ChatChannelType.Team)
        {
            if (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Count > 0)
            {
                var ledTeams = _teams.GetAll().Where(t => t.LeaderId == employee.Id).Select(t => t.Id).ToHashSet();
                if (channel.AllowedTeamIds.Any(t => ledTeams.Contains(t)))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(channel.CreatedById) && channel.CreatedById == employee.Id)
        {
            return true;
        }

        if (employee.HasPermission(AppPermissions.Chat.EditAllPublicChannels))
        {
            if (channel.ChannelType == ChatChannelType.General || channel.ChannelType == ChatChannelType.Project)
            {
                return true;
            }
        }

        return false;
    }

    public bool CanUserDeleteChannel(ChatChannel? channel, Employee? employee, bool isSessionUnlocked = true)
    {
        if (channel == null || employee == null || !employee.IsActive || employee.IsSuspended || employee.IsBanned)
        {
            return false;
        }

        if (channel.IsEncrypted && !isSessionUnlocked)
        {
            return false;
        }

        if (employee.IsAdmin)
        {
            return true;
        }

        if (channel.ChannelType == ChatChannelType.Team)
        {
            if (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Count > 0)
            {
                var ledTeams = _teams.GetAll().Where(t => t.LeaderId == employee.Id).Select(t => t.Id).ToHashSet();
                if (channel.AllowedTeamIds.Any(t => ledTeams.Contains(t)))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(channel.CreatedById) && channel.CreatedById == employee.Id)
        {
            return true;
        }

        return false;
    }
}
