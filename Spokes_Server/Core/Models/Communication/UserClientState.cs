namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;
using System;
using System.Collections.Generic;

/// <summary>
/// Persists mobile client state for a user, including active channel and per-channel message drafts.
/// </summary>
public class UserClientState : IDataEntity
{
    /// <summary>
    /// Entity ID is UserId. One document per user: {userId}.json.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The channel last opened or actively typed in.
    /// </summary>
    public string? ActiveChannelId { get; set; }

    /// <summary>
    /// Unsent drafts per channel. Key: ChannelId, Value: Draft text.
    /// </summary>
    public Dictionary<string, string> ChannelDrafts { get; set; } = new();

    /// <summary>
    /// Timestamp of last activity or draft modification.
    /// </summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}
