using System;
using System.Threading;
using System.Threading.Tasks;
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Service managing the lifecycle, in-room presence, audio playback, and server-side media pipeline stages
/// of the LiveKit bot across voice and call channels.
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
    /// Starts looping a ringback sound into the specified voice channel's WebRTC audio track until
    /// <see cref="StopRingingAsync"/> is called or <paramref name="maxDuration"/> elapses.
    /// </summary>
    Task StartRingingAsync(string channelId, string soundId, TimeSpan? maxDuration = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Immediately stops any active ringback playback in the specified voice channel.
    /// </summary>
    Task StopRingingAsync(string channelId) => Task.CompletedTask;

    /// <summary>
    /// Disconnects the bot from the specified voice channel and releases associated WebRTC resources.
    /// </summary>
    Task LeaveRoomAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules the bot to leave the room after the specified delay (grace period).
    /// If a participant joins before the delay expires, the departure is cancelled.
    /// </summary>
    void ScheduleBotDeparture(string channelId, TimeSpan delay);

    /// <summary>
    /// Registers a reusable server-side media pipeline stage (e.g., Physical Room DSP, transcription, recording).
    /// </summary>
    void RegisterPipelineStage(IVoiceRoomPipelineStage stage) { }

    /// <summary>
    /// Notifies active room media sessions that room or participant configuration has changed
    /// (such as a device joining/leaving a Physical Room or changing mute state).
    /// When <paramref name="channelId"/> is null, notifies all active channel sessions.
    /// </summary>
    Task NotifyRoomConfigurationChangedAsync(string? channelId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Runs an isolated server-side self-test verifying that the Voice Bot can connect to the local
    /// LiveKit daemon, publish audio frames, establish WebRTC transport (checking UDP vs TCP fallback),
    /// and verify built-in chime sound assets without affecting any active voice channels.
    /// </summary>
    Task<VoiceBotSelfTestResult> RunSelfTestAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new VoiceBotSelfTestResult(
            VoiceBotSelfTestStatus.Fail,
            "Voice Bot self-test is not supported by this implementation.",
            0,
            DateTime.UtcNow,
            "unknown",
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            0,
            0,
            0,
            []));
}
