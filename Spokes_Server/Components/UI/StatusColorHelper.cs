using MudBlazor;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Components.UI;

/// <summary>
/// Returns the semantic MudBlazor Color for a given entity or business status.
/// Located in Components.UI to keep Core domain helpers decoupled from UI presentation types.
/// Case-insensitive with whitespace trimming.
/// </summary>
public static class StatusColorHelper
{
    public static Color GetProjectStatusColor(
        string? statusName,
        IEnumerable<ProjectStatusConfig>? statuses,
        bool fallbackToSemantic = false)
    {
        if (!string.IsNullOrWhiteSpace(statusName) && statuses != null)
        {
            var match = statuses.FirstOrDefault(s =>
                string.Equals(s.Name, statusName, StringComparison.OrdinalIgnoreCase));
            if (match != null && Enum.TryParse<Color>(match.Color, out var parsed))
            {
                return parsed;
            }
        }

        return fallbackToSemantic ? GetColor(statusName) : Color.Default;
    }

    public static Color GetColor(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Color.Default;

        return status.Trim().ToLowerInvariant() switch
        {
            // Positive / Completed / Approved
            "paid" or "fully billed" or "approved" or "accepted" or "quote accepted" or "active" => Color.Success,

            // Informational / In-flight / Invoiced
            "final" or "partially paid" or "partially billed" or "payable" or "submitted" or "quoted" => Color.Info,

            // Cautionary / Awaiting Action
            "pending" or "awaiting approval" or "discovery" or "in progress" => Color.Warning,

            // Error / Negative / Overdue
            "overdue" or "clawback" or "rejected" or "cancelled" or "canceled" or "failed" => Color.Error,

            // Neutral / Draft / Closed / Written Off
            "written off" => Color.Secondary,
            "draft" or "closed" or "completed" or "project in progress" or "inactive" or "none" => Color.Default,

            _ => Color.Default
        };
    }
}

/// <summary>
/// Shared UI presentation mappings for Project Board property types and option colors.
/// </summary>
public static class BoardUiHelper
{
    public static string GetPropertyIcon(BoardPropertyType type) => type switch
    {
        BoardPropertyType.Select => Icons.Material.Filled.ArrowDropDownCircle,
        BoardPropertyType.MultiSelect => Icons.Material.Filled.ChecklistRtl,
        BoardPropertyType.Person => Icons.Material.Filled.Person,
        BoardPropertyType.Date => Icons.Material.Filled.CalendarToday,
        BoardPropertyType.Checkbox => Icons.Material.Filled.CheckBox,
        BoardPropertyType.URL => Icons.Material.Filled.Link,
        BoardPropertyType.Email => Icons.Material.Filled.Email,
        BoardPropertyType.Number => Icons.Material.Filled.Numbers,
        BoardPropertyType.Phone => Icons.Material.Filled.Phone,
        BoardPropertyType.Project => Icons.Material.Filled.AccountTree,
        _ => Icons.Material.Filled.ShortText
    };

    public static string GetOptionCssColor(string? colorName) => colorName switch
    {
        "gray" => "var(--mud-palette-grey)",
        "brown" => "#795548",
        "orange" => "var(--mud-palette-warning)",
        "yellow" => "#FBC02D",
        "green" => "var(--mud-palette-success)",
        "blue" => "var(--mud-palette-info)",
        "purple" => "#9C27B0",
        "pink" => "#E91E63",
        "red" => "var(--mud-palette-error)",
        _ => "var(--mud-palette-text-primary)"
    };
}

/// <summary>
/// Shared UI presentation mappings for Chat channels.
/// </summary>
public static class ChatUiHelper
{
    public static string GetChannelIcon(ChatChannel? channel, bool preferVoiceIcon = false, string? defaultIcon = null)
    {
        if (!string.IsNullOrEmpty(channel?.Icon))
            return channel.Icon;

        if (preferVoiceIcon && channel?.IsVoiceChannel == true)
            return Icons.Material.Filled.VolumeUp;

        if (!string.IsNullOrEmpty(defaultIcon))
            return defaultIcon;

        return channel?.ChannelType switch
        {
            ChatChannelType.General => Icons.Material.Filled.Public,
            ChatChannelType.Project => Icons.Material.Filled.Folder,
            ChatChannelType.Team => Icons.Material.Filled.Groups,
            ChatChannelType.Direct => Icons.Material.Filled.Person,
            ChatChannelType.Group => Icons.Material.Filled.Group,
            _ => Icons.Material.Filled.Tag
        };
    }

    public static string GetNotificationIcon(string? level) => level switch
    {
        ChatNotificationLevel.None => Icons.Material.Filled.NotificationsOff,
        ChatNotificationLevel.Mentions => Icons.Material.Filled.AlternateEmail,
        _ => Icons.Material.Filled.Notifications
    };

    public static Color GetNotificationColor(string? level) => level switch
    {
        ChatNotificationLevel.None => Color.Error,
        ChatNotificationLevel.Mentions => Color.Warning,
        _ => Color.Success
    };
}
