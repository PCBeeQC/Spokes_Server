using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MudBlazor;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceCircuitHandlerTests
{
    private readonly Mock<IJSRuntime> _jsRuntimeMock;
    private readonly Mock<ILogger<GlobalVoiceService>> _loggerMock;
    private readonly Mock<ISnackbar> _snackbarMock;

    public VoiceCircuitHandlerTests()
    {
        _jsRuntimeMock = new Mock<IJSRuntime>();
        _loggerMock = new Mock<ILogger<GlobalVoiceService>>();
        _snackbarMock = new Mock<ISnackbar>();
    }

    private GlobalVoiceService CreateGlobalVoiceService() =>
        new(
            new VoiceJsInteropService(_jsRuntimeMock.Object),
            new Mock<VoiceChannelService>().Object, 
            new ChatStateService(), 
            _loggerMock.Object);

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        var globalVoiceService = CreateGlobalVoiceService();
        var handler = new VoiceCircuitHandler(globalVoiceService);
        Assert.NotNull(handler);
        Assert.NotNull(globalVoiceService);
        Assert.False(globalVoiceService.RequiresIntegrityCheck);
    }

    [Fact]
    public async Task OnConnectionDownAsync_CallsBase_ReturnsCompletedTask()
    {
        // Arrange
        var globalVoiceService = CreateGlobalVoiceService();
        var handler = new VoiceCircuitHandler(globalVoiceService);

        // Act
        var task = handler.OnConnectionDownAsync(null!, CancellationToken.None);

        // Assert
        Assert.NotNull(globalVoiceService);
        Assert.NotNull(handler);
        Assert.NotNull(task);
        Assert.True(task.IsCompletedSuccessfully);
        await task; 
    }

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
}
