using Microsoft.JSInterop;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Utilities;

public class SpokesDomInteropService : JsModuleBase
{
    public SpokesDomInteropService(IJSRuntime jsRuntime) 
        : base(jsRuntime, "./js/spokes-dom-observer.js")
    {
    }

    public async Task WaitForElement(string selector, int timeout = 2000)
    {
        await SafeInvokeVoidAsync("waitForElement", selector, timeout);
    }
}
