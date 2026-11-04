namespace Spokes_Server.Core.Services.Communication.Voice.Pipeline;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LiveKit.Rtc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice.Dsp;

/// <summary>
/// Server-side LiveKit media pipeline stage that automatically activates <see cref="AcousticRoomProcessor"/>
/// whenever 2 or more enrolled (EmployeeId, DeviceId) participants from the same <see cref="Spokes_Server.Core.Models.Communication.PhysicalVoiceRoom"/>
/// are connected in the same voice channel.
/// Publishes per-participant cleaned audio tracks ("processed-mic:{userId}") and unsubscribes human clients
/// from the raw microphone tracks at the LiveKit SFU level.
/// </summary>
public sealed class PhysicalRoomPipelineStage : IVoiceRoomPipelineStage, IDisposable
{
    public const string ProcessedTrackPrefix = "processed-mic:";

    private readonly PhysicalVoiceRoomService _physicalRoomService;
    private readonly ChatStateService _chatState;
    private readonly LiveKitService _liveKitService;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<PhysicalRoomPipelineStage> _logger;

    private readonly ConcurrentDictionary<string, ChannelRoomPipelineState> _channelStates = new(StringComparer.Ordinal);

    public string StageId => "physical-room-isolation";

    public PhysicalRoomPipelineStage(
        PhysicalVoiceRoomService physicalRoomService,
        ChatStateService chatState,
        LiveKitService liveKitService,
        SystemConfigRepository systemConfigs,
        ILogger<PhysicalRoomPipelineStage>? logger = null)
    {
        _physicalRoomService = physicalRoomService ?? throw new ArgumentNullException(nameof(physicalRoomService));
        _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
        _liveKitService = liveKitService ?? throw new ArgumentNullException(nameof(liveKitService));
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger ?? NullLogger<PhysicalRoomPipelineStage>.Instance;

        _chatState.PhysicalRoomsChanged += OnPhysicalRoomsChanged;
    }

