using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Security;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Controllers;

/// <summary>
/// Receives signed webhook callbacks from the local LiveKit SFU daemon so participant joins, leaves,
/// and track mute changes reconcile server-side voice state in near real time.
/// </summary>
[ApiController]
[Route("spokesapi/voice")]
[AllowAnonymous]
[AllowDuringSetup]
public class LiveKitWebhookController : SpokesControllerBase
{
    private readonly VoiceChannelService _voiceChannelService;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ILogger<LiveKitWebhookController> _logger;

    public LiveKitWebhookController(
        VoiceChannelService voiceChannelService,
        SystemConfigRepository systemConfigs,
        ILogger<LiveKitWebhookController> logger)
    {
        _voiceChannelService = voiceChannelService ?? throw new ArgumentNullException(nameof(voiceChannelService));
        _systemConfigs = systemConfigs ?? throw new ArgumentNullException(nameof(systemConfigs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("livekit-webhook")]
    public async Task<IActionResult> HandleWebhookAsync(CancellationToken cancellationToken)
    {
        var config = _systemConfigs.Get();
        var apiKey = config.LiveKitApiKey;
        var apiSecret = config.LiveKitApiSecret;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return StatusCodeError(503, "LiveKit webhook credentials are not configured.", "LIVEKIT_NOT_CONFIGURED");
        }

        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return UnauthorizedError("Missing LiveKit webhook authorization header.");
        }

        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return BadRequestError("Empty webhook body.", "INVALID_WEBHOOK_BODY");
        }

        WebhookEvent webhookEvent;
        try
        {
            var receiver = new WebhookReceiver(apiKey, apiSecret);
            webhookEvent = receiver.Receive(body, authHeader);
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "[LiveKitWebhook] Rejected webhook with invalid JWT signature.");
            return UnauthorizedError("Invalid LiveKit webhook signature.");
        }
        catch (InvalidJsonException ex)
        {
            _logger.LogWarning(ex, "[LiveKitWebhook] Rejected malformed webhook JSON payload.");
            return BadRequestError("Malformed LiveKit webhook payload.", "INVALID_WEBHOOK_BODY");
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "[LiveKitWebhook] Unable to validate webhook due to LiveKit credential configuration.");
            return StatusCodeError(503, "LiveKit webhook verification unavailable.", "LIVEKIT_CONFIG_INVALID");
        }
        catch (Exception ex) when (ex.Message.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "[LiveKitWebhook] Rejected unauthorized webhook payload.");
            return UnauthorizedError("Invalid LiveKit webhook authorization.");
        }

        await _voiceChannelService.IngestWebhookEventAsync(webhookEvent, cancellationToken);
        return Success();
    }
}
