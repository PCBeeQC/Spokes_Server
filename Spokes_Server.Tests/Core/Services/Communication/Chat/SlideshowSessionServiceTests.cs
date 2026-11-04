namespace Spokes_Server.Tests.Core.Services.Communication.Chat;

using System;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Services.Communication.Chat;
using Xunit;

public class SlideshowSessionServiceTests
{
    private readonly SlideshowSessionService _service;

    public SlideshowSessionServiceTests()
    {
        _service = new SlideshowSessionService();
    }

    [Fact]
    public void CreateSession_GeneratesValid6DigitPinAndSessionId()
    {
        var session = _service.CreateSession("tv-conn-1");

        Assert.NotNull(session);
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
        Assert.Equal(6, session.PairingPin.Length);
        Assert.True(int.TryParse(session.PairingPin, out _));
        Assert.Equal("tv-conn-1", session.TvConnectionId);
        Assert.False(session.IsPaired);
    }

    [Theory]
    [InlineData("967 - 054", "967054")]
    [InlineData("967-054", "967054")]
    [InlineData("967054", "967054")]
    [InlineData("967 054", "967054")]
    [InlineData("  967 - 054  ", "967054")]
    public void NormalizePin_AcceptsDashesSpacesAndPlainDigits(string input, string expected)
    {
        var normalized = SlideshowSessionService.NormalizePin(input);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("967 - 054")]
    [InlineData("967-054")]
    [InlineData("967054")]
    [InlineData("967 054")]
    public void TryPair_SucceedsWithAnyPinFormat(string inputPinFormat)
    {
        var session = _service.CreateSession("tv-conn-1");
        // Pin is 6 digits e.g. "967054"
        string pin = session.PairingPin;
        // Inject same formatting pattern with actual pin digits
        string formattedInput = inputPinFormat.Replace("967", pin[..3]).Replace("054", pin[3..]);

        bool paired = _service.TryPair(formattedInput, "user-1", "host-conn-1", "album-1", out var pairedSession, out var error);

        Assert.True(paired);
        Assert.Null(error);
        Assert.NotNull(pairedSession);
        Assert.Equal(session.SessionId, pairedSession.SessionId);
    }

    [Fact]
    public void TryPair_Succeeds_WithValidPin_AndConsumesPin()
    {
        var session = _service.CreateSession("tv-conn-1");
        string pin = session.PairingPin;

        bool paired = _service.TryPair(pin, "user-1", "host-conn-1", "album-1", out var pairedSession, out var error);

        Assert.True(paired);
        Assert.Null(error);
        Assert.NotNull(pairedSession);
        Assert.True(pairedSession.IsPaired);
        Assert.Equal("user-1", pairedSession.HostUserId);
        Assert.Equal("host-conn-1", pairedSession.HostConnectionId);
        Assert.Equal("album-1", pairedSession.AlbumId);

        // PIN should be consumed (subsequent pairing with same PIN fails)
        bool pairedAgain = _service.TryPair(pin, "user-2", "host-conn-2", "album-1", out _, out var error2);
        Assert.False(pairedAgain);
        Assert.NotNull(error2);
    }

    [Fact]
    public void ValidateHostCommand_AcceptsHost_RejectsOthers()
    {
        var session = _service.CreateSession("tv-conn-1");
        _service.TryPair(session.PairingPin, "user-1", "host-conn-1", "album-1", out _, out _);

        bool hostValid = _service.ValidateHostCommand(session.SessionId, "host-conn-1", out var validSession);
        bool attackerValid = _service.ValidateHostCommand(session.SessionId, "attacker-conn", out _);

        Assert.True(hostValid);
        Assert.NotNull(validSession);
        Assert.False(attackerValid);
    }

    [Fact]
    public void NotifySlideChanged_FiresEvent_AndUpdatesCurrentIndex()
    {
        var session = _service.CreateSession("tv-conn-1");
        _service.TryPair(session.PairingPin, "user-1", "host-conn-1", "album-1", out _, out _);

        int receivedIndex = -1;
        string? receivedSessionId = null;

        _service.OnSlideChanged += (sId, idx) =>
        {
            receivedSessionId = sId;
            receivedIndex = idx;
        };

        _service.NotifySlideChanged(session.SessionId, 4);

        Assert.Equal(session.SessionId, receivedSessionId);
        Assert.Equal(4, receivedIndex);

        var retrieved = _service.GetSessionById(session.SessionId);
        Assert.NotNull(retrieved);
        Assert.Equal(4, retrieved.CurrentIndex);
    }

