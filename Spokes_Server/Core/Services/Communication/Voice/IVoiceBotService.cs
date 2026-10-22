using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Service managing the lifecycle, in-room presence, and audio playback of the server-side
/// LiveKit bot across voice and call channels.
/// </summary>
public interface IVoiceBotService : IAsyncDisposable
{
    /// <summary>
    /// Ensures that the bot has joined the specified voice channel with a published audio track.
    /// If the bot is already in the channel, this is a no-op and cancels any scheduled departure.
    /// </summary>
    Task EnsureBotJoinedAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plays an audio sound into the specified voice channel's WebRTC audio track.
    /// </summary>
    Task PlaySoundAsync(string channelId, string soundId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects the bot from the specified voice channel and releases associated WebRTC resources.
    /// </summary>
    Task LeaveRoomAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules the bot to leave the room after the specified delay (grace period).
    /// If a participant joins before the delay expires, the departure is cancelled.
    /// </summary>
    void ScheduleBotDeparture(string channelId, TimeSpan delay);
}
