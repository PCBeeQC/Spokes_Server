using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceCircuitHandlerTests
{
    private readonly Mock<IJSRuntime> _jsRuntimeMock;
    private readonly Mock<ILogger<GlobalVoiceService>> _loggerMock;

    public VoiceCircuitHandlerTests()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _loggerMock = new Mock<ILogger<GlobalVoiceService>>();
    }

    private GlobalVoiceService CreateGlobalVoiceService() =>
        new(
            new VoiceJsInteropService(_jsRuntimeMock.Object),
            new Mock<VoiceChannelService>().Object, 
            new VoiceStateService(),
            new ChatStateService(), 
            _loggerMock.Object);

    [Fact]
    public async Task OnConnectionUpAsync_UserInVoiceCall_MarksForIntegrityCheck()
    {
        // Arrange
        var globalVoiceService = CreateGlobalVoiceService();
        globalVoiceService.ApplyMockConnectedState("channel1", "user1", []);
        var handler = new VoiceCircuitHandler(globalVoiceService);

        // Act
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.NotNull(globalVoiceService);
        Assert.NotNull(handler);
        Assert.True(globalVoiceService.RequiresIntegrityCheck);
    }

    [Fact]
    public async Task OnConnectionUpAsync_UserNotInVoiceCall_DoesNotMarkForIntegrityCheck()
    {
        // Arrange
        var globalVoiceService = CreateGlobalVoiceService();
        var handler = new VoiceCircuitHandler(globalVoiceService);

        // Act
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.NotNull(globalVoiceService);
        Assert.NotNull(handler);
        Assert.False(globalVoiceService.RequiresIntegrityCheck);
    }

    [Fact]
    public async Task OnConnectionUpAsync_ServiceThrowsException_SwallowsExceptionAndReturnsTask()
    {
        // Arrange
        var handler = new VoiceCircuitHandler(null!);

        // Act
        var task = handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.NotNull(handler);
        Assert.NotNull(task);
        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }

    [Fact]
    public async Task OnConnectionDownAsync_WhenUserIsSpeaking_ClearsLocalSpeakingStateInChatState()
    {
        var voiceState = new VoiceStateService();
        var chatState = new ChatStateService();
        var globalVoiceService = new GlobalVoiceService(
            new VoiceJsInteropService(_jsRuntimeMock.Object),
            new Mock<VoiceChannelService>().Object,
            voiceState,
            chatState,
            _loggerMock.Object);

        voiceState.NotifyVoiceMemberJoined("channel1", "user1");
        globalVoiceService.ApplyMockConnectedState("channel1", "user1", ["user1", "user2"]);
        globalVoiceService.OnLocalSpeakingChanged(true);
        voiceState.SetUserSpeaking("channel1", "user1", true);
        Assert.Contains("user1", voiceState.GetActiveSpeakers("channel1"));

        var handler = new VoiceCircuitHandler(globalVoiceService);
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);

        Assert.DoesNotContain("user1", voiceState.GetActiveSpeakers("channel1"));
        Assert.DoesNotContain("user1", globalVoiceService.ActiveSpeakers);
    }
}
