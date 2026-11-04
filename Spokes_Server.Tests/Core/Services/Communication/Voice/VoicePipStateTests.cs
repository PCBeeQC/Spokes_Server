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
        var chatState = new ChatStateService();

        var voiceService = new GlobalVoiceService(
            jsInteropMock.Object,
            voiceChannelServiceMock.Object,
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
}
