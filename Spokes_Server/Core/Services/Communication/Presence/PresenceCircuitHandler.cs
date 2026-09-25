using Microsoft.AspNetCore.Components.Server.Circuits;
using Spokes_Server.Core.Services.Communication;
using System.Threading;
using System.Threading.Tasks;

using Spokes_Server.Core.Services.Communication.Chat;

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
    public bool HasPushEnabled { get; set; }
    public bool IsFocused { get; set; }
    public bool IsConnected { get; set; } = true;

    public bool IsMobile => string.Equals(DeviceType, "Mobile", StringComparison.OrdinalIgnoreCase);
    public bool IsActivelyViewed => IsConnected && IsFocused;

    public event Action? OnFocusChanged;
    public void NotifyFocusChanged() => OnFocusChanged?.Invoke();

    /// <summary>
    /// Evaluates whether an in-app notification (snackbar / audio) should be displayed in this circuit.
    /// When push notifications are enabled (or on mobile devices), in-app notifications are strictly
    /// suppressed when the user is not actively viewing the app, preventing split-brain dual notifications.
    /// </summary>
    public bool ShouldDisplayInAppNotification(PresenceStateService? presenceState = null)
    {
        if (IsMobile || HasPushEnabled)
        {
            if (!IsActivelyViewed)
                return false;

            if (!string.IsNullOrEmpty(SubscriptionId) && presenceState != null)
            {
                if (!presenceState.IsConnectionActivelyViewed(SubscriptionId))
                    return false;
            }
        }

        return true;
    }
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
    private readonly UserClientStateService _clientStateService;

    public PresenceCircuitHandler(
        PresenceStateService presenceState,
        UserCircuitContext circuitContext,
        UserClientStateService clientStateService)
    {
        _presenceState = presenceState;
        _circuitContext = circuitContext;
        _clientStateService = clientStateService;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitContext.IsConnected = false;
        _circuitContext.IsFocused = false;

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

        if (!string.IsNullOrEmpty(_circuitContext.UserId))
        {
            _clientStateService.FlushToDiskIfUnsent(_circuitContext.UserId);
        }

        return base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = false;

        // Re-establish presence instantly if the websocket reconnects and we already know who they are,
        // without waiting for the Blazor UI tree to deeply render and fire JS Interop again.
        if (!string.IsNullOrEmpty(_circuitContext.UserId))
        {
            var connectionId = _circuitContext.SubscriptionId ?? circuit.Id;
            _presenceState.RegisterConnection(_circuitContext.UserId, connectionId, _circuitContext.DeviceType, _circuitContext.HasPushEnabled, _circuitContext.SessionId, false);
        }
        // Note: Since they just reconnected, we cannot guarantee focus state. 
        // The browser's visibilitychange or focus event will correct this.
        // But we typically want to err on the side of giving them push notifications 
        // if we are unsure (PresenceTier changes will handle this).

        return base.OnConnectionUpAsync(circuit, cancellationToken);
    }
}
