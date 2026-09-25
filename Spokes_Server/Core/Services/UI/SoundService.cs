using Microsoft.JSInterop;
using System.Threading.Tasks;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Core;

namespace Spokes_Server.Core.Services.UI;

public class SoundService : ISoundService
{
    private readonly IJSRuntime _js;
    private readonly UserService _userService;

    public SoundService(IJSRuntime js, UserService userService)
    {
        _js = js;
        _userService = userService;
    }

    public ValueTask PlayChannelJoinAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.play", "channel_join", false);
    }

    public ValueTask PlayChannelLeaveAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.play", "channel_leave", false);
    }

    public ValueTask PlaySoundAsync(string soundName, bool isPreview = false)
    {
        if (string.IsNullOrWhiteSpace(soundName) || soundName == NotificationSoundIds.SystemDefault)
            return ValueTask.CompletedTask;

        return _js.InvokeVoidAsync("SoundManager.play", soundName, isPreview);
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
            await _js.InvokeVoidAsync("SoundManager.play", fileName, isPreview);
        }
    }

    public ValueTask StopCallRingtoneAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.stopCallRingtone");
    }

    public ValueTask StopPreviewAsync()
    {
        return _js.InvokeVoidAsync("SoundManager.stopPreview");
    }
}
