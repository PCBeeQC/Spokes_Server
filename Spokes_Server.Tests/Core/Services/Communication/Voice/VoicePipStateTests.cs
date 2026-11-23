using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using Spokes_Server.Components.UI;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoicePipStateTests
{
    private static (GlobalVoiceService VoiceService, VoiceUiStateService UiState, Mock<IVoiceJsInteropService> JsInteropMock) CreateHarness()
    {
        var jsInteropMock = new Mock<IVoiceJsInteropService>();
        jsInteropMock.SetupGet(x => x.IsModuleLoaded).Returns(true);
        jsInteropMock.Setup(x => x.ToggleOsPipAsync()).ReturnsAsync(true);
        jsInteropMock.Setup(x => x.IsOsPipSupportedAsync()).ReturnsAsync(true);

        var voiceChannelServiceMock = new Mock<VoiceChannelService>();
        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();

        var voiceService = new GlobalVoiceService(
            jsInteropMock.Object,
            voiceChannelServiceMock.Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance);

        var uiState = new VoiceUiStateService(
            Mock.Of<ISnackbar>(),
            Mock.Of<ISoundService>(),
            Mock.Of<IJSRuntime>(),
            voiceService,
            jsInteropMock.Object,
            Mock.Of<IVoiceDeviceService>(),
            NullLogger<VoiceUiStateService>.Instance);

        return (voiceService, uiState, jsInteropMock);
    }

    [Fact]
    public void ShouldShowInAppPip_TracksCallStageVisibilityCollapsedPillAndOsPipMutualExclusion()
    {
        var (voiceService, uiState, _) = CreateHarness();

        // Not in call -> never show in-app PiP
        Assert.False(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Connected to voice call while viewing the active call stage -> hidden
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);
        uiState.SetCallStageVisible(true);
        Assert.False(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Navigating away from the active call stage -> in-app PiP card appears
        uiState.SetCallStageVisible(false);
        Assert.True(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Minimizing collapses in-app PiP into a compact corner pill (still mounted & visible for in-place expand)
        uiState.SetInAppPipMinimized(true);
        Assert.True(uiState.ShouldShowInAppPip);
        Assert.True(uiState.IsInAppPipCollapsedPill);

        // Expanding restores full floating card in-place
        uiState.SetInAppPipMinimized(false);
        Assert.True(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Returning to call stage clears minimized pill state automatically
        uiState.SetInAppPipMinimized(true);
        uiState.SetCallStageVisible(true);
        Assert.False(uiState.IsInAppPipMinimized);
        Assert.False(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Navigating away again shows in-app PiP, then opening OS PiP hides duplicate in-app PiP (even if minimized)
        uiState.SetCallStageVisible(false);
        uiState.SetInAppPipMinimized(true);
        Assert.True(uiState.IsInAppPipCollapsedPill);

        voiceService.OnOsPipStateChanged(true);
        Assert.True(uiState.IsOsPipActive);
        Assert.False(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Closing OS PiP while still off-stage restores in-app PiP
        voiceService.OnOsPipStateChanged(false);
        Assert.False(uiState.IsOsPipActive);
        Assert.True(uiState.ShouldShowInAppPip);
    }

    [Fact]
    public void ShouldShowInAppPip_ForcesFullBleedOverlayWhenAndroidEntersNativePipModeEvenOnCallStageOrMinimizedPill()
    {
        var (voiceService, uiState, _) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1"]);
        uiState.SetCallStageVisible(false);
        uiState.SetInAppPipMinimized(true);
        Assert.True(uiState.IsInAppPipCollapsedPill);

        // Entering Android native OS PiP overrides collapsed pill so OS PiP renders full video/avatar stage
        voiceService.OnNativeMobilePipChanged(true);
        Assert.True(uiState.IsNativeMobilePipMode);
        Assert.True(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        // Also overrides active call stage visibility when swiping Home directly from /chat
        uiState.SetCallStageVisible(true);
        Assert.True(uiState.ShouldShowInAppPip);
        Assert.False(uiState.IsInAppPipCollapsedPill);

        voiceService.OnNativeMobilePipChanged(false);
        Assert.False(uiState.IsNativeMobilePipMode);
        Assert.False(uiState.ShouldShowInAppPip);
    }

    [Fact]
    public void OnVideoTrackStateChanged_TracksCameraAndScreenShareParticipantsIndependently()
    {
        var (voiceService, _, _) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);

        Assert.False(voiceService.HasAnyActiveVideoOrScreen);

        voiceService.OnVideoTrackStateChanged("emp-2", "camera", true);
        Assert.Contains("emp-2", voiceService.ActiveCameraParticipants);
        Assert.Empty(voiceService.ActiveScreenShareParticipants);
        Assert.True(voiceService.HasAnyActiveVideoOrScreen);

        voiceService.OnVideoTrackStateChanged("emp-2", "screenshare", true);
        Assert.Contains("emp-2", voiceService.ActiveCameraParticipants);
        Assert.Contains("emp-2", voiceService.ActiveScreenShareParticipants);
        Assert.True(voiceService.HasAnyActiveVideoOrScreen);

        // Turning off screen share preserves active camera state
        voiceService.OnVideoTrackStateChanged("emp-2", "screenshare", false);
        Assert.Contains("emp-2", voiceService.ActiveCameraParticipants);
        Assert.DoesNotContain("emp-2", voiceService.ActiveScreenShareParticipants);
        Assert.True(voiceService.HasAnyActiveVideoOrScreen);

        // Turning off camera returns call to voice-only mode
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", false);
        Assert.Empty(voiceService.ActiveCameraParticipants);
        Assert.False(voiceService.HasAnyActiveVideoOrScreen);
    }

    [Fact]
    public async Task NativeCallAction_ReturnToCall_UnminimizesPipAndRaisesReturnRequestedEvent()
    {
        var (voiceService, uiState, _) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1"]);
        uiState.SetCallStageVisible(false);
        uiState.SetInAppPipMinimized(true);

        var returnRequestedCount = 0;
        uiState.OnReturnToCallStageRequested += () => returnRequestedCount++;

        await voiceService.OnNativeCallAction("returnToCall");

        Assert.Equal(1, returnRequestedCount);
        Assert.False(uiState.IsInAppPipMinimized);
    }

    [Fact]
    public void ResetDeviceState_ClearsAllPipAndFocusFlags()
    {
        var (voiceService, uiState, _) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);
        uiState.SetCallStageVisible(false);
        uiState.SetFocusedParticipant("emp-2");
        uiState.SetInAppPipMinimized(true);
        voiceService.OnOsPipStateChanged(true);
        voiceService.OnNativeMobilePipChanged(true);

        uiState.ResetDeviceState();

        Assert.False(uiState.IsCallStageVisible);
        Assert.False(uiState.IsInAppPipMinimized);
        Assert.False(uiState.IsInAppPipCollapsedPill);
        Assert.False(uiState.IsOsPipActive);
        Assert.False(uiState.IsNativeMobilePipMode);
        Assert.Null(uiState.FocusedParticipantId);
    }

    [Fact]
    public void StagePresentation_EnforcesFaceTimeForUpToTwoParticipants_SupportsCornerSwap_AndSwitchesToGridForThreePlus()
    {
        var (voiceService, uiState, _) = CreateHarness();

        // 1. Solo call (1 participant) -> full-bleed FaceTime stage on self
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1"]);
        var soloStage = uiState.ComputeStagePresentationState();
        Assert.Equal(1, soloStage.VisualParticipantCount);
        Assert.True(soloStage.IsFaceTimeLayout);
        Assert.Equal("emp-1", soloStage.FocusedParticipantId);

        // 1b. Solo + ringing DM callee -> 2-tile FaceTime with ringing callee full-bleed
        var ringingStage = uiState.ComputeStagePresentationState(ringingCalleeId: "emp-callee");
        Assert.Equal(2, ringingStage.VisualParticipantCount);
        Assert.True(ringingStage.IsFaceTimeLayout);
        Assert.Equal("emp-callee", ringingStage.FocusedParticipantId);

        // 2. 2-person call with cameras OFF (voice-only) -> immediately uses FaceTime layout focusing remote emp-2
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);
        Assert.Equal("emp-2", uiState.FocusedParticipantId);
        var twoPersonVoiceOnly = uiState.ComputeStagePresentationState();
        Assert.Equal(2, twoPersonVoiceOnly.VisualParticipantCount);
        Assert.True(twoPersonVoiceOnly.IsFaceTimeLayout);
        Assert.Equal("emp-2", twoPersonVoiceOnly.FocusedParticipantId);

        // 2b. Turning camera ON then OFF in 2-person call preserves FaceTime layout
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", true);
        Assert.Equal("emp-2", uiState.FocusedParticipantId);
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", false);
        Assert.Equal("emp-2", uiState.FocusedParticipantId);
        Assert.True(uiState.ComputeStagePresentationState().IsFaceTimeLayout);

        // 2c. Tapping corner tile swaps focus to self; tapping already-focused full-bleed tile stays in FaceTime
        uiState.ToggleFocusParticipant("emp-1");
        Assert.Equal("emp-1", uiState.FocusedParticipantId);
        Assert.True(uiState.ComputeStagePresentationState().IsFaceTimeLayout);

        uiState.ToggleFocusParticipant("emp-1");
        Assert.Equal("emp-1", uiState.FocusedParticipantId);
        Assert.True(uiState.ComputeStagePresentationState().IsFaceTimeLayout);

        uiState.ToggleFocusParticipant("emp-2");
        Assert.Equal("emp-2", uiState.FocusedParticipantId);

        // 3. 3rd participant joins -> cleanly exits FaceTime into equal grid
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2", "emp-3"]);
        Assert.Null(uiState.FocusedParticipantId);
        var threePersonGrid = uiState.ComputeStagePresentationState();
        Assert.Equal(3, threePersonGrid.VisualParticipantCount);
        Assert.False(threePersonGrid.IsFaceTimeLayout);
        Assert.Null(threePersonGrid.FocusedParticipantId);

        // 3b. Manual focus in 3-person call is preserved when remote camera turns off, and toggles back to equal grid on tap
        voiceService.OnVideoTrackStateChanged("emp-3", "camera", true);
        uiState.SetFocusedParticipant("emp-3", userInitiated: true);
        Assert.Equal("emp-3", uiState.FocusedParticipantId);
        voiceService.OnVideoTrackStateChanged("emp-3", "camera", false);
        Assert.Equal("emp-3", uiState.FocusedParticipantId);
        Assert.False(uiState.ComputeStagePresentationState().IsFaceTimeLayout);

        uiState.ToggleFocusParticipant("emp-3");
        Assert.Null(uiState.FocusedParticipantId);

        // 4. 3rd participant leaves -> automatically returns to 2-person FaceTime layout
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);
        Assert.Equal("emp-2", uiState.FocusedParticipantId);
        var backToFaceTime = uiState.ComputeStagePresentationState();
        Assert.True(backToFaceTime.IsFaceTimeLayout);
        Assert.Equal("emp-2", backToFaceTime.FocusedParticipantId);
    }

    [Fact]
    public async Task SetNoiseSuppressionAsync_SkipsRedundantMicReinitOnNativeMobile_AndReinitializesOnWeb()
    {
        // Native mobile: SpokesNativeVoice.setNoiseFilter owns any required capture-source rebuild
        var (nativeVoice, _, nativeJsMock) = CreateHarness();
        bool nativeLoaded = false;
        nativeJsMock.SetupGet(x => x.IsModuleLoaded).Returns(() => nativeLoaded);
        nativeJsMock.Setup(x => x.InitializeAsync(It.IsAny<DotNetObjectReference<GlobalVoiceService>>()))
            .Callback(() => nativeLoaded = true)
            .Returns(Task.CompletedTask);
        nativeJsMock.Setup(x => x.IsAndroidDeviceAsync()).ReturnsAsync(true);
        nativeJsMock.Setup(x => x.IsNativePlatformAsync()).ReturnsAsync(true);
        await nativeVoice.EnsureModuleInitializedAsync();
        nativeVoice.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);

        await nativeVoice.SetNoiseSuppressionAsync("deepfilternet");
        nativeJsMock.Verify(x => x.SetNoiseSuppressionTypeAsync("deepfilternet"), Times.Once);
        nativeJsMock.Verify(x => x.ReinitializeMicrophoneAsync(), Times.Never);

        // Web/Desktop: SetNoiseSuppressionAsync must still reinitialize Web Audio mic pipeline
        var (webVoice, _, webJsMock) = CreateHarness();
        bool webLoaded = false;
        webJsMock.SetupGet(x => x.IsModuleLoaded).Returns(() => webLoaded);
        webJsMock.Setup(x => x.InitializeAsync(It.IsAny<DotNetObjectReference<GlobalVoiceService>>()))
            .Callback(() => webLoaded = true)
            .Returns(Task.CompletedTask);
        webJsMock.Setup(x => x.IsAndroidDeviceAsync()).ReturnsAsync(false);
        webJsMock.Setup(x => x.IsNativePlatformAsync()).ReturnsAsync(false);
        await webVoice.EnsureModuleInitializedAsync();
        webVoice.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);

        await webVoice.SetNoiseSuppressionAsync("rnnoise");
        webJsMock.Verify(x => x.SetNoiseSuppressionTypeAsync("rnnoise"), Times.Once);
        webJsMock.Verify(x => x.ReinitializeMicrophoneAsync(), Times.Once);
    }

    [Fact]
    public async Task NativeCallAction_ToggleScreenShare_InvokesScreenShareInteropWithCurrentUiStatePreferences()
    {
        var (voiceService, uiState, jsInteropMock) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);
        uiState.MaxScreenShareQuality = "1440p";
        uiState.MaxScreenShareFps = 60;
        uiState.ScreenShareMode = "detail";
        uiState.IsScreenShareAudioEnabled = true;

        await voiceService.OnNativeCallAction("toggleScreenShare");

        jsInteropMock.Verify(
            x => x.SetScreenShareEnabledAsync(true, "1440p", 60, "detail", true),
            Times.Once);
    }

    [Fact]
    public async Task FullscreenParticipant_EnforcesRemoteOnly_StaysOpenWhileAtLeastOneStreamActive_AndAutoExitsWhenAllStreamsStop()
    {
        var (voiceService, uiState, jsInteropMock) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);

        // Local user (emp-1) cannot be placed into remote fullscreen mode
        uiState.SetFullscreenParticipant("emp-1");
        Assert.Null(uiState.FullscreenParticipantId);

        // Remote user (emp-2) starts both screen share and camera
        voiceService.OnVideoTrackStateChanged("emp-2", "screenshare", true);
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", true);

        using var dummyRef = DotNetObjectReference.Create(new object());
        await uiState.EnterParticipantFullscreenAsync("emp-2", dummyRef);
        Assert.Equal("emp-2", uiState.FullscreenParticipantId);
        jsInteropMock.Verify(x => x.EnterParticipantFullscreenAsync("emp-2", dummyRef), Times.Once);

        // Rotating 90 deg and resetting zoom forward to JS interop
        await uiState.SetParticipantFullscreenRotationAsync(90);
        jsInteropMock.Verify(x => x.SetParticipantFullscreenRotationAsync(90), Times.Once);

        await uiState.ResetParticipantFullscreenZoomAsync();
        jsInteropMock.Verify(x => x.ResetParticipantFullscreenZoomAsync(), Times.Once);

        // Remote user stops screen share while keeping camera active -> stays in fullscreen (now zooming camera)
        voiceService.OnVideoTrackStateChanged("emp-2", "screenshare", false);
        Assert.Equal("emp-2", uiState.FullscreenParticipantId);

        // Remote user stops camera as well -> no streams remain, automatically exits fullscreen
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", false);
        Assert.Null(uiState.FullscreenParticipantId);
        jsInteropMock.Verify(x => x.ExitParticipantFullscreenAsync(false), Times.Once);
    }

    [Fact]
    public void ComputePipPresentationState_EnforcesFocusOnlyOnNativeMobile_AndAllowsMultiTileGridOnDesktop()
    {
        var (voiceService, uiState, _) = CreateHarness();
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2", "emp-3"]);
        voiceService.OnVideoTrackStateChanged("emp-2", "camera", true);
        voiceService.OnVideoTrackStateChanged("emp-3", "camera", true);
        uiState.SetCallStageVisible(false);

        // Desktop/Web with >= 3 participants and no focused user -> All-Users Grid allowed
        var desktopState = uiState.ComputePipPresentationState(isNativeMobileClient: false);
        Assert.True(desktopState.CanUseGrid);
        Assert.True(desktopState.IsGridMode);
        Assert.Equal("mode-grid", desktopState.ModeClass);

        // Native Mobile (Android/iOS) -> stays focused on primary speaker/video + corner self-cam (zero multi-tile grid)
        var mobileState = uiState.ComputePipPresentationState(isNativeMobileClient: true);
        Assert.False(mobileState.CanUseGrid);
        Assert.False(mobileState.IsGridMode);
        Assert.Equal("mode-video", mobileState.ModeClass);
        Assert.False(mobileState.HasFilmstrip);

        // Toggling layout on Desktop switches cleanly between Grid and Focus
        uiState.TogglePipGridOrFocusLayout();
        var desktopFocus = uiState.ComputePipPresentationState(isNativeMobileClient: false);
        Assert.False(desktopFocus.IsGridMode);
        Assert.True(desktopFocus.HasFilmstrip);

        uiState.TogglePipGridOrFocusLayout();
        var desktopBackToGrid = uiState.ComputePipPresentationState(isNativeMobileClient: false);
        Assert.True(desktopBackToGrid.IsGridMode);
    }

    [Fact]
    public async Task ActiveSpeakersUpdate_RefreshesStickySpeakerAndSyncsPipSpeakingParticipants()
    {
        var jsInteropMock = new Mock<IVoiceJsInteropService>();
        jsInteropMock.SetupGet(x => x.IsModuleLoaded).Returns(true);
        jsInteropMock.Setup(x => x.ToggleOsPipAsync()).ReturnsAsync(true);
        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var voiceService = new GlobalVoiceService(
            jsInteropMock.Object,
            new Mock<VoiceChannelService>().Object,
            voiceState,
            chatState,
            NullLogger<GlobalVoiceService>.Instance);
        using var uiState = new VoiceUiStateService(
            Mock.Of<ISnackbar>(),
            Mock.Of<ISoundService>(),
            Mock.Of<IJSRuntime>(),
            voiceService,
            jsInteropMock.Object,
            Mock.Of<IVoiceDeviceService>(),
            NullLogger<VoiceUiStateService>.Instance);

        voiceState.NotifyVoiceMemberJoined("chan-voice-1", "emp-1");
        voiceState.NotifyVoiceMemberJoined("chan-voice-1", "emp-2");
        voiceService.ApplyMockConnectedState("chan-voice-1", "emp-1", ["emp-1", "emp-2"]);

        int pipStateChanges = 0;
        uiState.OnPipStateChanged += () => pipStateChanges++;

        // Narrow OnActiveSpeakersUpdated event updates StickySpeakerId and pushes canonical speaking state to JS once
        voiceState.SetUserSpeaking("chan-voice-1", "emp-2", true);
        Assert.Equal("emp-2", uiState.StickySpeakerId);
        Assert.Equal(1, pipStateChanges);
        jsInteropMock.Verify(
            x => x.SyncSpeakingParticipantsAsync("chan-voice-1", It.Is<string[]>(ids => ids.Length == 1 && ids[0] == "emp-2")),
            Times.Once);

        // SyncSpeakingParticipantsAsync forwards canonical channel + speaker IDs to voiceInterop.js / voicePip.js
        await uiState.SyncSpeakingParticipantsAsync("chan-voice-1", ["emp-2"]);
        jsInteropMock.Verify(
            x => x.SyncSpeakingParticipantsAsync("chan-voice-1", It.Is<string[]>(ids => ids.Length == 1 && ids[0] == "emp-2")),
            Times.Exactly(2));

        // Opening OS PiP immediately syncs current canonical speakers into the newly opened window
        Assert.True(await uiState.ToggleOsPipAsync());
        jsInteropMock.Verify(
            x => x.SyncSpeakingParticipantsAsync("chan-voice-1", It.Is<string[]>(ids => ids.Length == 1 && ids[0] == "emp-2")),
            Times.Exactly(3));
    }
}
