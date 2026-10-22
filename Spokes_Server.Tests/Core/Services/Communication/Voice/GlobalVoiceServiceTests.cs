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
            var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
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
                
            _chatState = new ChatStateService();

            var companyProfiles = new CompanyProfileRepository(_persistence, _config);
            var messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
            var gatewayMock = new Mock<IChatRealtimeGateway>();

            _voiceChannelServiceMock = new Mock<VoiceChannelService>();
            _voiceChannelServiceMock.Setup(x => x.JoinVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((channelId, userId, _) => _chatState.NotifyVoiceMemberJoined(channelId, userId))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((channelId, userId, _) => _chatState.NotifyVoiceMemberLeft(channelId, userId))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.SetUserSpeakingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, bool, CancellationToken>((channelId, userId, isSpeaking, _) => _chatState.SetUserSpeaking(channelId, userId, isSpeaking))
                .Returns(Task.CompletedTask);
            _voiceChannelServiceMock.Setup(x => x.SetUserMutedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, bool, CancellationToken>((channelId, userId, isMuted, _) => _chatState.SetUserMuted(channelId, userId, isMuted))
                .Returns(Task.CompletedTask);
                
            _snackbarMock = new Mock<ISnackbar>();
            _loggerMock = new Mock<ILogger<GlobalVoiceService>>();
            _soundServiceMock = new Mock<ISoundService>();
            
            var jsInterop = new VoiceJsInteropService(_jsMock.Object);
            _deviceService = new VoiceDeviceService(jsInterop, Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDeviceService>.Instance);
            _service = new GlobalVoiceService(
                jsInterop,
                _voiceChannelServiceMock.Object,
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
            Assert.Contains("user1", _chatState.GetVoiceParticipants("ch1"));
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
            _chatState.NotifyVoiceMemberJoined("ch1", "user2");
            
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
            Assert.DoesNotContain("user1", _chatState.GetVoiceParticipants("ch1"));
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
            _service.OnActiveSpeakersChanged(new[] { "user1" });
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _chatState.GetActiveSpeakers("ch1"));

            await _service.ToggleMuteAsync();

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _chatState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task OnLocalMuted_WhenSpeaking_ClearsActiveSpeakersAndNotifiesChatService()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.OnActiveSpeakersChanged(new[] { "user1" });
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _chatState.GetActiveSpeakers("ch1"));

            _service.OnLocalMuted(true);

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _chatState.GetActiveSpeakers("ch1"));
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
        public async Task ObsoleteToggleMethods_ForwardToAsyncMethods()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsMuted);
            Assert.False(_service.IsVideoOn);
            Assert.False(_service.IsScreenSharing);

            await _service.ToggleMuteAsync();
            await _service.ToggleVideoAsync();
            await _service.ToggleScreenShareAsync();

            Assert.True(_service.IsMuted);
            Assert.True(_service.IsVideoOn);
            Assert.True(_service.IsScreenSharing);
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
            _chatState.NotifyVoiceMemberJoined("ch1", "user2"); // Present on server
            
            _service.OnParticipantConnected("user2");
            
            Assert.Contains("user2", _service.CallParticipants);
        }
        
        [Fact]
        public async Task OnParticipantConnected_AddsParticipant_EvenIfNotYetInServerState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            // Not yet present on server state, but connected via WebRTC
            
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
            
            _chatState.NotifyVoiceMemberJoined("ch1", "user3");
            
            Assert.Contains("user3", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberJoined_DoesNotAddLocalUser_ToCallParticipants()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.CallParticipants.Remove("user1");
            
            _chatState.NotifyVoiceMemberJoined("ch1", "user1");
            
            Assert.DoesNotContain("user1", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberLeft_RemovesParticipant_WhenInCall()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.CallParticipants.Add("user3");
            
            _chatState.NotifyVoiceMemberLeft("ch1", "user3");
            
            Assert.DoesNotContain("user3", _service.CallParticipants);
        }

        [Fact]
        public void EnsureParticipantSlot_AddsParticipantAndNotifies()
        {
            bool notified = false;
            _service.OnChange += () => notified = true;
            
            _service.EnsureParticipantSlot("userX");
            
            Assert.Contains("userX", _service.CallParticipants);
            Assert.True(notified);
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
        public void OnActiveSpeakersChanged_WithBotIdentity_FiltersOutBot()
        {
            _service.OnActiveSpeakersChanged(new[] { "user1", "bot:spokes-voice" });

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
        public async Task OnActiveSpeakersChanged_UpdatesSetAndNotifiesChatService()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            _service.OnActiveSpeakersChanged(new[] { "user1", "user2" });
            
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user2", _service.ActiveSpeakers);
            
        }

        [Fact]
        public async Task OnRoomDisconnected_CleansUpState()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            
            await _service.OnRoomDisconnected();
            
            Assert.False(_service.IsInVoiceCall);
            Assert.Empty(_service.CallParticipants);
            Assert.DoesNotContain("user1", _chatState.GetVoiceParticipants("ch1"));
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
            Assert.Equal("720p", _uiState.MaxVideoSendQuality);
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
        public void LogToServer_WritesToLogger()
        {
            _service.LogToServer("ERROR", "test error");
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("test error")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
        }
        
        [Fact]
        public void LogToServer_TruncatesLongMessages()
        {
            var longMsg = new string('A', 600);
            _service.LogToServer("INFO", longMsg);
            
            _loggerMock.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("[TRUNCATED]")),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)),
                Times.Once);
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
            
            _chatState.NotifyVoiceMemberLeft("ch1", "user1");
            await _service.VerifyServerStateIntegrityAsync();
            
            Assert.False(_service.RequiresIntegrityCheck);
            Assert.False(_service.IsInVoiceCall);
        }
        
        [Fact]
        public async Task VerifyServerStateIntegrityAsync_SyncsParticipants_IfUserStillInServerList()
        {
            await ConnectVoiceAsync("token", "url", "ch1", "user1");
            _service.MarkForIntegrityCheck();
            _chatState.NotifyVoiceMemberJoined("ch1", "user1");
            _chatState.NotifyVoiceMemberJoined("ch1", "user2");
            
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
            _chatState.NotifyVoiceMemberJoined("ch1", "user3");
            
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
        public void OnParticipantMuteChanged_TracksMutedParticipants_AndFiresOnChange()
        {
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

            _chatState.SetUserMuted("ch1", "user2", true);
            Assert.Contains("user2", _service.MutedParticipants);

            _chatState.SetUserMuted("ch1", "user2", false);
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
        public void LogToServer_LimitsTo10CallsPerMinute()
        {
            var mockLogger = new Mock<ILogger<GlobalVoiceService>>();
            var jsInterop = new VoiceJsInteropService(new Mock<IJSRuntime>().Object);
            var service = new GlobalVoiceService(
                jsInterop,
                new Mock<VoiceChannelService>().Object,
                new ChatStateService(),
                mockLogger.Object);

            for (int i = 0; i < 15; i++)
            {
                service.LogToServer("INFO", $"Message {i}");
            }

            // Only 10 logs should have been passed to ILogger.
            mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => true),
                    It.IsAny<Exception>(),
                    It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
                Times.Exactly(10));
        }

        [Fact]
        public void OnRemoteScreenAudioChanged_TracksParticipantAudioCorrectly()
        {
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
        public void OnParticipantDisconnected_RemovesFromRemoteScreenAudioSets()
        {
            _service.OnRemoteScreenAudioChanged("user2", true);
            _service.MutedRemoteScreenAudioParticipants.Add("user2");
            Assert.Contains("user2", _service.RemoteScreenAudioParticipants);
            Assert.Contains("user2", _service.MutedRemoteScreenAudioParticipants);

            _service.OnParticipantDisconnected("user2");
            Assert.DoesNotContain("user2", _service.RemoteScreenAudioParticipants);
            Assert.DoesNotContain("user2", _service.MutedRemoteScreenAudioParticipants);
        }
    }

