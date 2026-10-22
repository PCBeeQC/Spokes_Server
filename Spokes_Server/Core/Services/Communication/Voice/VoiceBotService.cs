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

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Server-side bot participant that connects to active LiveKit voice channels as a hidden
/// participant and streams system sounds, audio clips, and future announcements directly
/// into the WebRTC audio track.
/// </summary>
public class VoiceBotService : IVoiceBotService
{
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<VoiceBotService> _logger;
    private readonly IWebHostEnvironment? _webHostEnvironment;
    private readonly IConfiguration? _configuration;

    private readonly ConcurrentDictionary<string, BotRoomSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<AudioFrame>> _soundCache = new();
    private bool _soundsLoaded;
    private readonly object _soundLock = new();

    public VoiceBotService(
        SystemConfigRepository systemConfigs,
        ILogger<VoiceBotService> logger,
        IWebHostEnvironment? webHostEnvironment = null,
        IConfiguration? configuration = null)
    {
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webHostEnvironment = webHostEnvironment;
        _configuration = configuration;
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
                CanSubscribe = false,
                Hidden = false
            });

            var jwt = token.ToJwt();
            var port = GetVoicePort();
            var liveKitUrl = $"ws://127.0.0.1:{port}";

            var room = new Room();
            await room.ConnectAsync(liveKitUrl, jwt, new RoomOptions(), cancellationToken);

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

            var session = new BotRoomSession(room, audioSource, audioTrack, _logger);
            if (_sessions.TryAdd(channelId, session))
            {
                _logger.LogInformation("[VoiceBot] Successfully joined channel {ChannelId} with hidden audio track", channelId);
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
                session.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[VoiceBot] Error leaving channel {ChannelId}", channelId);
            }
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryRemove(key, out var session))
            {
                session.Dispose();
            }
        }
        await Task.CompletedTask;
    }

    private sealed class BotRoomSession : IDisposable
    {
        public Room Room { get; }
        public AudioSource AudioSource { get; }
        public LocalAudioTrack AudioTrack { get; }
        public SemaphoreSlim PlaybackLock { get; } = new(1, 1);

        private CancellationTokenSource? _departureCts;
        private readonly object _lock = new();
        private readonly ILogger _logger;
        private bool _disposed;

        public BotRoomSession(Room room, AudioSource audioSource, LocalAudioTrack audioTrack, ILogger logger)
        {
            Room = room;
            AudioSource = audioSource;
            AudioTrack = audioTrack;
            _logger = logger;
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

                CancelScheduledDeparture();

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
