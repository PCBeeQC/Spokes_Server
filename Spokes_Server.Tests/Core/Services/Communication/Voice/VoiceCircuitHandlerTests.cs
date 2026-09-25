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
            _jsRuntimeMock.Object, 
            null!, 
            null!, 
            _snackbarMock.Object, 
            _loggerMock.Object,
            null!);

    [Fact]
    public async Task OnConnectionDownAsync_CallsBase_ReturnsCompletedTask()
    {
        // Arrange
        var globalVoiceService = CreateGlobalVoiceService();
        var handler = new VoiceCircuitHandler(globalVoiceService);

        // Act
        var task = handler.OnConnectionDownAsync(null!, CancellationToken.None);

        // Assert
        Assert.True(task.IsCompletedSuccessfully);
        await task; 
    }

    [Fact]
    public async Task OnConnectionUpAsync_UserInVoiceCall_MarksForIntegrityCheck()
    {
        // Arrange
        var globalVoiceService = CreateGlobalVoiceService();
        globalVoiceService.ConnectMockForDemo("channel1", "user1", []);
        var handler = new VoiceCircuitHandler(globalVoiceService);

        // Act
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
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
        Assert.True(task.IsCompletedSuccessfully);
        await task; 
    }
}