    [Fact]
    public void EndSession_RemovesSession_AndFiresEvent()
    {
        var session = _service.CreateSession("tv-conn-1");
        string sessionId = session.SessionId;
        bool eventFired = false;

        _service.OnSessionEnded += (sId) =>
        {
            if (sId == sessionId) eventFired = true;
        };

        _service.EndSession(sessionId);

        Assert.True(eventFired);
        Assert.Null(_service.GetSessionById(sessionId));
        Assert.Null(_service.GetSessionByPin(session.PairingPin));
    }

    [Fact]
    public void CreateDirectSession_InstantlyPairsWithoutPin_AndAttachesBridge()
    {
        var session = _service.CreateDirectSession("user-mirror", "host-mirror-conn", "album-mirror", null);

        Assert.NotNull(session);
        Assert.True(session.IsPaired);
        Assert.Equal("user-mirror", session.HostUserId);
        Assert.Equal("host-mirror-conn", session.HostConnectionId);
        Assert.Equal("album-mirror", session.AlbumId);
        Assert.Empty(session.PairingPin);

        var retrieved = _service.GetSessionById(session.SessionId);
        Assert.NotNull(retrieved);
        Assert.Equal(session.SessionId, retrieved.SessionId);
        Assert.True(retrieved.IsPaired);
    }

    [Fact]
    public void NotifySessionStarted_CachesStagePayloadOnSession()
    {
        var session = _service.CreateDirectSession("user-1", "host-conn-1", "album-1", null);
        var payload = new SlideshowStagePayload
        {
            SessionId = session.SessionId,
            AlbumId = "album-1",
            AlbumTitle = "Vacation Photos",
            InitialIndex = 2,
            Items =
            [
                new SlideshowMediaItem { Id = "m1", FileName = "pic1.jpg" },
                new SlideshowMediaItem { Id = "m2", FileName = "pic2.jpg" },
                new SlideshowMediaItem { Id = "m3", FileName = "pic3.jpg" }
            ]
        };

        _service.NotifySessionStarted(session.SessionId, payload);

        var retrieved = _service.GetSessionById(session.SessionId);
        Assert.NotNull(retrieved);
        Assert.NotNull(retrieved.StagePayload);
        Assert.Equal("Vacation Photos", retrieved.StagePayload.AlbumTitle);
        Assert.Equal(2, retrieved.CurrentIndex);
        Assert.Equal(3, retrieved.StagePayload.Items.Count);
    }

    [Fact]
    public void HandleDisconnect_UnpairedTv_EndsSessionImmediately()
    {
        var session = _service.CreateSession("tv-screen-conn");
        string sessionId = session.SessionId;

        var disconnected = _service.HandleDisconnect("tv-screen-conn");

        Assert.NotNull(disconnected);
        Assert.Equal(sessionId, disconnected.SessionId);
        Assert.Null(_service.GetSessionById(sessionId));
    }

    [Fact]
    public void HandleDisconnect_PairedTv_PreservesSessionAndReturnsNull()
    {
        var session = _service.CreateSession("tv-screen-conn");
        _service.TryPair(session.PairingPin, "user-1", "host-conn-1", "album-1", out _, out _);

        var disconnected = _service.HandleDisconnect("tv-screen-conn");

        // Should return null so hub doesn't broadcast SessionEnded to host
        Assert.Null(disconnected);

        // Session should remain active and paired
        var activeSession = _service.GetSessionById(session.SessionId);
        Assert.NotNull(activeSession);
        Assert.True(activeSession.IsPaired);
        Assert.Equal(string.Empty, activeSession.TvConnectionId);
    }

    [Fact]
    public void HandleDisconnect_Host_EndsPairedSessionImmediately()
    {
        var session = _service.CreateSession("tv-screen-conn");
        _service.TryPair(session.PairingPin, "user-1", "host-conn-1", "album-1", out _, out _);

        var disconnected = _service.HandleDisconnect("host-conn-1");

        Assert.NotNull(disconnected);
        Assert.Equal(session.SessionId, disconnected.SessionId);
        Assert.Null(_service.GetSessionById(session.SessionId));
    }

