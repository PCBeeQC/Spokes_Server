using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Models.Communication.Notifications;

/// <summary>
/// Strongly typed breakdown of unread badge counts across email and chat channels,
/// including canonical per-thread unread counts and read watermarks for client wake reconciliation.
/// </summary>
public class BadgeBreakdownResult
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("breakdown")]
    public Dictionary<string, int> Breakdown { get; set; } = new();

    /// <summary>
    /// Canonical threadId -> unread count mapping matching PushNotificationRequest.ThreadId / Tag
    /// (e.g., "{channelId}", "email-{userId}", "mod-{reportId}").
    /// </summary>
    [JsonPropertyName("unreadByThread")]
    public Dictionary<string, int> UnreadByThread { get; set; } = new();

    /// <summary>
    /// Canonical threadId -> UTC timestamp when the user last read/visited that thread.
    /// Enables clients to deterministically discard any queued push with SentAtUtc &lt;= watermark,
    /// even if newer unread messages subsequently arrived in the same channel.
    /// </summary>
    [JsonPropertyName("readWatermarksUtc")]
    public Dictionary<string, DateTime> ReadWatermarksUtc { get; set; } = new();

    [JsonPropertyName("serverTimeUtc")]
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
}
