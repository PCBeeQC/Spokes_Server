namespace Spokes_Server.Core.Models.Communication.Notifications;

public static class NotificationCategories
{
    public const string Chat = "chat";
    public const string Call = "call";
    public const string Calendar = "calendar";
}

public class NotificationCategoryDefinition
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Icon { get; init; } = MudBlazor.Icons.Material.Filled.Notifications;
    public string DefaultSoundId { get; init; } = "spokes_default";
    public string DefaultSoundFileName { get; init; } = "spokesnotif1.wav";
    public int SortOrder { get; init; }
}
