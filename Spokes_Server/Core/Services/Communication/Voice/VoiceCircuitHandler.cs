using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<VoiceCircuitHandler>? _logger;

    public VoiceCircuitHandler(GlobalVoiceService globalVoiceService, ILogger<VoiceCircuitHandler>? logger = null)
    {
        _globalVoiceService = globalVoiceService;
        _logger = logger;
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
            _logger?.LogError(ex, "[VoiceCircuitHandler] Error syncing user on connection up: {Message}", ex.Message);
        }

        return base.OnConnectionUpAsync(circuit, cancellationToken);
    }
}
