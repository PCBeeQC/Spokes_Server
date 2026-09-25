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
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;
    public class GlobalVoiceServiceTests : IDisposable
    {
        private readonly Mock<IJSRuntime> _jsMock;
        private readonly Mock<IJSObjectReference> _voiceModuleMock;
        private readonly Mock<ChatService> _chatServiceMock;
        private readonly ChatStateService _chatState;
        private readonly Mock<ISnackbar> _snackbarMock;
        private readonly Mock<ILogger<GlobalVoiceService>> _loggerMock;
        private readonly Mock<Spokes_Server.Core.Services.UI.ISoundService> _soundServiceMock;
        private readonly GlobalVoiceService _service;
        
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
            var hubMock = new Mock<IHubContext<ChatHub>>();
            var hubClientsMock = new Mock<IHubClients>();
            var groupMock = new Mock<IClientProxy>();
            hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
            hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupMock.Object);

            _chatServiceMock = new Mock<ChatService>(
                null, null, messages, null, null, null, hubMock.Object, _chatState, null, null,
                new Mock<ILogger<ChatService>>().Object, null, null, null, null, null, null, null, null, null, null, null) { CallBase = true };
                
            _snackbarMock = new Mock<ISnackbar>();
            _loggerMock = new Mock<ILogger<GlobalVoiceService>>();
            _soundServiceMock = new Mock<Spokes_Server.Core.Services.UI.ISoundService>();
            
            _service = new GlobalVoiceService(
                _jsMock.Object,
                _chatServiceMock.Object,
                _chatState,
                _snackbarMock.Object,
                _loggerMock.Object,
                _soundServiceMock.Object
            );
        }
        
        public void Dispose()
        {
            _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _persistence.Dispose();
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); }
                catch { }
            }
        }

        [Fact]
        public void ConnectMockForDemo_SetsStateAndParticipants()
        {
            var channelId = "ch1";
            var userId = "user1";
            var mockParticipants = new HashSet<string> { "user1", "user2" };
            
            _service.ConnectMockForDemo(channelId, userId, mockParticipants);
            
            Assert.True(_service.IsInVoiceCall);
            Assert.False(_service.IsConnecting);
            Assert.Equal(channelId, _service.CurrentChannelId);
            Assert.Equal(userId, _service.CurrentUserId);
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Contains("user2", _service.CallParticipants);
        }
        
        [Fact]
        public void ConnectMockForDemo_NotifiesChatState()
        {
            var channelId = "ch1";
            var userId = "user1";
            var mockParticipants = new HashSet<string> { "user1", "user2" };
            
            var notifiedParticipants = new List<string>();
            _chatState.VoiceMemberJoined += (c, u) => { if (c == channelId) notifiedParticipants.Add(u); };
            
            _service.ConnectMockForDemo(channelId, userId, mockParticipants);
            
            Assert.Contains("user1", notifiedParticipants);
            Assert.Contains("user2", notifiedParticipants);
        }

        [Fact]
        public void DisconnectMockForDemo_ClearsStateAndNotifiesChatState()
        {
            var channelId = "ch1";
            var userId = "user1";
            var mockParticipants = new HashSet<string> { "user1", "user2" };
            _service.ConnectMockForDemo(channelId, userId, mockParticipants);
            
            var leftParticipants = new List<string>();
            _chatState.VoiceMemberLeft += (c, u) => { if (c == channelId) leftParticipants.Add(u); };
            
            _service.DisconnectMockForDemo();
            
            Assert.False(_service.IsInVoiceCall);
            Assert.Null(_service.CurrentChannelId);
            Assert.Null(_service.CurrentUserId);
            Assert.Empty(_service.CallParticipants);
            Assert.Contains("user1", leftParticipants);
            Assert.Contains("user2", leftParticipants);
        }

        [Fact]
        public async Task ConnectAsync_SetsStateAndCallsJS_HappyPath()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
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
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            await _service.ConnectAsync("token2", "url2", "ch2", "user2");
            
            _jsMock.Verify(x => x.InvokeAsync<IJSObjectReference>("import", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task ConnectAsync_HandlesIOSNoiseSuppression()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<bool>("isAppleMobileDevice", It.IsAny<object[]>())).Returns(new ValueTask<bool>(true));
            
            await _service.ConnectAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
            Assert.Equal("webrtc", _service.CurrentNoiseSuppression);
        }
        
        [Fact]
        public async Task ConnectAsync_IncludesExistingServerParticipants()
        {
            _chatState.NotifyVoiceMemberJoined("ch1", "user2");
            
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            Assert.Contains("user1", _service.CallParticipants);
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public async Task DisconnectAsync_CallsJSAndChatService_WhenConnected()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.DisconnectAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
            Assert.DoesNotContain("user1", _chatState.GetVoiceParticipants("ch1"));
            Assert.False(_service.IsInVoiceCall);
        }
        
        [Fact]
        public async Task DisconnectAsync_DoesNotThrowOnJSError()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _voiceModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()))
                .Throws(new JSDisconnectedException("disconnected"));
                
            await _service.DisconnectAsync();
            
            Assert.False(_service.IsInVoiceCall);
        }

        [Fact]
        public async Task ToggleMute_TogglesStateAndCallsJS()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsMuted);
            
            await _service.ToggleMute();
            
            Assert.True(_service.IsMuted);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setMicrophoneEnabled", It.Is<object[]>(args => (bool)args[0] == false)), Times.Once);
        }

        [Fact]
        public async Task ToggleMute_WhenSpeaking_ClearsActiveSpeakersAndNotifiesChatService()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _service.OnActiveSpeakersChanged(new[] { "user1" });
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _chatState.GetActiveSpeakers("ch1"));

            await _service.ToggleMute();

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _chatState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task OnLocalMuted_WhenSpeaking_ClearsActiveSpeakersAndNotifiesChatService()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _service.OnActiveSpeakersChanged(new[] { "user1" });
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user1", _chatState.GetActiveSpeakers("ch1"));

            _service.OnLocalMuted(true);

            Assert.True(_service.IsMuted);
            Assert.DoesNotContain("user1", _service.ActiveSpeakers);
            Assert.DoesNotContain("user1", _chatState.GetActiveSpeakers("ch1"));
        }

        [Fact]
        public async Task ToggleVideo_TogglesStateAndSetsCameras()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsVideoOn);
            
            await _service.ToggleVideo();
            
            Assert.True(_service.IsVideoOn);
            Assert.Equal("cam1", _service.ActiveCameraDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<CameraInitResult>("setCameraEnabled", It.Is<object[]>(args => (bool)args[0] == true)), Times.Once);
        }

        [Fact]
        public async Task SwitchCameraAsync_CallsJS_WhenVideoOn()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            await _service.ToggleVideo(); // Video is now ON
            
            await _service.SwitchCameraAsync("cam2");
            
            Assert.Equal("cam2", _service.ActiveCameraDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveCamera", It.Is<object[]>(args => (string)args[0] == "cam2")), Times.Once);
        }
        
        [Fact]
        public async Task SwitchMicrophoneAsync_CallsJS_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.SwitchMicrophoneAsync("mic2");
            
            Assert.Equal("mic2", _service.ActiveMicrophoneDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveMicrophone", It.Is<object[]>(args => (string)args[0] == "mic2")), Times.Once);
        }

        [Fact]
        public async Task SwitchSpeakerAsync_CallsJS_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.SwitchSpeakerAsync("speaker2");
            
            Assert.Equal("speaker2", _service.ActiveSpeakerDeviceId);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("switchActiveSpeaker", It.Is<object[]>(args => (string)args[0] == "speaker2")), Times.Once);
        }

        [Fact]
        public void OnAudioDevicesUpdated_UpdatesAvailableAndActiveDevices()
        {
            var mics = new List<AudioDevice> { new AudioDevice { DeviceId = "mic1" }, new AudioDevice { DeviceId = "mic2" } };
            var speakers = new List<AudioDevice> { new AudioDevice { DeviceId = "spk1" }, new AudioDevice { DeviceId = "spk2" } };
            
            _service.OnAudioDevicesUpdated(mics, "mic2", speakers, "spk2");
            
            Assert.Equal(2, _service.AvailableMicrophones.Count);
            Assert.Equal("mic2", _service.ActiveMicrophoneDeviceId);
            Assert.Equal(2, _service.AvailableSpeakers.Count);
            Assert.Equal("spk2", _service.ActiveSpeakerDeviceId);
        }
        
        [Fact]
        public void OnAudioDevicesUpdated_DefaultsToFirstDeviceIfActiveIdEmpty()
        {
            var mics = new List<AudioDevice> { new AudioDevice { DeviceId = "mic1" } };
            var speakers = new List<AudioDevice> { new AudioDevice { DeviceId = "spk1" } };
            
            _service.OnAudioDevicesUpdated(mics, "", speakers, null);
            
            Assert.Equal("mic1", _service.ActiveMicrophoneDeviceId);
            Assert.Equal("spk1", _service.ActiveSpeakerDeviceId);
        }

        [Fact]
        public async Task SetNoiseSuppressionAsync_UpdatesTypeAndCallsJS()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1", 64, "rnnoise");
            
            await _service.SetNoiseSuppressionAsync("webrtc");
            
            Assert.Equal("webrtc", _service.CurrentNoiseSuppression);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setNoiseSuppressionType", It.Is<object[]>(args => (string)args[0] == "webrtc")), Times.Once);
        }

        [Fact]
        public async Task ToggleScreenShare_TogglesStateAndCallsJS()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            Assert.False(_service.IsScreenSharing);
            
            await _service.ToggleScreenShare();
            
            Assert.True(_service.IsScreenSharing);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setScreenShareEnabled", It.Is<object[]>(args => (bool)args[0] == true)), Times.Once);
        }

        [Fact]
        public async Task ReattachTracksAsync_CallsJS_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.ReattachTracksAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("reattachAllTracks", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public async Task RefreshVideoLayoutsAsync_CallsJS_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.RefreshVideoLayoutsAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("refreshVideoLayouts", It.IsAny<object[]>()), Times.Once);
        }
        
        [Fact]
        public async Task SetRemoteVideoQualityAsync_CallsJS_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.SetRemoteVideoQualityAsync("user2", "high");
            
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
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _chatState.NotifyVoiceMemberJoined("ch1", "user2"); // Present on server
            
            _service.OnParticipantConnected("user2");
            
            Assert.Contains("user2", _service.CallParticipants);
        }
        
        [Fact]
        public async Task OnParticipantConnected_AddsParticipant_EvenIfNotYetInServerState()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            // Not yet present on server state, but connected via WebRTC
            
            _service.OnParticipantConnected("user2");
            
            Assert.Contains("user2", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberJoined_AddsParticipant_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            _chatState.NotifyVoiceMemberJoined("ch1", "user3");
            
            Assert.Contains("user3", _service.CallParticipants);
        }

        [Fact]
        public async Task ChatState_VoiceMemberLeft_RemovesParticipant_WhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
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
        public async Task ConnectAsync_ReentrancyGuard_IgnoresDuplicateCallToSameChannel()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<bool>("connectToVoice", It.IsAny<object[]>()), Times.Once);
        }

        [Fact]
        public void OnParticipantDisconnected_RemovesFromState()
        {
            _service.CallParticipants.Add("user2");
            _service.ActiveSpeakers.Add("user2");
            _service.RemoteVideoHeights.TryAdd("user2", 720);
            
            _service.OnParticipantDisconnected("user2");
            
            Assert.DoesNotContain("user2", _service.CallParticipants);
            Assert.DoesNotContain("user2", _service.ActiveSpeakers);
            Assert.False(_service.RemoteVideoHeights.ContainsKey("user2"));
        }

        [Fact]
        public async Task OnActiveSpeakersChanged_UpdatesSetAndNotifiesChatService()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            _service.OnActiveSpeakersChanged(new[] { "user1", "user2" });
            
            Assert.Contains("user1", _service.ActiveSpeakers);
            Assert.Contains("user2", _service.ActiveSpeakers);
            
        }

        [Fact]
        public async Task OnRoomDisconnected_CleansUpState()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
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
            
            Assert.Equal(720, _service.RemoteVideoHeights["user2"]);
            Assert.Equal(30, _service.RemoteVideoFps["user2"]);
        }
        
        [Fact]
        public void OnParticipantVideoDimensions_IgnoresUnknownParticipants()
        {
            _service.OnParticipantVideoDimensions("user2", 720, 30);
            
            Assert.False(_service.RemoteVideoHeights.ContainsKey("user2"));
        }

        [Fact]
        public void OnLocalVideoDimensions_UpdatesMaxValuesAndQuality()
        {
            _service.MaxVideoSendQuality = "2160p";
            
            _service.OnLocalVideoDimensions(1080, 30);
            
            Assert.Equal(1080, _service.MaxCameraHeight);
            Assert.Equal(30, _service.MaxCameraFps);
            Assert.Equal("720p", _service.MaxVideoSendQuality);
        }
        
        [Fact]
        public void OnLocalScreenDimensions_UpdatesMaxValuesAndQuality()
        {
            _service.MaxScreenShareQuality = "2160p";
            
            _service.OnLocalScreenDimensions(1080, 30);
            
            Assert.Equal(1080, _service.MaxScreenHeight);
            Assert.Equal(30, _service.MaxScreenFps);
            Assert.Equal("720p", _service.MaxScreenShareQuality);
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
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            
            await _service.DisposeAsync();
            
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("disconnectFromVoice", It.IsAny<object[]>()), Times.Once);
            _voiceModuleMock.Verify(x => x.DisposeAsync(), Times.Once);
            Assert.False(_service.IsInVoiceCall);
        }


        [Fact]
        public async Task VerifyServerStateIntegrityAsync_Disconnects_IfUserNotInServerList()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _service.MarkForIntegrityCheck();
            
            _chatState.NotifyVoiceMemberLeft("ch1", "user1");
            await _service.VerifyServerStateIntegrityAsync();
            
            Assert.False(_service.RequiresIntegrityCheck);
            Assert.False(_service.IsInVoiceCall);
        }
        
        [Fact]
        public async Task VerifyServerStateIntegrityAsync_SyncsParticipants_IfUserStillInServerList()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");
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
            await _service.ConnectAsync("token", "url", "ch1", "user1");
            _chatState.NotifyVoiceMemberJoined("ch1", "user3");
            
            _service.OnRoomConnected(["user2"]);
            
            Assert.Contains("user2", _service.CallParticipants);
            Assert.True(_service.IsInVoiceCall);
        }

        [Fact]
        public async Task SetMasterVolumeAsync_UpdatesProperty_AndInvokesJsWhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");

            await _service.SetMasterVolumeAsync(150.0);

            Assert.Equal(150.0, _service.VoiceSettings.MasterVolume);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setMasterVolume", It.Is<object[]>(args => (double)args[0] == 150.0)), Times.Once);
        }

        [Fact]
        public async Task SetParticipantVolumeAsync_UpdatesDictionary_AndInvokesJsWhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");

            await _service.SetParticipantVolumeAsync("user2", 125.0);

            Assert.True(_service.VoiceSettings.ParticipantVolumes.ContainsKey("user2"));
            Assert.Equal(125.0, _service.VoiceSettings.ParticipantVolumes["user2"]);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setParticipantVolume", It.Is<object[]>(args => (string)args[0] == "user2" && (double)args[1] == 125.0)), Times.Once);
        }

        [Fact]
        public async Task UpdateVoiceSettingsAsync_UpdatesProperty_AndSyncsToJsWhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");

            var settings = new Spokes_Server.Core.Models.HR.VoiceUserSettings
            {
                MasterVolume = 80.0
            };

            await _service.UpdateVoiceSettingsAsync(settings);

            Assert.Equal(80.0, _service.VoiceSettings.MasterVolume);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("initVoiceSettings", It.IsAny<object[]>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task SetVoiceSensitivityAsync_UpdatesVoiceSettings_AndCallsJsWhenInCall()
        {
            await _service.ConnectAsync("token", "url", "ch1", "user1");

            await _service.SetVoiceSensitivityAsync(65.0);

            Assert.Equal(65.0, _service.VoiceSettings.VoiceSensitivity);
            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("setVoiceSensitivity", It.Is<object[]>(args => (double)args[0] == 65.0)), Times.Once);
        }

        [Fact]
        public async Task SetVoiceSensitivityAsync_ClampsValues()
        {
            await _service.SetVoiceSensitivityAsync(-10.0);
            Assert.Equal(0.0, _service.VoiceSettings.VoiceSensitivity);

            await _service.SetVoiceSensitivityAsync(150.0);
            Assert.Equal(100.0, _service.VoiceSettings.VoiceSensitivity);
        }

        [Fact]
        public async Task PlayTestSpeakerSoundAsync_PlaysNotificationSoundViaVoiceModule()
        {
            await _service.PlayTestSpeakerSoundAsync("speaker1");

            _voiceModuleMock.Verify(x => x.InvokeAsync<IJSVoidResult>("playTestSpeakerSound", It.Is<object[]>(args => (string)args[0] == "speaker1")), Times.Once);
        }

        [Fact]
        public async Task PlayTestSpeakerSoundAsync_FallbackToSoundService_WhenVoiceModuleThrows()
        {
            _voiceModuleMock.Setup(x => x.InvokeAsync<IJSVoidResult>("playTestSpeakerSound", It.IsAny<object[]>()))
                .ThrowsAsync(new Exception("Module failure"));

            await _service.PlayTestSpeakerSoundAsync();

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
            await _service.ConnectAsync("token", "url", "ch1", "user1");

            _chatState.SetUserMuted("ch1", "user2", true);
            Assert.Contains("user2", _service.MutedParticipants);

            _chatState.SetUserMuted("ch1", "user2", false);
            Assert.DoesNotContain("user2", _service.MutedParticipants);
        }
    }

