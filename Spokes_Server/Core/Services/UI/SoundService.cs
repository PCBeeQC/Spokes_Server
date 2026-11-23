using Microsoft.JSInterop;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Presentation;
using Spokes_Server.Core.Services.Identity;

namespace Spokes_Server.Core.Services.UI;

public class SoundService : ISoundService
{
    private readonly IJSRuntime _js;
    private readonly UserService _userService;

    public bool IsCallRingtonePlaying { get; private set; }

    public SoundService(IJSRuntime js, UserService userService)
    {
        _js = js;
        _userService = userService;
    }

    private static bool IsCallRingtoneSound(string? soundNameOrCategory)
        => !string.IsNullOrEmpty(soundNameOrCategory) &&
           (soundNameOrCategory.Contains("ringtone", System.StringComparison.OrdinalIgnoreCase) ||
            soundNameOrCategory.Contains("call", System.StringComparison.OrdinalIgnoreCase));

    public Task PlayChannelLeaveAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.play", "channel_leave", false).AsTask();
    }

    public Task PlaySoundAsync(string soundName, bool isPreview = false)
    {
        if (string.IsNullOrWhiteSpace(soundName) || soundName == NotificationSoundIds.SystemDefault)
            return Task.CompletedTask;

        if (!isPreview && IsCallRingtoneSound(soundName))
        {
            IsCallRingtonePlaying = true;
        }

        return _js.InvokeVoidAsync("SoundManager.play", soundName, isPreview).AsTask();
    }

    public async Task PlayNotificationSoundAsync(string category, string? explicitSoundId = null, bool isPreview = false)
    {
        string? soundIdToUse = explicitSoundId;

        if (string.IsNullOrEmpty(soundIdToUse))
        {
            var employee = await _userService.GetEmployeeAsync();
            soundIdToUse = employee?.GetNotificationSound(category) ?? NotificationSoundIds.SpokesDefault;
        }

        var fileName = NotificationSoundCatalog.ResolveEffectiveSoundFileName(category, soundIdToUse);
        if (!string.IsNullOrEmpty(fileName))
        {
            if (!isPreview && (IsCallRingtoneSound(category) || IsCallRingtoneSound(fileName)))
            {
                IsCallRingtonePlaying = true;
            }
            await _js.InvokeVoidAsync("SoundManager.play", fileName, isPreview);
        }
    }

    public Task StopCallRingtoneAsync()
    {
        IsCallRingtonePlaying = false;
        return _js.InvokeVoidAsync("SoundManager.stopCallRingtone").AsTask();
    }

    public Task StopPreviewAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.stopPreview").AsTask();
    }
}
