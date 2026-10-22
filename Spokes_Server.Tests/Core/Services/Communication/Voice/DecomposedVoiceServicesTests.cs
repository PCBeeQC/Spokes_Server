using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class DecomposedVoiceServicesTests
{
    private static GlobalVoiceService CreateDefaultVoiceService() =>
        new(
            Mock.Of<IVoiceJsInteropService>(),
            new Mock<VoiceChannelService>().Object,
            new ChatStateService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalVoiceService>.Instance);

    private static VoiceUiStateService CreateVoiceUiStateService(
        ISnackbar? snackbar = null,
        ISoundService? soundService = null,
        IJSRuntime? js = null,
        GlobalVoiceService? voiceService = null,
        IVoiceJsInteropService? jsInterop = null,
        IVoiceDeviceService? deviceService = null,
        ILogger<VoiceUiStateService>? logger = null)
    {
        return new VoiceUiStateService(
            snackbar ?? Mock.Of<ISnackbar>(),
            soundService ?? Mock.Of<ISoundService>(),
            js ?? Mock.Of<IJSRuntime>(),
            voiceService ?? CreateDefaultVoiceService(),
            jsInterop ?? Mock.Of<IVoiceJsInteropService>(),
            deviceService ?? Mock.Of<IVoiceDeviceService>(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceUiStateService>.Instance);
    }

    [Fact]
    public void VoiceUiStateService_UpdateAudioDevices_UpdatesMicrophonesAndSpeakers()
    {
        var service = CreateVoiceUiStateService();
        var mics = new List<AudioDevice>
        {
            new() { DeviceId = "mic1", Label = "Built-in Mic" },
            new() { DeviceId = "mic2", Label = "USB Mic" }
        };
        var speakers = new List<AudioDevice>
        {
            new() { DeviceId = "spk1", Label = "Headphones" }
        };

        service.UpdateAudioDevices(mics, "mic2", speakers, "spk1");

        Assert.Equal(2, service.AvailableMicrophones.Count);
        Assert.Equal("mic2", service.ActiveMicrophoneDeviceId);
        Assert.Single(service.AvailableSpeakers);
        Assert.Equal("spk1", service.ActiveSpeakerDeviceId);
    }

    [Fact]
    public void VoiceUiStateService_ApplyLocalVideoDimensions_CapsQualityBasedOnResolution()
    {
        var service = CreateVoiceUiStateService();
        service.MaxVideoSendQuality = "2160p";

        service.ApplyLocalVideoDimensions(720, 24);

        Assert.Equal(720, service.MaxCameraHeight);
        Assert.Equal(24, service.MaxCameraFps);
        Assert.Equal("360p", service.MaxVideoSendQuality);
    }

    [Fact]
    public void VoiceUiStateService_ApplyLocalScreenDimensions_CapsScreenQuality()
    {
        var service = CreateVoiceUiStateService();
        service.MaxScreenShareQuality = "2160p";

        service.ApplyLocalScreenDimensions(1080, 30);

        Assert.Equal(1080, service.MaxScreenHeight);
        Assert.Equal(30, service.MaxScreenFps);
        Assert.Equal("720p", service.MaxScreenShareQuality);
    }

    [Fact]
    public void VoiceUiStateService_ResetDeviceState_ClearsCamerasAndRestoresDefaults()
    {
        var service = CreateVoiceUiStateService();
        service.AvailableCameras = [new CameraDevice { DeviceId = "cam1", Label = "HD WebCam" }];
        service.ActiveCameraDeviceId = "cam1";
        service.IosMicrophoneMode = "voiceIsolation";
        service.IsScreenShareAudioEnabled = true;

        service.ResetDeviceState();

        Assert.Empty(service.AvailableCameras);
        Assert.Equal("", service.ActiveCameraDeviceId);
        Assert.Equal("unsupported", service.IosMicrophoneMode);
        Assert.False(service.IsIosVoiceIsolationActive);
        Assert.False(service.IsScreenShareAudioEnabled);
    }

    [Fact]
    public void VoiceUiStateService_ShowVoiceLog_AddsSnackbarNotification()
    {
        var snackbarMock = new Mock<ISnackbar>();
        var service = CreateVoiceUiStateService(snackbar: snackbarMock.Object);

        service.ShowVoiceLog("Connection established", "success");

        snackbarMock.Verify(s => s.Add("Connection established", Severity.Success, null, null), Times.Once);
    }

    [Fact]
    public void VoiceUiStateService_VoiceEvents_DoNotTriggerUiSoundService()
    {
        var soundMock = new Mock<ISoundService>();
        var voiceService = CreateDefaultVoiceService();
        using var service = CreateVoiceUiStateService(soundService: soundMock.Object, voiceService: voiceService);

        voiceService.OnRoomConnected([]);
        voiceService.OnParticipantConnected("user2");
        voiceService.OnParticipantDisconnected("user2");

        soundMock.Verify(s => s.PlaySoundAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        soundMock.Verify(s => s.PlayChannelLeaveAsync(), Times.Never);
    }

    [Fact]
    public async Task VoiceUiStateService_OnRoomLeft_TriggersPlayChannelLeave()
    {
        var soundMock = new Mock<ISoundService>();
        var voiceService = CreateDefaultVoiceService();
        using var service = CreateVoiceUiStateService(soundService: soundMock.Object, voiceService: voiceService);

        voiceService.ApplyMockConnectedState("ch1", "user1", ["user1"]);
        await voiceService.OnRoomDisconnected();

        soundMock.Verify(s => s.PlayChannelLeaveAsync(), Times.Once);
    }

    [Fact]
    public void VoiceUiStateService_Constructor_ThrowsOnNullDependencies()
    {
        var snackbar = Mock.Of<ISnackbar>();
        var sound = Mock.Of<ISoundService>();
        var js = Mock.Of<IJSRuntime>();
        var voice = CreateDefaultVoiceService();
        var interop = Mock.Of<IVoiceJsInteropService>();
        var device = Mock.Of<IVoiceDeviceService>();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceUiStateService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(null!, sound, js, voice, interop, device, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, null!, js, voice, interop, device, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, sound, null!, voice, interop, device, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, sound, js, null!, interop, device, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, sound, js, voice, null!, device, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, sound, js, voice, interop, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new VoiceUiStateService(snackbar, sound, js, voice, interop, device, null!));
    }

    [Fact]
    public async Task VoiceJsInteropService_Disconnect_SafelyHandlesUninitializedModule()
    {
        var service = new VoiceJsInteropService(null);
        Assert.False(service.IsModuleLoaded);

        // Disconnecting before initialization should be a no-op and not throw
        await service.DisconnectFromVoiceAsync();
        await service.DisposeAsync();

        Assert.False(service.IsModuleLoaded);
    }

    [Fact]
    public void VoiceJsInteropService_DerivesFromJsModuleBase()
    {
        var service = new VoiceJsInteropService(null);
        Assert.IsAssignableFrom<JsModuleBase>(service);
    }

    [Fact]
    public async Task VoiceJsInteropService_SuppressesJSDisconnectedException_OnInteropCalls()
    {
        var jsRuntimeMock = new Mock<IJSRuntime>();
        var jsModuleMock = new Mock<IJSObjectReference>();

        jsRuntimeMock
            .Setup(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
            .ReturnsAsync(jsModuleMock.Object);

        jsModuleMock
            .Setup(m => m.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit closed"));

        jsModuleMock
            .Setup(m => m.InvokeAsync<bool>(It.IsAny<string>(), It.IsAny<object[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit closed"));

        var service = new VoiceJsInteropService(jsRuntimeMock.Object);
        await service.InitializeAsync(DotNetObjectReference.Create(this));
        Assert.True(service.IsModuleLoaded);

        // All void and typed interop calls should gracefully suppress disconnects and not throw
        await service.SetMicrophoneEnabledAsync(true);
        await service.DisconnectFromVoiceAsync();
        await service.SwitchActiveCameraAsync("cam1");
        await service.SwitchActiveMicrophoneAsync("mic1");
        await service.SwitchActiveSpeakerAsync("spk1");
        await service.UpdateAudioDevicesAsync();
        await service.SetNoiseSuppressionTypeAsync("krisp");
        await service.PromptIosVoiceIsolationAsync();
        await service.SetMasterVolumeAsync(80);
        await service.SetParticipantVolumeAsync("part1", 75);
        await service.SetVoiceSensitivityAsync(0.5);
        await service.InitVoiceSettingsAsync("{}");
        await service.PlayTestSpeakerSoundAsync("spk1");
        await service.SetScreenShareEnabledAsync(true, "1080p", 30);
        var audioResult = await service.SetScreenShareAudioEnabledAsync(true);
        Assert.False(audioResult);
        await service.ReattachAllTracksAsync();
        await service.RefreshVideoLayoutsAsync();
        await service.SetRemoteVideoQualityAsync("part1", "high");

        var isApple = await service.IsAppleMobileDeviceAsync();
        Assert.False(isApple);

        var isAndroid = await service.IsAndroidDeviceAsync();
        Assert.False(isAndroid);

        var iosMode = await service.GetIosMicrophoneModeAsync();
        Assert.Equal("unsupported", iosMode);

        var cameraResult = await service.SetCameraEnabledAsync(true, "720p", 30, "cam1");
        Assert.Null(cameraResult);

        await service.DisposeAsync();
    }

    [Fact]
    public void VoiceConnectionOptions_DefaultAndCustomConstructors_SetProperties()
    {
        var defaultOptions = new VoiceConnectionOptions();
        Assert.Equal(string.Empty, defaultOptions.Token);
        Assert.Equal(string.Empty, defaultOptions.Url);
        Assert.Equal(string.Empty, defaultOptions.ChannelId);
        Assert.Equal(string.Empty, defaultOptions.UserId);
        Assert.Equal(64, defaultOptions.AudioBitrateKbps);
        Assert.Equal("rnnoise", defaultOptions.NoiseSuppression);
        Assert.Null(defaultOptions.VoiceSettings);

        var customOptions = new VoiceConnectionOptions("tok1", "ws://url", "chan1", "user1", 128, "webrtc");
        Assert.Equal("tok1", customOptions.Token);
        Assert.Equal("ws://url", customOptions.Url);
        Assert.Equal("chan1", customOptions.ChannelId);
        Assert.Equal("user1", customOptions.UserId);
        Assert.Equal(128, customOptions.AudioBitrateKbps);
        Assert.Equal("webrtc", customOptions.NoiseSuppression);
    }

    [Fact]
    public void VoiceDeviceService_HandlesDeviceUpdatesAndDimensions()
    {
        var jsMock = new Mock<IVoiceJsInteropService>();
        var loggerMock = new Mock<ILogger>();
        var deviceService = new VoiceDeviceService(jsMock.Object, loggerMock.Object);

        List<AudioDevice>? receivedMics = null;
        string? activeMic = null;
        deviceService.OnAudioDevicesChanged += (mics, micId, _, _) =>
        {
            receivedMics = mics;
            activeMic = micId;
        };

        var micsList = new List<AudioDevice> { new() { DeviceId = "mic1", Label = "Built-in" } };
        deviceService.HandleAudioDevicesUpdated(micsList, "mic1", [], "");
        Assert.NotNull(receivedMics);
        Assert.Single(receivedMics);
        Assert.Equal("mic1", activeMic);

        deviceService.HandleParticipantVideoDimensions("user1", 720, 30);
        Assert.Equal(720, deviceService.RemoteVideoHeights["user1"]);
        Assert.Equal(30, deviceService.RemoteVideoFps["user1"]);

        deviceService.RemoveParticipantVideo("user1");
        Assert.False(deviceService.RemoteVideoHeights.ContainsKey("user1"));
        Assert.False(deviceService.RemoteVideoFps.ContainsKey("user1"));

        deviceService.HandleParticipantVideoDimensions("user2", 1080, 60);
        deviceService.Reset();
        Assert.Empty(deviceService.RemoteVideoHeights);
        Assert.Empty(deviceService.RemoteVideoFps);
    }


    [Fact]
    public void DemoVoiceAdapter_ConnectAndDisconnect_ManipulatesStateAndChatNotifications()
    {
        var chatState = new ChatStateService();
        var jsMock = new Mock<IVoiceJsInteropService>();
        var voiceChannelMock = new Mock<VoiceChannelService>();

        var voiceService = new GlobalVoiceService(
            jsMock.Object,
            voiceChannelMock.Object,
            chatState,
            Mock.Of<ILogger<GlobalVoiceService>>());

        var demoAdapter = new DemoVoiceAdapter(voiceService, chatState);

        var joinedUsers = new List<string>();
        chatState.VoiceMemberJoined += (cid, uid) => joinedUsers.Add(uid);

        var leftUsers = new List<string>();
        chatState.VoiceMemberLeft += (cid, uid) => leftUsers.Add(uid);

        demoAdapter.ConnectMock("chan1", "user1", ["user1", "user2"]);
        Assert.True(voiceService.IsInVoiceCall);
        Assert.Equal("chan1", voiceService.CurrentChannelId);
        Assert.Equal("user1", voiceService.CurrentUserId);
        Assert.Contains("user1", voiceService.CallParticipants);
        Assert.Contains("user2", voiceService.CallParticipants);
        Assert.Equal(2, joinedUsers.Count);

        demoAdapter.DisconnectMock();
        Assert.False(voiceService.IsInVoiceCall);
        Assert.Null(voiceService.CurrentChannelId);
        Assert.Null(voiceService.CurrentUserId);
        Assert.Empty(voiceService.CallParticipants);
        Assert.Equal(2, leftUsers.Count);
    }
}