    [Fact]
    public void RebindTvConnection_SucceedsForDisconnectedCastReceiverWithSecret_FailsOtherwise()
    {
        var session = _service.CreateSession("tv-screen-old", SlideshowDisplayKind.SignalRCastReceiver);
        Assert.False(string.IsNullOrWhiteSpace(session.TvSecret));

        // Fails while unpaired
        Assert.False(_service.RebindTvConnection(session.SessionId, "tv-screen-new", session.TvSecret));

        // Pair session
        _service.TryPair(session.PairingPin, "user-1", "host-conn-1", "album-1", out _, out _);

        // Fails while original TV connection is still active (prevents hijack before disconnect)
        Assert.False(_service.RebindTvConnection(session.SessionId, "tv-screen-new", session.TvSecret));

        // Disconnect TV
        _service.HandleDisconnect("tv-screen-old");

        // Fails with wrong or missing secret
        Assert.False(_service.RebindTvConnection(session.SessionId, "tv-screen-new", "wrong-secret"));
        Assert.False(_service.RebindTvConnection(session.SessionId, "tv-screen-new", null));

        // Rebind succeeds with valid TvSecret
        Assert.True(_service.RebindTvConnection(session.SessionId, "tv-screen-new", session.TvSecret));

        var updated = _service.GetSessionById(session.SessionId);
        Assert.NotNull(updated);
        Assert.Equal("tv-screen-new", updated.TvConnectionId);

        // Fails for BlazorPinStage even after disconnect and with valid TvSecret
        var blazorSession = _service.CreateSession("blazor-tv-old", SlideshowDisplayKind.BlazorPinStage);
        _service.TryPair(blazorSession.PairingPin, "user-1", "host-conn-2", "album-1", out _, out _);
        _service.HandleDisconnect("blazor-tv-old");
        Assert.False(_service.RebindTvConnection(blazorSession.SessionId, "blazor-tv-new", blazorSession.TvSecret));

        // Fails for unknown session
        Assert.False(_service.RebindTvConnection("non-existent", "tv-screen-new", session.TvSecret));
    }

    [Fact]
    public void TryPair_PerUserRateLimit_DoesNotWipeWaitingTvSessionsForOtherUsers()
    {
        var waitingTv = _service.CreateSession("tv-waiting", SlideshowDisplayKind.BlazorPinStage, "10.0.0.10");

        // Attacker makes 5 wrong PIN guesses
        for (int i = 0; i < 5; i++)
        {
            bool ok = _service.TryPair("000000", "attacker-user", "attacker-conn", "album-1", out _, out _);
            Assert.False(ok);
        }

        // 6th attempt by attacker is rate-limited even with the real PIN
        bool attackerSixth = _service.TryPair(waitingTv.PairingPin, "attacker-user", "attacker-conn", "album-1", out _, out var rateLimitErr);
        Assert.False(attackerSixth);
        Assert.Contains("Too many failed pairing attempts", rateLimitErr);

        // Waiting TV session was NOT wiped; legitimate presenter pairs immediately
        bool legitPair = _service.TryPair(waitingTv.PairingPin, "legit-host", "legit-conn", "album-1", out var paired, out var legitErr);
        Assert.True(legitPair);
        Assert.Null(legitErr);
        Assert.NotNull(paired);
        Assert.Equal(waitingTv.SessionId, paired.SessionId);
    }

    [Fact]
    public void CreateSession_EnforcesPerIpUnpairedCap_WithoutEvictingOtherIps()
    {
        var otherIpTv = _service.CreateSession("tv-other-ip", SlideshowDisplayKind.BlazorPinStage, "192.168.1.50");

        for (int i = 0; i < 5; i++)
        {
            _service.CreateSession($"tv-flood-{i}", SlideshowDisplayKind.BlazorPinStage, "192.168.1.99");
        }

        Assert.Throws<InvalidOperationException>(() =>
            _service.CreateSession("tv-flood-overflow", SlideshowDisplayKind.BlazorPinStage, "192.168.1.99"));

        // Other IP's waiting TV is untouched and another IP can still create a session
        Assert.NotNull(_service.GetSessionById(otherIpTv.SessionId));
        var thirdIpTv = _service.CreateSession("tv-third-ip", SlideshowDisplayKind.BlazorPinStage, "192.168.1.51");
        Assert.NotNull(thirdIpTv);
    }

