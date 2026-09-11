using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class ChatChannelRepository : JsonRepository<ChatChannel>
{
    public ChatChannelRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "chat"), "channel.json") { }

    protected override string GetFilePath(ChatChannel item)
    {
        return Path.Combine(_basePath, item.Id, "channel.json");
    }

    public override void LoadFromDisk()
    {
        base.LoadFromDisk();
        foreach (var channel in _cache.Values)
        {
            if (!channel.SupportsAnnouncements && (channel.IsAnnouncementOnly || channel.AllowedPostUserIds.Any() || channel.AllowedPostTeamIds.Any()))
            {
                channel.Normalize();
                Save(channel);
            }
        }
    }

    /// <summary>
    /// Get all channels of a specific type.
    /// </summary>
    public List<ChatChannel> GetByType(string channelType)
    {
        return _cache.Values
            .Where(c => c.ChannelType == channelType)
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    /// <summary>
    /// Get the default General channel for the organization.
    /// Creates one if it doesn't exist.
    /// </summary>
    public ChatChannel GetOrCreateDefaultGeneral()
    {
        var general = _cache.Values.FirstOrDefault(c => c.IsDefaultGeneral);
        if (general != null)
            return general;

        // Create the default general channel
        general = new ChatChannel
        {
            Id = "general",
            Name = "General",
            Description = "",
            ChannelType = ChatChannelType.General,
            IsDefaultGeneral = true,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        Save(general);
        return general;
    }

    /// <summary>
    /// Get channel for a specific project. Returns null if not found.
    /// </summary>
    public ChatChannel? GetProjectChannel(string projectId)
    {
        return _cache.Values
            .FirstOrDefault(c => c.ChannelType == ChatChannelType.Project &&
                                  c.LinkedEntityId == projectId);
    }

    /// <summary>
    /// Get or create a channel for a project.
    /// </summary>
    public ChatChannel GetOrCreateProjectChannel(string projectId, string projectName)
    {
        var channel = GetProjectChannel(projectId);
        if (channel != null)
            return channel;

        channel = new ChatChannel
        {
            Name = projectName,
            Description = $"Chat for project: {projectName}",
            ChannelType = ChatChannelType.Project,
            LinkedEntityId = projectId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        Save(channel);
        return channel;
    }

    /// <summary>
    /// Get channel for a specific team.
    /// </summary>
    public ChatChannel? GetTeamChannel(string teamId)
    {
        return _cache.Values
            .FirstOrDefault(c => c.ChannelType == ChatChannelType.Team &&
                                  c.LinkedEntityId == teamId);
    }

    /// <summary>
    /// Get or create a channel for a team.
    /// </summary>
    public ChatChannel GetOrCreateTeamChannel(string teamId, string teamName)
    {
        var channel = GetTeamChannel(teamId);
        if (channel != null)
            return channel;

        channel = new ChatChannel
        {
            Name = teamName,
            Description = $"Team chat: {teamName}",
            ChannelType = ChatChannelType.Team,
            LinkedEntityId = teamId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        Save(channel);
        return channel;
    }

    /// <summary>
    /// Get or create a Direct Message channel between two users.
    /// DM channel IDs are deterministic based on sorted participant IDs.
    /// </summary>
    public ChatChannel GetOrCreateDirectChannel(string userId1, string userId2, bool isEncrypted = false, string? createdById = null)
    {
        // Create a deterministic channel ID from sorted user IDs
        var sortedIds = new[] { userId1, userId2 }.OrderBy(id => id).ToList();
        var dmChannelId = $"dm_{sortedIds[0]}_{sortedIds[1]}";

        var channel = GetById(dmChannelId);
        if (channel != null)
            return channel;

        channel = new ChatChannel
        {
            Id = dmChannelId,
            Name = "Direct Message", // Will be displayed as other user's name in UI
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = sortedIds,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsEncrypted = isEncrypted,
            CreatedById = createdById ?? userId1
        };

        Save(channel);
        return channel;
    }

    /// <summary>
    /// Create a new Group Chat channel for multiple users.
    /// Always creates a new channel (unlike 1:1 DMs which are deduplicated).
    /// </summary>
    public ChatChannel CreateGroupChannel(List<string> userIds, string defaultName, bool isEncrypted = false, string? createdById = null)
    {
        var participantIds = userIds.Distinct().ToList();

        var channel = new ChatChannel
        {
            Name = defaultName,
            ChannelType = ChatChannelType.Group,
            ParticipantIds = participantIds,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            IsEncrypted = isEncrypted,
            CreatedById = createdById ?? participantIds.FirstOrDefault() ?? string.Empty
        };

        Save(channel);
        return channel;
    }

    /// <summary>
    /// Get all DM channels for a user.
    /// </summary>
    public List<ChatChannel> GetDirectChannelsForUser(string userId)
    {
        return _cache.Values
            .Where(c => (c.ChannelType == ChatChannelType.Direct || c.ChannelType == ChatChannelType.Group) &&
                        (c.ParticipantIds.Contains(userId) || c.CreatedById == userId))
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    /// <summary>
    /// Centralized, O(1) evaluation rule to check if a specific user can access a specific channel.
    /// This prevents logic duplication and enables fast routing of notifications.
    /// </summary>
    public bool EvaluateChannelAccessRule(ChatChannel channel, string userId, List<string> userProjectIds, List<string> userTeamIds, bool isAdmin = false)
    {
        switch (channel.ChannelType)
        {
            case ChatChannelType.General:
                if (channel.IsDefaultGeneral) return true;
                bool isRestricted = channel.ParticipantIds.Any() || channel.AllowedTeamIds.Any();
                if (!isRestricted || isAdmin) return true;
                if (channel.ParticipantIds.Contains(userId)) return true;
                if (channel.AllowedTeamIds.Any(t => userTeamIds.Contains(t))) return true;
                return false;

            case ChatChannelType.Project:
                return channel.LinkedEntityId != null && userProjectIds.Contains(channel.LinkedEntityId);

            case ChatChannelType.Team:
                if (isAdmin) return true;
                return (channel.LinkedEntityId != null && userTeamIds.Contains(channel.LinkedEntityId)) ||
                       (channel.AllowedTeamIds != null && channel.AllowedTeamIds.Any(t => userTeamIds.Contains(t)));

            case ChatChannelType.Direct:
            case ChatChannelType.Group:
                return channel.ParticipantIds.Contains(userId) || channel.CreatedById == userId;

            default:
                return false;
        }
    }

    /// <summary>
    /// Get all channels a user has access to.
    /// </summary>
    public List<ChatChannel> GetChannelsForUser(string userId, List<string> userProjectIds, List<string> userTeamIds, bool isAdmin = false)
    {
        return _cache.Values
            .Where(c => EvaluateChannelAccessRule(c, userId, userProjectIds, userTeamIds, isAdmin))
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    /// <summary>
    /// Evaluates if a user has permission to post to a channel.
    /// Used for Announcement channels.
    /// </summary>
    public bool EvaluateChannelPostAccessRule(ChatChannel channel, string userId, List<string> userTeamIds, bool isAdmin = false)
    {
        // Direct messages do not support announcement mode and are never restricted by announcement post rules.
        if (!channel.SupportsAnnouncements || !channel.IsAnnouncementOnly)
            return true;

        if (isAdmin) return true;
        if (channel.AllowedPostUserIds != null && channel.AllowedPostUserIds.Contains(userId)) return true;
        if (channel.AllowedPostTeamIds != null && channel.AllowedPostTeamIds.Any(t => userTeamIds.Contains(t))) return true;

        // Group chats created by this user
        if (channel.ChannelType == ChatChannelType.Group && channel.CreatedById == userId)
            return true;
            
        return false;
    }

    /// <summary>
    /// Update the last activity timestamp for a channel.
    /// </summary>
    public void UpdateLastActivity(string channelId)
    {
        var channel = GetById(channelId);
        if (channel != null)
        {
            channel.LastActivityAt = DateTime.UtcNow;
            Save(channel);
        }
    }

    /// <summary>
    /// Update the last activity timestamp for a channel asynchronously.
    /// </summary>
    public async Task UpdateLastActivityAsync(string channelId)
    {
        var channel = await GetByIdAsync(channelId);
        if (channel != null)
        {
            channel.LastActivityAt = DateTime.UtcNow;
            await SaveAsync(channel);
        }
    }
}


