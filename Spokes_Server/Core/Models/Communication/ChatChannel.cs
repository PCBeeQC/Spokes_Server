namespace Spokes_Server.Core.Models.Communication;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

public class ChatChannel : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is ChatChannel other && Id == other.Id;
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
    public List<string> ParticipantIds { get; set; } = [];

    /// <summary>
    /// Whether this is the default General channel for the organization.
    /// There should only be one channel with this set to true.
    /// </summary>
    public bool IsDefaultGeneral { get; set; }

    /// <summary>
    /// Identifies if this channel inherently acts as a Discord-style Voice Channel.
    /// </summary>
    public bool IsVoiceChannel { get; set; }

    /// <summary>
    /// Identifies if this channel restricts posting to specific users/teams.
    /// </summary>
    public bool IsAnnouncementOnly { get; set; }

    /// <summary>
    /// Direct messages are personal 1-to-1 conversations and cannot be converted into announcement channels.
    /// Announcements are only applicable to broadcast channels (General, Team, Project, Group).
    /// </summary>
    [JsonIgnore]
    public bool SupportsAnnouncements => ChannelType != ChatChannelType.Direct;

    /// <summary>
    /// A channel is public when it is a General channel with no team or participant restrictions.
    /// </summary>
    [JsonIgnore]
    public bool IsPublicChannel => ChannelType == ChatChannelType.General &&
                                   (AllowedTeamIds == null || AllowedTeamIds.Count == 0) &&
                                   (ParticipantIds == null || ParticipantIds.Count == 0);

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
    public List<string> AllowedPostUserIds { get; set; } = [];

    /// <summary>
    /// If IsAnnouncementOnly is true, this defines which teams are allowed to post.
    /// </summary>
    public List<string> AllowedPostTeamIds { get; set; } = [];

    /// <summary>
    /// Pinned message IDs for this channel.
    /// </summary>
    public List<string> PinnedMessageIds { get; set; } = [];

    /// <summary>
    /// Global display order for organizing channels in the sidebar.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// For Team-based access control (e.g. "Managers & Sales").
    /// </summary>
    public List<string> AllowedTeamIds { get; set; } = [];

    public bool IsArchived { get; set; }

    // Data-at-Rest E2EE
    public bool IsEncrypted { get; set; }
    public Dictionary<string, string> EncryptedChannelKeys { get; set; } = [];
    public int CurrentKeyEpoch { get; set; } = 0;
    public Dictionary<int, Dictionary<string, string>> EncryptedEpochKeys { get; set; } = [];

    public bool TryGetEncryptedKeyForEpoch(int epoch, string keyOwnerId, out string cipherKey)
    {
        cipherKey = string.Empty;
        if (string.IsNullOrEmpty(keyOwnerId)) return false;

        if (epoch == CurrentKeyEpoch &&
            EncryptedChannelKeys != null &&
            EncryptedChannelKeys.TryGetValue(keyOwnerId, out var currentKey) &&
            !string.IsNullOrEmpty(currentKey))
        {
            cipherKey = currentKey;
            return true;
        }

        if (EncryptedEpochKeys != null &&
            EncryptedEpochKeys.TryGetValue(epoch, out var epochMap) &&
            epochMap != null &&
            epochMap.TryGetValue(keyOwnerId, out var epochKey) &&
            !string.IsNullOrEmpty(epochKey))
        {
            cipherKey = epochKey;
            return true;
        }

        return false;
    }

    public bool HasAnyEpochKey(string keyOwnerId)
    {
        if (string.IsNullOrEmpty(keyOwnerId)) return false;
        if (EncryptedChannelKeys != null && EncryptedChannelKeys.ContainsKey(keyOwnerId)) return true;
        return EncryptedEpochKeys != null &&
               EncryptedEpochKeys.Values.Any(d => d != null && d.ContainsKey(keyOwnerId));
    }

    public IEnumerable<string> GetAllEncryptedKeysForOwner(string keyOwnerId)
    {
        if (string.IsNullOrEmpty(keyOwnerId)) yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (EncryptedChannelKeys != null &&
            EncryptedChannelKeys.TryGetValue(keyOwnerId, out var currentKey) &&
            !string.IsNullOrEmpty(currentKey) &&
            seen.Add(currentKey))
        {
            yield return currentKey;
        }

        if (EncryptedEpochKeys != null)
        {
            foreach (var kvp in EncryptedEpochKeys.OrderByDescending(k => k.Key))
            {
                if (kvp.Value != null &&
                    kvp.Value.TryGetValue(keyOwnerId, out var epochKey) &&
                    !string.IsNullOrEmpty(epochKey) &&
                    seen.Add(epochKey))
                {
                    yield return epochKey;
                }
            }
        }
    }

    public bool RemoveUserKeysFromAllEpochs(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;

        bool removed = false;
        if (EncryptedChannelKeys != null && EncryptedChannelKeys.Remove(userId))
        {
            removed = true;
        }

        if (EncryptedEpochKeys != null)
        {
            foreach (var epochMap in EncryptedEpochKeys.Values)
            {
                if (epochMap != null && epochMap.Remove(userId))
                {
                    removed = true;
                }
            }
        }

        return removed;
    }

    public ChatChannel Clone()
    {
        var epochKeysCopy = new Dictionary<int, Dictionary<string, string>>();
        if (EncryptedEpochKeys != null)
        {
            foreach (var kvp in EncryptedEpochKeys)
            {
                epochKeysCopy[kvp.Key] = kvp.Value != null
                    ? new Dictionary<string, string>(kvp.Value)
                    : [];
            }
        }

        return new ChatChannel
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            CategoryId = CategoryId,
            ChannelType = ChannelType,
            LinkedEntityId = LinkedEntityId,
            Description = Description,
            ParticipantIds = ParticipantIds != null ? [.. ParticipantIds] : [],
            IsDefaultGeneral = IsDefaultGeneral,
            IsVoiceChannel = IsVoiceChannel,
            IsAnnouncementOnly = IsAnnouncementOnly,
            AllowedPostUserIds = AllowedPostUserIds != null ? [.. AllowedPostUserIds] : [],
            AllowedPostTeamIds = AllowedPostTeamIds != null ? [.. AllowedPostTeamIds] : [],
            PinnedMessageIds = PinnedMessageIds != null ? [.. PinnedMessageIds] : [],
            DisplayOrder = DisplayOrder,
            AllowedTeamIds = AllowedTeamIds != null ? [.. AllowedTeamIds] : [],
            IsArchived = IsArchived,
            IsEncrypted = IsEncrypted,
            EncryptedChannelKeys = EncryptedChannelKeys != null ? new Dictionary<string, string>(EncryptedChannelKeys) : [],
            CurrentKeyEpoch = CurrentKeyEpoch,
            EncryptedEpochKeys = epochKeysCopy,
            AudioBitrateKbps = AudioBitrateKbps,
            CreatedAt = CreatedAt,
            CreatedById = CreatedById,
            LastActivityAt = LastActivityAt
        };
    }

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
