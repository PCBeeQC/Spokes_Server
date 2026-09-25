using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.UI;

public interface ISoundService
{
    ValueTask PlayChannelJoinAsync();
    ValueTask PlayChannelLeaveAsync();
    ValueTask PlaySoundAsync(string soundName, bool isPreview = false);
    Task PlayNotificationSoundAsync(string category, string? explicitSoundId = null, bool isPreview = false);
    ValueTask StopCallRingtoneAsync();
    ValueTask StopPreviewAsync();
}
