using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Livekit.Server;
using Livekit.Server.Sdk.Dotnet;
using LiveKit.Rtc;
using Room = LiveKit.Rtc.Room;
using SoxQualityRecipe = LiveKit.Proto.SoxQualityRecipe;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Communication.Voice.Pipeline;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Server-side media participant that connects to active LiveKit voice channels over local loopback
/// to stream system sounds, audio clips, and host pluggable <see cref="IVoiceRoomPipelineStage"/> modules
/// (such as Physical Room multi-mic cross-talk cancellation).
/// </summary>
public class VoiceBotService : IVoiceBotService
{
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<VoiceBotService> _logger;
    private readonly IWebHostEnvironment? _webHostEnvironment;
    private readonly IConfiguration? _configuration;
    private readonly List<IVoiceRoomPipelineStage> _pipelineStages = [];
    private readonly object _stagesLock = new();

    private readonly ConcurrentDictionary<string, BotRoomSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<AudioFrame>> _soundCache = new();
    private bool _soundsLoaded;
    private readonly object _soundLock = new();

    public VoiceBotService(
        SystemConfigRepository systemConfigs,
        ILogger<VoiceBotService> logger,
        IWebHostEnvironment? webHostEnvironment = null,
        IConfiguration? configuration = null,
        IEnumerable<IVoiceRoomPipelineStage>? pipelineStages = null)
    {
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webHostEnvironment = webHostEnvironment;
        _configuration = configuration;

        if (pipelineStages != null)
        {
            _pipelineStages.AddRange(pipelineStages);
        }
    }

    public virtual void RegisterPipelineStage(IVoiceRoomPipelineStage stage)
    {
        if (stage == null) return;
        lock (_stagesLock)
        {
            if (!_pipelineStages.Any(s => s.StageId == stage.StageId))
            {
                _pipelineStages.Add(stage);
            }
        }
    }

    private IReadOnlyList<IVoiceRoomPipelineStage> GetPipelineStagesSnapshot()
    {
        lock (_stagesLock)
        {
            return _pipelineStages.ToList();
        }
    }

    private string GetVoicePort() =>
        _configuration?["Spokes_Voice_Port"] ??
        Environment.GetEnvironmentVariable("Spokes_Voice_Port") ??
        "7880";

    /// <summary>
    /// Ensures standard sounds (channel_join, channel_leave) are loaded and resampled to 48kHz stereo 20ms frames.
    /// </summary>
    public void EnsureSoundsLoaded()
    {
        if (_soundsLoaded) return;

        lock (_soundLock)
        {
            if (_soundsLoaded) return;

            string baseDir = _webHostEnvironment?.WebRootPath ??
                Path.Combine(AppContext.BaseDirectory, "wwwroot");

            if (!Directory.Exists(baseDir))
            {
                // Fallback search
                var candidate = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                if (Directory.Exists(candidate)) baseDir = candidate;
            }

            var soundsDir = Path.Combine(baseDir, "sounds");
            if (Directory.Exists(soundsDir))
            {
                LoadWavSound("channel_join", Path.Combine(soundsDir, "channel_join.wav"));
                LoadWavSound("channel_leave", Path.Combine(soundsDir, "channel_leave.wav"));
            }
            else
            {
                _logger.LogWarning("[VoiceBot] Sounds directory not found at: {SoundsDir}", soundsDir);
            }

            _soundsLoaded = true;
        }
    }

    private void LoadWavSound(string soundId, string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("[VoiceBot] Sound file {Path} does not exist", filePath);
                return;
            }

