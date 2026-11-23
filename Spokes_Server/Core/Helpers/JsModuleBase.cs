using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// A resilient base class for ES JavaScript module interop that gracefully handles
/// transient disconnects/cancellations without permanently poisoning the cached import task.
/// Automatically resolves module paths with content-hash cache-busting via <see cref="AssetExtensions.ResolveModulePath"/>.
/// </summary>
public abstract class JsModuleBase : IAsyncDisposable
{
    private readonly IJSRuntime? _jsRuntime;
    private readonly string? _resolvedModulePath;
    private readonly object _loadLock = new();
    private Task<IJSObjectReference?>? _inFlightLoadTask;
    private IJSObjectReference? _loadedModule;
    private bool _disposed;

    public bool IsModuleLoaded => Volatile.Read(ref _loadedModule) != null && !Volatile.Read(ref _disposed);

    protected JsModuleBase(IJSRuntime? jsRuntime, string modulePath)
    {
        if (jsRuntime != null)
        {
            _jsRuntime = jsRuntime;
            _resolvedModulePath = AssetExtensions.ResolveModulePath(modulePath);
        }
    }

    /// <summary>
    /// Ensures the ES module is imported, coalescing concurrent callers onto one in-flight import
    /// and evicting any faulted/canceled load so subsequent calls cleanly retry after circuit reconnects.
    /// </summary>
    protected Task<IJSObjectReference?> EnsureModuleAsync()
    {
        if (_jsRuntime == null || Volatile.Read(ref _disposed))
        {
            return Task.FromResult<IJSObjectReference?>(null);
        }

        var loaded = Volatile.Read(ref _loadedModule);
        if (loaded != null)
        {
            return Task.FromResult<IJSObjectReference?>(loaded);
        }

        lock (_loadLock)
        {
            if (_disposed)
            {
                return Task.FromResult<IJSObjectReference?>(null);
            }

            if (_loadedModule != null)
            {
                return Task.FromResult<IJSObjectReference?>(_loadedModule);
            }

            if (_inFlightLoadTask != null && !_inFlightLoadTask.IsCompleted)
            {
                return _inFlightLoadTask;
            }

            Task<IJSObjectReference?>? loadTask = null;
            loadTask = LoadModuleCoreAsync(() => loadTask);
            if (!loadTask.IsCompleted)
            {
                _inFlightLoadTask = loadTask;
            }
            return loadTask;
        }
    }

    private async Task<IJSObjectReference?> LoadModuleCoreAsync(Func<Task<IJSObjectReference?>?> getSelfTask)
    {
        try
        {
            var module = await _jsRuntime!.InvokeAsync<IJSObjectReference>("import", _resolvedModulePath);

            bool shouldDisposeLateModule = false;
            lock (_loadLock)
            {
                if (_disposed)
                {
                    shouldDisposeLateModule = module != null;
                }
                else if (module != null)
                {
                    _loadedModule = module;
                }

                if (ReferenceEquals(_inFlightLoadTask, getSelfTask()))
                {
                    _inFlightLoadTask = null;
                }
            }

            if (shouldDisposeLateModule)
            {
                await DisposeModuleBestEffortAsync(module);
                return null;
            }

            return module;
        }
        catch
        {
            lock (_loadLock)
            {
                if (ReferenceEquals(_inFlightLoadTask, getSelfTask()))
                {
                    _inFlightLoadTask = null;
                }
            }

            throw;
        }
    }

    protected async Task SafeInvokeVoidAsync(string identifier, params object?[]? args)
    {
        if (_jsRuntime == null || Volatile.Read(ref _disposed)) return;
        try
        {
            var module = await EnsureModuleAsync();
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
        if (_jsRuntime == null || Volatile.Read(ref _disposed)) return default;
        try
        {
            var module = await EnsureModuleAsync();
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
        IJSObjectReference? moduleToDispose;
        Task<IJSObjectReference?>? pendingLoad;

        lock (_loadLock)
        {
            if (_disposed) return;
            _disposed = true;
            moduleToDispose = _loadedModule;
            _loadedModule = null;
            pendingLoad = _inFlightLoadTask;
            _inFlightLoadTask = null;
        }

        if (moduleToDispose != null)
        {
            await DisposeModuleBestEffortAsync(moduleToDispose);
        }

        if (pendingLoad != null)
        {
            try
            {
                // If the in-flight import finishes after _disposed = true, LoadModuleCoreAsync
                // handles disposing the late-arriving module reference cleanly.
                await pendingLoad;
            }
            catch
            {
                // Best-effort wait during circuit disposal
            }
        }
    }

    private static async ValueTask DisposeModuleBestEffortAsync(IJSObjectReference? module)
    {
        if (module == null) return;
        try
        {
            await module.DisposeAsync();
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
