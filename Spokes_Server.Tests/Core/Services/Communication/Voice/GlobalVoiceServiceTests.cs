using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using MudBlazor;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Hubs;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;
using Spokes_Server.Components.UI;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;
    public class GlobalVoiceServiceTests : IAsyncDisposable
    {
        private readonly Mock<IJSRuntime> _jsMock;
        private readonly Mock<IJSObjectReference> _voiceModuleMock;
        private readonly Mock<VoiceChannelService> _voiceChannelServiceMock;
        private readonly VoiceStateService _voiceState;
        private readonly ChatStateService _chatState;
        private readonly Mock<ISnackbar> _snackbarMock;
        private readonly Mock<ILogger<GlobalVoiceService>> _loggerMock;
        private readonly Mock<ISoundService> _soundServiceMock;
        private readonly VoiceDeviceService _deviceService;
        private readonly GlobalVoiceService _service;
        private readonly VoiceUiStateService _uiState;
        
        private readonly DiskPersistenceService _persistence;
        private readonly IConfiguration _config;
        private readonly string _testDataDir;
        
        public GlobalVoiceServiceTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_Voice_{Guid.NewGuid()}");
            Directory.CreateDirectory(_testDataDir);
            var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
            _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
            _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);

            _jsMock = new Mock<IJSRuntime>();
            _voiceModuleMock = new Mock<IJSObjectReference>();
            
            // Set up common JS module calls
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAndroidDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            _voiceModuleMock.Setup(x => x.InvokeAsync<CameraInitResult>("setCameraEnabled", It.IsAny<object[]>()))
                .Returns(new ValueTask<CameraInitResult>(new CameraInitResult { Cameras = [], ActiveCameraDeviceId = "cam1" }));
            
            _jsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
                .Returns(new ValueTask<IJSObjectReference>(_voiceModuleMock.Object));
                
            _voiceState = new VoiceStateService();
            _chatState = new ChatStateService();

            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            var messages = new ChatMessageRepository(_persistence, _config, companyProfiles);

            _voiceChannelServiceMock = new Mock<VoiceChannelService>();
            _voiceChannelServiceMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((channelId, userId, _) => _voiceState.NotifyVoiceMemberJoined(channelId, userId))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, CancellationToken>((channelId, userId, deviceId, deviceName, sessionId, _) => _voiceState.NotifyVoiceMemberJoined(channelId, userId, deviceId, deviceName, sessionId))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((channelId, userId, _) => _voiceState.NotifyVoiceMemberLeft(channelId, userId))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.TryLeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, bool, CancellationToken>((channelId, userId, sessionId, _, _) => _voiceState.TryNotifyVoiceMemberLeft(channelId, userId, sessionId))
                .ReturnsAsync(true);
            _voiceChannelServiceMock.Setup(x => x.SetUserSpeakingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, bool, CancellationToken>((channelId, userId, isSpeaking, _) => _voiceState.SetUserSpeaking(channelId, userId, isSpeaking))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.SetUserMutedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, bool, CancellationToken>((channelId, userId, isMuted, _) => _voiceState.SetUserMuted(channelId, userId, isMuted))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.TrySetUserMutedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, bool, string?, CancellationToken>((channelId, userId, isMuted, sessionId, _) => _voiceState.TrySetUserMuted(channelId, userId, isMuted, sessionId))
                .ReturnsAsync(true);
                
            _snackbarMock = new Mock<ISnackbar>();
            _loggerMock = new Mock<ILogger<GlobalVoiceService>>();
            _soundServiceMock = new Mock<ISoundService>();
            
            var jsInterop = new VoiceJsInteropService(_jsMock.Object);
            _deviceService = new VoiceDeviceService(jsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            _service = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                _deviceService
            );
            _uiState = new VoiceUiStateService(_snackbarMock.Object, _soundServiceMock.Object, _jsMock.Object, _service, jsInterop, _deviceService, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceUiStateService>.Instance);
        }
        
        public async ValueTask DisposeAsync()
        {
            _uiState.Dispose();
            await _service.DisposeAsync();
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
            }
        }

        private Task ConnectVoiceAsync(string token = "token", string url = "url", string channelId = "ch1", string userId = "user1", int audioBitrateKbps = 64, string noiseSuppression = "rnnoise", VoiceUserSettings? voiceSettings = null) =>
            _service.ConnectAsync(new VoiceConnectionOptions(token, url, channelId, userId, audioBitrateKbps, noiseSuppression, voiceSettings));

        [Fact]
        public void ApplyMockConnectedState_SetsStateAndParticipants()
        {
            var channelId = "ch1";
            var userId = "user1";
            var mockParticipants = new HashSet<string> { "user1", "user2" };
            
            _service.ApplyMockConnectedState(channelId, userId, mockParticipants);
            
            Assert.True(_service.IsInVoiceCall);
            Assert.False(_service.IsConnecting);
            Assert.Equal(channelId, _service.CurrentChannelId);
            Assert.Equal(userId, _service.CurrentUserId);
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public void ApplyMockDisconnectedState_ClearsStateAndParticipants()
        {
            var channelId = "ch1";
            var userId = "user1";
            var mockParticipants = new HashSet<string> { "user1", "user2" };
            _service.ApplyMockConnectedState(channelId, userId, mockParticipants);
            
            _service.ApplyMockDisconnectedState();
            
            Assert.False(_service.IsInVoiceCall);
            Assert.Null(_service.CurrentChannelId);
            Assert.Null(_service.CurrentUserId);
            Assert.Empty(_service.CallParticipants);
            Assert.Empty(_service.ActiveSpeakers);
        }

        [Fact]
        public async Task ConnectAsync_SetsStateAndCallsJS_HappyPath()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
            Assert.True(_service.IsInVoiceCall);
            Assert.Equal("ch1", _service.CurrentChannelId);
            Assert.Equal("user1", _service.CurrentUserId);
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Equal("rnnoise", _service.CurrentNoiseSuppression);
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()), Times.Once);
            Assert.Contains("user1", _voiceState.GetVoiceParticipants("ch1"));
        }

        [Fact]
        public async Task ConnectAsync_InitializesModuleOnlyOnce()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await ConnectVoiceAsync("token2", "url2", "ch2", "user2");
            
            _jsMock.Verify(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task ConnectAsync_HandlesIOSNoiseSuppression()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            
            await ConnectVoiceAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
            Assert.Equal("webrtc", _service.CurrentNoiseSuppression);
        }
        
        [Fact]
        public async Task ConnectAsync_IncludesExistingServerParticipants()
        {
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public async Task DisconnectAsync_CallsJSAndChatService_WhenConnected()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _service.DisconnectAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
            Assert.DoesNotContain("user1", _voiceState.GetVoiceParticipants("ch1"));
            Assert.False(_service.IsInVoiceCall);
        }
        
        [Fact]
        public async Task DisconnectAsync_DoesNotThrowOnJSError()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()))
                .Throws(new JSDisconnectedException("disconnected"));
                
            await _service.DisconnectAsync();
            
            Assert.False(_service.IsInVoiceCall);
        }

        [Fact]
        public async Task ToggleMuteAsync_TogglesStateAndCallsJS()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsMuted);
            
            await _service.ToggleMuteAsync();
            
            Assert.True(_service.IsMuted);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setMicrophoneEnabled", It.Is<object[]>(args => (bool)args[0] == false)), Times.Once);
        }

        [Fact]
        public async Task ToggleMuteAsync_WhenSpeaking_ClearsActiveSpeakersAndNotifiesChatService()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnLocalSpeakingChanged(true);
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _voiceState.GetActiveSpeakers("ch1"));

            await _service.ToggleMuteAsync();

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _voiceState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task OnLocalMuted_WhenSpeaking_ClearsActiveSpeakersAndNotifiesChatService()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnLocalSpeakingChanged(true);
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _voiceState.GetActiveSpeakers("ch1"));

            _service.OnLocalMuted(true);

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _voiceState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task ToggleVideoAsync_TogglesStateAndSetsCameras()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsVideoOn);
            
            await _service.ToggleVideoAsync();
            
            Assert.True(_service.IsVideoOn);
            Assert.Equal("cam1", _uiState.ActiveCameraDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<CameraInitResult>("setCameraEnabled", It.Is<object[]>(args => (bool)args[0] == true)), Times.Once);
        }

        [Fact]
        public async Task SwitchCameraAsync_CallsJS_WhenVideoOn()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await _service.ToggleVideoAsync(); // Video is now ON
            
            await _deviceService.SwitchCameraAsync("cam2", _service.IsVideoOn);
            
            Assert.Equal("cam2", _uiState.ActiveCameraDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveCamera", It.Is<object[]>(args => (string)args[0] == "cam2")), Times.Once);
        }
        
        [Fact]
        public async Task SwitchMicrophoneAsync_CallsJS_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _deviceService.SwitchMicrophoneAsync("mic2");
            
            Assert.Equal("mic2", _uiState.ActiveMicrophoneDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveMicrophone", It.Is<object[]>(args => (string)args[0] == "mic2")), Times.Once);
        }

        [Fact]
        public async Task SwitchSpeakerAsync_CallsJS_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _deviceService.SwitchSpeakerAsync("speaker2");
            
            Assert.Equal("speaker2", _uiState.ActiveSpeakerDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveSpeaker", It.Is<object[]>(args => (string)args[0] == "speaker2")), Times.Once);
        }

        [Fact]
        public void OnAudioDevicesUpdated_UpdatesAvailableAndActiveDevices()
        {
            var mics = new List<AudioDevice> { new AudioDevice { DeviceId = "mic1" }, new AudioDevice { DeviceId = "mic2" } };
            var speakers = new List<AudioDevice> { new AudioDevice { DeviceId = "spk1" }, new AudioDevice { DeviceId = "spk2" } };
            
            _service.OnAudioDevicesUpdated(mics, "mic2", speakers, "spk2");
            
            Assert.Equal(2, _uiState.AvailableMicrophones.Count);
            Assert.Equal("mic2", _uiState.ActiveMicrophoneDeviceId);
            Assert.Equal(2, _uiState.AvailableSpeakers.Count);
            Assert.Equal("spk2", _uiState.ActiveSpeakerDeviceId);
        }
        
        [Fact]
        public void OnAudioDevicesUpdated_DefaultsToFirstDeviceIfActiveIdEmpty()
        {
            var mics = new List<AudioDevice> { new AudioDevice { DeviceId = "mic1" } };
            var speakers = new List<AudioDevice> { new AudioDevice { DeviceId = "spk1" } };
            
            _service.OnAudioDevicesUpdated(mics, "", speakers, null);
            
            Assert.Equal("mic1", _uiState.ActiveMicrophoneDeviceId);
            Assert.Equal("spk1", _uiState.ActiveSpeakerDeviceId);
        }

        [Fact]
        public async Task SetNoiseSuppressionAsync_UpdatesTypeAndCallsJS()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
            await _service.SetNoiseSuppressionAsync("webrtc");
            
            Assert.Equal("webrtc", _service.CurrentNoiseSuppression);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setNoiseSuppressionType", It.Is<object[]>(args => (string)args[0] == "webrtc")), Times.Once);
        }

        [Fact]
        public async Task ToggleScreenShareAsync_TogglesStateAndCallsJS()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsScreenSharing);
            
            await _service.ToggleScreenShareAsync();
            
            Assert.True(_service.IsScreenSharing);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setScreenShareEnabled", It.Is<object[]>(args => (bool)args[0] == true)), Times.Once);
        }

        [Fact]
        public async Task ToggleScreenShareAsync_WithCaptureAudio_PassesParameterToJs()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsScreenSharing);

            await _service.ToggleScreenShareAsync("1080p", 60, "detail", true);

            Assert.True(_service.IsScreenSharing);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setScreenShareEnabled", It.Is<object[]>(args => 
                (bool)args[0] == true && 
                (string)args[1] == "1080p" && 
                (int)args[2] == 60 && 
                (string)args[3] == "detail" && 
                (bool)args[4] == true)), Times.Once);
        }

        [Fact]
        public async Task SetScreenShareAudioEnabledAsync_CallsJs_WhenScreenSharing()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await _service.ToggleScreenShareAsync();

            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("setScreenShareAudioEnabled", It.IsAny<object[]>()))
                .ReturnsAsync(true);

            var result = await _service.SetScreenShareAudioEnabledAsync(true);

            Assert.True(result);
            _voiceModuleMock.Verify(x => x.InvokeAsync<bool>("setScreenShareAudioEnabled", It.Is<object[]>(args => (bool)args[0] == true)), Times.Once);
        }

        [Fact]
        public async Task UpdateScreenShareSettingsAsync_CallsJs_WhenScreenSharing()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await _service.ToggleScreenShareAsync();

            await _service.UpdateScreenShareSettingsAsync("1440p", 60, "detail");

            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("updateScreenShareSettings", It.Is<object[]>(args =>
                (string)args[0] == "1440p" &&
                (int)args[1] == 60 &&
                (string)args[2] == "detail")), Times.Once);
        }

        [Fact]
        public async Task UpdateScreenShareSettingsAsync_DoesNotCallJs_WhenNotScreenSharing()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsScreenSharing);

            await _service.UpdateScreenShareSettingsAsync("1440p", 60, "detail");

            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("updateScreenShareSettings", It.IsAny<object[]>()), Times.Never);
        }

        [Fact]
        public async Task ReattachTracksAsync_CallsJS_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _deviceService.ReattachTracksAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("reattachAllTracks", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task RefreshVideoLayoutsAsync_CallsJS_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _deviceService.RefreshVideoLayoutsAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("refreshVideoLayouts", It.IsAny<object[]>()), Times.Once);
        }
        
        [Fact]
        public async Task SetRemoteVideoQualityAsync_CallsJS_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _deviceService.SetRemoteVideoQualityAsync("user2", "high");
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setRemoteVideoQuality", It.Is<object[]>(args => (string)args[0] == "user2" && (string)args[1] == "high")), Times.Once);
        }

        [Fact]
        public void MarkForIntegrityCheck_SetsFlagAndNotifies()
        {
            bool notified = false;
            _service.OnChange += () => notified = true;
            
            _service.MarkForIntegrityCheck();
            
            Assert.True(_service.RequiresIntegrityCheck);
            Assert.True(notified);
        }

        [Fact]
        public async Task OnParticipantConnected_AddsParticipant_IfInServerState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2"); // Present on server
            
            _service.OnParticipantConnected("user2");
            
            Assert.Contains("user2", _service.CallParticipants);
        }
        
        [Fact]
        public async Task OnParticipantConnected_RejectsUnverifiedParticipant_UntilVerifiedInServerState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            // Not yet present on server state — fail-closed against spoofed WebRTC identity
            _service.OnParticipantConnected("user2");
            Assert.DoesNotContain("user2", _service.CallParticipants);

            // Once server confirms user2 joined, WebRTC event accepts user2
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnParticipantConnected("user2");
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public async Task OnParticipantConnected_IgnoresEvent_ForLocalUser()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            _service.OnParticipantConnected("user1");
            
            Assert.Contains("user1", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberJoined_AddsParticipant_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            _voiceState.NotifyVoiceMemberJoined("ch1", "user3");
            
            Assert.Contains("user3", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberJoined_DoesNotAddLocalUser_ToCallParticipants()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.CallParticipants.Remove("user1");
            
            _voiceState.NotifyVoiceMemberJoined("ch1", "user1");
            
            Assert.DoesNotContain("user1", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberLeft_RemovesParticipant_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.CallParticipants.Add("user3");
            
            _voiceState.NotifyVoiceMemberLeft("ch1", "user3");
            
            Assert.DoesNotContain("user3", _service.CallParticipants);
        }

        [Fact]
        public async Task EnsureParticipantSlot_AddsParticipantAndNotifies()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "userX");
            _service.CallParticipants.Remove("userX");
            bool notified = false;
            _service.OnChange += () => notified = true;
            
            _service.EnsureParticipantSlot("userX");
            
            Assert.Contains("userX", _service.CallParticipants);
            Assert.True(notified);
        }

        [Fact]
        public async Task EnsureParticipantSlot_WithUnverifiedIdentity_IgnoresAndDoesNotAdd()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            bool notified = false;
            _service.OnChange += () => notified = true;

            _service.EnsureParticipantSlot("spoofed-user");

            Assert.DoesNotContain("spoofed-user", _service.CallParticipants);
            Assert.False(notified);
        }

        [Fact]
        public void EnsureParticipantSlot_WithBotIdentity_IgnoresAndDoesNotAdd()
        {
            bool notified = false;
            _service.OnChange += () => notified = true;

            _service.EnsureParticipantSlot("bot:spokes-voice");

            Assert.DoesNotContain("bot:spokes-voice", _service.CallParticipants);
            Assert.False(notified);
        }

        [Fact]
        public void OnParticipantConnected_WithBotIdentity_IgnoresAndDoesNotAdd()
        {
            bool notified = false;
            _service.OnChange += () => notified = true;

            _service.OnParticipantConnected("bot:spokes-voice");

            Assert.DoesNotContain("bot:spokes-voice", _service.CallParticipants);
            Assert.False(notified);
        }

        [Fact]
        public async Task ServerActiveSpeakers_WithBotIdentity_FiltersOutBot()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnLocalSpeakingChanged(true);
            _voiceState.SetUserSpeaking("ch1", "bot:spokes-voice", true);

            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("bot:spokes-voice", _service.ActiveSpeakers);
        }

        [Fact]
        public async Task ConnectAsync_ReentrancyGuard_IgnoresDuplicateCallToSameChannel()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public void OnParticipantDisconnected_RemovesFromState()
        {
            _service.CallParticipants.Add("user2");
            _service.ActiveSpeakers.Add("user2");
            _deviceService.RemoteVideoHeights.TryAdd("user2", 720);
            
            _service.OnParticipantDisconnected("user2");
            
            Assert.DoesNotContain("user2", _service.CallParticipants);
            Assert.DoesNotContain("user2", _service.ActiveSpeakers);
            Assert.False(_deviceService.RemoteVideoHeights.ContainsKey("user2"));
        }

        [Fact]
        public async Task OnParticipantDisconnected_IgnoresEvent_ForLocalUser()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            _service.OnParticipantDisconnected("user1");
            
            Assert.Contains("user1", _service.CallParticipants);
        }

        [Fact]
        public async Task OnLocalSpeakingChanged_UpdatesSetAndNotifiesChatService_AlongsideServerRemoteSpeakers()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");

            _service.OnLocalSpeakingChanged(true);
            _voiceState.SetUserSpeaking("ch1", "user2", true);

            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user2", _service.ActiveSpeakers);
            Assert.Contains("user1", _voiceState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task OnRoomDisconnected_CleansUpState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _service.OnRoomDisconnected();
            
            Assert.False(_service.IsInVoiceCall);
            Assert.Empty(_service.CallParticipants);
            Assert.DoesNotContain("user1", _voiceState.GetVoiceParticipants("ch1"));
        }

        [Fact]
        public async Task OnRoomDisconnectedWithReason_DuplicateIdentity_ResetsLocalStateWithoutLeavingServerVoiceChannel()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            bool roomLeftFired = false;
            _service.OnRoomLeft += () => roomLeftFired = true;

            await _service.OnRoomDisconnectedWithReason("DUPLICATE_IDENTITY");

            Assert.True(roomLeftFired);
            Assert.False(_service.IsInVoiceCall);
            Assert.Empty(_service.CallParticipants);
            Assert.Contains("user1", _voiceState.GetVoiceParticipants("ch1"));
            _voiceChannelServiceMock.Verify(
                x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public void OnLocalMuted_SetsMuteState()
        {
            _service.OnLocalMuted(true);
            Assert.True(_service.IsMuted);
            
            _service.OnLocalMuted(false);
            Assert.False(_service.IsMuted);
        }

        [Fact]
        public void OnParticipantVideoDimensions_UpdatesDictionaries()
        {
            _service.CallParticipants.Add("user2");
            
            _service.OnParticipantVideoDimensions("user2", 720, 30);
            
            Assert.Equal(720, _deviceService.RemoteVideoHeights["user2"]);
            Assert.Equal(30, _deviceService.RemoteVideoFps["user2"]);
        }
        
        [Fact]
        public void OnParticipantVideoDimensions_IgnoresUnknownParticipants()
        {
            _service.OnParticipantVideoDimensions("user2", 720, 30);
            
            Assert.False(_deviceService.RemoteVideoHeights.ContainsKey("user2"));
        }

        [Fact]
        public void OnLocalVideoDimensions_UpdatesMaxValuesAndQuality()
        {
            _uiState.MaxVideoSendQuality = "2160p";
            
            _service.OnLocalVideoDimensions(1080, 30);
            
            Assert.Equal(1080, _uiState.MaxCameraHeight);
            Assert.Equal(30, _uiState.MaxCameraFps);
            Assert.Equal("Auto", _uiState.MaxVideoSendQuality);
        }
        
        [Fact]
        public void OnLocalScreenDimensions_UpdatesMaxValuesAndQuality()
        {
            _uiState.MaxScreenShareQuality = "2160p";
            
            _service.OnLocalScreenDimensions(1080, 30);
            
            Assert.Equal(1080, _uiState.MaxScreenHeight);
            Assert.Equal(30, _uiState.MaxScreenFps);
            Assert.Equal("720p", _uiState.MaxScreenShareQuality);
        }
        
        [Fact]
        public void OnVoiceLog_AddsToSnackbar()
        {
            _service.OnVoiceLog("test msg", "success");
            
            _snackbarMock.Verify(x => x.Add("test msg", Severity.Success, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()), Times.Once);
        }
        
        [Fact]
        public async Task DisposeAsync_CleansUpConnections()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _service.DisposeAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
            _voiceModuleMock.Verify(x => x.DisposeAsync(), Times.Once);
            Assert.False(_service.IsInVoiceCall);
        }

        [Fact]
        public async Task VerifyServerStateIntegrityAsync_Disconnects_IfUserNotInServerList()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.MarkForIntegrityCheck();
            
            _voiceState.NotifyVoiceMemberLeft("ch1", "user1");
            await _service.VerifyServerStateIntegrityAsync();
            
            Assert.False(_service.RequiresIntegrityCheck);
            Assert.False(_service.IsInVoiceCall);
        }
        
        [Fact]
        public async Task VerifyServerStateIntegrityAsync_SyncsParticipants_IfUserStillInServerList()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.MarkForIntegrityCheck();
            _voiceState.NotifyVoiceMemberJoined("ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            
            await _service.VerifyServerStateIntegrityAsync();
            
            Assert.False(_service.RequiresIntegrityCheck);
            Assert.True(_service.IsInVoiceCall);
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public async Task OnRoomConnected_AddsParticipantsAndSyncsWithServer()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user3");
            
            _service.OnRoomConnected(["user2"]);
            
            Assert.Contains("user2", _service.CallParticipants);
            Assert.True(_service.IsInVoiceCall);
        }

        [Fact]
        public async Task SetMasterVolumeAsync_UpdatesProperty_AndInvokesJsWhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            await _service.SetMasterVolumeAsync(150.0);

            Assert.Equal(150.0, _uiState.VoiceSettings.MasterVolume);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setMasterVolume", It.Is<object[]>(args => (double)args[0] == 150.0)), Times.Once);
        }

        [Fact]
        public async Task SetParticipantVolumeAsync_UpdatesDictionary_AndInvokesJsWhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            await _service.SetParticipantVolumeAsync("user2", 125.0);

            Assert.True(_uiState.VoiceSettings.ParticipantVolumes.ContainsKey("user2"));
            Assert.Equal(125.0, _uiState.VoiceSettings.ParticipantVolumes["user2"]);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setParticipantVolume", It.Is<object[]>(args => (string)args[0] == "user2" && (double)args[1] == 125.0)), Times.Once);
        }

        [Fact]
        public async Task UpdateVoiceSettingsAsync_UpdatesProperty_AndSyncsToJsWhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            var settings = new Spokes_Server.Core.Models.HR.VoiceUserSettings
            {
                MasterVolume = 80.0
            };

            await _service.UpdateVoiceSettingsAsync(settings);

            Assert.Equal(80.0, _uiState.VoiceSettings.MasterVolume);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("initVoiceSettings", It.IsAny<object[]>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task SetVoiceSensitivityAsync_UpdatesVoiceSettings_AndCallsJsWhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            await _service.SetVoiceSensitivityAsync(65.0);

            Assert.Equal(65.0, _uiState.VoiceSettings.VoiceSensitivity);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setVoiceSensitivity", It.Is<object[]>(args => (double)args[0] == 65.0)), Times.Once);
        }

        [Fact]
        public async Task SetVoiceSensitivityAsync_ClampsValues()
        {
            await _service.SetVoiceSensitivityAsync(-10.0);
            Assert.Equal(0.0, _uiState.VoiceSettings.VoiceSensitivity);

            await _service.SetVoiceSensitivityAsync(150.0);
            Assert.Equal(100.0, _uiState.VoiceSettings.VoiceSensitivity);
        }

        [Fact]
        public async Task SetAutoGainControlAndIncomingNormalization_UpdatesUiStateAndInvokesJs()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            await _service.SetAutoGainControlAsync(false);
            Assert.False(_uiState.VoiceSettings.AutoGainControl);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setAutoGainControl", It.Is<object[]>(args => (bool)args[0] == false)), Times.Once);

            await _service.SetIncomingVoiceNormalizationAsync(false);
            Assert.False(_uiState.VoiceSettings.IncomingVoiceNormalization);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setIncomingVoiceNormalization", It.Is<object[]>(args => (bool)args[0] == false)), Times.Once);
        }

        [Fact]
        public async Task PlayTestSpeakerSoundAsync_PlaysNotificationSoundViaVoiceModule()
        {
            await _uiState.PlayTestSpeakerSoundAsync("speaker1");

            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("playTestSpeakerSound", It.Is<object[]>(args => (string)args[0] == "speaker1")), Times.Once);
        }

        [Fact]
        public async Task PlayTestSpeakerSoundAsync_FallbackToSoundService_WhenVoiceModuleThrows()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("playTestSpeakerSound", It.IsAny<object[]>()))
                .ThrowsAsync(new Exception("Module failure"));

            await _uiState.PlayTestSpeakerSoundAsync();

            _soundServiceMock.Verify(x => x.PlaySoundAsync("spokesnotif1", true), Times.Once);
        }

        [Fact]
        public async Task OnParticipantMuteChanged_TracksMutedParticipants_AndFiresOnChange()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            bool changedFired = false;
            _service.OnChange += () => changedFired = true;

            _service.OnParticipantMuteChanged("user2", true);
            Assert.Contains("user2", _service.MutedParticipants);
            Assert.True(changedFired);

            changedFired = false;
            _service.OnParticipantMuteChanged("user2", false);
            Assert.DoesNotContain("user2", _service.MutedParticipants);
            Assert.True(changedFired);
        }

        [Fact]
        public async Task VoiceUserMutedChanged_FromChatState_SyncsMutedParticipants()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            _voiceState.SetUserMuted("ch1", "user2", true);
            Assert.Contains("user2", _service.MutedParticipants);

            _voiceState.SetUserMuted("ch1", "user2", false);
            Assert.DoesNotContain("user2", _service.MutedParticipants);
        }

        [Fact]
        public void IosMicrophoneMode_DefaultsToUnsupported_AndIsVoiceIsolationActiveIsFalse()
        {
            Assert.Equal("unsupported", _uiState.IosMicrophoneMode);
            Assert.False(_uiState.IsIosVoiceIsolationActive);
        }

        [Fact]
        public async Task GetIosMicrophoneModeAsync_WhenIsIOS_UpdatesState()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            _voiceModuleMock.Setup(x => x.InvokeAsync<string>("getIosMicrophoneMode", It.IsAny<object[]>()))
                .Returns(new ValueTask<string>("voiceIsolation"));

            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            var mode = await _deviceService.GetIosMicrophoneModeAsync(_service.IsIOS);

            Assert.Equal("voiceIsolation", mode);
            Assert.Equal("voiceIsolation", _uiState.IosMicrophoneMode);
            Assert.True(_uiState.IsIosVoiceIsolationActive);
        }

        [Fact]
        public async Task DisconnectAsync_ResetsIosMicrophoneMode()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            _voiceModuleMock.Setup(x => x.InvokeAsync<string>("getIosMicrophoneMode", It.IsAny<object[]>()))
                .Returns(new ValueTask<string>("voiceIsolation"));

            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await _deviceService.GetIosMicrophoneModeAsync(_service.IsIOS);
            Assert.Equal("voiceIsolation", _uiState.IosMicrophoneMode);

            await _service.DisconnectAsync();

            Assert.Equal("unsupported", _uiState.IosMicrophoneMode);
            Assert.False(_uiState.IsIosVoiceIsolationActive);
        }

        [Fact]
        public async Task OnRemoteScreenAudioChanged_TracksParticipantAudioCorrectly()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            Assert.Empty(_service.RemoteScreenAudioParticipants);
            Assert.Empty(_service.MutedRemoteScreenAudioParticipants);

            // Active screen audio
            _service.OnRemoteScreenAudioChanged("user2", true);
            Assert.Contains("user2", _service.RemoteScreenAudioParticipants);

            // Inactive screen audio
            _service.OnRemoteScreenAudioChanged("user2", false);
            Assert.DoesNotContain("user2", _service.RemoteScreenAudioParticipants);
        }

        [Fact]
        public async Task ToggleRemoteScreenAudioMuteAsync_TogglesStateAndInvokesJs()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnRemoteScreenAudioChanged("user2", true);

            // First toggle: mutes
            await _service.ToggleRemoteScreenAudioMuteAsync("user2");
            Assert.Contains("user2", _service.MutedRemoteScreenAudioParticipants);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setRemoteScreenAudioMuted", It.Is<object[]>(args => (string)args[0] == "user2" && (bool)args[1] == true)), Times.Once);

            // Second toggle: unmutes
            await _service.ToggleRemoteScreenAudioMuteAsync("user2");
            Assert.DoesNotContain("user2", _service.MutedRemoteScreenAudioParticipants);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setRemoteScreenAudioMuted", It.Is<object[]>(args => (string)args[0] == "user2" && (bool)args[1] == false)), Times.Once);
        }

        [Fact]
        public async Task OnParticipantDisconnected_RemovesFromRemoteScreenAudioSets()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnRemoteScreenAudioChanged("user2", true);
            _service.MutedRemoteScreenAudioParticipants.Add("user2");
            Assert.Contains("user2", _service.RemoteScreenAudioParticipants);
            Assert.Contains("user2", _service.MutedRemoteScreenAudioParticipants);

            _service.OnParticipantDisconnected("user2");
            Assert.DoesNotContain("user2", _service.RemoteScreenAudioParticipants);
            Assert.DoesNotContain("user2", _service.MutedRemoteScreenAudioParticipants);
        }

        [Fact]
        public async Task OnLocalHardwareMuteChanged_True_SetsHardwareAndEffectiveMuteAndSyncsChannel()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnLocalSpeakingChanged(true);
            Assert.Contains("user1", _service.ActiveSpeakers);

            _service.OnLocalHardwareMuteChanged(true);

            Assert.True(_service.IsHardwareMuted);
            Assert.False(_service.IsSoftwareMuted);
            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _service.MutedParticipants);
            Assert.Contains("user1", _voiceState.GetMutedUsers("ch1"));
        }

        [Fact]
        public async Task OnLocalHardwareMuteChanged_False_ClearsMuteWhenNotSoftwareMuted_PreservesMuteWhenSoftwareMuted()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            // Case 1: Only hardware-muted -> lowering headset boom unmutes cleanly
            _service.OnLocalHardwareMuteChanged(true);
            Assert.True(_service.IsMuted);

            _service.OnLocalHardwareMuteChanged(false);
            Assert.False(_service.IsHardwareMuted);
            Assert.False(_service.IsSoftwareMuted);
            Assert.False(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.MutedParticipants);
            Assert.DoesNotContain("user1", _voiceState.GetMutedUsers("ch1"));

            // Case 2: Both software-muted and hardware-muted -> lowering boom preserves software mute
            await _service.ToggleMuteAsync();
            _service.OnLocalHardwareMuteChanged(true);
            Assert.True(_service.IsSoftwareMuted);
            Assert.True(_service.IsHardwareMuted);
            Assert.True(_service.IsMuted);

            _service.OnLocalHardwareMuteChanged(false);
            Assert.False(_service.IsHardwareMuted);
            Assert.True(_service.IsSoftwareMuted);
            Assert.True(_service.IsMuted);
            Assert.Contains("user1", _service.MutedParticipants);
            Assert.Contains("user1", _voiceState.GetMutedUsers("ch1"));
        }

        [Fact]
        public async Task ToggleMuteAsync_WhileHardwareMuted_FiresOnHardwareMuteBlockedUnmuteAndShowsWarningToast()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnLocalHardwareMuteChanged(true);
            Assert.True(_service.IsHardwareMuted);
            Assert.True(_service.IsMuted);

            int blockedEventCount = 0;
            _service.OnHardwareMuteBlockedUnmute += () => blockedEventCount++;

            // Clicking Unmute while hardware boom is flipped up keeps IsMuted == true and warns user
            await _service.ToggleMuteAsync();

            Assert.True(_service.IsHardwareMuted);
            Assert.False(_service.IsSoftwareMuted);
            Assert.True(_service.IsMuted);
            Assert.Equal(1, blockedEventCount);
            _snackbarMock.Verify(
                x => x.Add("Microphone is muted by hardware switch", Severity.Warning, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
                Times.Once);
        }

        [Fact]
        public async Task RequestExternalDisconnect_DisconnectsActiveCallCleanly()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.True(_service.IsInVoiceCall);

            await _service.RequestExternalDisconnect();

            Assert.False(_service.IsInVoiceCall);
            Assert.DoesNotContain("user1", _voiceState.GetVoiceParticipants("ch1"));
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task SetNoiseSuppressionAsync_WhileConnectedAndUnmuted_CallsReinitializeMicrophoneWithoutMuteFlicker()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1", 64, "webrtc");
            _voiceModuleMock.Invocations.Clear();

            await _service.SetNoiseSuppressionAsync("deepfilternet");

            Assert.Equal("deepfilternet", _service.CurrentNoiseSuppression);
            Assert.False(_service.IsMuted);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setNoiseSuppressionType", It.Is<object[]>(args => (string)args[0] == "deepfilternet")), Times.Once);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("reinitializeMicrophone", It.IsAny<object[]>()), Times.Once);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setMicrophoneEnabled", It.IsAny<object[]>()), Times.Never);
        }

        [Fact]
        public async Task SyntheticParticipantFiltering_ExcludesBotIdentities()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");

            _service.OnRoomConnected(new[] { "user2", "bot:room1" });
            Assert.Contains("user2", _service.CallParticipants);
            Assert.DoesNotContain("bot:room1", _service.CallParticipants);

            _service.OnParticipantConnected("bot:room2");
            Assert.DoesNotContain("bot:room2", _service.CallParticipants);

            _voiceState.SetUserSpeaking("ch1", "user2", true);
            _voiceState.SetUserSpeaking("ch1", "bot:room1", true);
            Assert.Contains("user2", _service.ActiveSpeakers);
            Assert.DoesNotContain("bot:room1", _service.ActiveSpeakers);
        }

        [Fact]
        public async Task ConnectAsync_WhenJsReturnsFalse_ResetsStateAndDisconnectsJsRoom()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(false));

            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            Assert.False(_service.IsInVoiceCall);
            Assert.False(_service.IsConnecting);
            Assert.Null(_service.CurrentChannelId);
            Assert.Null(_service.CurrentUserId);
            Assert.Empty(_service.CallParticipants);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task UpdateVideoSettingsAsync_WhenVideoOn_CallsUpdateCameraSettingsInPlace()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            await _service.ToggleVideoAsync("Auto", 30, "cam1");
            Assert.True(_service.IsVideoOn);

            await _service.UpdateVideoSettingsAsync("1080p", 60, "cam1");

            Assert.True(_service.IsVideoOn);
            _voiceModuleMock.Verify(
                x => x.InvokeAsync<IJSVoidResult>(
                    "updateCameraSettings",
                    It.Is<object[]>(args => (string)args[0] == "1080p" && (int)args[1] == 60 && (string)args[2] == "cam1")),
                Times.Once);
        }

        [Fact]
        public async Task ConnectAsync_PassesVoicePreJoinContext_ToConnectToVoice()
        {
            VoicePreJoinContext? capturedContext = null;
            _voiceModuleMock
                .Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()))
                .Callback<string, object[]>((_, args) =>
                {
                    if (args.Length >= 6)
                    {
                        capturedContext = args[5] as VoicePreJoinContext;
                    }
                })
                .Returns(new ValueTask<bool>(true));

            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            Assert.NotNull(capturedContext);
            _voiceModuleMock.Verify(
                x => x.InvokeAsync<IJSVoidResult>("hydratePreJoinVoiceState", It.IsAny<object[]>()),
                Times.Never);
        }

        [Fact]
        public async Task OnServerVoiceMemberLeft_ImmediatelyRemovesParticipant_AndNeverCrossReconcilesServerState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnParticipantConnected("user2");
            Assert.Contains("user2", _service.CallParticipants);

            // When server SignalR VoiceMemberLeft fires, local UI state must immediately remove user2
            // without polling JS or re-adding user2 back to server ChatStateService.
            _voiceState.NotifyVoiceMemberLeft("ch1", "user2");
            await Task.Delay(10);

            Assert.DoesNotContain("user2", _service.CallParticipants);
            _voiceChannelServiceMock.Verify(
                x => x.ReconcileMemberStateAsync("ch1", "user2", It.IsAny<VoiceParticipantMetadata?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task SyncRoomSnapshotAsync_IgnoresTombstonedVoiceSession_AndNeverCrossReconcilesRemoteParticipants()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            // user2 joins with sess-closed and then leaves (tombstoning sess-closed)
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2", "dev-2", "PC", "sess-closed");
            _voiceState.TryNotifyVoiceMemberLeft("ch1", "user2", "sess-closed");
            Assert.True(_voiceState.IsVoiceSessionClosed("sess-closed"));

            // user3 has pre-join server session context (connecting peer not yet in GetVoiceParticipants)
            _voiceState.SetVoiceParticipantSessionContext("ch1", "user3", "dev-3", "PC3", "webrtc", 25.0, "desktop", false, "sess-open");

            var closedMeta = new VoiceParticipantMetadata
            {
                VoiceSessionId = "sess-closed",
                DeviceId = "dev-2",
                DeviceName = "PC"
            }.ToJson();

            var snapshot = new VoiceRoomSnapshotDto
            {
                ChannelId = "ch1",
                LocalUserId = "user1",
                LocalIsMuted = false,
                Participants =
                [
                    new VoiceParticipantSnapshotDto { UserId = "user2", IsMuted = false, Metadata = closedMeta },
                    new VoiceParticipantSnapshotDto { UserId = "user3", IsMuted = false },
                    new VoiceParticipantSnapshotDto { UserId = "spoofed-unverified", IsMuted = false }
                ]
            };

            await _service.SyncRoomSnapshotAsync(snapshot);

            Assert.DoesNotContain("user2", _service.CallParticipants);
            Assert.Contains("user3", _service.CallParticipants);
            Assert.DoesNotContain("spoofed-unverified", _service.CallParticipants);
            _voiceChannelServiceMock.Verify(
                x => x.ReconcileMemberStateAsync("ch1", "user2", It.IsAny<VoiceParticipantMetadata?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
                Times.Never);
            _voiceChannelServiceMock.Verify(
                x => x.ReconcileMemberStateAsync("ch1", "user3", It.IsAny<VoiceParticipantMetadata?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task VerifyServerStateIntegrityAsync_WhenServerDroppedSelfButLiveKitConnected_SelfHealsServerSession()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.True(_service.IsInVoiceCall);

            // Simulate C# ChatState losing user1 while LiveKit WebRTC room is still healthy
            _voiceState.NotifyVoiceMemberLeft("ch1", "user1");
            _voiceModuleMock
                .Setup(x => x.InvokeAsync<bool>("isRoomConnected", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(true));

            _service.MarkForIntegrityCheck();
            await _service.VerifyServerStateIntegrityAsync();

            Assert.True(_service.IsInVoiceCall);
            _voiceChannelServiceMock.Verify(
                x => x.ReconcileMemberStateAsync("ch1", "user1", It.Is<VoiceParticipantMetadata?>(m => m != null && !string.IsNullOrEmpty(m.VoiceSessionId)), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task VerifyServerStateIntegrityAsync_WhenJsInteropThrowsTaskCanceledDuringCircuitRestore_DefersCheckAndDoesNotDisconnect()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.True(_service.IsInVoiceCall);

            // Simulate transient server participant loss during a Simultaneous Blazor + LiveKit reconnect
            _voiceState.NotifyVoiceMemberLeft("ch1", "user1");
            _voiceModuleMock
                .Setup(x => x.InvokeAsync<bool>("isRoomConnected", It.IsAny<object[]>()))
                .ThrowsAsync(new TaskCanceledException("Circuit restoring"));

            _service.MarkForIntegrityCheck();
            await _service.VerifyServerStateIntegrityAsync();

            // Must NOT disconnect the caller; must keep RequiresIntegrityCheck = true for the next render
            Assert.True(_service.IsInVoiceCall);
            Assert.True(_service.RequiresIntegrityCheck);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Never);
        }

        [Fact]
        public async Task SyncRoomSnapshotAsync_And_OnRoomReconnectedAsync_ReconcileParticipantsAndClearStuckRemoteMute()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user3");

            // Inject stale/stuck mute + missing participant into C# state
            _service.OnParticipantConnected("user2");
            _service.OnParticipantMuteChanged("user2", true);
            Assert.True(_service.IsParticipantMuted("user2"));

            var snapshot = new VoiceRoomSnapshotDto
            {
                ChannelId = "ch1",
                LocalUserId = "user1",
                LocalIsMuted = false,
                Participants =
                [
                    new VoiceParticipantSnapshotDto { UserId = "user2", IsMuted = false },
                    new VoiceParticipantSnapshotDto { UserId = "user3", IsMuted = true }
                ]
            };

            await _service.SyncRoomSnapshotAsync(snapshot);

            Assert.Contains("user2", _service.CallParticipants);
            Assert.Contains("user3", _service.CallParticipants);
            Assert.False(_service.IsParticipantMuted("user2"));
            Assert.True(_service.IsParticipantMuted("user3"));

            // Also verify OnRoomReconnectedAsync updates mute & triggers state sync cleanly
            snapshot.Participants[0].IsMuted = true;
            await _service.OnRoomReconnectedAsync(snapshot);
            Assert.True(_service.IsParticipantMuted("user2"));
        }

        [Fact]
        public async Task OnRoomReconnectedAsync_WhenSessionWasTombstonedByTransientParticipantLeft_ReopensSessionAndSelfHeals()
        {
            await _service.ConnectAsync(new VoiceConnectionOptions("token", "url", "ch-reopen", "user1", 64, "rnnoise")
            {
                VoiceSessionId = "sess-livekit-reconnect",
                DeviceId = "dev-1",
                DeviceName = "PC"
            });

            // Simulate a transient participant_left webhook during LiveKit full reconnect tombstoning the active session ID
            _voiceState.TryNotifyVoiceMemberLeft("ch-reopen", "user1", "sess-livekit-reconnect");
            Assert.True(_voiceState.IsVoiceSessionClosed("sess-livekit-reconnect"));
            Assert.DoesNotContain("user1", _voiceState.GetVoiceParticipants("ch-reopen"));

            _voiceChannelServiceMock
                .Setup(x => x.ReconcileMemberStateAsync("ch-reopen", "user1", It.IsAny<VoiceParticipantMetadata?>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, VoiceParticipantMetadata?, bool?, CancellationToken>((cid, uid, meta, _, _) =>
                {
                    if (!_voiceState.IsVoiceSessionClosed(meta?.VoiceSessionId))
                    {
                        _voiceState.NotifyVoiceMemberJoined(cid, uid, meta?.DeviceId, meta?.DeviceName, meta?.VoiceSessionId);
                    }
                })
                .Returns(Task.CompletedTask);

            await _service.OnRoomReconnectedAsync(new VoiceRoomSnapshotDto
            {
                ChannelId = "ch-reopen",
                LocalUserId = "user1",
                LocalIsMuted = false,
                Participants = []
            });

            Assert.False(_voiceState.IsVoiceSessionClosed("sess-livekit-reconnect"));
            Assert.Contains("user1", _voiceState.GetVoiceParticipants("ch-reopen"));
            Assert.Equal("sess-livekit-reconnect", _voiceState.GetVoiceParticipantSessionId("ch-reopen", "user1"));
        }

        [Fact]
        public async Task OnDirectRoomAudioStatusChanged_WhenActivePeersUnchanged_DoesNotFireOnChangeRenderStorm()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            int onChangeCount = 0;
            int onDiagnosticsCount = 0;
            _service.OnChange += () => onChangeCount++;
            _service.OnDiagnosticsOrSettingsChanged += () => onDiagnosticsCount++;

            var t1 = new DirectRoomClientTelemetry
            {
                RoomId = "room-1",
                EstimatedEndToEndMs = 8.4,
                ActiveDirectPeerUserIds = ["user2"]
            };
            _service.OnDirectRoomAudioStatusChanged(t1);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onDiagnosticsCount);

            // Subsequent 750ms telemetry ticks with fluctuating metrics but identical ActiveDirectPeerUserIds must not trigger UI re-renders
            var t2 = new DirectRoomClientTelemetry
            {
                RoomId = "room-1",
                EstimatedEndToEndMs = 9.1,
                MaxPeerRttMs = 2.3,
                TxPacketCount = 500,
                ActiveDirectPeerUserIds = ["user2"]
            };
            _service.OnDirectRoomAudioStatusChanged(t2);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onDiagnosticsCount);

            // Changing active peers triggers OnDiagnosticsOrSettingsChanged immediately without waking broad OnChange listeners
            var t3 = new DirectRoomClientTelemetry
            {
                RoomId = "room-1",
                EstimatedEndToEndMs = 9.1,
                ActiveDirectPeerUserIds = ["user2", "user3"]
            };
            _service.OnDirectRoomAudioStatusChanged(t3);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(2, onDiagnosticsCount);
        }

        [Fact]
        public async Task Wave3_GranularVoiceEvents_AvoidRedundantOnChangeStorms()
        {
            // Match Chat.razor prewarmed join path (module already loaded & deviceId already resolved)
            await _service.EnsureModuleInitializedAsync();

            int connectOnChangeCount = 0;
            _service.OnChange += () => connectOnChangeCount++;
            await _service.ConnectAsync(new VoiceConnectionOptions("token", "url", "ch-w3", "user1", 64, "rnnoise")
            {
                DeviceId = "dev-1",
                DeviceName = "Desktop"
            });

            // ConnectAsync should coalesce into 2 OnChange calls (ConnectingToServer + Connected), skipping PreparingAudio & ConfiguringRoomAudio storms
            Assert.Equal(2, connectOnChangeCount);

            int onChangeCount = 0;
            int onDiagnosticsCount = 0;
            int onDevicesCount = 0;
            _service.OnChange += () => onChangeCount++;
            _service.OnDiagnosticsOrSettingsChanged += () => onDiagnosticsCount++;
            _deviceService.OnAudioDevicesChanged += (_, _, _, _) => onDevicesCount++;

            // Device enumeration only notifies IVoiceDeviceService.OnAudioDevicesChanged, never broad OnChange
            _service.OnAudioDevicesUpdated(
                [new AudioDevice { DeviceId = "mic-1", Label = "USB Mic" }],
                "mic-1",
                [new AudioDevice { DeviceId = "spk-1", Label = "Speakers" }],
                "spk-1");
            Assert.Equal(1, onDevicesCount);
            Assert.Equal(0, onChangeCount);

            // No-op local unmute callback when already unmuted must NOT fire OnChange
            _service.OnLocalMuted(false);
            Assert.Equal(0, onChangeCount);

            // Real mute transition fires OnChange once; duplicate muted callback is a no-op
            _service.OnLocalMuted(true);
            Assert.Equal(1, onChangeCount);
            _service.OnLocalMuted(true);
            Assert.Equal(1, onChangeCount);

            // Settings/DSP mutations notify OnDiagnosticsOrSettingsChanged instead of broad OnChange
            await _service.SetNoiseSuppressionAsync("none");
            await _service.SetMasterVolumeAsync(0.75);
            await _service.SetVoiceSensitivityAsync(42);
            Assert.Equal(1, onChangeCount);
            Assert.True(onDiagnosticsCount >= 3);
        }

        [Fact]
        public async Task OnRoomDisconnectedWithReason_DuplicateIdentity_DoesNotRemoveDirectLanEndpoints()
        {
            var rooms = new PhysicalVoiceRoomRepository(_persistence, _config);
            var employees = new Spokes_Server.Core.Data.Repositories.HR.EmployeeRepository(_persistence, _config);
            employees.Save(new Employee { Id = "user1", FirstName = "User", LastName = "One", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Chat.Use] });
            employees.Save(new Employee { Id = "user2", FirstName = "User", LastName = "Two", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Chat.Use] });

            var voiceBot = new Mock<IVoiceBotService>();
            var privacy = new Mock<Spokes_Server.Core.Services.Security.IUserPrivacyService>();
            privacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

            var roomService = new PhysicalVoiceRoomService(
                rooms,
                _voiceState,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalVoiceRoomService>.Instance,
                employees,
                voiceBot.Object,
                userPrivacy: privacy.Object);
            var directService = new PhysicalRoomDirectSessionService(
                roomService,
                _voiceState,
                employees,
                privacy.Object);

            var room = (await roomService.CreateRoomAsync(
                "Dup Studio",
                "user1",
                "dev-1",
                "User1 PC",
                audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
            await roomService.EnrollUserDeviceAsync(room.Id, "user2", "dev-2", "User2 PC", actorUserId: "user2");

            var jsInterop = new VoiceJsInteropService(_jsMock.Object);
            await using var service = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                _deviceService,
                roomService,
                directService);

            await service.ConnectAsync(new VoiceConnectionOptions("tok", "ws://lk", "ch-dup", "user1", 64, "rnnoise")
            {
                DeviceId = "dev-1",
                DeviceName = "User1 PC",
                VoiceSessionId = "sess-old"
            });

            _voiceState.SetVoiceParticipantSessionContext("ch-dup", "user1", "dev-1", "User1 PC", "rnnoise", 25.0, "desktop", false);
            _voiceState.NotifyVoiceMemberJoined("ch-dup", "user1", "dev-1", "User1 PC", "sess-new");
            _voiceState.SetVoiceParticipantSessionContext("ch-dup", "user2", "dev-2", "User2 PC", "rnnoise", 25.0, "desktop", false);
            _voiceState.NotifyVoiceMemberJoined("ch-dup", "user2", "dev-2", "User2 PC", "sess-u2");

            // Simulate user1's newer session overwriting direct endpoints
            directService.UpdateParticipantEndpoints("ch-dup", "user1", "dev-1", ["192.168.1.50:53000"], voiceSessionId: "sess-new");
            directService.UpdateParticipantEndpoints("ch-dup", "user2", "dev-2", ["192.168.1.51:53000"], voiceSessionId: "sess-u2");

            // Old tab receives DUPLICATE_IDENTITY disconnect -> must NOT wipe active LAN endpoints
            await service.OnRoomDisconnectedWithReason("DUPLICATE_IDENTITY");

            Assert.False(service.IsInVoiceCall);
            var descForUser2 = directService.BuildSessionDescriptorForParticipant("ch-dup", "user2", "dev-2");
            Assert.NotNull(descForUser2);
            Assert.Contains(descForUser2!.Peers, p => p.UserId == "user1" && p.Endpoints.Contains("192.168.1.50:53000"));
        }

        [Fact]
        public async Task MessageEdited_WhenCallInviteDeclinedWhileAlone_RaisesOnOutgoingCallDeclined()
        {
            await ConnectVoiceAsync("token", "url", "dm-ch-1", "user1");

            string? declinedChannelId = null;
            _service.OnOutgoingCallDeclined += cid => declinedChannelId = cid;

            _chatState.NotifyMessageEdited(new ChatMessage
            {
                Id = "inv-1",
                ChannelId = "dm-ch-1",
                ChannelType = ChatChannelType.Direct,
                SenderId = "user1",
                MessageType = ChatMessageType.CallInvite,
                CallStatus = CallStatus.Declined
            });

            Assert.Equal("dm-ch-1", declinedChannelId);
        }

        [Fact]
        public async Task MessageEdited_WhenCallInviteDeclinedWithRemoteParticipantInCall_DoesNotRaiseOnOutgoingCallDeclined()
        {
            await ConnectVoiceAsync("token", "url", "dm-ch-2", "user1");
            _voiceState.NotifyVoiceMemberJoined("dm-ch-2", "user2");

            string? declinedChannelId = null;
            _service.OnOutgoingCallDeclined += cid => declinedChannelId = cid;

            _chatState.NotifyMessageEdited(new ChatMessage
            {
                Id = "inv-2",
                ChannelId = "dm-ch-2",
                ChannelType = ChatChannelType.Direct,
                SenderId = "user1",
                MessageType = ChatMessageType.CallInvite,
                CallStatus = CallStatus.Declined
            });

            Assert.Null(declinedChannelId);
        }

        [Fact]
        public void ChatComposer_BuildMessageContentWithGifs_AppendsMultipleGifsCleanly()
        {
            var gifs = new List<string>
            {
                "https://media.tenor.com/a.gif#ar=1.33",
                "https://media.giphy.com/b.gif#ar=1.0"
            };

            var onlyGifs = Spokes_Server.Components.Pages.Chat.ChatComposer.BuildMessageContentWithGifs("", gifs);
            Assert.Equal("![GIF](https://media.tenor.com/a.gif#ar=1.33)\n![GIF](https://media.giphy.com/b.gif#ar=1.0)", onlyGifs);

            var withText = Spokes_Server.Components.Pages.Chat.ChatComposer.BuildMessageContentWithGifs("Check this out!  ", gifs);
            Assert.Equal("Check this out!\n![GIF](https://media.tenor.com/a.gif#ar=1.33)\n![GIF](https://media.giphy.com/b.gif#ar=1.0)", withText);

            Assert.Equal("https://media.tenor.com/a.gif", Spokes_Server.Components.Pages.Chat.ChatComposer.GetCleanGifPreviewUrl("https://media.tenor.com/a.gif#ar=1.33"));
        }

        [Fact]
        public async Task ConnectAsync_WhenJsInteropReturnsFalseAfterOnRoomConnectedFired_RecoversConnectionAndJoinsVoice()
        {
            _voiceModuleMock
                .Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()))
                .Callback(() => _service.OnRoomConnected(["user2"]))
                .Returns(new ValueTask<bool>(false));

            await _service.ConnectAsync(new VoiceConnectionOptions("token", "url", "ch-ios", "user-ios", 64, "webrtc")
            {
                VoiceSessionId = "sess-ios-recovered"
            });

            Assert.True(_service.IsInVoiceCall);
            Assert.False(_voiceState.IsVoiceSessionClosed("sess-ios-recovered"));
            Assert.Contains("user-ios", _voiceState.GetVoiceParticipants("ch-ios"));
        }

        [Fact]
        public async Task ConnectAsync_WhenJsInteropFailsCompletely_CleansUpPreJoinContextWithoutTombstoningSessionId()
        {
            _voiceModuleMock
                .Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(false));

            await _service.ConnectAsync(new VoiceConnectionOptions("token", "url", "ch-fail", "user-ios", 64, "webrtc")
            {
                VoiceSessionId = "sess-ios-not-tombstoned"
            });

            Assert.False(_service.IsInVoiceCall);
            Assert.DoesNotContain("user-ios", _voiceState.GetVoiceParticipants("ch-fail"));
            Assert.False(_voiceState.IsVoiceSessionClosed("sess-ios-not-tombstoned"));
        }

        [Fact]
        public async Task ActiveSpeakers_FiresNarrowEventOnlyOnDiff_NeverGlobalOnChange()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnRoomConnected(["user2"]);

            int onChangeCount = 0;
            int onSpeakersCount = 0;
            _service.OnChange += () => onChangeCount++;
            _service.OnActiveSpeakersUpdated += () => onSpeakersCount++;

            _voiceState.SetUserSpeaking("ch1", "user2", true);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onSpeakersCount);

            // Duplicate identical speaker state must not trigger another UI event
            _voiceState.SetUserSpeaking("ch1", "user2", true);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onSpeakersCount);

            // Clearing active speakers fires narrow event once
            _voiceState.SetUserSpeaking("ch1", "user2", false);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(2, onSpeakersCount);
        }

        [Fact]
        public async Task OnParticipantAndLocalVideoDimensions_FiresNarrowEventOnlyOnDiff_NeverGlobalOnChange()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _voiceState.NotifyVoiceMemberJoined("ch1", "user2");
            _service.OnRoomConnected(["user2"]);

            int onChangeCount = 0;
            int onDimsCount = 0;
            _service.OnChange += () => onChangeCount++;
            _service.OnVideoDimensionsChanged += () => onDimsCount++;

            _service.OnParticipantVideoDimensions("user2", 720, 30);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onDimsCount);

            // Identical repeated report is a no-op
            _service.OnParticipantVideoDimensions("user2", 720, 30);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onDimsCount);

            // Actual change fires narrow event once
            _service.OnParticipantVideoDimensions("user2", 1080, 60);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(2, onDimsCount);

            _service.OnLocalVideoDimensions(1920, 30);
            _service.OnLocalVideoDimensions(1920, 30);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(3, onDimsCount);
        }

        [Theory]
        [InlineData(false)] // Web / Desktop
        [InlineData(true)]  // Native iOS / Android
        public async Task SingleSourceOfTruth_AllPlatformsMirrorCanonicalServerSpeakers_AndLocalMuteFiltersPerViewer(bool isNativeMobile)
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isNativePlatformApp", It.IsAny<object[]>())).Returns(new ValueTask<bool>(isNativeMobile));
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(isNativeMobile));

            await ConnectVoiceAsync("token", "url", "ch1", "mats3");
            _voiceState.NotifyVoiceMemberJoined("ch1", "mats2");
            _voiceState.NotifyVoiceMemberJoined("ch1", "admin-first");
            _service.OnRoomConnected(["mats2", "admin-first"]);
            Assert.Equal(isNativeMobile, _service.IsNativePlatform);

            int onChangeCount = 0;
            int onSpeakersCount = 0;
            _service.OnChange += () => onChangeCount++;
            _service.OnActiveSpeakersUpdated += () => onSpeakersCount++;

            // 1. mats2 is managed by Physical Room DSP -> raw mic bleed does NOT flash mats2 on any platform
            _voiceState.SetPhysicalRoomDspManaged("ch1", "mats2", true);
            _voiceState.SetUserSpeaking("ch1", "mats2", true);
            Assert.DoesNotContain("mats2", _service.ActiveSpeakers);
            Assert.Equal(0, onSpeakersCount);

            // 2. Post-isolation DSP confirms mats2 is genuinely speaking -> lights up identically on Web & Mobile
            _voiceState.SetPhysicalRoomDspSpeaking("ch1", "mats2", true);
            Assert.Contains("mats2", _service.ActiveSpeakers);
            Assert.Equal(0, onChangeCount);
            Assert.Equal(1, onSpeakersCount);

            // 3. Local user (mats3) reports own mic speaking -> merges cleanly without clobbering mats2
            _service.OnLocalSpeakingChanged(true);
            Assert.Contains("mats2", _service.ActiveSpeakers);
            Assert.Contains("mats3", _service.ActiveSpeakers);
            Assert.Equal(2, onSpeakersCount);

            // 4. Local user stops speaking -> mats2 remains active
            _service.OnLocalSpeakingChanged(false);
            Assert.Contains("mats2", _service.ActiveSpeakers);
            Assert.DoesNotContain("mats3", _service.ActiveSpeakers);
            Assert.Equal(3, onSpeakersCount);

            // 5. Local "Mute for me" (participant volume 0%) hides mats2 ONLY for this viewer while server keeps mats2 speaking
            await _service.SetParticipantVolumeAsync("mats2", 0.0);
            Assert.DoesNotContain("mats2", _service.ActiveSpeakers);
            Assert.Contains("mats2", _voiceState.GetActiveSpeakers("ch1"));

            // Turning mats2's volume back up immediately restores mats2's speaking halo
            await _service.SetParticipantVolumeAsync("mats2", 100.0);
            Assert.Contains("mats2", _service.ActiveSpeakers);

            // 6. Server mute on mats2 clears speaking immediately
            _voiceState.SetUserMuted("ch1", "mats2", true);
            Assert.DoesNotContain("mats2", _service.ActiveSpeakers);
        }

        [Fact]
        public async Task PhysicalRoomDspManagedSelf_FollowsPostDspAuthorityEvenForOwnTile_SuppressingDeskBleed()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "admin-first");
            _voiceState.NotifyVoiceMemberJoined("ch1", "mats2");
            _service.OnRoomConnected(["mats2"]);

            // Claim Physical Room DSP authority for admin-first (active 2+ Room Alpha room)
            _voiceState.SetPhysicalRoomDspManaged("ch1", "admin-first", true);

            // Roommate mats2 talks loudly and bleeds into admin-first's desk mic -> local VAD reports true
            _service.OnLocalSpeakingChanged(true);

            // Because admin-first is DSP-managed, raw desk bleed MUST NOT flash admin-first's own tile
            Assert.DoesNotContain("admin-first", _service.ActiveSpeakers);
            Assert.DoesNotContain("admin-first", _voiceState.GetActiveSpeakers("ch1"));

            // When admin-first genuinely speaks and passes server post-isolation DSP -> own tile lights up
            _voiceState.SetPhysicalRoomDspSpeaking("ch1", "admin-first", true);
            Assert.Contains("admin-first", _service.ActiveSpeakers);

            // DSP silence clears own tile cleanly
            _voiceState.SetPhysicalRoomDspSpeaking("ch1", "admin-first", false);
            Assert.DoesNotContain("admin-first", _service.ActiveSpeakers);

            // When mats2 leaves the physical room and DSP authority releases, local mic VAD seamlessly owns self again
            _voiceState.SetPhysicalRoomDspManaged("ch1", "admin-first", false);
            Assert.Contains("admin-first", _service.ActiveSpeakers);
        }

        [Fact]
        public async Task RefreshDirectRoomTelemetryAsync_PullsFreshMetricsOnDemand_AndSkipsWhenInactiveOrNonDesktop()
        {
            var freshTelemetry = new DirectRoomClientTelemetry
            {
                RoomId = "room-1",
                EstimatedEndToEndMs = 9.7,
                MaxPeerRttMs = 0.9,
                TxPacketCount = 1234,
                ActiveDirectPeerUserIds = ["user2"]
            };

            _voiceModuleMock.Setup(x => x.InvokeAsync<DirectRoomClientTelemetry>("getDirectRoomAudioTelemetry", It.IsAny<object[]>()))
                .Returns(new ValueTask<DirectRoomClientTelemetry>(freshTelemetry));

            // 1. Not in voice call -> zero JS calls
            Assert.Null(await _service.RefreshDirectRoomTelemetryAsync());
            _voiceModuleMock.Verify(x => x.InvokeAsync<DirectRoomClientTelemetry>("getDirectRoomAudioTelemetry", It.IsAny<object[]>()), Times.Never);

            // 2. In voice call on non-desktop client (IsDesktopDirectRoomSupported == false) -> zero JS calls
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isDesktopDirectRoomSupported", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(false));
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.Null(await _service.RefreshDirectRoomTelemetryAsync());
            _voiceModuleMock.Verify(x => x.InvokeAsync<DirectRoomClientTelemetry>("getDirectRoomAudioTelemetry", It.IsAny<object[]>()), Times.Never);

            // 3. Reconnect as Desktop client -> pulls fresh metrics on demand and updates DirectRoomTelemetry
            await _service.DisconnectAsync();
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isDesktopDirectRoomSupported", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(true));
            _voiceModuleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
                .Returns(new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
                {
                    IsDesktopDirectRoomSupported = true,
                    DeviceId = "dev-desktop-1",
                    DeviceName = "Desktop PC"
                }));

            var desktopJsInterop = new VoiceJsInteropService(_jsMock.Object);
            var desktopDeviceService = new VoiceDeviceService(desktopJsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            await using var desktopService = new GlobalVoiceService(
                desktopJsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                desktopDeviceService);

            await desktopService.ConnectAsync(new VoiceConnectionOptions("token", "url", "ch1", "user1", 64, "rnnoise"));

            var pulled = await desktopService.RefreshDirectRoomTelemetryAsync();
            Assert.NotNull(pulled);
            Assert.Equal(9.7, pulled!.EstimatedEndToEndMs, precision: 1);
            Assert.Equal(1234, pulled.TxPacketCount);
            Assert.Same(pulled, desktopService.DirectRoomTelemetry);
            _voiceModuleMock.Verify(x => x.InvokeAsync<DirectRoomClientTelemetry>("getDirectRoomAudioTelemetry", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task SyncDirectRoomSessionOnlyAsync_DeduplicatesIdenticalPayloads_AndResyncsOnChangeOrReconnect()
        {
            var rooms = new PhysicalVoiceRoomRepository(_persistence, _config);
            var employees = new Spokes_Server.Core.Data.Repositories.HR.EmployeeRepository(_persistence, _config);
            employees.Save(new Employee { Id = "emp-a", FirstName = "A", LastName = "User", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Chat.Use] });
            employees.Save(new Employee { Id = "emp-b", FirstName = "B", LastName = "User", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Chat.Use] });
            employees.Save(new Employee { Id = "emp-remote", FirstName = "Remote", LastName = "User", IsActive = true, Permissions = [Spokes_Server.Core.Constants.AppPermissions.Chat.Use] });

            var voiceBot = new Mock<IVoiceBotService>();
            var privacy = new Mock<Spokes_Server.Core.Services.Security.IUserPrivacyService>();
            privacy.Setup(p => p.CanDiscoverUser(It.IsAny<Employee?>(), It.IsAny<Employee>())).Returns(true);

            var roomService = new PhysicalVoiceRoomService(
                rooms,
                _voiceState,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PhysicalVoiceRoomService>.Instance,
                employees,
                voiceBot.Object,
                userPrivacy: privacy.Object);
            var directService = new PhysicalRoomDirectSessionService(roomService, _voiceState, employees, privacy.Object);

            var room = (await roomService.CreateRoomAsync("Dedup Room", "emp-a", "dev-a", "Desktop A", audioMode: PhysicalRoomAudioMode.LowLatencyCleaned)).Value!;
            await roomService.EnrollUserDeviceAsync(room.Id, "emp-b", "dev-b", "Desktop B", actorUserId: "emp-b");

            int syncCalls = 0;
            var localModuleMock = new Mock<IJSObjectReference>();
            localModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
            localModuleMock.Setup(x => x.InvokeAsync<bool>("isAndroidDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(false));
            localModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            localModuleMock.Setup(x => x.InvokeAsync<bool>("isDesktopDirectRoomSupported", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            localModuleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
                .Returns(new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
                {
                    IsDesktopDirectRoomSupported = true,
                    DeviceId = "dev-a",
                    DeviceName = "Desktop A"
                }));
            localModuleMock.Setup(x => x.InvokeAsync<string[]>("prepareDirectRoomEndpoints", It.IsAny<object[]>()))
                .Returns(new ValueTask<string[]>(["192.168.1.10:50000"]));
            localModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("syncDirectRoomSession", It.IsAny<object[]>()))
                .Callback(() => syncCalls++)
                .Returns(new ValueTask<IJSVoidResult>(Mock.Of<IJSVoidResult>()));
            localModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("syncPhysicalRoomState", It.IsAny<object[]>()))
                .Returns(new ValueTask<IJSVoidResult>(Mock.Of<IJSVoidResult>()));

            var localJsMock = new Mock<IJSRuntime>();
            localJsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
                .Returns(new ValueTask<IJSObjectReference>(localModuleMock.Object));

            _voiceState.SetVoiceParticipantSessionContext("ch-dedup", "emp-b", "dev-b", "Desktop B", "rnnoise", 20.0, "desktop", false);
            _voiceState.NotifyVoiceMemberJoined("ch-dedup", "emp-b", "dev-b", "Desktop B");
            directService.UpdateParticipantEndpoints("ch-dedup", "emp-b", "dev-b", ["192.168.1.11:50000"]);
            _voiceState.NotifyVoiceMemberJoined("ch-dedup", "emp-remote");

            var jsInterop = new VoiceJsInteropService(localJsMock.Object);
            var devSvc = new VoiceDeviceService(jsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            await using var svc = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                devSvc,
                roomService,
                directService);

            await svc.ConnectAsync(new VoiceConnectionOptions("tok", "ws://lk", "ch-dedup", "emp-a", 64, "rnnoise"));
            Assert.Equal(1, syncCalls);

            // Repeated remote mute toggles & redundant RefreshPhysicalRoomStateAsync calls with unchanged descriptor -> ZERO extra SyncDirectRoomSessionAsync RPCs
            _voiceState.SetUserMuted("ch-dedup", "emp-remote", true);
            _voiceState.SetUserMuted("ch-dedup", "emp-remote", false);
            await svc.RefreshPhysicalRoomStateAsync();
            await svc.RefreshPhysicalRoomStateAsync();
            await Task.Delay(30);
            Assert.Equal(1, syncCalls);

            // Peer endpoint change -> immediately resyncs (call #2)
            directService.UpdateParticipantEndpoints("ch-dedup", "emp-b", "dev-b", ["192.168.1.11:50001"]);
            await Task.Delay(30);
            Assert.Equal(2, syncCalls);

            // LiveKit reconnect resets signature cache -> resyncs fresh state (call #3)
            await svc.OnRoomReconnectedAsync(null);
            Assert.Equal(3, syncCalls);
        }

        [Fact]
        public async Task TryBeginConnecting_SingleSourceOfTruth_AllowsConnectAsyncAndPreventsDuplicates()
        {
            int changeCount = 0;
            _service.OnChange += () => changeCount++;

            Assert.True(_service.TryBeginConnecting("ch1"));
            Assert.True(_service.IsConnecting);
            Assert.Equal("ch1", _service.ConnectingChannelId);
            Assert.Equal(VoiceConnectingStage.PreparingAudio, _service.ConnectingStage);
            Assert.Equal(1, changeCount);

            // Second concurrent TryBeginConnecting is rejected cleanly
            Assert.False(_service.TryBeginConnecting("ch1"));
            Assert.False(_service.TryBeginConnecting("ch2"));
            Assert.Equal(1, changeCount);

            // ConnectAsync for the same pre-reserved channel continues cleanly without being ignored
            await ConnectVoiceAsync("token", "url", "ch1", "user1");

            Assert.True(_service.IsInVoiceCall);
            Assert.False(_service.IsConnecting);
            Assert.Null(_service.ConnectingChannelId);
            Assert.Equal(VoiceConnectingStage.None, _service.ConnectingStage);
            _voiceModuleMock.Verify(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task TryBeginConnecting_WhenModuleAndDeviceAlreadyWarmedUp_StartsDirectlyInConnectingToServerWithoutDoubleNotify()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
                .Returns(new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
                {
                    SavedNoiseFilter = "rnnoise",
                    DeviceId = "warm-dev-1",
                    DeviceName = "Warm PC"
                }));

            await _service.EnsureModuleInitializedAsync();

            var observedStages = new List<(bool Connecting, bool InCall, VoiceConnectingStage Stage)>();
            _service.OnChange += () => observedStages.Add((_service.IsConnecting, _service.IsInVoiceCall, _service.ConnectingStage));

            Assert.True(_service.TryBeginConnecting("ch-warm"));
            Assert.Equal(VoiceConnectingStage.ConnectingToServer, _service.ConnectingStage);
            Assert.Single(observedStages);

            await ConnectVoiceAsync("token", "url", "ch-warm", "user1");

            // Should emit only 2 OnChange notifications total:
            // 1) TryBeginConnecting -> ConnectingToServer
            // 2) ConnectAsync finally -> InCall=true, Connecting=false, Stage=None
            Assert.Equal(2, observedStages.Count);
            Assert.Equal((true, false, VoiceConnectingStage.ConnectingToServer), observedStages[0]);
            Assert.Equal((false, true, VoiceConnectingStage.None), observedStages[1]);
        }

        [Fact]
        public void CancelPreparingConnect_ReleasesPreConnectState_WhenCalledBeforeConnectAsync()
        {
            Assert.True(_service.TryBeginConnecting("ch-cancel"));
            Assert.True(_service.IsConnecting);

            // Mismatched channel does not cancel another channel's reservation
            _service.CancelPreparingConnect("ch-other");
            Assert.True(_service.IsConnecting);

            _service.CancelPreparingConnect("ch-cancel");
            Assert.False(_service.IsConnecting);
            Assert.Null(_service.ConnectingChannelId);
            Assert.Equal(VoiceConnectingStage.None, _service.ConnectingStage);
        }

        [Fact]
        public async Task EnsureModuleInitializedAsync_DefaultsSavedNoiseFilterToRnnoiseWhenUnset()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
                .Returns(new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
                {
                    SavedNoiseFilter = string.Empty,
                    DeviceId = "dev-default",
                    DeviceName = "PC"
                }));

            await _service.EnsureModuleInitializedAsync();

            Assert.Equal("rnnoise", _service.SavedNoiseFilter);
        }

        [Fact]
        public async Task ConnectAsync_AfterTransientPrewarmImportCancellation_RetriesImportAndConnects()
        {
            var localJsMock = new Mock<IJSRuntime>();
            var localModuleMock = new Mock<IJSObjectReference>();
            int importCalls = 0;

            localModuleMock.Setup(x => x.InvokeAsync<VoiceClientBootstrapInfo>("getClientVoiceBootstrap", It.IsAny<object[]>()))
                .Returns(new ValueTask<VoiceClientBootstrapInfo>(new VoiceClientBootstrapInfo
                {
                    IsAndroid = true,
                    IsNativePlatform = true,
                    DeviceId = "android-dev-1",
                    DeviceName = "Pixel"
                }));
            localModuleMock.Setup(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()))
                .Returns(new ValueTask<bool>(true));
            localModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>(It.IsAny<string>(), It.IsAny<object[]>()))
                .Returns(new ValueTask<IJSVoidResult>(Mock.Of<IJSVoidResult>()));

            localJsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
                .Returns(() =>
                {
                    importCalls++;
                    if (importCalls == 1)
                    {
                        return new ValueTask<IJSObjectReference>(Task.FromException<IJSObjectReference>(
                            new TaskCanceledException("WebView paused during boot prewarm")));
                    }
                    return new ValueTask<IJSObjectReference>(localModuleMock.Object);
                });

            var jsInterop = new VoiceJsInteropService(localJsMock.Object);
            var devSvc = new VoiceDeviceService(jsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            await using var svc = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                devSvc);

            // Simulate GlobalVoiceBar startup prewarm hitting a transient cancel/disconnect on Android
            await svc.PrepareConnectionAsync("wss://lk.example.test");
            Assert.False(jsInterop.IsModuleLoaded);
            Assert.False(svc.IsAndroid);
            Assert.Equal(1, importCalls);

            // User later taps CONNECT TO VOICE on the same reconnected circuit -> must retry import and succeed
            await svc.ConnectAsync(new VoiceConnectionOptions("tok", "wss://lk.example.test", "ch-voice", "user-android", 64, "rnnoise"));

            Assert.True(jsInterop.IsModuleLoaded);
            Assert.True(svc.IsAndroid);
            Assert.True(svc.IsNativePlatform);
            Assert.True(svc.IsInVoiceCall);
            Assert.Equal("android-dev-1", svc.CurrentDeviceId);
            Assert.Equal(2, importCalls);
            localModuleMock.Verify(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task ConnectAsync_WhenModuleStillUnavailableAfterRetry_FailsFastWithoutPopulatingServerSession()
        {
            var localJsMock = new Mock<IJSRuntime>();
            localJsMock.Setup(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()))
                .Returns(() => new ValueTask<IJSObjectReference>(Task.FromException<IJSObjectReference>(
                    new JSDisconnectedException("Circuit disconnected"))));

            var jsInterop = new VoiceJsInteropService(localJsMock.Object);
            var devSvc = new VoiceDeviceService(jsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            await using var svc = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
                _voiceState,
                _chatState,
                _loggerMock.Object,
                devSvc);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ConnectAsync(new VoiceConnectionOptions("tok", "wss://lk.example.test", "ch-down", "user-down", 64, "rnnoise")));

            Assert.False(svc.IsInVoiceCall);
            Assert.False(svc.IsConnecting);
            Assert.Empty(_voiceState.GetVoiceParticipants("ch-down"));
        }
    }

