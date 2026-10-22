using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Models.Communication.Notifications;

/// <summary>
/// Strongly typed breakdown of unread badge counts across email and chat channels.
/// </summary>
public class BadgeBreakdownResult
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("breakdown")]
    public Dictionary<string, int> Breakdown { get; set; } = new();
}
