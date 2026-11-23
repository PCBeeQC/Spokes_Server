using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Presentation;

public class NotificationSoundCatalogTests
{
    [Fact]
    public void GetAllSounds_ContainsSpecialAndStandardSounds()
    {
        var sounds = NotificationSoundCatalog.GetAllSounds();

        Assert.Contains(sounds, s => s.Id == NotificationSoundIds.SpokesDefault);
        Assert.Contains(sounds, s => s.Id == NotificationSoundIds.SystemDefault);
        Assert.Contains(sounds, s => s.Id == "spokesnotif1");
        Assert.Contains(sounds, s => s.Id == "mixkit_marimba_ringtone_1359");
    }

    [Fact]
    public void GetSoundsForCategory_FiltersCorrectlyAndDeduplicatesDefaultSound()
    {
        var chatSounds = NotificationSoundCatalog.GetSoundsForCategory(NotificationCategories.Chat);
        var callSounds = NotificationSoundCatalog.GetSoundsForCategory(NotificationCategories.Call);
        var calendarSounds = NotificationSoundCatalog.GetSoundsForCategory(NotificationCategories.Calendar);
        var emailSounds = NotificationSoundCatalog.GetSoundsForCategory(NotificationCategories.Email);

        // Special sounds should appear in all categories
        Assert.Contains(chatSounds, s => s.Id == NotificationSoundIds.SpokesDefault);
        Assert.Contains(chatSounds, s => s.Id == NotificationSoundIds.SystemDefault);
        Assert.Contains(callSounds, s => s.Id == NotificationSoundIds.SpokesDefault);
        Assert.Contains(callSounds, s => s.Id == NotificationSoundIds.SystemDefault);
        Assert.Contains(calendarSounds, s => s.Id == NotificationSoundIds.SpokesDefault);
        Assert.Contains(calendarSounds, s => s.Id == NotificationSoundIds.SystemDefault);
        Assert.Contains(emailSounds, s => s.Id == NotificationSoundIds.SpokesDefault);
        Assert.Contains(emailSounds, s => s.Id == NotificationSoundIds.SystemDefault);

        // Chat default is Spokes Chime, so it should be labeled as Default and not duplicated
        Assert.Contains(chatSounds, s => s.Id == NotificationSoundIds.SpokesDefault && s.DisplayName == "Spokes Chime (Default)");
        Assert.DoesNotContain(chatSounds, s => s.Id == "spokesnotif1");

        // Email uses the exact same sounds as Chat, with Spokes Chime as default
        Assert.Contains(emailSounds, s => s.Id == NotificationSoundIds.SpokesDefault && s.DisplayName == "Spokes Chime (Default)");
        Assert.DoesNotContain(emailSounds, s => s.Id == "spokesnotif1");
        Assert.Equal(chatSounds.Select(s => s.Id).OrderBy(id => id), emailSounds.Select(s => s.Id).OrderBy(id => id));

        // Call default is Marimba Ringtone, so it should be labeled as Default and not duplicated
        Assert.Contains(callSounds, s => s.Id == NotificationSoundIds.SpokesDefault && s.DisplayName == "Marimba Ringtone (Default)");
        Assert.DoesNotContain(callSounds, s => s.Id == "mixkit_marimba_ringtone_1359");

        // Calendar default is Quick Chime, so it should be labeled as Default and not duplicated
        Assert.Contains(calendarSounds, s => s.Id == NotificationSoundIds.SpokesDefault && s.DisplayName == "Quick Chime (Default)");
        Assert.DoesNotContain(calendarSounds, s => s.Id == "mixkit_alert_quick_chime_766");
        // Spokes Chime is available in Calendar as a non-default option
        Assert.Contains(calendarSounds, s => s.Id == "spokesnotif1");
    }

