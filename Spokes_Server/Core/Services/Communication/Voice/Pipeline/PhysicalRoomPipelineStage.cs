namespace Spokes_Server.Core.Services.Communication.Voice.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LiveKit.Rtc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;

/// <summary>
/// Server-side LiveKit media pipeline stage that automatically activates <see cref="AcousticRoomProcessor"/>
/// whenever 2 or more enrolled (EmployeeId, DeviceId) participants from the same <see cref="Spokes_Server.Core.Models.Communication.PhysicalVoiceRoom"/>
/// are connected in the same voice channel.
/// Publishes per-participant cleaned audio tracks ("processed-mic:{userId}"); human clients deterministically reconcile
/// subscriptions between <c>processed-mic:{userId}</c>, raw microphone tracks, and Direct LAN audio on their own signaling session.
/// </summary>
public sealed class PhysicalRoomPipelineStage : IVoiceRoomPipelineStage, IDisposable
{
    public const string ProcessedTrackPrefix = "processed-mic:";

    private readonly PhysicalVoiceRoomService _physicalRoomService;
    private readonly VoiceStateService _voiceState;
    private readonly ILogger<PhysicalRoomPipelineStage> _logger;

    private readonly ConcurrentDictionary<string, ChannelRoomPipelineState> _channelStates = new(StringComparer.Ordinal);

    public string StageId => "physical-room-isolation";

    public PhysicalRoomPipelineStage(
        PhysicalVoiceRoomService physicalRoomService,
        VoiceStateService voiceState,
        ILogger<PhysicalRoomPipelineStage>? logger = null)
    {
        _physicalRoomService = physicalRoomService ?? throw new ArgumentNullException(nameof(physicalRoomService));
        _voiceState = voiceState ?? throw new ArgumentNullException(nameof(voiceState));
        _logger = logger ?? NullLogger<PhysicalRoomPipelineStage>.Instance;

        _voiceState.PhysicalRoomsChanged += OnPhysicalRoomsChanged;
    }

    public void Dispose()
    {
        _voiceState.PhysicalRoomsChanged -= OnPhysicalRoomsChanged;
    }

    private void OnPhysicalRoomsChanged()
    {
        foreach (var state in _channelStates.Values)
        {
            _ = ReconcileChannelDspStateSafeAsync(state);
        }
    }

