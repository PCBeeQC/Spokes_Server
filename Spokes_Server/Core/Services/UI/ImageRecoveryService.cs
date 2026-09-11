using System;

namespace Spokes_Server.Core.Services.UI;

/// <summary>
/// A scoped service (one per active SignalR circuit) that coordinates UI recovery when tokens expire.
/// </summary>
public class ImageRecoveryService
{
    /// <summary>
    /// Triggered when the client detects that an image token has been wiped (e.g. by a server restart).
    /// </summary>
    public event Action? OnTokensWiped;

    public void NotifyTokensWiped()
    {
        OnTokensWiped?.Invoke();
    }
}