    public void Dispose()
    {
        _chatState.PhysicalRoomsChanged -= OnPhysicalRoomsChanged;
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
    /// Returns live acoustic calibration telemetry for a given channel and physical room (if active).
    /// </summary>
    public IReadOnlyList<PairwiseAcousticCalibration> GetActiveCalibrations(string channelId, string physicalRoomId)
    {
        if (_channelStates.TryGetValue(channelId, out var state) &&
            state.RoomProcessors.TryGetValue(physicalRoomId, out var processor))
        {
            return processor.GetCalibrations();
        }
        return [];
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

        // 1. Stop streams for users who are no longer in an active >=2 Physical Room or whose raw track was removed
        foreach (var activeUserId in state.ActiveUserWorkers.Keys.ToList())
        {
            bool isUserStillCoLocated = activeCoLocatedUserToRoom.ContainsKey(activeUserId);
            bool hasSubscribedRawTrack = state.RawTracks.ContainsKey(activeUserId);

            if (!isUserStillCoLocated || !hasSubscribedRawTrack)
            {
                if (state.ActiveUserWorkers.TryRemove(activeUserId, out var worker))
                {
                    await worker.StopAsync();

                    // Only tear down the bot's auxiliary output track and restore raw subscriptions when the user
                    // actually leaves the co-located room group (not during a 100ms noise-filter track swap or mute).
                    if (!isUserStillCoLocated)
                    {
                        state.LastTrackSidByUser.TryRemove(activeUserId, out _);
                        await state.Context.UnpublishAuxiliaryAudioTrackAsync($"{ProcessedTrackPrefix}{activeUserId}", cancellationToken);

                        if (!string.IsNullOrWhiteSpace(worker.RawTrackSid))
                        {
                            await UpdateHumanSubscriptionsForRawTrackAsync(state.Context.ChannelId, activeUserId, worker.RawTrackSid, subscribe: true, cancellationToken);
                        }
                    }
                }
            }
        }

        // Also clean up auxiliary tracks for any user who left the co-located room while muted
        foreach (var trackedUserId in state.LastTrackSidByUser.Keys.ToList())
        {
            if (!activeCoLocatedUserToRoom.ContainsKey(trackedUserId))
            {
                state.LastTrackSidByUser.TryRemove(trackedUserId, out _);
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
                if (existingWorker.RawTrackSid == rawTrackInfo.TrackSid && ReferenceEquals(existingWorker.Processor, processor))
                {
                    // Ensure any newly joined human participants are unsubscribed from this user's raw track
                    await UpdateHumanSubscriptionsForRawTrackAsync(state.Context.ChannelId, userId, rawTrackInfo.TrackSid, subscribe: false, cancellationToken);
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
                () => PersistRoomCalibrations(state.Context.ChannelId, roomId, processor));

            if (state.ActiveUserWorkers.TryAdd(userId, newWorker))
            {
                newWorker.Start();
                await UpdateHumanSubscriptionsForRawTrackAsync(state.Context.ChannelId, userId, rawTrackInfo.TrackSid, subscribe: false, cancellationToken);
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
            var devId = _chatState.GetVoiceParticipantDevice(channelId, uid)?.DeviceId;
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
                var srcTelemetry = _chatState.GetVoiceParticipantAudioTelemetry(channelId, saved.SourceEmployeeId);
                var tgtTelemetry = _chatState.GetVoiceParticipantAudioTelemetry(channelId, saved.TargetEmployeeId);
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
        var telemetry = _chatState.GetVoiceParticipantAudioTelemetry(channelId, uid);
        var devName = _chatState.GetVoiceParticipantDevice(channelId, uid)?.DeviceName ?? string.Empty;

        string filterType = telemetry?.FilterType ?? "webrtc";
        double estimatedCaptureLatencyMs = telemetry?.EstimatedCaptureLatencyMs ?? 0.0;
        double serverToClientDelayMs = telemetry?.ServerToClientDelayMs ?? 0.0;

        double baseCaptureMs = 15.0;
        if (devName.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            baseCaptureMs = 65.0;
        }
        else if (devName.Contains("iOS", StringComparison.OrdinalIgnoreCase) ||
                 devName.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
                 devName.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            baseCaptureMs = 30.0;
        }

        double filterLookaheadMs = filterType.ToLowerInvariant() switch
        {
            "rnnoise" => 15.3,
            "deepfilternet" => 30.3,
            "none" => 0.0,
            _ => 10.0
        };

        double platformFloorMs = Math.Round(baseCaptureMs + filterLookaheadMs, 1);
        if (estimatedCaptureLatencyMs <= 0 || (baseCaptureMs > 15.0 && estimatedCaptureLatencyMs < platformFloorMs))
        {
            estimatedCaptureLatencyMs = platformFloorMs;
        }

        processor.SetParticipantClientPipelineInfo(
            uid,
            filterType,
            estimatedCaptureLatencyMs,
            serverToClientDelayMs);
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

            var srcDevId = _chatState.GetVoiceParticipantDevice(channelId, cal.SourceUserId)?.DeviceId;
            var tgtDevId = _chatState.GetVoiceParticipantDevice(channelId, cal.TargetUserId)?.DeviceId;
            if (string.IsNullOrWhiteSpace(srcDevId) || string.IsNullOrWhiteSpace(tgtDevId))
            {
                continue;
            }

            var srcTelemetry = _chatState.GetVoiceParticipantAudioTelemetry(channelId, cal.SourceUserId);
            var tgtTelemetry = _chatState.GetVoiceParticipantAudioTelemetry(channelId, cal.TargetUserId);

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

    private async Task UpdateHumanSubscriptionsForRawTrackAsync(
        string channelId,
        string trackOwnerUserId,
        string rawTrackSid,
        bool subscribe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawTrackSid)) return;

        var config = _systemConfigs.Get();
        if (string.IsNullOrEmpty(config.LiveKitApiKey) || string.IsNullOrEmpty(config.LiveKitApiSecret))
        {
            return;
        }

        var participants = _chatState.GetVoiceParticipants(channelId);
        foreach (var listenerId in participants)
        {
            if (string.IsNullOrWhiteSpace(listenerId) ||
                string.Equals(listenerId, trackOwnerUserId, StringComparison.Ordinal) ||
                listenerId.StartsWith("bot:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await _liveKitService.UpdateParticipantSubscriptionsAsync(
                channelId,
                listenerId,
                [rawTrackSid],
                subscribe,
                config.LiveKitApiKey,
                config.LiveKitApiSecret,
                cancellationToken);
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

        private readonly ILogger _logger;
        private readonly Action? _onPersistCalibrations;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loopTask;

        public ParticipantDspWorker(
            string userId,
            string rawTrackSid,
            RemoteAudioTrack rawTrack,
            PublishedServerAudioTrack outputTrack,
            AcousticRoomProcessor processor,
            ILogger logger,
            Action? onPersistCalibrations = null)
        {
            UserId = userId;
            RawTrackSid = rawTrackSid;
            RawTrack = rawTrack;
            OutputTrack = outputTrack;
            Processor = processor;
            _logger = logger;
            _onPersistCalibrations = onPersistCalibrations;
        }

        public void Start()
        {
            _loopTask = Task.Run(() => RunAudioLoopAsync(_cts.Token));
        }

        public async Task StopAsync()
        {
            try { _cts.Cancel(); } catch { }
            if (_loopTask != null)
            {
                try { await _loopTask; } catch { }
            }
            _cts.Dispose();
        }

        private async Task RunAudioLoopAsync(CancellationToken cancellationToken)
        {
            int framesSincePersist = 0;
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

                    short[] cleanedPcm16 = Processor.ProcessParticipantFrame(UserId, pcm16Span, wallClockMs);
                    byte[] cleanedBytes = MemoryMarshal.AsBytes(cleanedPcm16.AsSpan()).ToArray();

                    var outFrame = new AudioFrame(
                        cleanedBytes,
                        AcousticRoomProcessor.SampleRate,
                        1,
                        cleanedPcm16.Length,
                        null);

                    await OutputTrack.AudioSource.CaptureFrameAsync(outFrame, cancellationToken);

                    if (++framesSincePersist >= 150)
                    {
                        framesSincePersist = 0;
                        try { _onPersistCalibrations?.Invoke(); } catch { }
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
                try { _onPersistCalibrations?.Invoke(); } catch { }
            }
        }
    }
}