    [Fact]
    public void GetSoundDisplayName_ReturnsTailoredNames()
    {
        Assert.Equal("Spokes Chime (Default)", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Chat, NotificationSoundIds.SpokesDefault));
        Assert.Equal("Spokes Chime (Default)", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Email, NotificationSoundIds.SpokesDefault));
        Assert.Equal("Marimba Ringtone (Default)", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Call, NotificationSoundIds.SpokesDefault));
        Assert.Equal("Quick Chime (Default)", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Calendar, NotificationSoundIds.SpokesDefault));
        Assert.Equal("System Default", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Chat, NotificationSoundIds.SystemDefault));
        Assert.Equal("System Default", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Email, NotificationSoundIds.SystemDefault));
        Assert.Equal("Cowbell Hit", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Chat, "mixkit_cowbell_sharp_hit_1743"));
        Assert.Equal("Cowbell Hit", NotificationSoundCatalog.GetSoundDisplayName(NotificationCategories.Email, "mixkit_cowbell_sharp_hit_1743"));
    }

    [Fact]
    public void ResolveEffectiveSoundFileName_ReturnsNullForSystemDefault()
    {
        var chatSound = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Chat, NotificationSoundIds.SystemDefault);
        var callSound = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Call, NotificationSoundIds.SystemDefault);
        var calSound = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Calendar, NotificationSoundIds.SystemDefault);
        var emailSound = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Email, NotificationSoundIds.SystemDefault);

        Assert.Null(chatSound);
        Assert.Null(callSound);
        Assert.Null(calSound);
        Assert.Null(emailSound);
    }

    [Theory]
    [InlineData(NotificationCategories.Chat, "spokesnotif1.wav")]
    [InlineData(NotificationCategories.Call, "mixkit_marimba_ringtone_1359.wav")]
    [InlineData(NotificationCategories.Calendar, "mixkit_alert_quick_chime_766.wav")]
    [InlineData(NotificationCategories.Email, "spokesnotif1.wav")]
    public void ResolveEffectiveSoundFileName_ReturnsCategoryDefaultWhenSpokesDefaultOrNull(string category, string expectedFileName)
    {
        var fromDefault = NotificationSoundCatalog.ResolveEffectiveSoundFileName(category, NotificationSoundIds.SpokesDefault);
        var fromNull = NotificationSoundCatalog.ResolveEffectiveSoundFileName(category, null);
        var fromEmpty = NotificationSoundCatalog.ResolveEffectiveSoundFileName(category, "");

        Assert.Equal(expectedFileName, fromDefault);
        Assert.Equal(expectedFileName, fromNull);
        Assert.Equal(expectedFileName, fromEmpty);
    }

    [Fact]
    public void ResolveEffectiveSoundFileName_ReturnsCustomSoundWhenSpecified()
    {
        var custom = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Chat, "mixkit_cowbell_sharp_hit_1743");
        var emailCustom = NotificationSoundCatalog.ResolveEffectiveSoundFileName(
            NotificationCategories.Email, "mixkit_cowbell_sharp_hit_1743");

        Assert.Equal("mixkit_cowbell_sharp_hit_1743.wav", custom);
        Assert.Equal("mixkit_cowbell_sharp_hit_1743.wav", emailCustom);
    }

    [Fact]
    public void GetEffectivePushSound_ReturnsDefaultStringForSystemDefault()
    {
        var pushSound = NotificationSoundCatalog.GetEffectivePushSound(
            NotificationCategories.Chat, NotificationSoundIds.SystemDefault);
        var emailPushSound = NotificationSoundCatalog.GetEffectivePushSound(
            NotificationCategories.Email, NotificationSoundIds.SystemDefault);

        Assert.Equal("default", pushSound);
        Assert.Equal("default", emailPushSound);
    }

    [Theory]
    [InlineData(NotificationCategories.Chat, "spokesnotif1.wav")]
    [InlineData(NotificationCategories.Call, "mixkit_marimba_ringtone_1359.wav")]
    [InlineData(NotificationCategories.Calendar, "mixkit_alert_quick_chime_766.wav")]
    [InlineData(NotificationCategories.Email, "spokesnotif1.wav")]
    public void GetEffectivePushSound_ReturnsCategoryDefaultWhenSpokesDefaultOrNull(string category, string expectedFileName)
    {
        var fromDefault = NotificationSoundCatalog.GetEffectivePushSound(category, NotificationSoundIds.SpokesDefault);
        var fromNull = NotificationSoundCatalog.GetEffectivePushSound(category, null);

        Assert.Equal(expectedFileName, fromDefault);
        Assert.Equal(expectedFileName, fromNull);
    }

    [Fact]
    public void GetEffectivePushSound_ReturnsCustomSoundFileName()
    {
        var pushSound = NotificationSoundCatalog.GetEffectivePushSound(
            NotificationCategories.Call, "mixkit_vintage_telephone_ringtone_1356");
        var emailPushSound = NotificationSoundCatalog.GetEffectivePushSound(
            NotificationCategories.Email, "mixkit_happy_bell_alert_601");

        Assert.Equal("mixkit_vintage_telephone_ringtone_1356.wav", pushSound);
        Assert.Equal("mixkit_happy_bell_alert_601.wav", emailPushSound);
    }

    [Theory]
    [InlineData(null, "mixkit_marimba_ringtone_1359.wav")]
    [InlineData("", "mixkit_marimba_ringtone_1359.wav")]
    [InlineData(NotificationSoundIds.SpokesDefault, "mixkit_marimba_ringtone_1359.wav")]
    [InlineData(NotificationSoundIds.SystemDefault, "mixkit_marimba_ringtone_1359.wav")]
    [InlineData("mixkit_vintage_telephone_ringtone_1356", "mixkit_vintage_telephone_ringtone_1356.wav")]
    [InlineData("mixkit_vintage_telephone_ringtone_1356.wav", "mixkit_vintage_telephone_ringtone_1356.wav")]
    [InlineData("mixkit_on_hold_ringtone_1361", "mixkit_on_hold_ringtone_1361.wav")]
    public void ResolveCallRingbackFileName_ReturnsCustomRingtoneOrFallsBackToCallDefault(string? soundId, string expectedFileName)
    {
        var resolved = NotificationSoundCatalog.ResolveCallRingbackFileName(soundId);

        Assert.Equal(expectedFileName, resolved);
    }
}