    [Fact]
    public void DirectStageBinding_AllowsSingleCircuitWithValidSecret_AndGuardsStageOwnerEndSession()
    {
        var direct = _service.CreateDirectSession("host-1", "host-conn-1", "album-1");
        Assert.Equal(SlideshowDisplayKind.DirectExternalDisplay, direct.DisplayKind);
        Assert.False(string.IsNullOrWhiteSpace(direct.TvSecret));

        // Fails with missing or invalid secret
        Assert.False(_service.TryBindDirectStageCircuit(direct.SessionId, null, out _, out _));
        Assert.False(_service.TryBindDirectStageCircuit(direct.SessionId, "wrong-secret", out _, out _));

        // First stage circuit binds successfully
        Assert.True(_service.TryBindDirectStageCircuit(direct.SessionId, direct.TvSecret, out var stageToken, out var boundSession));
        Assert.False(string.IsNullOrEmpty(stageToken));
        Assert.NotNull(boundSession);

        // Second concurrent stage circuit is rejected even with valid secret
        Assert.False(_service.TryBindDirectStageCircuit(direct.SessionId, direct.TvSecret, out _, out _));

        // Non-owner stage Dispose cannot end the active direct presentation
        Assert.False(_service.EndSessionIfStageOwner(direct.SessionId, null, null));
        Assert.False(_service.EndSessionIfStageOwner(direct.SessionId, null, "forged-stage-token"));
        Assert.NotNull(_service.GetSessionById(direct.SessionId));

        // Verified bound stage circuit ends the session cleanly on Dispose
        Assert.True(_service.EndSessionIfStageOwner(direct.SessionId, null, stageToken));
        Assert.Null(_service.GetSessionById(direct.SessionId));
    }

    [Fact]
    public void SlideshowMediaItem_ThumbnailUrl_FallsBackToUrlWhenNotSet()
    {
        var item = new SlideshowMediaItem
        {
            Id = "media-1",
            FileName = "photo.jpg",
            Url = "/spokesapi/slideshow/session-1/media/media-1"
        };

        // When ThumbnailUrl is not set, it should fall back to Url
        Assert.Equal("/spokesapi/slideshow/session-1/media/media-1", item.ThumbnailUrl);

        // When explicitly set, it should return the thumbnail URL
        item.ThumbnailUrl = "/spokesapi/slideshow/session-1/media/media-1/thumb";
        Assert.Equal("/spokesapi/slideshow/session-1/media/media-1/thumb", item.ThumbnailUrl);

        // When set to empty, it should fall back to Url again
        item.ThumbnailUrl = string.Empty;
        Assert.Equal("/spokesapi/slideshow/session-1/media/media-1", item.ThumbnailUrl);
    }

    [Fact]
    public void NotifyVideoControlUpdated_DispatchesAllParametersIncludingLooping()
    {
        var session = _service.CreateSession("tv-screen-conn");
        string? receivedSessionId = null;
        bool? receivedIsPlaying = null;
        double? receivedCurrentTime = null;
        bool? receivedIsMuted = null;
        bool? receivedIsLooping = null;

        _service.OnVideoControlUpdated += (sId, playing, time, muted, looping) =>
        {
            receivedSessionId = sId;
            receivedIsPlaying = playing;
            receivedCurrentTime = time;
            receivedIsMuted = muted;
            receivedIsLooping = looping;
        };

        _service.NotifyVideoControlUpdated(session.SessionId, isPlaying: true, currentTime: 42.5, isMuted: false, isLooping: true);

        Assert.Equal(session.SessionId, receivedSessionId);
        Assert.True(receivedIsPlaying);
        Assert.Equal(42.5, receivedCurrentTime);
        Assert.False(receivedIsMuted);
        Assert.True(receivedIsLooping);
    }

    [Theory]
    [InlineData("image/jpeg", 1080, 1920, true, true)]
    [InlineData("image/png", 1920, 1080, false, false)]
    [InlineData("video/mp4", 1080, 1920, true, false)]
    [InlineData("video/quicktime", 720, 1280, true, false)]
    public void SlideshowMediaItem_IsPortraitPhoto_OnlyTrueForStillImages(string contentType, int width, int height, bool expectedIsPortrait, bool expectedIsPortraitPhoto)
    {
        var item = new SlideshowMediaItem
        {
            ContentType = contentType,
            ImageWidth = width,
            ImageHeight = height
        };

        Assert.Equal(expectedIsPortrait, item.IsPortrait);
        Assert.Equal(expectedIsPortraitPhoto, item.IsPortraitPhoto);
    }
}

