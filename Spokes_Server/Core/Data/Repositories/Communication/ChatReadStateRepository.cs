using Spokes_Server.Core.Models.Communication;
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

    protected override string GetFilePath(ChatReadState item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public ChatReadState? GetReadState(string userId, string channelId) =>
        GetById(ChatReadState.CreateId(userId, channelId));

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

    public List<ChatReadState> GetReadStatesForUser(string userId)
    {
        EnsureLoaded();
        return _cache.Values.Where(rs => rs.UserId == userId).ToList();
    }

    /// <summary>
    /// Returns the last read timestamp, or <see cref="DateTime.MinValue"/> if never read.
    /// </summary>
    public DateTime GetLastReadAt(string userId, string channelId) =>
        GetReadState(userId, channelId)?.LastReadAt ?? DateTime.MinValue;

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
    /// Returns the stored notification level, or the default for the channel type if not set.
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
