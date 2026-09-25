using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Utilities;

namespace Spokes_Server.Tests.Core.Utilities;

public class SpokesDomInteropServiceTests
{
    [Fact]
    public async Task WaitForElement_InvokesJsModuleWithSelectorAndTimeout()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.Is<object?[]>(args => args != null && args.Length == 1 && (string)args[0]! == "./js/spokes-dom-observer.js")))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.Is<object?[]>(args => args != null && args.Length == 2 && (string)args[0]! == ".my-selector" && (int)args[1]! == 3000)))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var service = new SpokesDomInteropService(mockJs.Object);

        // Act
        await service.WaitForElement(".my-selector", 3000);

        // Assert
        mockJs.Verify(
            js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.Is<object?[]>(args => args != null && args.Length == 1 && (string)args[0]! == "./js/spokes-dom-observer.js")),
            Times.Once);

        mockModule.Verify(
            m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.Is<object?[]>(args => args != null && args.Length == 2 && (string)args[0]! == ".my-selector" && (int)args[1]! == 3000)),
            Times.Once);
    }

    [Fact]
    public async Task WaitForElement_DefaultTimeout_Uses2000()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.Is<object?[]>(args => args != null && args.Length == 1 && (string)args[0]! == "./js/spokes-dom-observer.js")))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.Is<object?[]>(args => args != null && args.Length == 2 && (string)args[0]! == ".my-selector" && (int)args[1]! == 2000)))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var service = new SpokesDomInteropService(mockJs.Object);

        // Act
        await service.WaitForElement(".my-selector");

        // Assert
        mockModule.Verify(
            m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.Is<object?[]>(args => args != null && args.Length == 2 && (string)args[0]! == ".my-selector" && (int)args[1]! == 2000)),
            Times.Once);
    }

    [Fact]
    public async Task WaitForElement_WhenDisconnected_DoesNotThrow()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.IsAny<object?[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected."));

        var service = new SpokesDomInteropService(mockJs.Object);

        // Act
        var ex = await Record.ExceptionAsync(() => service.WaitForElement(".my-selector"));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task WaitForElement_WhenTaskCanceled_DoesNotThrow()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.IsAny<object?[]>()))
            .ThrowsAsync(new TaskCanceledException());

        var service = new SpokesDomInteropService(mockJs.Object);

        // Act
        var ex = await Record.ExceptionAsync(() => service.WaitForElement(".my-selector"));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public async Task WaitForElement_WhenUnhandledException_PropagatesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>(
                "import",
                It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>(
                "waitForElement",
                It.IsAny<object?[]>()))
            .ThrowsAsync(new InvalidOperationException("Fatal error"));

        var service = new SpokesDomInteropService(mockJs.Object);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.WaitForElement(".my-selector"));
    }
}
