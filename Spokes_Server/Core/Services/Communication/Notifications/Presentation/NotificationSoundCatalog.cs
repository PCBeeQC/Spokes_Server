using Spokes_Server.Core.Models.Communication.Notifications;

namespace Spokes_Server.Core.Services.Communication.Notifications.Presentation;

public static class NotificationSoundCatalog
{
    private static readonly List<NotificationSoundDefinition> _sounds =
    [
        new NotificationSoundDefinition
        {
            Id = NotificationSoundIds.SpokesDefault,
            DisplayName = "Spokes Default",
            FileName = null,
            Description = "Official Spokes sound tailored for this category.",
            SupportedCategories = ["*"],
            IsSpecial = true
        },
        new NotificationSoundDefinition
        {
            Id = NotificationSoundIds.SystemDefault,
            DisplayName = "System Default",
            FileName = null,
            Description = "Uses your device's native ringtone on mobile and stays silent on desktop.",
            SupportedCategories = ["*"],
            IsSpecial = true
        },
        new NotificationSoundDefinition
        {
            Id = "spokesnotif1",
            DisplayName = "Spokes Chime",
            FileName = "spokesnotif1.wav",
            Description = "Classic Spokes chime sound.",
            SupportedCategories = [NotificationCategories.Chat, NotificationCategories.Calendar, NotificationCategories.Email]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_alert_quick_chime_766",
            DisplayName = "Quick Chime",
            FileName = "mixkit_alert_quick_chime_766.wav",
            Description = "Crisp and subtle chime.",
            SupportedCategories = [NotificationCategories.Calendar]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_marimba_ringtone_1359",
            DisplayName = "Marimba Ringtone",
            FileName = "mixkit_marimba_ringtone_1359.wav",
            Description = "Modern rhythmic marimba ringtone.",
            SupportedCategories = [NotificationCategories.Call]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_cowbell_sharp_hit_1743",
            DisplayName = "Cowbell Hit",
            FileName = "mixkit_cowbell_sharp_hit_1743.wav",
            Description = "Sharp, distinct percussion strike.",
            SupportedCategories = [NotificationCategories.Chat, NotificationCategories.Calendar, NotificationCategories.Email]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_happy_bell_alert_601",
            DisplayName = "Happy Bell",
            FileName = "mixkit_happy_bell_alert_601.wav",
            Description = "Warm, uplifting bell tone.",
            SupportedCategories = [NotificationCategories.Chat, NotificationCategories.Calendar, NotificationCategories.Email]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_uplifting_bells_notification_938",
            DisplayName = "Uplifting Bells",
            FileName = "mixkit_uplifting_bells_notification_938.wav",
            Description = "Gentle harmonic chimes.",
            SupportedCategories = [NotificationCategories.Calendar]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_long_pop_2358",
            DisplayName = "Pop",
            FileName = "mixkit_long_pop_2358.wav",
            Description = "Soft bubble pop.",
            SupportedCategories = [NotificationCategories.Calendar]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_vintage_telephone_ringtone_1356",
            DisplayName = "Vintage Phone",
            FileName = "mixkit_vintage_telephone_ringtone_1356.wav",
            Description = "Traditional mechanical telephone ring.",
            SupportedCategories = [NotificationCategories.Call]
        },
        new NotificationSoundDefinition
        {
            Id = "mixkit_on_hold_ringtone_1361",
            DisplayName = "On-Hold Ringtone",
            FileName = "mixkit_on_hold_ringtone_1361.wav",
            Description = "Calm harmonic electronic ring.",
            SupportedCategories = [NotificationCategories.Call]
        }
    ];

    public static IReadOnlyList<NotificationSoundDefinition> GetAllSounds() => _sounds;

    private static (NotificationCategoryDefinition? Category, NotificationSoundDefinition? DefaultSound) ResolveCategoryDefault(string categoryId)
    {
        var category = NotificationCategoryRegistry.GetCategory(categoryId);
        var defaultSound = _sounds.FirstOrDefault(s => !s.IsSpecial && s.FileName != null && s.FileName.Equals(category?.DefaultSoundFileName, StringComparison.OrdinalIgnoreCase));
        return (category, defaultSound);
    }