    private async Task ReconcileChannelDspStateSafeAsync(ChannelRoomPipelineState state)
    {
        try
        {
            await ReconcileChannelDspStateAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PhysicalRoomPipeline] Failed to reconcile channel {ChannelId} after PhysicalRoomsChanged", state.Context.ChannelId);
        }
    }

    public async Task OnSessionAttachedAsync(IVoiceRoomMediaSessionContext context, CancellationToken cancellationToken = default)
    {
        var state = _channelStates.GetOrAdd(context.ChannelId, _ => new ChannelRoomPipelineState(context));
        await ReconcileChannelDspStateAsync(state, cancellationToken);
    }

    public async Task OnRemoteAudioTrackSubscribedAsync(
        IVoiceRoomMediaSessionContext context,
        string participantIdentity,
        string trackSid,
        RemoteAudioTrack track,
        CancellationToken cancellationToken = default)
    {
        var state = _channelStates.GetOrAdd(context.ChannelId, _ => new ChannelRoomPipelineState(context));
        await state.ReconcileLock.WaitAsync(cancellationToken);
        try
        {
            state.RawTracks[participantIdentity] = (trackSid, track);
            await ReconcileChannelDspStateCoreAsync(state, cancellationToken);
        }
        finally
        {
            state.ReconcileLock.Release();
        }
    }

    public async Task OnRemoteAudioTrackUnsubscribedAsync(
        IVoiceRoomMediaSessionContext context,
        string participantIdentity,
        string trackSid,
        CancellationToken cancellationToken = default)
    {
        if (_channelStates.TryGetValue(context.ChannelId, out var state))
        {
            await state.ReconcileLock.WaitAsync(cancellationToken);
            try
            {
                // Only remove the raw track if its TrackSid matches the unsubscribed trackSid.
                // This prevents an out-of-order unsubscribe from an old filter track (e.g. RNNoise -> WebRTC switch)
                // from wiping out the newly subscribed replacement track!
                if (state.RawTracks.TryGetValue(participantIdentity, out var existing) &&
                    (string.IsNullOrEmpty(trackSid) || string.Equals(existing.TrackSid, trackSid, StringComparison.Ordinal)))
                {
                    state.RawTracks.TryRemove(participantIdentity, out _);
                }

                await ReconcileChannelDspStateCoreAsync(state, cancellationToken);
            }
            finally
            {
                state.ReconcileLock.Release();
            }
        }
    }

    public async Task OnRoomConfigurationChangedAsync(IVoiceRoomMediaSessionContext context, CancellationToken cancellationToken = default)
    {
        var state = _channelStates.GetOrAdd(context.ChannelId, _ => new ChannelRoomPipelineState(context));
        await ReconcileChannelDspStateAsync(state, cancellationToken);
    }

    public async Task OnSessionDetachedAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (_channelStates.TryRemove(channelId, out var state))
        {
            foreach (var kvp in state.RoomProcessors)
            {
                PersistRoomCalibrations(channelId, kvp.Key, kvp.Value);
            }
            await state.DisposeAsync();
        }
    }

    /// <summary>
    /// Returns a comprehensive diagnostic snapshot (gate states, pairwise matrix, physics decomposition, timeline, warnings)
    /// for an active physical room in the specified voice channel.
    /// </summary>
    public PhysicalRoomDiagnosticSnapshot? GetRoomDiagnosticSnapshot(string channelId, string physicalRoomId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(physicalRoomId))
        {
            return null;
        }

        if (_channelStates.TryGetValue(channelId, out var state) &&
            state.RoomProcessors.TryGetValue(physicalRoomId, out var processor))
        {
            foreach (var uid in state.ActiveUserWorkers.Keys)
            {
                SyncParticipantPipelineTelemetry(channelId, uid, processor);
            }
            return processor.GetDiagnosticSnapshot();
        }
        return null;
    }

    /// <summary>
    /// Resets rolling diagnostic counters and timeline buckets for an active physical room processor
    /// (while preserving learned calibrations) to start a fresh diagnostic capture window.
    /// </summary>
    public bool ResetRoomDiagnosticCounters(string channelId, string physicalRoomId)
    {
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(physicalRoomId))
        {
            return false;
        }

        if (_channelStates.TryGetValue(channelId, out var state) &&
            state.RoomProcessors.TryGetValue(physicalRoomId, out var processor))
        {
            processor.ResetDiagnosticCounters();
            return true;
        }
        return false;
    }

    private async Task ReconcileChannelDspStateAsync(ChannelRoomPipelineState state, CancellationToken cancellationToken)
    {
        await state.ReconcileLock.WaitAsync(cancellationToken);
        try
        {
            await ReconcileChannelDspStateCoreAsync(state, cancellationToken);
        }
        finally
        {
            state.ReconcileLock.Release();
        }
    }

    private async Task ReconcileChannelDspStateCoreAsync(ChannelRoomPipelineState state, CancellationToken cancellationToken)
    {
        var activeGroups = _physicalRoomService.GetActiveCoLocatedRoomGroups(state.Context.ChannelId);
        var activeCoLocatedUserToRoom = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var kvp in activeGroups)
        {
            string roomId = kvp.Key;
            var userIds = kvp.Value.ActiveUserIds;

            // Ensure processor exists for this physical room and synchronize its registered participant list.
            // Keeping all co-located users registered ensures Room Isolation stays active (never dropping to "Standby (1 Device)")
            // when a user switches noise filters (RNNoise <-> WebRTC) or temporarily mutes their microphone.
            var processor = state.RoomProcessors.GetOrAdd(roomId, _ => new AcousticRoomProcessor());
            processor.SyncParticipants(userIds);
            SyncRoomProcessorMetadataAndSavedCalibrations(state.Context.ChannelId, kvp.Value.Room, userIds, processor);
            foreach (var uid in userIds)
            {
                activeCoLocatedUserToRoom[uid] = roomId;
            }
        }

        // Clean up processors for physical rooms that no longer have >= 2 active participants
        foreach (var existingRoomId in state.RoomProcessors.Keys.ToList())
        {
            if (!activeGroups.ContainsKey(existingRoomId))
            {
                if (state.RoomProcessors.TryRemove(existingRoomId, out var removedProcessor))
                {
                    PersistRoomCalibrations(state.Context.ChannelId, existingRoomId, removedProcessor);
                }
            }
        }

        // 1. Stop streams for users who are no longer in an active >=2 Physical Room or whose raw track was removed.
        // Readiness Invariant: processed-mic:{userId} must NEVER remain published without an active ParticipantDspWorker
        // pumping frames from a subscribed RawTrack, otherwise remote callers will subscribe to a silent track.
        foreach (var activeUserId in state.ActiveUserWorkers.Keys.ToList())
        {
            bool isUserStillCoLocated = activeCoLocatedUserToRoom.ContainsKey(activeUserId);
            bool hasSubscribedRawTrack = state.RawTracks.ContainsKey(activeUserId);

            if (!isUserStillCoLocated || !hasSubscribedRawTrack)
            {
                if (state.ActiveUserWorkers.TryRemove(activeUserId, out var worker))
                {
                    await worker.StopAsync();
                    if (!isUserStillCoLocated)
                    {
                        state.LastTrackSidByUser.TryRemove(activeUserId, out _);
                    }
                    await state.Context.UnpublishAuxiliaryAudioTrackAsync($"{ProcessedTrackPrefix}{activeUserId}", cancellationToken);
                }
            }
        }

        // Also clean up auxiliary tracks for any user who left the co-located room or lost their raw track
        foreach (var trackedUserId in state.LastTrackSidByUser.Keys.ToList())
        {
            if (!activeCoLocatedUserToRoom.ContainsKey(trackedUserId))
            {
                state.LastTrackSidByUser.TryRemove(trackedUserId, out _);
                await state.Context.UnpublishAuxiliaryAudioTrackAsync($"{ProcessedTrackPrefix}{trackedUserId}", cancellationToken);
            }
            else if (!state.RawTracks.ContainsKey(trackedUserId))
            {
                await state.Context.UnpublishAuxiliaryAudioTrackAsync($"{ProcessedTrackPrefix}{trackedUserId}", cancellationToken);
            }
        }

        // 2. Start workers for co-located users who have a subscribed raw track
        foreach (var kvp in activeCoLocatedUserToRoom)
        {
            string userId = kvp.Key;
            string roomId = kvp.Value;

            if (!state.RawTracks.TryGetValue(userId, out var rawTrackInfo))
            {
                continue;
            }

            if (!state.RoomProcessors.TryGetValue(roomId, out var processor))
            {
                continue;
            }

            if (state.ActiveUserWorkers.TryGetValue(userId, out var existingWorker))
            {
                if (existingWorker.RawTrackSid == rawTrackInfo.TrackSid &&
                    ReferenceEquals(existingWorker.Processor, processor) &&
                    existingWorker.IsHealthy)
                {
                    continue;
                }

                state.ActiveUserWorkers.TryRemove(userId, out _);
                await existingWorker.StopAsync();
            }

            // Detect if this participant switched audio tracks (e.g. changing Noise Filter between RNNoise, DeepFilterNet, and Basic WebRTC)
            if (state.LastTrackSidByUser.TryGetValue(userId, out var previousTrackSid) &&
                !string.Equals(previousTrackSid, rawTrackInfo.TrackSid, StringComparison.Ordinal))
            {
                processor.NotifyParticipantTrackReplaced(userId);
            }
            state.LastTrackSidByUser[userId] = rawTrackInfo.TrackSid;

            var publishedAux = await state.Context.PublishAuxiliaryAudioTrackAsync(
                $"{ProcessedTrackPrefix}{userId}",
                sampleRate: AcousticRoomProcessor.SampleRate,
                numChannels: 1,
                cancellationToken: cancellationToken);

            if (publishedAux == null)
            {
                continue;
            }

            var newWorker = new ParticipantDspWorker(
                userId,
                rawTrackInfo.TrackSid,
                rawTrackInfo.Track,
                publishedAux,
                processor,
                _logger,
                () => _voiceState.IsUserMuted(state.Context.ChannelId, userId),
                () => PersistRoomCalibrations(state.Context.ChannelId, roomId, processor),
                () => _ = Task.Run(() => ReconcileChannelDspStateAsync(state, CancellationToken.None)),
                (uid, speaking) => _voiceState.SetPhysicalRoomDspSpeaking(state.Context.ChannelId, uid, speaking),
                (uid, managed) => _voiceState.SetPhysicalRoomDspManaged(state.Context.ChannelId, uid, managed));

            if (state.ActiveUserWorkers.TryAdd(userId, newWorker))
            {
                newWorker.Start();
                _logger.LogInformation(
                    "[PhysicalRoomPipeline] Activated server-side cross-talk isolation for user {UserId} in channel {ChannelId} (room {RoomId}, track {TrackSid})",
                    userId, state.Context.ChannelId, roomId, rawTrackInfo.TrackSid);
            }
            else
            {
                await newWorker.StopAsync();
            }
        }
    }

    private void SyncRoomProcessorMetadataAndSavedCalibrations(
        string channelId,
        PhysicalVoiceRoom room,
        IReadOnlyCollection<string> userIds,
        AcousticRoomProcessor processor)
    {
        var userToDeviceId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var uid in userIds)
        {
            var devId = _voiceState.GetVoiceParticipantDevice(channelId, uid)?.DeviceId;
            if (!string.IsNullOrWhiteSpace(devId))
            {
                userToDeviceId[uid] = devId;
            }

            SyncParticipantPipelineTelemetry(channelId, uid, processor);
        }

        if (room.SavedCalibrations == null || room.SavedCalibrations.Count == 0)
        {
            return;
        }

        foreach (var saved in room.SavedCalibrations)
        {
            if (userToDeviceId.TryGetValue(saved.SourceEmployeeId, out var srcDevId) &&
                string.Equals(srcDevId, saved.SourceDeviceId, StringComparison.Ordinal) &&
                userToDeviceId.TryGetValue(saved.TargetEmployeeId, out var tgtDevId) &&
                string.Equals(tgtDevId, saved.TargetDeviceId, StringComparison.Ordinal))
            {
                var srcTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, saved.SourceEmployeeId);
                var tgtTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, saved.TargetEmployeeId);
                string currentSrcFilter = srcTelemetry?.FilterType ?? "client-default";
                string currentTgtFilter = tgtTelemetry?.FilterType ?? "client-default";

                // Only seed the saved delay if the client filter pair matches (or has not changed to a different lookahead profile)
                if (string.Equals(saved.SourceFilterType, currentSrcFilter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(saved.TargetFilterType, currentTgtFilter, StringComparison.OrdinalIgnoreCase))
                {
                    processor.SeedSavedPairCalibration(
                        saved.SourceEmployeeId,
                        saved.TargetEmployeeId,
                        saved.DelaySamples,
                        saved.BleedGain,
                        saved.Confidence);
                }
            }
        }
    }

    private void SyncParticipantPipelineTelemetry(string channelId, string uid, AcousticRoomProcessor processor)
    {
        var telemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, uid);
        var devName = _voiceState.GetVoiceParticipantDevice(channelId, uid)?.DeviceName ?? string.Empty;

        string filterType = telemetry?.FilterType ?? "webrtc";
        string platform = telemetry?.Platform ?? string.Empty;
        double reportedLatencyMs = telemetry?.EstimatedCaptureLatencyMs ?? 0.0;
        double serverToClientDelayMs = telemetry?.ServerToClientDelayMs ?? 0.0;

        double effectiveCaptureLatencyMs = ResolveEffectiveCaptureLatencyMs(
            reportedLatencyMs,
            platform,
            devName,
            filterType);

        processor.SetParticipantClientPipelineInfo(
            uid,
            effectiveCaptureLatencyMs,
            serverToClientDelayMs);
    }

    internal static double ResolveEffectiveCaptureLatencyMs(
        double reportedLatencyMs,
        string? platform,
        string? deviceName,
        string? filterType)
    {
        bool isNativeAndroid = string.Equals(platform, "android-native", StringComparison.OrdinalIgnoreCase);
        string safeDeviceName = deviceName ?? string.Empty;

        double baseCaptureMs = 15.0;
        if (isNativeAndroid)
        {
            // Native JavaAudioDeviceModule + hardware AEC path bypasses WebView AudioContext/Worklet queues.
            baseCaptureMs = 25.0;
        }
        else if (safeDeviceName.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
        {
            baseCaptureMs = 65.0;
        }
        else if (safeDeviceName.Contains("iOS", StringComparison.OrdinalIgnoreCase) ||
                 safeDeviceName.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                 safeDeviceName.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase))
        {
            baseCaptureMs = 30.0;
        }

        double filterLookaheadMs = (filterType ?? "webrtc").ToLowerInvariant() switch
        {
            "rnnoise" => isNativeAndroid ? 10.0 : 15.3,
            "deepfilternet" => 30.3,
            "none" => 0.0,
            _ => 10.0
        };

        double platformFloorMs = Math.Round(baseCaptureMs + filterLookaheadMs, 1);
        double effectiveLatencyMs = reportedLatencyMs;
        if (effectiveLatencyMs <= 0 || (baseCaptureMs > 15.0 && effectiveLatencyMs < platformFloorMs))
        {
            effectiveLatencyMs = platformFloorMs;
        }

        return effectiveLatencyMs;
    }

    private void PersistRoomCalibrations(string channelId, string roomId, AcousticRoomProcessor processor)
    {
        var calibrations = processor.GetCalibrations();
        foreach (var cal in calibrations)
        {
            if (cal.IsReciprocalPrior || !cal.IsLiveVerified || cal.Confidence < 0.45)
            {
                continue;
            }

            var srcDevId = _voiceState.GetVoiceParticipantDevice(channelId, cal.SourceUserId)?.DeviceId;
            var tgtDevId = _voiceState.GetVoiceParticipantDevice(channelId, cal.TargetUserId)?.DeviceId;
            if (string.IsNullOrWhiteSpace(srcDevId) || string.IsNullOrWhiteSpace(tgtDevId))
            {
                continue;
            }

            var srcTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, cal.SourceUserId);
            var tgtTelemetry = _voiceState.GetVoiceParticipantAudioTelemetry(channelId, cal.TargetUserId);

            _physicalRoomService.SaveLearnedPairCalibration(roomId, new PhysicalRoomSavedPairCalibration
            {
                SourceEmployeeId = cal.SourceUserId,
                SourceDeviceId = srcDevId,
                TargetEmployeeId = cal.TargetUserId,
                TargetDeviceId = tgtDevId,
                SourceFilterType = srcTelemetry?.FilterType ?? "client-default",
                TargetFilterType = tgtTelemetry?.FilterType ?? "client-default",
                DelaySamples = cal.DelaySamples48k,
                BleedGain = cal.BleedGainLinear,
                Confidence = cal.Confidence,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private sealed class ChannelRoomPipelineState : IAsyncDisposable
    {
        public IVoiceRoomMediaSessionContext Context { get; }
        public SemaphoreSlim ReconcileLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, (string TrackSid, RemoteAudioTrack Track)> RawTracks { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, string> LastTrackSidByUser { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, AcousticRoomProcessor> RoomProcessors { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, ParticipantDspWorker> ActiveUserWorkers { get; } = new(StringComparer.Ordinal);

        public ChannelRoomPipelineState(IVoiceRoomMediaSessionContext context)
        {
            Context = context;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var kvp in ActiveUserWorkers)
            {
                try { await kvp.Value.StopAsync(); } catch { }
            }
            ActiveUserWorkers.Clear();
            LastTrackSidByUser.Clear();
            RoomProcessors.Clear();
            RawTracks.Clear();
            ReconcileLock.Dispose();
        }
    }

    private sealed class ParticipantDspWorker
    {
        public string UserId { get; }
        public string RawTrackSid { get; }
        public RemoteAudioTrack RawTrack { get; }
        public PublishedServerAudioTrack OutputTrack { get; }
        public AcousticRoomProcessor Processor { get; }

        private const float DspSpeechRmsThreshold = 0.012f; // ~ -38.4 dBFS on post-leveler cleaned audio
        private const int DspSpeechAttackFrames = 2;
        private const long DspSpeechHangoverMs = 300;

        private readonly ILogger _logger;
        private readonly Func<bool>? _isParticipantMuted;
        private readonly Action? _onPersistCalibrations;
        private readonly Action? _onUnexpectedStop;
        private readonly Action<string, bool>? _onDspSpeakingChanged;
        private readonly Action<string, bool>? _onDspManagedChanged;
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<AudioFrame> _egressChannel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        private Task? _egressTask;
        private int _persistInFlight;
        private Task? _loopTask;

        public bool IsHealthy =>
            !_cts.IsCancellationRequested &&
            _loopTask is { IsCompleted: false } &&
            _egressTask is { IsCompleted: false };

        public ParticipantDspWorker(
            string userId,
            string rawTrackSid,
            RemoteAudioTrack rawTrack,
            PublishedServerAudioTrack outputTrack,
            AcousticRoomProcessor processor,
            ILogger logger,
            Func<bool>? isParticipantMuted = null,
            Action? onPersistCalibrations = null,
            Action? onUnexpectedStop = null,
            Action<string, bool>? onDspSpeakingChanged = null,
            Action<string, bool>? onDspManagedChanged = null)
        {
            UserId = userId;
            RawTrackSid = rawTrackSid;
            RawTrack = rawTrack;
            OutputTrack = outputTrack;
            Processor = processor;
            _logger = logger;
            _isParticipantMuted = isParticipantMuted;
            _onPersistCalibrations = onPersistCalibrations;
            _onUnexpectedStop = onUnexpectedStop;
            _onDspSpeakingChanged = onDspSpeakingChanged;
            _onDspManagedChanged = onDspManagedChanged;
        }

        public void Start()
        {
            try { _onDspManagedChanged?.Invoke(UserId, true); } catch { }
            _egressTask = Task.Run(() => RunAudioEgressLoopAsync(_cts.Token));
            _loopTask = Task.Run(() => RunAudioLoopAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            try { _cts.Cancel(); } catch { }
            if (_loopTask != null)
            {
                try { await _loopTask; } catch { }
            }
            _egressChannel.Writer.TryComplete();
            if (_egressTask != null)
            {
                try { await _egressTask; } catch { }
            }
            _cts.Dispose();
        }

        private void TriggerBackgroundPersist()
        {
            if (_onPersistCalibrations == null) return;
            if (Interlocked.CompareExchange(ref _persistInFlight, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _onPersistCalibrations.Invoke();
                }
                catch
                {
                }
                finally
                {
                    Volatile.Write(ref _persistInFlight, 0);
                }
            });
        }

        private async Task RunAudioEgressLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var outFrame in _egressChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        await OutputTrack.AudioSource.CaptureFrameAsync(outFrame, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception frameEx)
                    {
                        // Swallow transient native queue full / FFI frame capture errors so the egress loop stays alive
                        _logger.LogDebug(frameEx, "[PhysicalRoomPipeline] Transient audio egress frame drop for {UserId}: {Message}", UserId, frameEx.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PhysicalRoomPipeline] Audio egress loop ended for {UserId}: {Message}", UserId, ex.Message);
            }
        }

        private static float ComputePcm16Rms(ReadOnlySpan<short> samples)
        {
            if (samples.IsEmpty) return 0f;
            double sumSq = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double norm = samples[i] / 32768.0;
                sumSq += norm * norm;
            }
            return (float)Math.Sqrt(sumSq / samples.Length);
        }

        private async Task RunAudioLoopAsync(CancellationToken cancellationToken)
        {
            int framesSincePersist = 0;
            bool isDspSpeaking = false;
            int consecutiveSpeechFrames = 0;
            long lastSpeechTickMs = 0;
            try
            {
                using var audioStream = new AudioStream(RawTrack, sampleRate: AcousticRoomProcessor.SampleRate, numChannels: 1);
                await foreach (var frameEvent in audioStream.WithCancellation(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var frame = frameEvent.Frame;
                    byte[] rawBytes = frame.DataBytes;
                    if (rawBytes == null || rawBytes.Length < 2) continue;

                    ReadOnlySpan<short> pcm16Span = MemoryMarshal.Cast<byte, short>(rawBytes.AsSpan());
                    long wallClockMs = Environment.TickCount64;

                    if (_isParticipantMuted?.Invoke() == true)
                    {
                        short[] silentPcm16 = new short[pcm16Span.Length];
                        Processor.ProcessParticipantFrame(UserId, silentPcm16, wallClockMs);
                        consecutiveSpeechFrames = 0;
                        if (isDspSpeaking)
                        {
                            isDspSpeaking = false;
                            _onDspSpeakingChanged?.Invoke(UserId, false);
                        }
                        continue;
                    }

                    short[] cleanedPcm16 = Processor.ProcessParticipantFrame(UserId, pcm16Span, wallClockMs);
                    float cleanedRms = ComputePcm16Rms(cleanedPcm16);
                    if (cleanedRms >= DspSpeechRmsThreshold)
                    {
                        consecutiveSpeechFrames++;
                        lastSpeechTickMs = wallClockMs;
                        if (!isDspSpeaking && consecutiveSpeechFrames >= DspSpeechAttackFrames)
                        {
                            isDspSpeaking = true;
                            _onDspSpeakingChanged?.Invoke(UserId, true);
                        }
                    }
                    else
                    {
                        consecutiveSpeechFrames = 0;
                        if (isDspSpeaking && wallClockMs - lastSpeechTickMs >= DspSpeechHangoverMs)
                        {
                            isDspSpeaking = false;
                            _onDspSpeakingChanged?.Invoke(UserId, false);
                        }
                    }

                    byte[] cleanedBytes = MemoryMarshal.AsBytes(cleanedPcm16.AsSpan()).ToArray();

                    var outFrame = new AudioFrame(
                        cleanedBytes,
                        AcousticRoomProcessor.SampleRate,
                        1,
                        cleanedPcm16.Length,
                        null);

                    _egressChannel.Writer.TryWrite(outFrame);

                    if (++framesSincePersist >= 150)
                    {
                        framesSincePersist = 0;
                        TriggerBackgroundPersist();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PhysicalRoomPipeline] Audio stream loop ended for {UserId}: {Message}", UserId, ex.Message);
            }
            finally
            {
                if (isDspSpeaking)
                {
                    try { _onDspSpeakingChanged?.Invoke(UserId, false); } catch { }
                }
                try { _onDspManagedChanged?.Invoke(UserId, false); } catch { }
                try { _onPersistCalibrations?.Invoke(); } catch { }
                if (!cancellationToken.IsCancellationRequested)
                {
                    try { _onUnexpectedStop?.Invoke(); } catch { }
                }
            }
        }
    }
}
