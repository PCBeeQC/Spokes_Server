namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




/// <summary>
/// Tracks when a user last read each chat channel.
/// Used for calculating unread message counts that persist across sessions.
/// </summary>
public class ChatReadState : IDataEntity
{
    /// <summary>
    /// Composite ID: "{UserId}_{ChannelId}"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    

    public string UserId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of when the user last read this channel.
    /// Messages with SentAt after this time are considered unread.
    /// </summary>
    public DateTime LastReadAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Notification level for this channel: "All", "Mentions", or "None".
    /// Null means use the default for the channel type.
    /// </summary>
    public string? NotificationLevel { get; set; }

    public static string CreateId(string userId, string channelId) => $"{userId}_{channelId}";

    /// <summary>
    /// Returns the default notification level for a given channel type.
    /// DMs default to "All"; everything else defaults to "All".
    /// </summary>
    public static string DefaultNotificationLevel(string? channelType, bool isVoiceChannel, string defaultPublicSub = "Mentions")
    {
        if (isVoiceChannel)
            return "Mentions";

        // Public/General channels default to the configured value; everything else defaults to All
        if (channelType == nameof(ChatChannelType.General))
            return defaultPublicSub;
        return "All";
    }
}



