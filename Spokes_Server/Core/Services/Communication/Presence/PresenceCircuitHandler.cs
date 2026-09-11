using Microsoft.AspNetCore.Components.Server.Circuits;
using Spokes_Server.Core.Services.Communication;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Services.Communication.Presence;

/// <summary>
/// A scoped service that holds the current circuit's presence correlation data,
/// populated by the UI (e.g., MainLayout) during component initialization.
/// </summary>
public class UserCircuitContext
{
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    public string? SubscriptionId { get; set; }
    public string DeviceType { get; set; } = "Desktop";
    public bool HasPushEnabled { get; set; } = false;
    public bool IsFocused { get; set; } = false;
}

/// <summary>
/// Listens to real-time SignalR circuit connectivity events to instantly update presence 
/// without waiting for the 3-minute Blazor circuit retention timeout. 
/// Mirrors Discord's immediate connection-drop detection.
/// </summary>
public class PresenceCircuitHandler : CircuitHandler
{
    private readonly PresenceStateService _presenceState;
    private readonly UserCircuitContext _circuitContext;

    public PresenceCircuitHandler(PresenceStateService presenceState, UserCircuitContext circuitContext)
    {
        _presenceState = presenceState;
        _circuitContext = circuitContext;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Immediately flag user as offline when websocket severs
        if (!string.IsNullOrEmpty(_circuitContext.SubscriptionId))
        {
            _presenceState.RemoveConnection(_circuitContext.SubscriptionId);
        }
        else
        {
            // Fallback for anonymous or very early disconnects
            _presenceState.RemoveConnection(circuit.Id);
        }

        return base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Re-establish presence instantly if the websocket reconnects and we already know who they are,
        // without waiting for the Blazor UI tree to deeply render and fire JS Interop again.
        if (!string.IsNullOrEmpty(_circuitContext.UserId))
        {
            var connectionId = _circuitContext.SubscriptionId ?? circuit.Id;
            _presenceState.RegisterConnection(_circuitContext.UserId, connectionId, _circuitContext.DeviceType, _circuitContext.HasPushEnabled, _circuitContext.SessionId, _circuitContext.IsFocused);
        }
        // Note: Since they just reconnected, we cannot guarantee focus state. 
        // The browser's visibilitychange or focus event will correct this.
        // But we typically want to err on the side of giving them push notifications 
        // if we are unsure (PresenceTier changes will handle this).

        return base.OnConnectionUpAsync(circuit, cancellationToken);
    }
}
