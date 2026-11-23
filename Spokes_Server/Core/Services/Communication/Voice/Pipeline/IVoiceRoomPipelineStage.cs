namespace Spokes_Server.Core.Services.Communication.Voice.Pipeline;

using System;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rtc;

/// <summary>
/// Represents an auxiliary server-generated audio track published into a LiveKit room
/// (for example, a cleaned participant microphone track "processed-mic:{userId}").
/// </summary>
public sealed class PublishedServerAudioTrack : IDisposable
{
    public string TrackName { get; }
    public AudioSource AudioSource { get; }
    public LocalAudioTrack AudioTrack { get; }
    public string PublicationSid { get; }
    private bool _disposed;

    public PublishedServerAudioTrack(string trackName, AudioSource audioSource, LocalAudioTrack audioTrack, string? publicationSid = null)
    {
        TrackName = trackName;
        AudioSource = audioSource;
        AudioTrack = audioTrack;
        PublicationSid = !string.IsNullOrWhiteSpace(publicationSid) ? publicationSid : (audioTrack?.Sid ?? string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { AudioSource.Dispose(); } catch { }
    }
}

/// <summary>
/// Context exposed by <see cref="VoiceBotService"/>'s active per-channel WebRTC session to registered
/// <see cref="IVoiceRoomPipelineStage"/> processors.
/// </summary>
public interface IVoiceRoomMediaSessionContext
{
    string ChannelId { get; }
    Room Room { get; }

    /// <summary>
    /// Creates and publishes a named server-side audio track into the channel's LiveKit room.
    /// </summary>
    Task<PublishedServerAudioTrack?> PublishAuxiliaryAudioTrackAsync(
        string trackName,
        int sampleRate = 48000,
        int numChannels = 1,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpublishes and disposes a previously published auxiliary server-side audio track.
    /// </summary>
    Task UnpublishAuxiliaryAudioTrackAsync(string trackName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pluggable stage in the server-side real-time voice/video media pipeline for a LiveKit room.
/// Enables modular server-side features such as Physical Room cross-talk cancellation, transcription,
/// recording, or AI voice processing without duplicating WebRTC room connections.
/// </summary>
public interface IVoiceRoomPipelineStage
{
    string StageId { get; }

    Task OnSessionAttachedAsync(IVoiceRoomMediaSessionContext context, CancellationToken cancellationToken = default);

    Task OnRemoteAudioTrackSubscribedAsync(
        IVoiceRoomMediaSessionContext context,
        string participantIdentity,
        string trackSid,
        RemoteAudioTrack track,
        CancellationToken cancellationToken = default);

    Task OnRemoteAudioTrackUnsubscribedAsync(
        IVoiceRoomMediaSessionContext context,
        string participantIdentity,
        string trackSid,
        CancellationToken cancellationToken = default);

    Task OnRoomConfigurationChangedAsync(IVoiceRoomMediaSessionContext context, CancellationToken cancellationToken = default);

    Task OnSessionDetachedAsync(string channelId, CancellationToken cancellationToken = default);
}