            var wavBytes = File.ReadAllBytes(filePath);
            var frames = ParseAndResampleWav(wavBytes);
            if (frames.Count > 0)
            {
                _soundCache[soundId] = frames;
                _logger.LogInformation("[VoiceBot] Loaded and pre-sliced {SoundId} into {Count} 20ms frames", soundId, frames.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoiceBot] Failed to parse sound file: {Path}", filePath);
        }
    }

    public static List<AudioFrame> ParseAndResampleWav(byte[] wavBytes)
    {
        if (wavBytes.Length < 44) return [];

        int channels = BitConverter.ToInt16(wavBytes, 22);
        int sampleRate = BitConverter.ToInt32(wavBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(wavBytes, 34);

        int dataOffset = 12;
        while (dataOffset < wavBytes.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(wavBytes, dataOffset, 4);
            int chunkSize = BitConverter.ToInt32(wavBytes, dataOffset + 4);
            if (chunkId == "data")
            {
                dataOffset += 8;
                break;
            }
            dataOffset += 8 + chunkSize;
        }

        if (dataOffset >= wavBytes.Length) return [];

        int dataLength = wavBytes.Length - dataOffset;
        byte[] pcmData = new byte[dataLength];
        Array.Copy(wavBytes, dataOffset, pcmData, 0, dataLength);

        // Resample to 48000 Hz if needed
        List<byte> resampledPcm = [];
        if (sampleRate != 48000)
        {
            using var resampler = new AudioResampler((uint)sampleRate, 48000, (uint)channels, SoxQualityRecipe.SoxrQualityHigh);
            var resampledFrames = resampler.Push(pcmData);
            resampledFrames.AddRange(resampler.Flush());
            foreach (var f in resampledFrames)
            {
                resampledPcm.AddRange(f.DataBytes);
            }
        }
        else
        {
            resampledPcm.AddRange(pcmData);
        }

        // Slice into 20ms frames (48000 samples/sec * 0.02 sec = 960 samples per channel)
        const int frameSizeSamples = 960;
        int bytesPerFrame = frameSizeSamples * channels * (bitsPerSample / 8);
        byte[] fullBytes = resampledPcm.ToArray();

        List<AudioFrame> finalFrames = [];
        for (int i = 0; i + bytesPerFrame <= fullBytes.Length; i += bytesPerFrame)
        {
            byte[] chunk = new byte[bytesPerFrame];
            Array.Copy(fullBytes, i, chunk, 0, bytesPerFrame);
            finalFrames.Add(new AudioFrame(chunk, 48000, channels, frameSizeSamples, null));
        }

        return finalFrames;
    }

    public virtual async Task EnsureBotJoinedAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;

        EnsureSoundsLoaded();

        if (_sessions.TryGetValue(channelId, out var existingSession))
        {
            existingSession.CancelScheduledDeparture();
            await NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);
            return;
        }

        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogWarning("[VoiceBot] Cannot join channel {ChannelId}: LiveKit API key or secret missing", channelId);
            return;
        }

        try
        {
            var roomName = $"channel-{channelId}";
            var token = new AccessToken(apiKey, apiSecret)
                .WithIdentity("bot:spokes-voice")
                .WithName("Spokes Voice Bot");

            token.WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = true,
                CanPublishData = true,
                CanSubscribe = true,
                Hidden = false
            });

            var jwt = token.ToJwt();
            var port = GetVoicePort();
            var liveKitUrl = $"ws://127.0.0.1:{port}";

            var room = new Room();
            await room.ConnectAsync(liveKitUrl, jwt, new RoomOptions { AutoSubscribe = true }, cancellationToken);

            var audioSource = new AudioSource(sampleRate: 48000, numChannels: 2, queueSizeMs: 1000);
            var audioTrack = audioSource.CreateTrack("spokes-bot-audio");

            var publishOptions = new TrackPublishOptions
            {
                AudioEncoding = new AudioEncodingOptions
                {
                    MaxBitrate = 96000
                }
            };

            if (room.LocalParticipant == null)
            {
                throw new InvalidOperationException("Room LocalParticipant is null after connection.");
            }

            await room.LocalParticipant.PublishTrackAsync(audioTrack, publishOptions);

            var session = new BotRoomSession(channelId, room, audioSource, audioTrack, GetPipelineStagesSnapshot, _logger);
            if (_sessions.TryAdd(channelId, session))
            {
                _logger.LogInformation("[VoiceBot] Successfully joined channel {ChannelId} with server media pipeline session", channelId);
                await session.InitializePipelineStagesAsync(cancellationToken);
            }
            else
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoiceBot] Failed to connect bot to channel {ChannelId}: {Message}", channelId, ex.Message);
        }
    }

    public virtual async Task NotifyRoomConfigurationChangedAsync(string? channelId = null, CancellationToken cancellationToken = default)
    {
        var stages = GetPipelineStagesSnapshot();
        if (stages.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(channelId))
        {
            if (_sessions.TryGetValue(channelId, out var session))
            {
                foreach (var stage in stages)
                {
                    try
                    {
                        await stage.OnRoomConfigurationChangedAsync(session, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceBot] Stage {StageId} failed OnRoomConfigurationChanged for channel {ChannelId}", stage.StageId, channelId);
                    }
                }
            }
            return;
        }

        foreach (var kvp in _sessions)
        {
            foreach (var stage in stages)
            {
                try
                {
                    await stage.OnRoomConfigurationChangedAsync(kvp.Value, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VoiceBot] Stage {StageId} failed OnRoomConfigurationChanged for channel {ChannelId}", stage.StageId, kvp.Key);
                }
            }
        }
    }

    public virtual async Task PlaySoundAsync(string channelId, string soundId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(soundId)) return;

        EnsureSoundsLoaded();

        if (!_soundCache.TryGetValue(soundId, out var frames) || frames.Count == 0)
        {
            _logger.LogDebug("[VoiceBot] Sound {SoundId} not found in sound cache", soundId);
            return;
        }

        if (!_sessions.TryGetValue(channelId, out var session))
        {
            await EnsureBotJoinedAsync(channelId, cancellationToken);
            _sessions.TryGetValue(channelId, out session);
        }

        if (session == null)
        {
            _logger.LogWarning("[VoiceBot] Cannot play sound {SoundId}: Bot session not active for channel {ChannelId}", soundId, channelId);
            return;
        }

        session.CancelScheduledDeparture();

        // Brief delay before join chime to give newly connected WebRTC client decoder pipeline time to settle
        if (soundId == "channel_join")
        {
            await Task.Delay(150, cancellationToken);
        }

        await session.PlaybackLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var frame in frames)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await session.AudioSource.CaptureFrameAsync(frame, cancellationToken);
                await Task.Delay(18, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[VoiceBot] Error streaming audio frames for {SoundId} into channel {ChannelId}", soundId, channelId);
        }
        finally
        {
            session.PlaybackLock.Release();
        }
    }

    public virtual void ScheduleBotDeparture(string channelId, TimeSpan delay)
    {
        if (_sessions.TryGetValue(channelId, out var session))
        {
            session.ScheduleDeparture(delay, async () =>
            {
                await LeaveRoomAsync(channelId, CancellationToken.None);
            });
        }
    }

    public virtual async Task LeaveRoomAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(channelId, out var session))
        {
            try
            {
                _logger.LogInformation("[VoiceBot] Leaving channel {ChannelId} and disposing WebRTC session", channelId);
                await session.DetachPipelineStagesAsync(cancellationToken);
                session.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceBot] Error leaving channel {ChannelId}", channelId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                try
                {
                    await session.DetachPipelineStagesAsync(CancellationToken.None);
                }
                catch { }
                session.Dispose();
            }
        }
    }

    private sealed class BotRoomSession : IVoiceRoomMediaSessionContext, IDisposable
    {
        public string ChannelId { get; }
        public Room Room { get; }
        public AudioSource AudioSource { get; }
        public LocalAudioTrack AudioTrack { get; }
        public SemaphoreSlim PlaybackLock { get; } = new(1, 1);

        private readonly ConcurrentDictionary<string, PublishedServerAudioTrack> _auxiliaryTracks = new(StringComparer.Ordinal);
        private readonly Func<IReadOnlyList<IVoiceRoomPipelineStage>> _getStages;
        private CancellationTokenSource? _departureCts;
        private readonly object _lock = new();
        private readonly ILogger _logger;
        private bool _disposed;

        public BotRoomSession(
            string channelId,
            Room room,
            AudioSource audioSource,
            LocalAudioTrack audioTrack,
            Func<IReadOnlyList<IVoiceRoomPipelineStage>> getStages,
            ILogger logger)
        {
            ChannelId = channelId;
            Room = room;
            AudioSource = audioSource;
            AudioTrack = audioTrack;
            _getStages = getStages;
            _logger = logger;

            Room.TrackSubscribed += OnRoomTrackSubscribed;
            Room.TrackUnsubscribed += OnRoomTrackUnsubscribed;
        }

        public async Task InitializePipelineStagesAsync(CancellationToken cancellationToken)
        {
            var stages = _getStages();
            foreach (var stage in stages)
            {
                try
                {
                    await stage.OnSessionAttachedAsync(this, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[VoiceBot] Error attaching pipeline stage {StageId} in channel {ChannelId}", stage.StageId, ChannelId);
                }
            }
        }

        public async Task DetachPipelineStagesAsync(CancellationToken cancellationToken)
        {
            var stages = _getStages();
            foreach (var stage in stages)
            {
                try
                {
                    await stage.OnSessionDetachedAsync(ChannelId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VoiceBot] Error detaching pipeline stage {StageId} in channel {ChannelId}", stage.StageId, ChannelId);
                }
            }
        }

        private void OnRoomTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
        {
            if (_disposed || e.Track is not RemoteAudioTrack remoteAudioTrack) return;
            var identity = e.Participant?.Identity;
            if (string.IsNullOrWhiteSpace(identity) || identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Ignore screen share audio tracks (only process microphone audio tracks)
            if (e.Publication?.Source == LiveKit.Proto.TrackSource.SourceScreenshareAudio)
            {
                return;
            }

            string trackSid = e.Publication?.Sid ?? remoteAudioTrack.Sid ?? string.Empty;
            var stages = _getStages();
            if (stages.Count == 0) return;

            _ = Task.Run(async () =>
            {
                foreach (var stage in stages)
                {
                    try
                    {
                        await stage.OnRemoteAudioTrackSubscribedAsync(this, identity, trackSid, remoteAudioTrack, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceBot] Stage {StageId} error in OnRemoteAudioTrackSubscribed for {Identity}", stage.StageId, identity);
                    }
                }
            });
        }

        private void OnRoomTrackUnsubscribed(object? sender, TrackSubscribedEventArgs e)
        {
            if (_disposed || e.Track is not RemoteAudioTrack remoteAudioTrack) return;
            var identity = e.Participant?.Identity;
            if (string.IsNullOrWhiteSpace(identity) || identity.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string trackSid = e.Publication?.Sid ?? remoteAudioTrack.Sid ?? string.Empty;
            var stages = _getStages();
            if (stages.Count == 0) return;

            _ = Task.Run(async () =>
            {
                foreach (var stage in stages)
                {
                    try
                    {
                        await stage.OnRemoteAudioTrackUnsubscribedAsync(this, identity, trackSid, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[VoiceBot] Stage {StageId} error in OnRemoteAudioTrackUnsubscribed for {Identity}", stage.StageId, identity);
                    }
                }
            });
        }

        public async Task<PublishedServerAudioTrack?> PublishAuxiliaryAudioTrackAsync(
            string trackName,
            int sampleRate = 48000,
            int numChannels = 1,
            CancellationToken cancellationToken = default)
        {
            if (_disposed || string.IsNullOrWhiteSpace(trackName)) return null;
            if (_auxiliaryTracks.TryGetValue(trackName, out var existing))
            {
                return existing;
            }

            if (Room.LocalParticipant == null) return null;

            var auxSource = new AudioSource(sampleRate: sampleRate, numChannels: numChannels, queueSizeMs: 400);
            var auxTrack = auxSource.CreateTrack(trackName);
            var pubOptions = new TrackPublishOptions
            {
                Source = LiveKit.Proto.TrackSource.SourceMicrophone,
                AudioEncoding = new AudioEncodingOptions
                {
                    MaxBitrate = 64000
                }
            };

            await Room.LocalParticipant.PublishTrackAsync(auxTrack, pubOptions);
            var published = new PublishedServerAudioTrack(trackName, auxSource, auxTrack);
            if (!_auxiliaryTracks.TryAdd(trackName, published))
            {
                published.Dispose();
                _auxiliaryTracks.TryGetValue(trackName, out published);
            }

            return published;
        }

        public async Task UnpublishAuxiliaryAudioTrackAsync(string trackName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trackName)) return;
            if (_auxiliaryTracks.TryRemove(trackName, out var published))
            {
                try
                {
                    if (!_disposed && Room.LocalParticipant != null && !string.IsNullOrWhiteSpace(published.AudioTrack.Sid))
                    {
                        await Room.LocalParticipant.UnpublishTrackAsync(published.AudioTrack.Sid);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VoiceBot] Failed to unpublish auxiliary track {TrackName}", trackName);
                }
                finally
                {
                    published.Dispose();
                }
            }
        }

        public void CancelScheduledDeparture()
        {
            lock (_lock)
            {
                _departureCts?.Cancel();
                _departureCts?.Dispose();
                _departureCts = null;
            }
        }

        public void ScheduleDeparture(TimeSpan delay, Func<Task> departureAction)
        {
            lock (_lock)
            {
                CancelScheduledDeparture();
                var cts = new CancellationTokenSource();
                _departureCts = cts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay, cts.Token);
                        if (!cts.IsCancellationRequested)
                        {
                            await departureAction();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceBot] Error executing scheduled bot departure");
                    }
                });
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                Room.TrackSubscribed -= OnRoomTrackSubscribed;
                Room.TrackUnsubscribed -= OnRoomTrackUnsubscribed;

                CancelScheduledDeparture();

                foreach (var kvp in _auxiliaryTracks)
                {
                    try { kvp.Value.Dispose(); } catch { }
                }
                _auxiliaryTracks.Clear();

                try
                {
                    PlaybackLock.Dispose();
                }
                catch { }

                try
                {
                    AudioSource.Dispose();
                }
                catch { }

                try
                {
                    Room.Disconnect();
                    Room.Dispose();
                }
                catch { }
            }
        }
    }
}
