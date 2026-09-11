using Microsoft.JSInterop;

namespace Spokes_Server.Core.Utilities;

/// <summary>
/// A safe base class for JavaScript module interop that gracefully handles 
/// JSDisconnectedException when navigating away during an active interop call.
/// </summary>
public abstract class JsModuleBase : IAsyncDisposable
{
    protected readonly Lazy<Task<IJSObjectReference>> moduleTask;

    protected JsModuleBase(IJSRuntime jsRuntime, string modulePath)
    {
        moduleTask = new Lazy<Task<IJSObjectReference>>(() => jsRuntime.InvokeAsync<IJSObjectReference>("import", modulePath).AsTask());
    }

    protected async Task SafeInvokeVoidAsync(string identifier, params object?[]? args)
    {
        try
        {
            var module = await moduleTask.Value;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* Ignored as component is likely destroyed */ }
        catch (TaskCanceledException) { /* Ignored */ }
    }

    protected async Task<T?> SafeInvokeAsync<T>(string identifier, params object?[]? args)
    {
        try
        {
            var module = await moduleTask.Value;
            return await module.InvokeAsync<T>(identifier, args);
        }
        catch (JSDisconnectedException) { return default; }
        catch (TaskCanceledException) { return default; }
    }

    public async ValueTask DisposeAsync()
    {
        if (moduleTask.IsValueCreated)
        {
            try
            {
                var module = await moduleTask.Value;
                await module.DisposeAsync();
            }
            catch { /* Ignored during disposal */ }
        }
    }
}
