using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spokes_Server.Core.Services.Communication.Voice;

public class VoiceDeviceService : IVoiceDeviceService
{
    private readonly IVoiceJsInteropService? _jsInterop;
    private readonly ILogger _logger;

    public event Action<List<AudioDevice>, string, List<AudioDevice>, string>? OnAudioDevicesChanged;
    public event Action<List<CameraDevice>, string>? OnCamerasUpdated;
    public event Action<string>? OnActiveCameraChanged;
    public event Action<string>? OnActiveMicrophoneChanged;
    public event Action<string>? OnActiveSpeakerChanged;
    public event Action<string>? OnIosMicrophoneModeChanged;
    public event Action<int, int>? OnLocalVideoDimensionsChanged;
    public event Action<int, int>? OnLocalScreenDimensionsChanged;

    public ConcurrentDictionary<string, int> RemoteVideoHeights { get; } = new();
    public ConcurrentDictionary<string, int> RemoteVideoFps { get; } = new();

    public VoiceDeviceService(IVoiceJsInteropService? jsInterop = null, ILogger? logger = null)
    {
        _jsInterop = jsInterop;
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task SwitchCameraAsync(string? deviceId, bool isVideoOn)
    {
        try
        {
            if (_jsInterop != null && _jsInterop.IsModuleLoaded && isVideoOn)
            {
                var resolvedId = await _jsInterop.SwitchActiveCameraAsync(deviceId);
                var effectiveId = !string.IsNullOrWhiteSpace(resolvedId) ? resolvedId : (deviceId ?? "");
                if (!string.IsNullOrWhiteSpace(effectiveId))
                {
                    OnActiveCameraChanged?.Invoke(effectiveId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] SwitchCamera failed: {Message}", ex.Message);
        }
    }

    public async Task UpdateCameraSettingsAsync(string quality, int fps, string deviceId, bool isVideoOn)
    {
        try
        {
            if (_jsInterop != null && _jsInterop.IsModuleLoaded && isVideoOn)
            {
                await _jsInterop.UpdateCameraSettingsAsync(quality, fps, deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] UpdateCameraSettings failed: {Message}", ex.Message);
        }
    }

    public async Task SwitchMicrophoneAsync(string deviceId)
    {
        try
        {
            if (_jsInterop != null)
            {
                await _jsInterop.SwitchActiveMicrophoneAsync(deviceId);
                OnActiveMicrophoneChanged?.Invoke(deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] SwitchMicrophone failed: {Message}", ex.Message);
        }
    }

    public async Task SwitchSpeakerAsync(string deviceId)
    {
        try
        {
            if (_jsInterop != null)
            {
                await _jsInterop.SwitchActiveSpeakerAsync(deviceId);
                OnActiveSpeakerChanged?.Invoke(deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Voice] SwitchSpeaker failed: {Message}", ex.Message);
        }
    }

    public async Task RefreshAudioDevicesAsync()
    {
        try
        {
            if (_jsInterop != null)
            {
                await _jsInterop.UpdateAudioDevicesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh audio devices");
        }
    }

    public async Task<string> GetIosMicrophoneModeAsync(bool isIos)
    {
        if (_jsInterop != null && _jsInterop.IsModuleLoaded && isIos)
        {
            try
            {
                var mode = await _jsInterop.GetIosMicrophoneModeAsync();
                OnIosMicrophoneModeChanged?.Invoke(mode);
                return mode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get iOS microphone mode");
            }
        }
        return "unsupported";
    }

    public async Task PromptIosVoiceIsolationAsync(bool isIos, Func<Task>? onRefresh = null)
    {
        if (_jsInterop != null && _jsInterop.IsModuleLoaded && isIos)
        {
            try
            {
                await _jsInterop.PromptIosVoiceIsolationAsync();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2500);
                    try
                    {
                        var mode = await GetIosMicrophoneModeAsync(isIos);
                        if (onRefresh != null)
                        {
                            await onRefresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to refresh iOS microphone mode");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prompt iOS voice isolation");
            }
        }
    }

    public async Task SetRemoteVideoQualityAsync(string participantId, string quality)
    {
        if (_jsInterop != null && _jsInterop.IsModuleLoaded)
        {
            await _jsInterop.SetRemoteVideoQualityAsync(participantId, quality);
        }
    }

    private Task? _inFlightReattachTask;

    public async Task ReattachTracksAsync()
    {
        if (_jsInterop == null || !_jsInterop.IsModuleLoaded)
        {
            return;
        }

        var existing = _inFlightReattachTask;
        if (existing is { IsCompleted: false })
        {
            await existing;
            return;
        }

        var task = ReattachTracksCoreAsync();
        _inFlightReattachTask = task;
        await task;
    }

    private async Task ReattachTracksCoreAsync()
    {
        try
        {
            await Task.Yield();
            if (_jsInterop != null && _jsInterop.IsModuleLoaded)
            {
                await _jsInterop.ReattachAllTracksAsync();
            }
        }
        finally
        {
            _inFlightReattachTask = null;
        }
    }

    public async Task RefreshVideoLayoutsAsync()
    {
        if (_jsInterop != null && _jsInterop.IsModuleLoaded)
        {
            await _jsInterop.RefreshVideoLayoutsAsync();
        }
    }

    public void HandleAudioDevicesUpdated(List<AudioDevice> mics, string activeMicId, List<AudioDevice> speakers, string activeSpeakerId)
    {
        OnAudioDevicesChanged?.Invoke(mics, activeMicId, speakers, activeSpeakerId);
    }

    public void HandleCamerasUpdated(List<CameraDevice> cameras, string activeCameraId)
    {
        OnCamerasUpdated?.Invoke(cameras, activeCameraId);
    }

    private int _lastLocalVideoMaxEdge;
    private int _lastLocalVideoFps;
    private int _lastLocalScreenMaxEdge;
    private int _lastLocalScreenFps;

    public bool HandleParticipantVideoDimensions(string participantId, int height, int fps)
    {
        bool heightUnchanged = RemoteVideoHeights.TryGetValue(participantId, out var prevH) && prevH == height;
        bool fpsUnchanged = RemoteVideoFps.TryGetValue(participantId, out var prevFps) && prevFps == fps;
        if (heightUnchanged && fpsUnchanged)
        {
            return false;
        }

        RemoteVideoHeights[participantId] = height;
        RemoteVideoFps[participantId] = fps;
        return true;
    }

    public bool HandleLocalVideoDimensions(int maxEdge, int fps)
    {
        if (_lastLocalVideoMaxEdge == maxEdge && _lastLocalVideoFps == fps)
        {
            return false;
        }

        _lastLocalVideoMaxEdge = maxEdge;
        _lastLocalVideoFps = fps;
        OnLocalVideoDimensionsChanged?.Invoke(maxEdge, fps);
        return true;
    }

    public bool HandleLocalScreenDimensions(int maxEdge, int fps)
    {
        if (_lastLocalScreenMaxEdge == maxEdge && _lastLocalScreenFps == fps)
        {
            return false;
        }

        _lastLocalScreenMaxEdge = maxEdge;
        _lastLocalScreenFps = fps;
        OnLocalScreenDimensionsChanged?.Invoke(maxEdge, fps);
        return true;
    }

    public void RemoveParticipantVideo(string participantId)
    {
        RemoteVideoHeights.TryRemove(participantId, out _);
        RemoteVideoFps.TryRemove(participantId, out _);
    }

    public void Reset()
    {
        RemoteVideoHeights.Clear();
        RemoteVideoFps.Clear();
        _lastLocalVideoMaxEdge = 0;
        _lastLocalVideoFps = 0;
        _lastLocalScreenMaxEdge = 0;
        _lastLocalScreenFps = 0;
        OnIosMicrophoneModeChanged?.Invoke("unsupported");
    }
}
