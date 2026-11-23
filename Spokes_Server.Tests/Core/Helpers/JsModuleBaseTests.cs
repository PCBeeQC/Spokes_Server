using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Tests.Core.Helpers;

public class JsModuleBaseTests
{
    private sealed class TestJsModule : JsModuleBase
    {
        public TestJsModule(IJSRuntime jsRuntime, string modulePath = "./testModule.js")
            : base(jsRuntime, modulePath)
        {
        }

        public Task<IJSObjectReference?> CallEnsureModuleAsync()
            => EnsureModuleAsync();

        public Task CallSafeInvokeVoidAsync(string identifier, params object?[]? args)
            => SafeInvokeVoidAsync(identifier, args);

        public Task<T?> CallSafeInvokeAsync<T>(string identifier, params object?[]? args)
            => SafeInvokeAsync<T>(identifier, args);
    }

    [Fact]
    public void Constructor_WhenInstantiated_DoesNotEvaluateModuleTask()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();

        // Act
        var module = new TestJsModule(mockJs.Object);

        // Assert
        Assert.NotNull(module);
        Assert.False(module.IsModuleLoaded);
        mockJs.Verify(js => js.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object?[]>()), Times.Never);
    }

    [Fact]
    public async Task SafeInvokeVoidAsync_SuccessfulCall_InvokesModuleMethodWithArguments()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.Is<object?[]>(args => args != null && args.Length == 1 && (string)args[0]! == "./testModule.js")))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("testMethod", It.Is<object?[]>(args => args != null && args.Length == 2 && (string)args[0]! == "hello" && (int)args[1]! == 42)))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        var module = new TestJsModule(mockJs.Object);

        // Act
        await module.CallSafeInvokeVoidAsync("testMethod", "hello", 42);

        // Assert
        Assert.NotNull(module);
        Assert.True(module.IsModuleLoaded);
        mockModule.Verify(m => m.InvokeAsync<IJSVoidResult>("testMethod", It.IsAny<object?[]>()), Times.Once);
    }

    [Fact]
    public async Task SafeInvokeVoidAsync_WhenJSDisconnectedExceptionThrown_CatchesAndSuppressesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("disconnectingMethod", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSDisconnectedException("Circuit disconnected."));

        var module = new TestJsModule(mockJs.Object);

        // Act
        var ex = await Record.ExceptionAsync(() => module.CallSafeInvokeVoidAsync("disconnectingMethod"));

        // Assert
        Assert.NotNull(module);
        Assert.Null(ex);
    }

    [Fact]
    public async Task SafeInvokeVoidAsync_WhenTaskCanceledExceptionThrown_CatchesAndSuppressesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("canceledMethod", It.IsAny<object?[]>()))
            .ThrowsAsync(new TaskCanceledException());

        var module = new TestJsModule(mockJs.Object);

        // Act
        var ex = await Record.ExceptionAsync(() => module.CallSafeInvokeVoidAsync("canceledMethod"));

        // Assert
        Assert.NotNull(module);
        Assert.Null(ex);
    }

    [Fact]
    public async Task SafeInvokeVoidAsync_WhenUnhandledExceptionThrown_PropagatesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("failingMethod", It.IsAny<object?[]>()))
            .ThrowsAsync(new InvalidOperationException("Fatal error"));

        var module = new TestJsModule(mockJs.Object);

        // Act & Assert
        Assert.NotNull(module);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.CallSafeInvokeVoidAsync("failingMethod"));
    }

    [Fact]
    public async Task SafeInvokeAsync_SuccessfulCall_ReturnsExpectedResult()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<string>("getValue", It.IsAny<object?[]>()))
            .Returns(new ValueTask<string>("expected result"));

        var module = new TestJsModule(mockJs.Object);

        // Act
        var result = await module.CallSafeInvokeAsync<string>("getValue", "param1");

        // Assert
        Assert.NotNull(mockJs);
        Assert.Equal("expected result", result);
        Assert.True(module.IsModuleLoaded);
    }

    [Fact]
    public async Task SafeInvokeAsync_SuccessfulCallWithValueType_ReturnsExpectedResult()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<int>("calculate", It.IsAny<object?[]>()))
            .Returns(new ValueTask<int>(100));

        var module = new TestJsModule(mockJs.Object);

        // Act
        var result = await module.CallSafeInvokeAsync<int>("calculate");

        // Assert
        Assert.NotNull(mockJs);
        Assert.NotNull(mockModule);
        Assert.NotNull(module);
        Assert.Equal(100, result);
    }

    [Fact]
    public async Task SafeInvokeAsync_WhenJSDisconnectedExceptionThrown_ReturnsDefault()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<string>("getString", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSDisconnectedException("Disconnected"));

        mockModule
            .Setup(m => m.InvokeAsync<int>("getInt", It.IsAny<object?[]>()))
            .ThrowsAsync(new JSDisconnectedException("Disconnected"));

        var module = new TestJsModule(mockJs.Object);

        // Act
        var stringResult = await module.CallSafeInvokeAsync<string>("getString");
        var intResult = await module.CallSafeInvokeAsync<int>("getInt");

        // Assert
        Assert.NotNull(mockJs);
        Assert.NotNull(mockModule);
        Assert.NotNull(module);
        Assert.Null(stringResult);
        Assert.Equal(0, intResult);
    }

    [Fact]
    public async Task SafeInvokeAsync_WhenTaskCanceledExceptionThrown_ReturnsDefault()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<string>("getString", It.IsAny<object?[]>()))
            .ThrowsAsync(new TaskCanceledException());

        mockModule
            .Setup(m => m.InvokeAsync<int>("getInt", It.IsAny<object?[]>()))
            .ThrowsAsync(new TaskCanceledException());

        var module = new TestJsModule(mockJs.Object);

        // Act
        var stringResult = await module.CallSafeInvokeAsync<string>("getString");
        var intResult = await module.CallSafeInvokeAsync<int>("getInt");

        // Assert
        Assert.NotNull(mockJs);
        Assert.NotNull(mockModule);
        Assert.NotNull(module);
        Assert.Null(stringResult);
        Assert.Equal(0, intResult);
    }

    [Fact]
    public async Task SafeInvokeAsync_WhenUnhandledExceptionThrown_PropagatesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<string>("failingMethod", It.IsAny<object?[]>()))
            .ThrowsAsync(new InvalidOperationException("Crash"));

        var module = new TestJsModule(mockJs.Object);

        // Act & Assert
        Assert.NotNull(module);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.CallSafeInvokeAsync<string>("failingMethod"));
    }

    [Fact]
    public async Task DisposeAsync_WhenModuleTaskNeverEvaluated_DoesNotAttemptToImportOrDispose()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        var module = new TestJsModule(mockJs.Object);

        // Act
        await module.DisposeAsync();

        // Assert
        Assert.NotNull(module);
        Assert.False(module.IsModuleLoaded);
        mockJs.Verify(js => js.InvokeAsync<IJSObjectReference>(It.IsAny<string>(), It.IsAny<object?[]>()), Times.Never);
        mockModule.Verify(m => m.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_WhenModuleTaskEvaluated_DisposesModule()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("init", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        mockModule
            .Setup(m => m.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var module = new TestJsModule(mockJs.Object);

        // Act: Evaluate module first via SafeInvokeVoidAsync
        await module.CallSafeInvokeVoidAsync("init");
        Assert.NotNull(module);
        Assert.True(module.IsModuleLoaded);

        // Act: Dispose module
        await module.DisposeAsync();

        // Assert
        Assert.False(module.IsModuleLoaded);
        mockModule.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenDisposalThrowsException_CatchesAndSuppressesException()
    {
        // Arrange
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(mockModule.Object));

        mockModule
            .Setup(m => m.InvokeAsync<IJSVoidResult>("init", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSVoidResult>(default(IJSVoidResult)!));

        mockModule
            .Setup(m => m.DisposeAsync())
            .ThrowsAsync(new InvalidOperationException("Failed to dispose"));

        var module = new TestJsModule(mockJs.Object);

        // Act: Evaluate moduleTask
        await module.CallSafeInvokeVoidAsync("init");

        // Act: Dispose module with throwing DisposeAsync
        var ex = await Record.ExceptionAsync(async () => await module.DisposeAsync());

        // Assert
        Assert.NotNull(module);
        Assert.Null(ex);
        mockModule.Verify(m => m.DisposeAsync(), Times.Once);
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("disconnected")]
    [InlineData("jsexception")]
    public async Task EnsureModuleAsync_WhenInitialImportFails_RetriesCleanlyOnNextCall(string failureKind)
    {
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();
        int importAttempts = 0;

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(() =>
            {
                importAttempts++;
                if (importAttempts == 1)
                {
                    Exception ex = failureKind switch
                    {
                        "canceled" => new TaskCanceledException("Import timed out during mobile pause"),
                        "disconnected" => new JSDisconnectedException("Circuit transiently disconnected"),
                        _ => new JSException("Failed to fetch dynamically imported module")
                    };
                    return new ValueTask<IJSObjectReference>(Task.FromException<IJSObjectReference>(ex));
                }
                return new ValueTask<IJSObjectReference>(mockModule.Object);
            });

        mockModule
            .Setup(m => m.InvokeAsync<string>("ping", It.IsAny<object?[]>()))
            .Returns(new ValueTask<string>("pong"));

        await using var module = new TestJsModule(mockJs.Object);

        if (failureKind == "jsexception")
        {
            await Assert.ThrowsAsync<JSException>(() => module.CallEnsureModuleAsync());
        }
        else
        {
            var first = await module.CallSafeInvokeAsync<string>("ping");
            Assert.Null(first);
        }

        Assert.False(module.IsModuleLoaded);
        Assert.Equal(1, importAttempts);

        // Subsequent call must retry import instead of remaining permanently poisoned
        var second = await module.CallSafeInvokeAsync<string>("ping");
        Assert.Equal("pong", second);
        Assert.True(module.IsModuleLoaded);
        Assert.Equal(2, importAttempts);
    }

    [Fact]
    public async Task EnsureModuleAsync_CoalescesConcurrentCallersOntoSingleImport()
    {
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();
        var tcs = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(() => new ValueTask<IJSObjectReference>(tcs.Task));

        await using var module = new TestJsModule(mockJs.Object);

        var t1 = module.CallEnsureModuleAsync();
        var t2 = module.CallEnsureModuleAsync();
        var t3 = module.CallEnsureModuleAsync();

        tcs.SetResult(mockModule.Object);
        var results = await Task.WhenAll(t1, t2, t3);

        Assert.All(results, r => Assert.Same(mockModule.Object, r));
        Assert.True(module.IsModuleLoaded);
        mockJs.Verify(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhileImportInFlight_DisposesLateArrivingModuleWithoutLeak()
    {
        var mockJs = new Mock<IJSRuntime>();
        var mockModule = new Mock<IJSObjectReference>();
        var tcs = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);

        mockJs
            .Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(() => new ValueTask<IJSObjectReference>(tcs.Task));
        mockModule
            .Setup(m => m.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var module = new TestJsModule(mockJs.Object);
        var importTask = module.CallEnsureModuleAsync();

        var disposeTask = module.DisposeAsync().AsTask();
        tcs.SetResult(mockModule.Object);

        var resolved = await importTask;
        await disposeTask;

        Assert.Null(resolved);
        Assert.False(module.IsModuleLoaded);
        mockModule.Verify(m => m.DisposeAsync(), Times.Once);
    }
}
