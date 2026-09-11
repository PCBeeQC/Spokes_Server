namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;




public class ChatChannel : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ChatChannel)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// Display name for the channel. For DMs, this might be auto-generated.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// MudBlazor Icon constant string (e.g. @Icons.Material.Filled.Work)
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// For custom dynamic routing, the ID of the ChatCategory this channel belongs to.
    /// </summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// Type of channel: "Project", "Team", "Direct", "General"
    /// </summary>
    public string ChannelType { get; set; } = ChatChannelType.General;

    /// <summary>
    /// For Project/Team channels, the associated ProjectId or TeamId.
    /// For General channels, this can be empty or a custom identifier.
    /// </summary>
    public string? LinkedEntityId { get; set; }

    /// <summary>
    /// Description of the channel purpose.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// For Direct Messages - the list of participant Employee IDs.
    /// For other channel types, this can be empty (all project/team members have access).
    /// </summary>
    public List<string> ParticipantIds { get; set; } = new();

    /// <summary>
    /// Whether this is the default General channel for the organization.
    /// There should only be one channel with this set to true.
    /// </summary>
    public bool IsDefaultGeneral { get; set; } = false;

    /// <summary>
    /// Identifies if this channel inherently acts as a Discord-style Voice Channel.
    /// </summary>
    public bool IsVoiceChannel { get; set; } = false;

    /// <summary>
    /// Identifies if this channel restricts posting to specific users/teams.
    /// </summary>
    public bool IsAnnouncementOnly { get; set; } = false;

    /// <summary>
    /// Direct messages are personal 1-to-1 conversations and cannot be converted into announcement channels.
    /// Announcements are only applicable to broadcast channels (General, Team, Project, Group).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsAnnouncements => ChannelType != ChatChannelType.Direct;

    /// <summary>
    /// Normalizes channel state to maintain domain invariants.
    /// If the channel does not support announcements, announcement flags and access lists are reset.
    /// </summary>
    public void Normalize()
    {
        if (!SupportsAnnouncements)
        {
            IsAnnouncementOnly = false;
            AllowedPostUserIds?.Clear();
            AllowedPostTeamIds?.Clear();
        }
    }

    /// <summary>
    /// If IsAnnouncementOnly is true, this defines which users are allowed to post.
    /// </summary>
    public List<string> AllowedPostUserIds { get; set; } = new();

    /// <summary>
    /// If IsAnnouncementOnly is true, this defines which teams are allowed to post.
    /// </summary>
    public List<string> AllowedPostTeamIds { get; set; } = new();

    /// <summary>
    /// Pinned message IDs for this channel.
    /// </summary>
    public List<string> PinnedMessageIds { get; set; } = new();

    /// <summary>
    /// Global display order for organizing channels in the sidebar.
    /// </summary>
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// For Team-based access control (e.g. "Managers & Sales").
    /// </summary>
    public List<string> AllowedTeamIds { get; set; } = new();

    public bool IsArchived { get; set; } = false;

    // Data-at-Rest E2EE
    public bool IsEncrypted { get; set; } = false;
    public Dictionary<string, string> EncryptedChannelKeys { get; set; } = new();

    /// <summary>
    /// Preferred Audio Bitrate in Kbps for Voice Channels (e.g. 64, 96, 128, 256, 384).
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 64;


    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedById { get; set; } = string.Empty;

    /// <summary>
    /// Last activity timestamp for sorting channels by recency.
    /// </summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
}



