using Microsoft.AspNetCore.Components;

namespace Spokes_Server.Components.Shared;

/// <summary>
/// A base component class that provides a safe asynchronous execution context and automatic disposal 
/// of event subscriptions, preventing memory leaks and "Cannot access a disposed object" exceptions 
/// during SignalR circuit termination or rapid navigation.
/// </summary>
public abstract class SpokesComponentBase : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _isDisposed = false;
    protected readonly List<IDisposable> _disposables = new();

    /// <summary>
    /// Safely invokes an action on the Blazor synchronization context. 
    /// If the component has been disposed or cancellation is requested, the action is gracefully ignored.
    /// </summary>
    protected Task SafeInvokeAsync(Action action)
    {
        if (_isDisposed || _cts.IsCancellationRequested) return Task.CompletedTask;
        
        return InvokeAsync(() => 
        {
            if (_isDisposed || _cts.IsCancellationRequested) return;
            try
            {
                action();
                StateHasChanged();
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex.GetType().Name == "JSDisconnectedException")
            {
                // Gracefully ignore disconnections during render
            }
        });
    }

    /// <summary>
    /// Safely invokes an asynchronous function on the Blazor synchronization context.
    /// </summary>
    protected Task SafeInvokeAsync(Func<Task> asyncAction)
    {
        if (_isDisposed || _cts.IsCancellationRequested) return Task.CompletedTask;

        return InvokeAsync(async () => 
        {
            if (_isDisposed || _cts.IsCancellationRequested) return;
            try
            {
                await asyncAction();
                StateHasChanged();
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex.GetType().Name == "JSDisconnectedException" || ex is TaskCanceledException)
            {
                // Gracefully ignore disconnections during async render
            }
        });
    }

    public virtual ValueTask DisposeAsync()
    {
        _isDisposed = true;
        
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        // DO NOT _cts.Dispose() here to avoid ObjectDisposedExceptions when racing with background threads checking IsCancellationRequested

        foreach (var disposable in _disposables)
        {
            disposable?.Dispose();
        }
        _disposables.Clear();

        return ValueTask.CompletedTask;
    }
}
