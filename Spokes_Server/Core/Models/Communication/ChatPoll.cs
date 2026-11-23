namespace Spokes_Server.Core.Models.Communication;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Spokes_Server.Core.Data;

/// <summary>
/// Represents an interactive poll posted within a chat channel or direct message.
/// </summary>
public class ChatPoll : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChannelId { get; set; } = string.Empty;
    public string CreatorId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public List<ChatPollOption> Options { get; set; } = new();
    public bool AllowMultipleAnswers { get; set; }
    public bool AllowAddOptions { get; set; }
    public bool IsAnonymous { get; set; }
    public bool IsClosed { get; set; }
    public bool IsEncrypted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    public bool IsExpired(DateTime utcNow) =>
        IsClosed || (ExpiresAt.HasValue && ExpiresAt.Value <= utcNow);

    [JsonIgnore]
    public int TotalVotes => Options?.Sum(o => o.VoteCount) ?? 0;
}

/// <summary>
/// Represents a selectable answer option within a <see cref="ChatPoll"/>.
/// When <see cref="ChatPoll.IsAnonymous"/> is true, <see cref="VoterIds"/> and <see cref="AddedByUserId"/>
/// are never populated in RAM or on disk; keyed option-scoped <see cref="AnonymousVoterTokens"/> prevent duplicate voting
/// while keeping persisted ballots anonymous.
/// </summary>
public class ChatPollOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = string.Empty;
    public string? Emoji { get; set; }
    public string? AddedByUserId { get; set; }
    public int AnonymousVoteCount { get; set; }
    public List<string> VoterIds { get; set; } = new();
    public List<string> AnonymousVoterTokens { get; set; } = new();

    [JsonIgnore]
    public int VoteCount => Math.Max(AnonymousVoteCount, Math.Max(AnonymousVoterTokens?.Count ?? 0, VoterIds?.Count ?? 0));
}
