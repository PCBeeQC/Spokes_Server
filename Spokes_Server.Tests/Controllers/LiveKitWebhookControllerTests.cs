using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class LiveKitWebhookControllerTests : IDisposable
{
    private const string ApiKey = "livekit-test-key";
    private const string ApiSecret = "0123456789abcdef0123456789abcdef";

    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly VoiceStateService _voiceState;
    private readonly ChatStateService _chatState;
    private readonly VoiceChannelService _voiceChannelService;
    private readonly LiveKitWebhookController _controller;

    public LiveKitWebhookControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Spokes_LKWebhook_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _tempDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"})
            .Build();

        _persistence = new DiskPersistenceService(Mock.Of<ILogger<DiskPersistenceService>>());
        _systemConfigs = new SystemConfigRepository(_persistence, config);
        var sysCfg = _systemConfigs.Get();
        sysCfg.LiveKitApiKey = ApiKey;
        sysCfg.LiveKitApiSecret = ApiSecret;
        _systemConfigs.Save(sysCfg);

        _voiceState = new VoiceStateService();
        _chatState = new ChatStateService();
        var companies = new CompanyProfileRepository(_persistence, config);
        var messages = new ChatMessageRepository(_persistence, config, companies);
        var notifications = new Mock<INotificationRoutingService>();
        var liveKit = new LiveKitService(Mock.Of<ILogger<LiveKitService>>(), config);
        var employees = new EmployeeRepository(_persistence, config);
        var channels = new ChatChannelRepository(_persistence, config);
        var readStates = new ChatReadStateRepository(_persistence, config, companies);

        _voiceChannelService = new VoiceChannelService(
            _voiceState,
            _chatState,
            messages,
            notifications.Object,
            _systemConfigs,
            liveKit,
            Mock.Of<ILogger<VoiceChannelService>>(),
            employees,
            channels,
            readStates);

        _controller = new LiveKitWebhookController(
            _voiceChannelService,
            _systemConfigs,
            Mock.Of<ILogger<LiveKitWebhookController>>());
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private void SetWebhookHttpRequest(string body, string? authHeader)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.ContentType = "application/webhook+json";
        if (authHeader != null)
        {
            ctx.Request.Headers.Authorization = authHeader;
        }
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private static string CreateSignedWebhookToken(string body, string key = ApiKey, string secret = ApiSecret)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        var sha256B64 = Convert.ToBase64String(hashBytes);
        var handler = new JwtSecurityTokenHandler();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = key,
            Subject = new ClaimsIdentity([new Claim("sha256", sha256B64)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = creds
        };
        var token = handler.CreateToken(descriptor);
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task HandleWebhookAsync_MissingAuthorizationHeader_Returns401()
    {
        SetWebhookHttpRequest("{\"event\":\"participant_joined\"}", null);
        var result = await _controller.HandleWebhookAsync(CancellationToken.None);
        var obj = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, obj.StatusCode);
    }

    [Fact]
    public async Task HandleWebhookAsync_InvalidSignature_Returns401()
    {
        var body = "{\"event\":\"participant_joined\",\"room\":{\"name\":\"channel-ch-1\"}}";
        var badToken = CreateSignedWebhookToken(body, ApiKey, "wrong-secret-0123456789abcdef0123456789");
        SetWebhookHttpRequest(body, badToken);

        var result = await _controller.HandleWebhookAsync(CancellationToken.None);
        var obj = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, obj.StatusCode);
    }

    [Fact]
    public async Task HandleWebhookAsync_ValidSignedWebhook_UpdatesJoinMuteAndRespectsSessionFence()
    {
        var metaV1 = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-1",
            DeviceId = "dev-1",
            DeviceName = "PC"
        }.ToJson();
        var metaV2 = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-2",
            DeviceId = "dev-1",
            DeviceName = "PC"
        }.ToJson();

        // 1. participant_joined restores user-1 and device into ChatStateService
        var joinedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "participant_joined",
            room = new { name = "channel-ch-webhook" },
            participant = new
            {
                identity = "user-1",
                sid = "PA_1",
                metadata = metaV1,
                state = 2 // ACTIVE
            }
        });
        SetWebhookHttpRequest(joinedJson, CreateSignedWebhookToken(joinedJson));
        var joinRes = await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.IsType<OkObjectResult>(joinRes);
        Assert.Contains("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));
        Assert.Equal("dev-1", _voiceState.GetVoiceParticipantDevice("ch-webhook", "user-1")?.DeviceId);

        // 2. track_unmuted clears stuck muted state
        _voiceState.SetUserMuted("ch-webhook", "user-1", true);
        var unmutedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "track_unmuted",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_1", metadata = metaV1, state = 2 },
            track = new { sid = "TR_1", type = 0, source = 2, muted = false } // AUDIO + MICROPHONE
        });
        SetWebhookHttpRequest(unmutedJson, CreateSignedWebhookToken(unmutedJson));
        var unmuteRes = await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.IsType<OkObjectResult>(unmuteRes);
        Assert.False(_voiceState.IsUserMuted("ch-webhook", "user-1"));

        // 2b. track_published with muted=false clears muted state even when participant.tracks is omitted
        _voiceState.SetUserMuted("ch-webhook", "user-1", true);
        var trackPublishedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "track_published",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_1", metadata = metaV1, state = 2 },
            track = new { sid = "TR_2", type = 0, source = 2, muted = false }
        });
        SetWebhookHttpRequest(trackPublishedJson, CreateSignedWebhookToken(trackPublishedJson));
        var pubRes = await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.IsType<OkObjectResult>(pubRes);
        Assert.False(_voiceState.IsUserMuted("ch-webhook", "user-1"));

        // 3. User quickly reconnects with sess-2, then stale sess-1 participant_left webhook arrives late -> IGNORED!
        await _voiceChannelService.JoinVoiceAsync("ch-webhook", "user-1", "dev-1", "PC", "sess-2");
        var staleLeftJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "participant_left",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_1", metadata = metaV1, state = 3 }
        });
        SetWebhookHttpRequest(staleLeftJson, CreateSignedWebhookToken(staleLeftJson));
        await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.Contains("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));

        // 4. Current sess-2 participant_left webhook removes user cleanly
        var currentLeftJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "participant_left",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_2", metadata = metaV2, state = 3 }
        });
        SetWebhookHttpRequest(currentLeftJson, CreateSignedWebhookToken(currentLeftJson));
        await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));

        // 5. Late track_unpublished or participant_joined for tombstoned sess-2 must NOT resurrect user-1
        var lateTrackUnpublishedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "track_unpublished",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_2", metadata = metaV2, state = 2 },
            track = new { sid = "TR_2", type = 0, source = 2, muted = true }
        });
        SetWebhookHttpRequest(lateTrackUnpublishedJson, CreateSignedWebhookToken(lateTrackUnpublishedJson));
        await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));

        var lateJoinedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "participant_joined",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_2", metadata = metaV2, state = 2 }
        });
        SetWebhookHttpRequest(lateJoinedJson, CreateSignedWebhookToken(lateJoinedJson));
        await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));

        // 6. Full LiveKit ICE/PeerConnection reconnect using the same JWT (sess-2) but a new participant SID (PA_3)
        // must reopen the webhook-closed session and restore user-1 into ChatStateService.
        var reconnectedJoinedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "participant_joined",
            room = new { name = "channel-ch-webhook" },
            participant = new { identity = "user-1", sid = "PA_3", metadata = metaV2, state = 2 }
        });
        SetWebhookHttpRequest(reconnectedJoinedJson, CreateSignedWebhookToken(reconnectedJoinedJson));
        await _controller.HandleWebhookAsync(CancellationToken.None);
        Assert.Contains("user-1", _voiceState.GetVoiceParticipants("ch-webhook"));
        Assert.False(_voiceState.IsVoiceSessionClosed("sess-2"));
    }
}
