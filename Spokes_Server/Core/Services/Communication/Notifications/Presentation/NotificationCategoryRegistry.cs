using MudBlazor;
using Spokes_Server.Core.Models.Communication.Notifications;

namespace Spokes_Server.Core.Services.Communication.Notifications.Presentation;

public static class NotificationCategoryRegistry
{
    private static readonly List<NotificationCategoryDefinition> _categories =
    [
        new NotificationCategoryDefinition
        {
            Id = NotificationCategories.Chat,
            DisplayName = "Chat Messages",
            Description = "Notifications for direct messages, mentions, and channel updates.",
            Icon = Icons.Material.Filled.ChatBubble,
            DefaultSoundId = NotificationSoundIds.SpokesDefault,
            DefaultSoundFileName = "spokesnotif1.wav",
            SortOrder = 1
        },
        new NotificationCategoryDefinition
        {
            Id = NotificationCategories.Call,
            DisplayName = "Incoming Calls",
            Description = "Ringtone and alerts for incoming voice call invitations.",
            Icon = Icons.Material.Filled.Phone,
            DefaultSoundId = NotificationSoundIds.SpokesDefault,
            DefaultSoundFileName = "mixkit_marimba_ringtone_1359.wav",
            SortOrder = 2
        },
        new NotificationCategoryDefinition
        {
            Id = NotificationCategories.Calendar,
            DisplayName = "Calendar Alerts",
            Description = "Upcoming event reminders and calendar notifications.",
            Icon = Icons.Material.Filled.CalendarMonth,
            DefaultSoundId = NotificationSoundIds.SpokesDefault,
            DefaultSoundFileName = "mixkit_alert_quick_chime_766.wav",
            SortOrder = 3
        },
        new NotificationCategoryDefinition
        {
            Id = NotificationCategories.Email,
            DisplayName = "Incoming Emails",
            Description = "Notifications for new incoming email messages.",
            Icon = Icons.Material.Filled.Email,
            DefaultSoundId = NotificationSoundIds.SpokesDefault,
            DefaultSoundFileName = "spokesnotif1.wav",
            SortOrder = 4,
            IsBusinessOnly = true
        }
    ];

    public static IReadOnlyList<NotificationCategoryDefinition> GetAllCategories(bool isBusinessEdition = true) =>
        _categories
            .Where(c => !c.IsBusinessOnly || isBusinessEdition)
            .OrderBy(c => c.SortOrder)
            .ToList();

    public static NotificationCategoryDefinition? GetCategory(string categoryId) =>
        _categories.FirstOrDefault(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));
}
