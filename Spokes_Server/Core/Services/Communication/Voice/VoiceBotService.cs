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
    private readonly LiveKitService? _liveKitService;
    private readonly List<IVoiceRoomPipelineStage> _pipelineStages = [];
    private readonly object _stagesLock = new();

    private readonly ConcurrentDictionary<string, BotRoomSession> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingBotJoin> _pendingJoins = new();
    private readonly ConcurrentDictionary<string, List<AudioFrame>> _soundCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _ringingTokens = new();
    private readonly SemaphoreSlim _selfTestLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _soundsLoaded;
    private readonly object _soundLock = new();

    private sealed record PendingBotJoin(Task Task, CancellationTokenSource Cts);

    public VoiceBotService(
        SystemConfigRepository systemConfigs,
        ILogger<VoiceBotService> logger,
        IWebHostEnvironment? webHostEnvironment = null,
        IConfiguration? configuration = null,
        IEnumerable<IVoiceRoomPipelineStage>? pipelineStages = null,
        LiveKitService? liveKitService = null)
    {
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _webHostEnvironment = webHostEnvironment;
        _configuration = configuration;
        _liveKitService = liveKitService;

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

    private string? ResolveSoundsDirectory()
    {
        string baseDir = _webHostEnvironment?.WebRootPath ??
            Path.Combine(AppContext.BaseDirectory, "wwwroot");

        if (!Directory.Exists(baseDir))
        {
            var candidate = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            if (Directory.Exists(candidate)) baseDir = candidate;
        }

        var soundsDir = Path.Combine(baseDir, "sounds");
        return Directory.Exists(soundsDir) ? soundsDir : null;
    }

    public static string NormalizeSoundId(string soundId)
    {
        if (string.IsNullOrWhiteSpace(soundId)) return string.Empty;
        var fileName = Path.GetFileName(soundId.Trim());
        if (fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }
        return fileName;
    }

    private List<AudioFrame>? GetOrLoadSoundFrames(string soundId)
    {
        var normalizedId = NormalizeSoundId(soundId);
        if (string.IsNullOrEmpty(normalizedId)) return null;

        EnsureSoundsLoaded();

        if (_soundCache.TryGetValue(normalizedId, out var cached) && cached.Count > 0)
        {
            return cached;
        }

        lock (_soundLock)
        {
            if (_soundCache.TryGetValue(normalizedId, out cached) && cached.Count > 0)
            {
                return cached;
            }

            var soundsDir = ResolveSoundsDirectory();
            if (soundsDir != null)
            {
                LoadWavSound(normalizedId, Path.Combine(soundsDir, $"{normalizedId}.wav"));
            }

            return _soundCache.TryGetValue(normalizedId, out cached) && cached.Count > 0 ? cached : null;
        }
    }

    /// <summary>
    /// Ensures standard sounds (channel_join, channel_leave) are loaded and resampled to 48kHz stereo 20ms frames.
    /// </summary>
    public void EnsureSoundsLoaded()
    {
        if (_soundsLoaded) return;

        lock (_soundLock)
        {
            if (_soundsLoaded) return;

            var soundsDir = ResolveSoundsDirectory();
            if (soundsDir != null)
            {
                LoadWavSound("channel_join", Path.Combine(soundsDir, "channel_join.wav"));
                LoadWavSound("channel_leave", Path.Combine(soundsDir, "channel_leave.wav"));
            }
            else
            {
                _logger.LogWarning("[VoiceBot] Sounds directory not found");
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
        if (string.IsNullOrWhiteSpace(channelId) || _shutdownCts.IsCancellationRequested) return;

        EnsureSoundsLoaded();

        if (_sessions.TryGetValue(channelId, out var existingSession))
        {
            if (existingSession.IsConnected)
            {
                existingSession.CancelScheduledDeparture();
                await NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);
                return;
            }

            if (_sessions.TryRemove(new KeyValuePair<string, BotRoomSession>(channelId, existingSession)))
            {
                _logger.LogInformation("[VoiceBot] Evicting disconnected bot session for channel {ChannelId} before re-joining", channelId);
                try { await existingSession.DetachPipelineStagesAsync(CancellationToken.None); } catch { }
                try { existingSession.Dispose(); } catch { }
            }
        }

        // Coalesce concurrent join attempts per channel with a bounded timeout so multiple
        // callers share a single bot WebRTC session initialization.
        var pending = GetOrStartPendingJoin(channelId);
        if (pending != null)
        {
            await pending.Task.WaitAsync(cancellationToken);
        }
    }

    private PendingBotJoin? GetOrStartPendingJoin(string channelId)
    {
        if (_shutdownCts.IsCancellationRequested || _sessions.ContainsKey(channelId))
        {
            return null;
        }

        if (_pendingJoins.TryGetValue(channelId, out var existing) && !existing.Task.IsCompleted)
        {
            return existing;
        }

        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey ?? string.Empty;
        var apiSecret = config.LiveKitApiSecret ?? string.Empty;

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
        {
            _logger.LogWarning("[VoiceBot] Cannot join channel {ChannelId}: LiveKit API key or secret missing", channelId);
            return null;
        }

        var joinCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        joinCts.CancelAfter(TimeSpan.FromSeconds(12));

        PendingBotJoin? created = null;
        var joinTask = Task.Run(async () =>
        {
            try
            {
                await ConnectBotSessionAsync(channelId, apiKey, apiSecret, joinCts.Token);
            }
            finally
            {
                if (created != null)
                {
                    _pendingJoins.TryRemove(new KeyValuePair<string, PendingBotJoin>(channelId, created));
                }
                joinCts.Dispose();
            }
        }, CancellationToken.None);

        created = new PendingBotJoin(joinTask, joinCts);
        if (!_pendingJoins.TryAdd(channelId, created))
        {
            try { joinCts.Cancel(); } catch { }
            return _pendingJoins.TryGetValue(channelId, out var winner) ? winner : null;
        }

        return created;
    }

    private async Task ConnectBotSessionAsync(string channelId, string apiKey, string apiSecret, CancellationToken cancellationToken)
    {
        if (_sessions.ContainsKey(channelId) || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Room? room = null;
        AudioSource? audioSource = null;
        BotRoomSession? session = null;
        try
        {
            var roomName = LiveKitService.GetRoomName(channelId);
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

            room = new Room();
            audioSource = new AudioSource(sampleRate: 48000, numChannels: 2, queueSizeMs: 2000);
            var audioTrack = audioSource.CreateTrack("spokes-bot-audio");

            // Wire TrackSubscribed/TrackUnsubscribed BEFORE ConnectAsync so early-published
            // human microphone tracks emitted during the join handshake are never missed.
            session = new BotRoomSession(
                channelId,
                room,
                audioSource,
                audioTrack,
                GetPipelineStagesSnapshot,
                deadSession => OnBotSessionDisconnected(channelId, deadSession),
                _logger);
            room = null;
            audioSource = null;

            await session.Room.ConnectAsync(liveKitUrl, jwt, new RoomOptions { AutoSubscribe = true }, cancellationToken);

            var publishOptions = new TrackPublishOptions
            {
                AudioEncoding = new AudioEncodingOptions
                {
                    MaxBitrate = 96000
                }
            };

            if (session.Room.LocalParticipant == null)
            {
                throw new InvalidOperationException("Room LocalParticipant is null after connection.");
            }

            await session.Room.LocalParticipant.PublishTrackAsync(audioTrack, publishOptions);

            if (_sessions.TryAdd(channelId, session))
            {
                var addedSession = session;
                session = null;
                _logger.LogInformation("[VoiceBot] Successfully joined channel {ChannelId} with server media pipeline session", channelId);
                await addedSession.InitializePipelineStagesAsync(cancellationToken);
                await NotifyRoomConfigurationChangedAsync(channelId, cancellationToken);
            }
            else
            {
                session.Dispose();
                session = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("[VoiceBot] Cancelled or timed out joining channel {ChannelId}", channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoiceBot] Failed to connect bot to channel {ChannelId}: {Message}", channelId, ex.Message);
        }
        finally
        {
            try { session?.Dispose(); } catch { }
            try { audioSource?.Dispose(); } catch { }
            if (room != null)
            {
                try { room.Disconnect(); } catch { }
                try { room.Dispose(); } catch { }
            }
        }
    }

    private void OnBotSessionDisconnected(string channelId, BotRoomSession deadSession)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, BotRoomSession>(channelId, deadSession)))
        {
            _logger.LogInformation("[VoiceBot] Bot session for channel {ChannelId} disconnected from LiveKit room; cleaning up session", channelId);
            _ = Task.Run(async () =>
            {
                try { await deadSession.DetachPipelineStagesAsync(CancellationToken.None); } catch { }
                try { deadSession.Dispose(); } catch { }
            });
        }
    }

    public virtual async Task NotifyRoomConfigurationChangedAsync(string? channelId = null, CancellationToken cancellationToken = default)
    {
        var stages = GetPipelineStagesSnapshot();
        if (stages.Count == 0) return;

        var targets = !string.IsNullOrWhiteSpace(channelId)
            ? (_sessions.TryGetValue(channelId, out var s) ? new[] { new KeyValuePair<string, BotRoomSession>(channelId, s) } : Array.Empty<KeyValuePair<string, BotRoomSession>>())
            : _sessions.ToArray();

        foreach (var kvp in targets)
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

        var frames = GetOrLoadSoundFrames(soundId);
        if (frames == null || frames.Count == 0)
        {
            _logger.LogDebug("[VoiceBot] Sound {SoundId} not found in sound cache", soundId);
            return;
        }

        var session = await EnsureConnectedSessionAsync(channelId, requireConnected: true, cancellationToken);
        if (session == null)
        {
            _logger.LogWarning("[VoiceBot] Cannot play sound {SoundId}: Bot session not active for channel {ChannelId}", soundId, channelId);
            return;
        }

        session.CancelScheduledDeparture();

        // Brief delay before join chime to give newly connected WebRTC client decoder pipeline time to settle
        if (NormalizeSoundId(soundId) == "channel_join")
        {
            await Task.Delay(150, cancellationToken);
        }

        using var chimeCts = session.BeginChimePlayback(cancellationToken);
        var playbackToken = chimeCts.Token;

        await session.PlaybackLock.WaitAsync(playbackToken);
        try
        {
            await StreamAudioFramesAsync(session, frames, stopIfDisconnected: true, playbackToken);
        }
        catch (OperationCanceledException) when (playbackToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Preempted by a newer chime or disconnected session
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

    private async Task<BotRoomSession?> EnsureConnectedSessionAsync(string channelId, bool requireConnected, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(channelId, out var session) || (requireConnected && !session.IsConnected))
        {
            await EnsureBotJoinedAsync(channelId, cancellationToken);
            _sessions.TryGetValue(channelId, out session);
        }

        return (session != null && (!requireConnected || session.IsConnected)) ? session : null;
    }

    private static async Task StreamAudioFramesAsync(
        BotRoomSession session,
        IReadOnlyList<AudioFrame> frames,
        bool stopIfDisconnected,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < frames.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested || (stopIfDisconnected && !session.IsConnected)) break;
            await session.AudioSource.CaptureFrameAsync(frames[i], cancellationToken);
            var targetMs = (i + 1) * 20;
            var remainingMs = (int)(targetMs - sw.ElapsedMilliseconds);
            if (remainingMs > 1)
            {
                await Task.Delay(remainingMs, cancellationToken);
            }
        }
    }

    public virtual async Task StartRingingAsync(
        string channelId,
        string soundId,
        TimeSpan? maxDuration = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(soundId) || _shutdownCts.IsCancellationRequested)
            return;

        await StopRingingAsync(channelId);

        var frames = GetOrLoadSoundFrames(soundId);
        if (frames == null || frames.Count == 0)
        {
            _logger.LogDebug("[VoiceBot] Ringback sound {SoundId} not found in sound cache", soundId);
            return;
        }

        var ringCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
        ringCts.CancelAfter(maxDuration ?? TimeSpan.FromSeconds(60));

        if (!_ringingTokens.TryAdd(channelId, ringCts))
        {
            if (_ringingTokens.TryRemove(channelId, out var previous))
            {
                try { previous.Cancel(); previous.Dispose(); } catch { }
            }
            _ringingTokens[channelId] = ringCts;
        }

        try
        {
            var session = await EnsureConnectedSessionAsync(channelId, requireConnected: false, ringCts.Token);
            if (session == null || ringCts.IsCancellationRequested)
            {
                return;
            }

            session.CancelScheduledDeparture();

            // Give caller's newly connected WebRTC decoder pipeline a brief moment to settle
            await Task.Delay(200, ringCts.Token);

            while (!ringCts.IsCancellationRequested)
            {
                await session.PlaybackLock.WaitAsync(ringCts.Token);
                try
                {
                    await StreamAudioFramesAsync(session, frames, stopIfDisconnected: false, ringCts.Token);
                }
                finally
                {
                    session.PlaybackLock.Release();
                }

                if (ringCts.IsCancellationRequested) break;

                // Pause 1.2 seconds between ring cycles
                await Task.Delay(1200, ringCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal termination when callee joins, declines, caller leaves, or timeout expires
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VoiceBot] Error streaming caller ringback {SoundId} in channel {ChannelId}", soundId, channelId);
        }
        finally
        {
            if (_ringingTokens.TryGetValue(channelId, out var active) && ReferenceEquals(active, ringCts))
            {
                _ringingTokens.TryRemove(channelId, out _);
            }
            try { ringCts.Dispose(); } catch { }
        }
    }

    public virtual Task StopRingingAsync(string channelId)
    {
        if (!string.IsNullOrWhiteSpace(channelId) && _ringingTokens.TryRemove(channelId, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { }
        }
        return Task.CompletedTask;
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
        await StopRingingAsync(channelId);

        if (_pendingJoins.TryRemove(channelId, out var pending))
        {
            try { pending.Cts.Cancel(); } catch { }
        }

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
        try { _shutdownCts.Cancel(); } catch { }
        foreach (var pendingKey in _pendingJoins.Keys)
        {
            if (_pendingJoins.TryRemove(pendingKey, out var pending))
            {
                try { pending.Cts.Cancel(); } catch { }
            }
        }

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
        _shutdownCts.Dispose();
        _selfTestLock.Dispose();
    }

    public virtual async Task<VoiceBotSelfTestResult> RunSelfTestAsync(CancellationToken cancellationToken = default)
    {
        var testedAtUtc = DateTime.UtcNow;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var steps = new List<VoiceBotSelfTestStep>();

        if (_shutdownCts.IsCancellationRequested)
        {
            return new VoiceBotSelfTestResult(
                VoiceBotSelfTestStatus.Fail,
                "Server is shutting down.",
                totalSw.ElapsedMilliseconds,
                testedAtUtc,
                "unknown",
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                0,
                0,
                steps);
        }

        if (!await _selfTestLock.WaitAsync(0, cancellationToken))
        {
            return new VoiceBotSelfTestResult(
                VoiceBotSelfTestStatus.Warning,
                "A Voice Bot self-test is already in progress.",
                totalSw.ElapsedMilliseconds,
                testedAtUtc,
                "unknown",
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                0,
                0,
                steps);
        }

        Room? room = null;
        AudioSource? audioSource = null;
        string? diagRoomName = null;
        string apiKey = string.Empty;
        string apiSecret = string.Empty;
        int loadedSoundCount = 0;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(12));
            var ct = linkedCts.Token;

            // Step 1: Configuration & built-in chime sound assets
            var stepSw = System.Diagnostics.Stopwatch.StartNew();
            var config = _systemConfigs.Get();
            apiKey = config.LiveKitApiKey ?? string.Empty;
            apiSecret = config.LiveKitApiSecret ?? string.Empty;

            EnsureSoundsLoaded();
            bool hasJoinSound = _soundCache.TryGetValue("channel_join", out var joinFrames) && joinFrames.Count > 0;
            bool hasLeaveSound = _soundCache.TryGetValue("channel_leave", out var leaveFrames) && leaveFrames.Count > 0;
            loadedSoundCount = (hasJoinSound ? 1 : 0) + (hasLeaveSound ? 1 : 0);

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Configuration & Sounds",
                    VoiceBotSelfTestStatus.Fail,
                    stepSw.ElapsedMilliseconds,
                    "LiveKit API key or secret is missing in SystemConfig."));

                return new VoiceBotSelfTestResult(
                    VoiceBotSelfTestStatus.Fail,
                    "LiveKit credentials are not configured.",
                    totalSw.ElapsedMilliseconds,
                    testedAtUtc,
                    "unknown",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    0,
                    0,
                    loadedSoundCount,
                    steps);
            }

            if (loadedSoundCount == 2)
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Configuration & Sounds",
                    VoiceBotSelfTestStatus.Pass,
                    stepSw.ElapsedMilliseconds,
                    "Credentials configured; join and leave chime WAV files loaded (48 kHz)."));
            }
            else
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Configuration & Sounds",
                    VoiceBotSelfTestStatus.Warning,
                    stepSw.ElapsedMilliseconds,
                    $"Credentials configured, but only {loadedSoundCount}/2 built-in chime WAV files loaded from wwwroot/sounds."));
            }

            // Step 2: Connect to isolated diagnostic room (non-channel prefix so webhooks & active channel lists ignore it)
            stepSw.Restart();
            diagRoomName = $"spokes-diag-voicebot-{Guid.NewGuid():N}";
            var voicePort = GetVoicePort();
            var liveKitUrl = $"ws://127.0.0.1:{voicePort}";

            string jwt;
            try
            {
                var token = new AccessToken(apiKey, apiSecret)
                    .WithIdentity("bot:spokes-selftest")
                    .WithName("Spokes Voice Bot Self-Test");

                token.WithGrants(new VideoGrants
                {
                    RoomJoin = true,
                    Room = diagRoomName,
                    CanPublish = true,
                    CanPublishData = false,
                    CanSubscribe = false,
                    Hidden = true
                });

                jwt = token.ToJwt();
            }
            catch (Exception ex)
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Signaling & Room Join",
                    VoiceBotSelfTestStatus.Fail,
                    stepSw.ElapsedMilliseconds,
                    $"Failed to generate LiveKit access token: {ex.Message}"));

                return new VoiceBotSelfTestResult(
                    VoiceBotSelfTestStatus.Fail,
                    "LiveKit API credentials are invalid.",
                    totalSw.ElapsedMilliseconds,
                    testedAtUtc,
                    "unknown",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    0,
                    0,
                    loadedSoundCount,
                    steps);
            }

            room = new Room();
            try
            {
                await room.ConnectAsync(liveKitUrl, jwt, new RoomOptions { AutoSubscribe = false }, ct);
            }
            catch (Exception ex)
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Signaling & Room Join",
                    VoiceBotSelfTestStatus.Fail,
                    stepSw.ElapsedMilliseconds,
                    $"Failed to connect to {liveKitUrl}: {ex.Message}"));

                return new VoiceBotSelfTestResult(
                    VoiceBotSelfTestStatus.Fail,
                    $"Voice Bot could not reach LiveKit at {liveKitUrl}.",
                    totalSw.ElapsedMilliseconds,
                    testedAtUtc,
                    "unknown",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    0,
                    0,
                    loadedSoundCount,
                    steps);
            }

            if (!room.IsConnected || room.LocalParticipant == null)
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Signaling & Room Join",
                    VoiceBotSelfTestStatus.Fail,
                    stepSw.ElapsedMilliseconds,
                    $"Connected to {liveKitUrl}, but room did not enter a connected state."));

                return new VoiceBotSelfTestResult(
                    VoiceBotSelfTestStatus.Fail,
                    "Voice Bot failed to initialize LiveKit room session.",
                    totalSw.ElapsedMilliseconds,
                    testedAtUtc,
                    "unknown",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    0,
                    0,
                    loadedSoundCount,
                    steps);
            }

            steps.Add(new VoiceBotSelfTestStep(
                "Signaling & Room Join",
                VoiceBotSelfTestStatus.Pass,
                stepSw.ElapsedMilliseconds,
                $"Joined isolated diagnostic room via {liveKitUrl}."));

            // Step 3: Publish audio track & stream short PCM burst to exercise media pipeline
            stepSw.Restart();
            audioSource = new AudioSource(sampleRate: 48000, numChannels: 2, queueSizeMs: 500);
            var audioTrack = audioSource.CreateTrack("spokes-selftest-audio");
            var publishOptions = new TrackPublishOptions
            {
                AudioEncoding = new AudioEncodingOptions
                {
                    MaxBitrate = 64000
                }
            };

            try
            {
                await room.LocalParticipant.PublishTrackAsync(audioTrack, publishOptions, ct);

                const int testFrameCount = 10; // 200ms at 20ms per frame
                byte[] silentPcm = new byte[960 * 2 * 2]; // 960 samples * 2ch * 16-bit
                var frameSw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < testFrameCount; i++)
                {
                    var frame = hasJoinSound && joinFrames != null && i < joinFrames.Count
                        ? joinFrames[i]
                        : new AudioFrame(silentPcm, 48000, 2, 960, null);
                    await audioSource.CaptureFrameAsync(frame, ct);
                    int remainingMs = ((i + 1) * 20) - (int)frameSw.ElapsedMilliseconds;
                    if (remainingMs > 1)
                    {
                        await Task.Delay(remainingMs, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                steps.Add(new VoiceBotSelfTestStep(
                    "Audio Publish & Stream",
                    VoiceBotSelfTestStatus.Fail,
                    stepSw.ElapsedMilliseconds,
                    $"Failed to publish or stream test audio frames: {ex.Message}"));

                return new VoiceBotSelfTestResult(
                    VoiceBotSelfTestStatus.Fail,
                    "Voice Bot failed to publish audio track to LiveKit.",
                    totalSw.ElapsedMilliseconds,
                    testedAtUtc,
                    "unknown",
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    0,
                    0,
                    loadedSoundCount,
                    steps);
            }

            steps.Add(new VoiceBotSelfTestStep(
                "Audio Publish & Stream",
                VoiceBotSelfTestStatus.Pass,
                stepSw.ElapsedMilliseconds,
                "Published 48 kHz stereo audio track and streamed 200 ms test burst."));

            // Step 4: Inspect WebRTC transport & outbound RTP stats
            stepSw.Restart();
            VoiceBotTransportStatsSnapshot transport = new("unknown", string.Empty, string.Empty, string.Empty, null, 0, 0);
            try
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    var rtcStats = await room.GetRtcStatsAsync();
                    var candidateStats = ExtractTransportStats(rtcStats);
                    if (!string.Equals(candidateStats.TransportProtocol, "unknown", StringComparison.OrdinalIgnoreCase) ||
                        candidateStats.OutboundPacketsSent > transport.OutboundPacketsSent)
                    {
                        transport = candidateStats;
                    }

                    if (!string.Equals(transport.TransportProtocol, "unknown", StringComparison.OrdinalIgnoreCase) &&
                        transport.OutboundPacketsSent > 0)
                    {
                        break;
                    }

                    await Task.Delay(250, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VoiceBot] Self-test GetRtcStatsAsync failed: {Message}", ex.Message);
            }

            var transportStepStatus = VoiceBotSelfTestStatus.Pass;
            string transportDetail;
            if (string.Equals(transport.TransportProtocol, "udp", StringComparison.OrdinalIgnoreCase) && transport.OutboundPacketsSent > 0)
            {
                var rttText = transport.RoundTripTimeMs.HasValue ? $", RTT {transport.RoundTripTimeMs.Value:F1} ms" : string.Empty;
                transportDetail = $"Direct UDP media verified ({transport.RemoteEndpoint}), {transport.OutboundPacketsSent} RTP packets sent ({transport.OutboundBytesSent} B{rttText}).";
            }
            else if (string.Equals(transport.TransportProtocol, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                transportStepStatus = VoiceBotSelfTestStatus.Warning;
                transportDetail = $"Connected over TCP fallback ({transport.RemoteEndpoint}) instead of UDP Mux ({transport.OutboundPacketsSent} RTP packets sent). Verify LiveKit UDP Mux ports and container networking.";
            }
            else if (transport.OutboundPacketsSent > 0)
            {
                transportStepStatus = VoiceBotSelfTestStatus.Warning;
                transportDetail = $"Sent {transport.OutboundPacketsSent} RTP packets, but ICE candidate transport protocol was not reported.";
            }
            else
            {
                transportStepStatus = VoiceBotSelfTestStatus.Fail;
                transportDetail = string.Equals(transport.TransportProtocol, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? "WebRTC media transport did not establish a nominated ICE candidate pair or transmit RTP packets."
                    : $"ICE transport ({transport.TransportProtocol.ToUpperInvariant()} {transport.RemoteEndpoint}) reported 0 outbound RTP packets.";
            }

            steps.Add(new VoiceBotSelfTestStep(
                "WebRTC Media Transport",
                transportStepStatus,
                stepSw.ElapsedMilliseconds,
                transportDetail));

            // Step 5: Clean disconnect & ephemeral room deletion
            stepSw.Restart();
            try
            {
                audioSource.Dispose();
                audioSource = null;
            }
            catch { }

            try
            {
                await room.DisconnectAsync();
            }
            catch
            {
                try { room.Disconnect(); } catch { }
            }
            finally
            {
                try { room.Dispose(); } catch { }
                room = null;
            }

            if (_liveKitService != null && !string.IsNullOrWhiteSpace(diagRoomName))
            {
                await _liveKitService.DeleteRoomAsync(diagRoomName, apiKey, apiSecret, CancellationToken.None);
                diagRoomName = null;
            }

            steps.Add(new VoiceBotSelfTestStep(
                "Cleanup",
                VoiceBotSelfTestStatus.Pass,
                stepSw.ElapsedMilliseconds,
                "Disconnected cleanly and removed temporary diagnostic room."));

            var overallStatus = steps.Any(s => s.Status == VoiceBotSelfTestStatus.Fail)
                ? VoiceBotSelfTestStatus.Fail
                : steps.Any(s => s.Status == VoiceBotSelfTestStatus.Warning)
                    ? VoiceBotSelfTestStatus.Warning
                    : VoiceBotSelfTestStatus.Pass;

            var summary = overallStatus switch
            {
                VoiceBotSelfTestStatus.Pass => $"Voice Bot is healthy over UDP ({transport.RemoteEndpoint}).",
                VoiceBotSelfTestStatus.Warning when string.Equals(transport.TransportProtocol, "tcp", StringComparison.OrdinalIgnoreCase)
                    => $"Voice Bot connected, but fell back to TCP ({transport.RemoteEndpoint}) instead of UDP.",
                VoiceBotSelfTestStatus.Warning => "Voice Bot connected with warnings — check step details below.",
                _ => "Voice Bot self-test failed — check step details below."
            };

            return new VoiceBotSelfTestResult(
                overallStatus,
                summary,
                totalSw.ElapsedMilliseconds,
                testedAtUtc,
                transport.TransportProtocol,
                transport.LocalEndpoint,
                transport.RemoteEndpoint,
                transport.CandidateType,
                transport.RoundTripTimeMs,
                transport.OutboundPacketsSent,
                transport.OutboundBytesSent,
                loadedSoundCount,
                steps);
        }
        catch (OperationCanceledException)
        {
            steps.Add(new VoiceBotSelfTestStep(
                "Timeout",
                VoiceBotSelfTestStatus.Fail,
                totalSw.ElapsedMilliseconds,
                "Voice Bot self-test timed out before completing."));

            return new VoiceBotSelfTestResult(
                VoiceBotSelfTestStatus.Fail,
                "Voice Bot self-test timed out.",
                totalSw.ElapsedMilliseconds,
                testedAtUtc,
                "unknown",
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                0,
                0,
                loadedSoundCount,
                steps);
        }
        finally
        {
            try { audioSource?.Dispose(); } catch { }
            if (room != null)
            {
                try { room.Disconnect(); } catch { }
                try { room.Dispose(); } catch { }
            }
            if (_liveKitService != null && !string.IsNullOrWhiteSpace(diagRoomName) && !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret))
            {
                try { await _liveKitService.DeleteRoomAsync(diagRoomName, apiKey, apiSecret, CancellationToken.None); } catch { }
            }
            _selfTestLock.Release();
        }
    }

    internal static VoiceBotTransportStatsSnapshot ExtractTransportStats(RtcStats? rtcStats) =>
        ExtractTransportStats(rtcStats?.PublisherStats);

    internal static VoiceBotTransportStatsSnapshot ExtractTransportStats(IEnumerable<LiveKit.Proto.RtcStats>? statsList)
    {
        if (statsList == null)
        {
            return new VoiceBotTransportStatsSnapshot("unknown", string.Empty, string.Empty, string.Empty, null, 0, 0);
        }

        string? selectedPairId = null;
        var candidatePairs = new Dictionary<string, LiveKit.Proto.CandidatePairStats>(StringComparer.Ordinal);
        var localCandidates = new Dictionary<string, LiveKit.Proto.IceCandidateStats>(StringComparer.Ordinal);
        var remoteCandidates = new Dictionary<string, LiveKit.Proto.IceCandidateStats>(StringComparer.Ordinal);
        ulong outboundPackets = 0;
        ulong outboundBytes = 0;
        ulong transportPacketsSent = 0;
        ulong transportBytesSent = 0;

        foreach (var stat in statsList)
        {
            if (stat == null) continue;

            switch (stat.StatsCase)
            {
                case LiveKit.Proto.RtcStats.StatsOneofCase.Transport when stat.Transport?.Transport_ != null:
                    if (!string.IsNullOrWhiteSpace(stat.Transport.Transport_.SelectedCandidatePairId))
                    {
                        selectedPairId = stat.Transport.Transport_.SelectedCandidatePairId;
                    }
                    transportPacketsSent = Math.Max(transportPacketsSent, stat.Transport.Transport_.PacketsSent);
                    transportBytesSent = Math.Max(transportBytesSent, stat.Transport.Transport_.BytesSent);
                    break;

                case LiveKit.Proto.RtcStats.StatsOneofCase.CandidatePair when stat.CandidatePair?.CandidatePair_ != null:
                    var pairId = stat.CandidatePair.Rtc?.Id;
                    if (!string.IsNullOrWhiteSpace(pairId))
                    {
                        candidatePairs[pairId] = stat.CandidatePair.CandidatePair_;
                    }
                    break;

                case LiveKit.Proto.RtcStats.StatsOneofCase.LocalCandidate when stat.LocalCandidate?.Candidate != null:
                    var localId = stat.LocalCandidate.Rtc?.Id;
                    if (!string.IsNullOrWhiteSpace(localId))
                    {
                        localCandidates[localId] = stat.LocalCandidate.Candidate;
                    }
                    break;

                case LiveKit.Proto.RtcStats.StatsOneofCase.RemoteCandidate when stat.RemoteCandidate?.Candidate != null:
                    var remoteId = stat.RemoteCandidate.Rtc?.Id;
                    if (!string.IsNullOrWhiteSpace(remoteId))
                    {
                        remoteCandidates[remoteId] = stat.RemoteCandidate.Candidate;
                    }
                    break;

                case LiveKit.Proto.RtcStats.StatsOneofCase.OutboundRtp when stat.OutboundRtp?.Sent != null:
                    outboundPackets += stat.OutboundRtp.Sent.PacketsSent;
                    outboundBytes += stat.OutboundRtp.Sent.BytesSent;
                    break;
            }
        }

        LiveKit.Proto.CandidatePairStats? activePair = null;
        if (!string.IsNullOrWhiteSpace(selectedPairId) && candidatePairs.TryGetValue(selectedPairId, out var selected))
        {
            activePair = selected;
        }
        else
        {
            activePair = candidatePairs.Values.FirstOrDefault(p => p.Nominated)
                ?? candidatePairs.Values.FirstOrDefault(p => p.PacketsSent > 0 || p.BytesSent > 0);
        }

        LiveKit.Proto.IceCandidateStats? local = null;
        LiveKit.Proto.IceCandidateStats? remote = null;
        if (activePair != null)
        {
            if (!string.IsNullOrWhiteSpace(activePair.LocalCandidateId))
            {
                localCandidates.TryGetValue(activePair.LocalCandidateId, out local);
            }
            if (!string.IsNullOrWhiteSpace(activePair.RemoteCandidateId))
            {
                remoteCandidates.TryGetValue(activePair.RemoteCandidateId, out remote);
            }
        }

        var rawProtocol = !string.IsNullOrWhiteSpace(remote?.Protocol)
            ? remote!.Protocol
            : (!string.IsNullOrWhiteSpace(local?.Protocol) ? local!.Protocol : "unknown");
        var normalizedProtocol = rawProtocol.Trim().ToLowerInvariant();

        string FormatEndpoint(LiveKit.Proto.IceCandidateStats? c) =>
            c == null || string.IsNullOrWhiteSpace(c.Address)
                ? string.Empty
                : (c.Port > 0 ? $"{c.Address}:{c.Port}" : c.Address);

        var candidateType = remote?.CandidateType.ToString() ?? local?.CandidateType.ToString() ?? string.Empty;
        double? rttMs = activePair is { HasCurrentRoundTripTime: true }
            ? Math.Round(activePair.CurrentRoundTripTime * 1000.0, 2)
            : null;

        ulong effectivePackets = outboundPackets > 0 ? outboundPackets : transportPacketsSent;
        ulong effectiveBytes = outboundBytes > 0 ? outboundBytes : transportBytesSent;

        return new VoiceBotTransportStatsSnapshot(
            normalizedProtocol,
            FormatEndpoint(local),
            FormatEndpoint(remote),
            candidateType,
            rttMs,
            effectivePackets,
            effectiveBytes);
    }

    private sealed class BotRoomSession : IVoiceRoomMediaSessionContext, IDisposable
    {
        public string ChannelId { get; }
        public Room Room { get; }
        public AudioSource AudioSource { get; }
        public LocalAudioTrack AudioTrack { get; }
        public SemaphoreSlim PlaybackLock { get; } = new(1, 1);
        private readonly SemaphoreSlim _auxiliaryTrackLock = new(1, 1);

        private readonly ConcurrentDictionary<string, PublishedServerAudioTrack> _auxiliaryTracks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string TrackSid, RemoteAudioTrack Track)> _subscribedAudioTracks = new(StringComparer.Ordinal);
        private readonly Func<IReadOnlyList<IVoiceRoomPipelineStage>> _getStages;
        private readonly Action<BotRoomSession>? _onDisconnected;
        private CancellationTokenSource? _departureCts;
        private CancellationTokenSource? _activeChimeCts;
        private readonly object _lock = new();
        private readonly ILogger _logger;
        private volatile bool _stagesInitialized;
        private volatile bool _disconnected;
        private bool _disposed;

        public bool IsConnected => !_disposed && !_disconnected && Room.IsConnected;

        public BotRoomSession(
            string channelId,
            Room room,
            AudioSource audioSource,
            LocalAudioTrack audioTrack,
            Func<IReadOnlyList<IVoiceRoomPipelineStage>> getStages,
            Action<BotRoomSession>? onDisconnected,
            ILogger logger)
        {
            ChannelId = channelId;
            Room = room;
            AudioSource = audioSource;
            AudioTrack = audioTrack;
            _getStages = getStages;
            _onDisconnected = onDisconnected;
            _logger = logger;

            Room.TrackSubscribed += OnRoomTrackSubscribed;
            Room.TrackUnsubscribed += OnRoomTrackUnsubscribed;
            Room.Disconnected += OnRoomDisconnected;
        }

        private void OnRoomDisconnected(object? sender, LiveKit.Proto.DisconnectReason e)
        {
            _disconnected = true;
            lock (_lock)
            {
                try { _activeChimeCts?.Cancel(); } catch { }
            }
            _onDisconnected?.Invoke(this);
        }

        public CancellationTokenSource BeginChimePlayback(CancellationToken externalToken)
        {
            lock (_lock)
            {
                if (_activeChimeCts != null)
                {
                    try { _activeChimeCts.Cancel(); } catch { }
                }
                var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
                if (_disposed || _disconnected)
                {
                    cts.Cancel();
                }
                _activeChimeCts = cts;
                return cts;
            }
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

            // Scan existing remote participants for any microphone audio tracks that were already subscribed
            // during ConnectAsync before pipeline stages finished attaching.
            if (Room.RemoteParticipants != null)
            {
                foreach (var participant in Room.RemoteParticipants.Values)
                {
                    var identity = participant?.Identity;
                    if (string.IsNullOrWhiteSpace(identity) ||
                        LiveKitService.IsSyntheticParticipantIdentity(identity) ||
                        participant?.TrackPublications == null)
                    {
                        continue;
                    }

                    foreach (var pub in participant.TrackPublications.Values)
                    {
                        if (pub == null ||
                            pub.Source == LiveKit.Proto.TrackSource.SourceScreenshareAudio ||
                            pub.Track is not RemoteAudioTrack remoteAudioTrack)
                        {
                            continue;
                        }

                        string trackSid = pub.Sid ?? remoteAudioTrack.Sid ?? string.Empty;
                        _subscribedAudioTracks[identity] = (trackSid, remoteAudioTrack);
                    }
                }
            }

            _stagesInitialized = true;

            foreach (var kvp in _subscribedAudioTracks)
            {
                string identity = kvp.Key;
                var (trackSid, remoteAudioTrack) = kvp.Value;
                foreach (var stage in stages)
                {
                    try
                    {
                        await stage.OnRemoteAudioTrackSubscribedAsync(this, identity, trackSid, remoteAudioTrack, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[VoiceBot] Stage {StageId} error replaying initial subscribed track for {Identity}", stage.StageId, identity);
                    }
                }
            }
        }

        public async Task DetachPipelineStagesAsync(CancellationToken cancellationToken)
        {
            _stagesInitialized = false;
            _subscribedAudioTracks.Clear();
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

        private bool TryExtractHumanRemoteAudioTrack(
            TrackSubscribedEventArgs e,
            out string identity,
            out string trackSid,
            out RemoteAudioTrack remoteAudioTrack)
        {
            identity = string.Empty;
            trackSid = string.Empty;
            remoteAudioTrack = null!;

            if (_disposed || e.Track is not RemoteAudioTrack audioTrack) return false;
            var participantIdentity = e.Participant?.Identity;
            if (string.IsNullOrWhiteSpace(participantIdentity) || LiveKitService.IsSyntheticParticipantIdentity(participantIdentity))
            {
                return false;
            }

            identity = participantIdentity;
            trackSid = e.Publication?.Sid ?? audioTrack.Sid ?? string.Empty;
            remoteAudioTrack = audioTrack;
            return true;
        }

        private void DispatchStageTrackEvent(
            string identity,
            Func<IVoiceRoomPipelineStage, Task> invokeStage,
            string eventName,
            LogLevel errorLogLevel)
        {
            if (!_stagesInitialized) return;
            var stages = _getStages();
            if (stages.Count == 0) return;

            _ = Task.Run(async () =>
            {
                foreach (var stage in stages)
                {
                    try
                    {
                        await invokeStage(stage);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(errorLogLevel, ex, "[VoiceBot] Stage {StageId} error in {EventName} for {Identity}", stage.StageId, eventName, identity);
                    }
                }
            });
        }

        private void OnRoomTrackSubscribed(object? sender, TrackSubscribedEventArgs e)
        {
            if (!TryExtractHumanRemoteAudioTrack(e, out var identity, out var trackSid, out var remoteAudioTrack))
            {
                return;
            }

            // Ignore screen share audio tracks (only process microphone audio tracks)
            if (e.Publication?.Source == LiveKit.Proto.TrackSource.SourceScreenshareAudio)
            {
                return;
            }

            _subscribedAudioTracks[identity] = (trackSid, remoteAudioTrack);
            DispatchStageTrackEvent(
                identity,
                stage => stage.OnRemoteAudioTrackSubscribedAsync(this, identity, trackSid, remoteAudioTrack, CancellationToken.None),
                nameof(IVoiceRoomPipelineStage.OnRemoteAudioTrackSubscribedAsync),
                LogLevel.Warning);
        }

        private void OnRoomTrackUnsubscribed(object? sender, TrackSubscribedEventArgs e)
        {
            if (!TryExtractHumanRemoteAudioTrack(e, out var identity, out var trackSid, out _))
            {
                return;
            }

            if (_subscribedAudioTracks.TryGetValue(identity, out var existing) &&
                (string.IsNullOrEmpty(trackSid) || string.Equals(existing.TrackSid, trackSid, StringComparison.Ordinal)))
            {
                _subscribedAudioTracks.TryRemove(identity, out _);
            }

            DispatchStageTrackEvent(
                identity,
                stage => stage.OnRemoteAudioTrackUnsubscribedAsync(this, identity, trackSid, CancellationToken.None),
                nameof(IVoiceRoomPipelineStage.OnRemoteAudioTrackUnsubscribedAsync),
                LogLevel.Debug);
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

            await _auxiliaryTrackLock.WaitAsync(cancellationToken);
            try
            {
                if (_disposed || _disconnected || Room.LocalParticipant == null) return null;
                if (_auxiliaryTracks.TryGetValue(trackName, out existing))
                {
                    return existing;
                }

                // Defensive sweep: if any orphaned publication with the same trackName exists on LocalParticipant,
                // unpublish it before publishing the new track so duplicate processed-mic:<userId> tracks never accumulate.
                if (Room.LocalParticipant.TrackPublications != null)
                {
                    foreach (var stalePub in Room.LocalParticipant.TrackPublications.Values.ToList())
                    {
                        if (stalePub != null &&
                            string.Equals(stalePub.Name, trackName, StringComparison.Ordinal) &&
                            !string.IsNullOrWhiteSpace(stalePub.Sid))
                        {
                            try
                            {
                                await Room.LocalParticipant.UnpublishTrackAsync(stalePub.Sid);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "[VoiceBot] Failed to clean up stale auxiliary publication {Sid} ({TrackName})", stalePub.Sid, trackName);
                            }
                        }
                    }
                }

                var auxSource = new AudioSource(sampleRate: sampleRate, numChannels: numChannels, queueSizeMs: 200);
                var auxTrack = auxSource.CreateTrack(trackName);
                var pubOptions = new TrackPublishOptions
                {
                    Source = LiveKit.Proto.TrackSource.SourceMicrophone,
                    AudioEncoding = new AudioEncodingOptions
                    {
                        MaxBitrate = 64000
                    }
                };

                var publication = await Room.LocalParticipant.PublishTrackAsync(auxTrack, pubOptions);
                var pubSid = !string.IsNullOrWhiteSpace(publication?.Sid) ? publication.Sid : auxTrack.Sid;
                var published = new PublishedServerAudioTrack(trackName, auxSource, auxTrack, pubSid);
                _auxiliaryTracks[trackName] = published;
                return published;
            }
            finally
            {
                _auxiliaryTrackLock.Release();
            }
        }

        public async Task UnpublishAuxiliaryAudioTrackAsync(string trackName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trackName)) return;
            await _auxiliaryTrackLock.WaitAsync(cancellationToken);
            try
            {
                _auxiliaryTracks.TryRemove(trackName, out var published);
                try
                {
                    if (!_disposed && Room.LocalParticipant != null)
                    {
                        var sidsToUnpublish = new HashSet<string>(StringComparer.Ordinal);
                        if (published != null)
                        {
                            if (!string.IsNullOrWhiteSpace(published.PublicationSid))
                            {
                                sidsToUnpublish.Add(published.PublicationSid);
                            }
                            if (!string.IsNullOrWhiteSpace(published.AudioTrack?.Sid))
                            {
                                sidsToUnpublish.Add(published.AudioTrack.Sid);
                            }
                        }

                        if (Room.LocalParticipant.TrackPublications != null)
                        {
                            foreach (var pub in Room.LocalParticipant.TrackPublications.Values)
                            {
                                if (pub != null &&
                                    string.Equals(pub.Name, trackName, StringComparison.Ordinal) &&
                                    !string.IsNullOrWhiteSpace(pub.Sid))
                                {
                                    sidsToUnpublish.Add(pub.Sid);
                                }
                            }
                        }

                        foreach (var sid in sidsToUnpublish)
                        {
                            await Room.LocalParticipant.UnpublishTrackAsync(sid);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[VoiceBot] Failed to unpublish auxiliary track {TrackName}", trackName);
                }
                finally
                {
                    published?.Dispose();
                }
            }
            finally
            {
                _auxiliaryTrackLock.Release();
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
                _disconnected = true;

                try { _activeChimeCts?.Cancel(); } catch { }
                _activeChimeCts = null;

                Room.TrackSubscribed -= OnRoomTrackSubscribed;
                Room.TrackUnsubscribed -= OnRoomTrackUnsubscribed;
                Room.Disconnected -= OnRoomDisconnected;

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
                    _auxiliaryTrackLock.Dispose();
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
