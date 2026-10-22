namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.HR;

using System;
using System.Collections.Generic;
using System.Linq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Models.Common;

public class ChatMessage : IDataEntity, IHasAssociatedFiles
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ChatMessage)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// The channel this message belongs to. Can be a ProjectId, TeamId, DMChannelId, or "general" for org-wide.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Type of channel: "Project", "Team", "Direct", "General"
    /// </summary>
    public string ChannelType { get; set; } = ChatChannelType.General;

    /// <summary>
    /// The Employee ID of the sender.
    /// </summary>
    public string SenderId { get; set; } = string.Empty;

    /// <summary>
    /// Message content. Supports Markdown formatting.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Type of the message. Default is "Text". Other options: "CallInvite"
    /// <summary>
    /// Type of message: Text, CallInvite, etc.
    /// </summary>
    public string MessageType { get; set; } = ChatMessageType.Text;

    /// <summary>
    /// Metadata related to the call if MessageType == ChatMessageType.CallInvite (Active, Joined, Declined, Ended)
    /// </summary>
    public string CallStatus { get; set; } = string.Empty;

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAt { get; set; }
    public bool IsDeleted { get; set; } = false;
    public bool IsEncrypted { get; set; } = false;

    /// <summary>
    /// Simple emoji reactions as strings (e.g., "👍", "❤️").
    /// Format: "emoji:userId" for tracking who reacted.
    /// </summary>
    public List<string> Reactions { get; set; } = new();

    /// <summary>
    /// For threaded replies - the ID of the parent message.
    /// </summary>
    public string? ReplyToId { get; set; }

    /// <summary>
    /// File attachments for this message.
    /// </summary>
    public List<ChatAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// IDs of users explicitly @mentioned in this message.
    /// </summary>
    public List<string> MentionedUserIds { get; set; } = new();

    /// <summary>
    /// IDs of calendar events explicitly #mentioned in this message.
    /// </summary>
    public List<string> MentionedEventIds { get; set; } = new();

    /// <summary>
    /// IDs of albums explicitly #mentioned in this message.
    /// </summary>
    public List<string> MentionedAlbumIds { get; set; } = new();

    /// <summary>
    /// Whether the message contains @everyone.
    /// </summary>
    public bool MentionsEveryone { get; set; }

    /// <summary>
    /// Whether the message contains @team.
    /// </summary>
    public bool MentionsTeam { get; set; }

    /// <summary>
    /// List of User IDs who have seen this message.
    /// </summary>
    public List<string> ReadBy { get; set; } = new();

    /// <summary>
    /// Checks whether a given user has read this message.
    /// The sender is always considered to have read their own message.
    /// </summary>
    public bool IsReadBy(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        if (userId == SenderId) return true;
        return ReadBy != null && ReadBy.Contains(userId);
    }

    /// <summary>
    /// Returns all user IDs who have seen this message, ensuring the sender is included.
    /// </summary>
    public IEnumerable<string> GetAllReaders()
    {
        if (ReadBy == null || ReadBy.Count == 0)
        {
            if (!string.IsNullOrEmpty(SenderId))
            {
                yield return SenderId;
            }
            yield break;
        }

        bool senderYielded = false;
        foreach (var reader in ReadBy)
        {
            if (reader == SenderId)
            {
                senderYielded = true;
            }
            yield return reader;
        }

        if (!senderYielded && !string.IsNullOrEmpty(SenderId))
        {
            yield return SenderId;
        }
    }

    public IEnumerable<string> GetAssociatedFilePaths()
    {
        return Attachments?.Select(a => a.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)) ?? Enumerable.Empty<string>();
    }
}

public class ChatAttachment : IFileAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool HasServerThumbnail { get; set; } = false;
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ThumbnailUrl
    {
        get
        {
            if (!HasServerThumbnail || string.IsNullOrWhiteSpace(FilePath)) 
                return FilePath;
                
            if (FilePath.StartsWith("/spokesapi/files/"))
            {
                return FilePath.Replace("/spokesapi/files/", "/spokesapi/files/thumb/");
            }
            if (FilePath.StartsWith("/internal/attachments/"))
            {
                return FilePath.Replace("/internal/attachments/", "/internal/attachments/thumb/");
            }
            if (FilePath.StartsWith("/spokesapi/attachments/"))
            {
                return FilePath.Replace("/spokesapi/attachments/", "/spokesapi/attachments/thumb/");
            }
            
            return FilePath;
        }
    }
}

public static class ChatChannelType
{
    public const string Project = "Project";
    public const string Team = "Team";
    public const string Direct = "Direct";
    public const string General = "General";
    public const string Group = "Group";
}

public static class ChatMessageType
{
    public const string Text = "Text";
    public const string CallInvite = "CallInvite";
    public static readonly string[] All = [Text, CallInvite];
}

public static class CallStatus
{
    public const string Active = "Active";
    public const string Joined = "Joined";
    public const string Declined = "Declined";
    public const string Ended = "Ended";
    public static readonly string[] All = [Active, Joined, Declined, Ended];
}



