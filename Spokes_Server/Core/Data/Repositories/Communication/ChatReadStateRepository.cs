using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data.Repositories.Core;

namespace Spokes_Server.Core.Data.Repositories.Communication;

public class ChatReadStateRepository : JsonRepository<ChatReadState>
{
    private readonly CompanyProfileRepository _companyProfile;

    public ChatReadStateRepository(DiskPersistenceService writer, IConfiguration config, CompanyProfileRepository companyProfile)
        : base(writer, Path.Combine(config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data"), "chat", "readstates"), "*.json")
    {
        _companyProfile = companyProfile;
    }

    protected override string GetFilePath(ChatReadState item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    /// <summary>
    /// Get the read state for a specific user and channel.
    /// </summary>
    public ChatReadState? GetReadState(string userId, string channelId)
    {
        var id = ChatReadState.CreateId(userId, channelId);
        return GetById(id);
    }

    /// <summary>
    /// Mark a channel as read for a user (updates LastReadAt to now).
    /// </summary>
    public void MarkAsRead(string userId, string channelId, string? channelType = null, bool isVoiceChannel = false)
    {
        var id = ChatReadState.CreateId(userId, channelId);
        var state = GetById(id);

        if (state == null)
        {
            var defaultPublicSub = _companyProfile.Get().DefaultPublicChannelSubscription;
            state = new ChatReadState
            {
                Id = id,
                UserId = userId,
                ChannelId = channelId,
                LastReadAt = DateTime.UtcNow,
                NotificationLevel = ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel, defaultPublicSub)
            };
        }
        else
        {
            state.LastReadAt = DateTime.UtcNow;
        }

        Save(state);
    }

    /// <summary>
    /// Get all read states for a user.
    /// </summary>
    public List<ChatReadState> GetReadStatesForUser(string userId)
    {
        return _cache.Values.Where(rs => rs.UserId == userId).ToList();
    }

    /// <summary>
    /// Get the last read timestamp for a user on a channel.
    /// Returns DateTime.MinValue if never read.
    /// </summary>
    public DateTime GetLastReadAt(string userId, string channelId)
    {
        var state = GetReadState(userId, channelId);
        return state?.LastReadAt ?? DateTime.MinValue;
    }

    /// <summary>
    /// Set the notification level for a user on a channel.
    /// </summary>
    public void SetNotificationLevel(string userId, string channelId, string level)
    {
        var id = ChatReadState.CreateId(userId, channelId);
        var state = GetById(id);

        if (state == null)
        {
            state = new ChatReadState
            {
                Id = id,
                UserId = userId,
                ChannelId = channelId,
                NotificationLevel = level
            };
        }
        else
        {
            state.NotificationLevel = level;
        }

        Save(state);
    }

    /// <summary>
    /// Get the notification level for a user on a channel.
    /// Returns the stored value, or the default for the channel type if not set.
    /// </summary>
    public string GetNotificationLevel(string userId, string channelId, string? channelType = null, bool isVoiceChannel = false)
    {
        var state = GetReadState(userId, channelId);

        if (state?.NotificationLevel != null)
            return state.NotificationLevel;

        if (state != null)
            return ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel, "Mentions");

        var defaultPublicSub = _companyProfile.Get().DefaultPublicChannelSubscription;
        return ChatReadState.DefaultNotificationLevel(channelType, isVoiceChannel, defaultPublicSub);
    }
}


