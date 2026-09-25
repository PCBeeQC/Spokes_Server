namespace Spokes_Server.Core.Models.Communication.Notifications;

public static class NotificationSoundIds
{
    public const string SystemDefault = "system_default";
    public const string SpokesDefault = "spokes_default";
}

public class NotificationSoundDefinition
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> SupportedCategories { get; init; } = [];
    public bool IsSpecial { get; init; }

    public bool SupportsCategory(string categoryId)
    {
        if (SupportedCategories.Count == 0 || SupportedCategories.Contains("*"))
            return true;
        return SupportedCategories.Contains(categoryId, StringComparer.OrdinalIgnoreCase);
    }
}
