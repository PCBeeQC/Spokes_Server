using Microsoft.JSInterop;

namespace Spokes_Server.Core.Utilities;

public class SpokesDomInteropService : JsModuleBase
{
    public SpokesDomInteropService(IJSRuntime jsRuntime) 
        : base(jsRuntime, "./js/spokes-dom-observer.js")
    {
    }

    public Task WaitForElement(string selector, int timeout = 2000) =>
        SafeInvokeVoidAsync("waitForElement", selector, timeout);
}
