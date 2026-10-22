using Microsoft.JSInterop;
using Spokes_Server.Core.Helpers;

namespace Spokes_Server.Core.Services.UI;

public class SpokesDomInteropService : JsModuleBase
{
    public SpokesDomInteropService(IJSRuntime jsRuntime) 
        : base(jsRuntime, "./js/spokes-dom-observer.js")
    {
    }

    public Task WaitForElementAsync(string selector, int timeout = 2000) =>
        SafeInvokeVoidAsync("waitForElement", selector, timeout);
}