    public static string NormalizeSoundIdForCategory(string categoryId, string? soundId)
    {
        var (_, defaultSound) = ResolveCategoryDefault(categoryId);
        if (string.IsNullOrEmpty(soundId) || soundId == NotificationSoundIds.SpokesDefault || soundId == defaultSound?.Id)
        {
            return NotificationSoundIds.SpokesDefault;
        }
        return soundId;
    }

    private static string ResolveSoundFileOrCategoryDefault(string categoryId, string? soundId)
    {
        if (!string.IsNullOrEmpty(soundId) && !string.Equals(soundId, NotificationSoundIds.SpokesDefault, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedId = soundId.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? soundId[..^4]
                : soundId;
            var sound = GetSoundById(normalizedId);
            if (!string.IsNullOrEmpty(sound?.FileName))
            {
                return sound.FileName;
            }
        }

        return NotificationCategoryRegistry.GetCategory(categoryId)?.DefaultSoundFileName ?? "spokesnotif1.wav";
    }

    /// <summary>
    /// Resolves the call ringtone .wav file name for in-call caller ringback playback.
    /// When the user has selected System Default or Spokes Default, falls back to the Call category default
    /// ("mixkit_marimba_ringtone_1359.wav") so the calling user always hears an audible ringback tone.
    /// </summary>
    public static string ResolveCallRingbackFileName(string? soundId) =>
        ResolveSoundFileOrCategoryDefault(NotificationCategories.Call, soundId);

    public static IReadOnlyList<NotificationSoundDefinition> GetSoundsForCategory(string categoryId)
    {
        var (category, defaultSound) = ResolveCategoryDefault(categoryId);
        var defaultDisplayName = defaultSound != null ? $"{defaultSound.DisplayName} (Default)" : "Spokes Default";

        var result = new List<NotificationSoundDefinition>
        {
            new NotificationSoundDefinition
            {
                Id = NotificationSoundIds.SpokesDefault,
                DisplayName = defaultDisplayName,
                FileName = defaultSound?.FileName,
                Description = $"Default sound for {category?.DisplayName ?? categoryId}.",
                SupportedCategories = [categoryId],
                IsSpecial = true
            },
            new NotificationSoundDefinition
            {
                Id = NotificationSoundIds.SystemDefault,
                DisplayName = "System Default",
                FileName = null,
                Description = "Uses your device's native ringtone on mobile and stays silent on desktop.",
                SupportedCategories = [categoryId],
                IsSpecial = true
            }
        };

        var otherSounds = _sounds
            .Where(s => !s.IsSpecial && s.SupportsCategory(categoryId) && (defaultSound == null || !string.Equals(s.Id, defaultSound.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        result.AddRange(otherSounds);
        return result;
    }

    public static string GetSoundDisplayName(string categoryId, string? soundId)
    {
        if (string.IsNullOrEmpty(soundId) || string.Equals(soundId, NotificationSoundIds.SpokesDefault, StringComparison.OrdinalIgnoreCase))
        {
            var (_, defaultSound) = ResolveCategoryDefault(categoryId);
            return defaultSound != null ? $"{defaultSound.DisplayName} (Default)" : "Spokes Default";
        }

        if (string.Equals(soundId, NotificationSoundIds.SystemDefault, StringComparison.OrdinalIgnoreCase))
        {
            return "System Default";
        }

        var match = GetSoundById(soundId);
        return match?.DisplayName ?? soundId;
    }

    public static NotificationSoundDefinition? GetSoundById(string soundId) =>
        _sounds.FirstOrDefault(s => string.Equals(s.Id, soundId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the sound file name for desktop playback.
    /// Returns null if System Default (which stays silent on desktop to let the OS handle it).
    /// </summary>
    public static string? ResolveEffectiveSoundFileName(string categoryId, string? soundId) =>
        string.Equals(soundId, NotificationSoundIds.SystemDefault, StringComparison.OrdinalIgnoreCase)
            ? null // Desktop stays silent
            : ResolveSoundFileOrCategoryDefault(categoryId, soundId);

    /// <summary>
    /// Resolves the sound string for APNs and Android FCM push payloads.
    /// Returns "default" for System Default, or the exact .wav file name for bundled sounds.
    /// </summary>
    public static string GetEffectivePushSound(string categoryId, string? soundId) =>
        string.Equals(soundId, NotificationSoundIds.SystemDefault, StringComparison.OrdinalIgnoreCase)
            ? "default"
            : ResolveSoundFileOrCategoryDefault(categoryId, soundId);
}
