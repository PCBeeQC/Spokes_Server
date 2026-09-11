using Microsoft.AspNetCore.Components.Server.Circuits;
using Spokes_Server.Core.Services;

namespace Spokes_Server.Core.Services.Communication.Voice;

/// <summary>
/// Handles Blazor Server circuit (websocket) drops to instantly remove users
/// from voice channels when they close their browser tab or lose internet connection.
/// Because circuits are retained for 12 hours (PWA backgrounding), DisposeAsync 
/// is not sufficient to clear voice presence instantly.
/// </summary>
public class VoiceCircuitHandler : CircuitHandler
{
    private readonly GlobalVoiceService _globalVoiceService;

    public VoiceCircuitHandler(GlobalVoiceService globalVoiceService)
    {
        _globalVoiceService = globalVoiceService;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // We DO NOT instantly disconnect the user here anymore. 
        // Backgrounded tabs (like iOS Safari or Chrome throttled tabs) will drop the SignalR connection,
        // but their WebRTC stream is exempt from background throttling and continues flawlessly!
        // We let the LiveKit Watchdog (VoiceStateSyncService) manage their real presence.
        return base.OnConnectionDownAsync(circuit, cancellationToken);
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // When the circuit wakes up (user returns to the tab after a long sleep),
        // we must check if the Watchdog kicked them while they were offline.
        try
        {
            if (_globalVoiceService.IsInVoiceCall)
            {
                // To avoid circuit handler deadlocks caused by JS interop during reconnection,
                // we only flag the service here. The actual verification will run safely
                // during the GlobalVoiceBar component's OnAfterRenderAsync lifecycle.
                _globalVoiceService.MarkForIntegrityCheck();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceCircuitHandler] Error syncing user on connection up: {ex.Message}");
        }

        return base.OnConnectionUpAsync(circuit, cancellationToken);
    }
}
