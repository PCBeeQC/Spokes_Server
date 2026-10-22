using Microsoft.JSInterop;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// A safe base class for JavaScript module interop that gracefully handles 
/// JSDisconnectedException when navigating away during an active interop call.
/// </summary>
public abstract class JsModuleBase : IAsyncDisposable
{
    protected readonly Lazy<Task<IJSObjectReference>>? moduleTask;

    protected JsModuleBase(IJSRuntime? jsRuntime, string modulePath)
    {
        if (jsRuntime != null)
        {
            moduleTask = new Lazy<Task<IJSObjectReference>>(() => jsRuntime.InvokeAsync<IJSObjectReference>("import", modulePath).AsTask());
        }
    }

    protected async Task SafeInvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (moduleTask == null) return;
        try
        {
            var module = await moduleTask.Value;
            if (module != null)
            {
                await module.InvokeVoidAsync(identifier, args);
            }
        }
        catch (JSDisconnectedException)
        {
            // Expected lifecycle behavior: client disconnected before JS call completed
        }
        catch (TaskCanceledException)
        {
            // Expected lifecycle behavior: interop operation was canceled
        }
    }

    protected async Task<T?> SafeInvokeAsync<T>(string identifier, params object?[]? args)
    {
        if (moduleTask == null) return default;
        try
        {
            var module = await moduleTask.Value;
            if (module != null)
            {
                return await module.InvokeAsync<T>(identifier, args);
            }
            return default;
        }
        catch (JSDisconnectedException)
        {
            // Expected lifecycle behavior: client disconnected before JS call completed
            return default;
        }
        catch (TaskCanceledException)
        {
            // Expected lifecycle behavior: interop operation was canceled
            return default;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (moduleTask != null && moduleTask.IsValueCreated)
        {
            try
            {
                var module = await moduleTask.Value;
                if (module != null)
                {
                    await module.DisposeAsync();
                }
            }
            catch (JSDisconnectedException)
            {
                // Expected lifecycle behavior: client disconnected before JS module disposed
            }
            catch (TaskCanceledException)
            {
                // Expected lifecycle behavior: interop operation was canceled
            }
            catch (Exception)
            {
                // Best-effort module cleanup when circuit is disposed
            }
        }
    }
}
