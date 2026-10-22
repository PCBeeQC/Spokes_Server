using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.UI;

public interface ISoundService
{
    Task PlayChannelLeaveAsync();
    Task PlaySoundAsync(string soundName, bool isPreview = false);
    Task PlayNotificationSoundAsync(string category, string? explicitSoundId = null, bool isPreview = false);
    Task StopCallRingtoneAsync();
    Task StopPreviewAsync();
}
