using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Presence;

public class PresenceStateConnectionTests
{
    private readonly VoiceStateService _voiceState;
    private readonly PresenceStateService _presenceState;

    public PresenceStateConnectionTests()
    {
        _voiceState = new VoiceStateService();
        _presenceState = new PresenceStateService(_voiceState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown-sub")]
    public void IsConnectionActivelyViewed_MissingOrNull_ReturnsFalse(string? subId)
    {
        Assert.False(_presenceState.IsConnectionActivelyViewed(subId));
    }

    [Fact]
    public void IsConnectionActivelyViewed_RegisteredUnfocused_ReturnsFalse()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user1",
            SubscriptionId = "sub-1",
            DeviceType = "Mobile",
            IsFocused = false
        });

        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_RegisteredFocused_ReturnsTrue()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user1",
            SubscriptionId = "sub-1",
            DeviceType = "Mobile",
            IsFocused = true
        });

        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_FocusChangedToFalse_ReturnsFalse()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user1",
            SubscriptionId = "sub-1",
            DeviceType = "Mobile",
            IsFocused = true
        });
        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));

        _presenceState.SetConnectionFocus("user1", "sub-1", false);
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_RemovedConnection_ReturnsFalse()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user1",
            SubscriptionId = "sub-1",
            DeviceType = "Mobile",
            IsFocused = true
        });
        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));

        _presenceState.RemoveConnection("sub-1");
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void DeadTransport_ImmediatelyResolvesToOfflineInsteadOfAway_AndIsEvictedOnSweep()
    {
        bool isAlive = true;
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-chrome",
            SubscriptionId = "sub-chrome",
            SessionId = "sess-chrome",
            DeviceType = "Desktop",
            IsFocused = false,
            IsTransportAlive = () => isAlive
        });

        _presenceState.SetConnectionLastInteraction("sub-chrome", System.DateTime.UtcNow.AddMinutes(-10));
        _presenceState.SweepStaleConnections();
        Assert.Equal(UserPresenceBadge.Away, _presenceState.GetUserPresenceBadge("user-chrome"));

        // Circuit disconnects: badge must immediately resolve to Offline (never stuck on Away)
        isAlive = false;
        Assert.False(_presenceState.HasConnection("sub-chrome"));
        Assert.False(_presenceState.HasActiveConnection("user-chrome"));
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-chrome"));
        Assert.Empty(_presenceState.GetOnlineSessions());

        // Watchdog sweep evicts the dead entry cleanly and notifies subscribers
        string? notifiedUser = null;
        _presenceState.OnUserPresenceChanged += u => notifiedUser = u;
        _presenceState.SweepStaleConnections();
        Assert.Equal("user-chrome", notifiedUser);
        Assert.Empty(_presenceState.GetActiveConnections());
    }

    [Fact]
    public void ClosingChromeTab_RemainsAwayWhileIdleElectronOpen_ThenOfflineWhenElectronCloses()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-multi",
            SubscriptionId = "sub-electron",
            SessionId = "sess-electron",
            DeviceType = "Desktop",
            IsFocused = false
        });
        _presenceState.SetConnectionPresenceState("sub-electron", ConnectionPresenceState.Idle);
        _presenceState.SetConnectionLastInteraction("sub-electron", System.DateTime.UtcNow.AddMinutes(-10));

        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-multi",
            SubscriptionId = "sub-chrome",
            SessionId = "sess-chrome",
            DeviceType = "Desktop",
            IsFocused = true
        });

        Assert.Equal(UserPresenceBadge.OnlineDesktop, _presenceState.GetUserPresenceBadge("user-multi"));

        // Closing the Chrome tab falls back to Away because Electron is still open and idle
        _presenceState.MarkConnectionOffline("sub-chrome");
        Assert.Equal(UserPresenceBadge.Away, _presenceState.GetUserPresenceBadge("user-multi"));

        // Closing the last remaining instance transitions immediately to Offline
        _presenceState.MarkConnectionOffline("sub-electron");
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-multi"));
    }

    [Fact]
    public void MarkConnectionOffline_BlocksLateNonReconnectResurrection_AllowsGenuineReconnect()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-race",
            SubscriptionId = "sub-race",
            DeviceType = "Desktop",
            IsFocused = true
        });
        Assert.Equal(UserPresenceBadge.OnlineDesktop, _presenceState.GetUserPresenceBadge("user-race"));

        // Out-of-band pagehide beacon marks connection offline
        _presenceState.MarkConnectionOffline("sub-race");
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-race"));

        // Late queued JS interop calls arriving milliseconds later must NOT resurrect the closed tab
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-race",
            SubscriptionId = "sub-race",
            DeviceType = "Desktop",
            IsFocused = true,
            IsReconnect = false
        });
        _presenceState.PingDeviceActive("sub-race");
        _presenceState.SetConnectionFocus("user-race", "sub-race", true);
        _presenceState.SetConnectionPresenceState("sub-race", ConnectionPresenceState.Idle);

        Assert.False(_presenceState.HasConnection("sub-race"));
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-race"));

        // Genuine reconnect (e.g. OnConnectionUpAsync or BFCache restore on live circuit) restores connection
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-race",
            SubscriptionId = "sub-race",
            DeviceType = "Desktop",
            IsFocused = true,
            IsReconnect = true,
            IsTransportAlive = () => true
        });
        Assert.True(_presenceState.HasConnection("sub-race"));
        Assert.Equal(UserPresenceBadge.OnlineDesktop, _presenceState.GetUserPresenceBadge("user-race"));
    }

    [Fact]
    public void DelayedHttpUnfocus_WithLowerSequenceThanForegroundFocus_IsRejectedAndKeepsOnlineMobile()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-android",
            SubscriptionId = "sub-android",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            PresenceSequence = 1
        });
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-android"));

        // Foreground resume arrives first over SignalR WebSocket (seq=3)
        _presenceState.SetConnectionFocus("user-android", "sub-android", true, sequence: 3);

        // Delayed background HTTP /unfocus (seq=2) arrives later and MUST be rejected
        var applied = _presenceState.SetConnectionFocusBySubscription("sub-android", false, sequence: 2);
        Assert.False(applied);
        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-android"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-android"));

        // Paired SignalR + HTTP unfocus with matching seq=4 applies cleanly and idempotently
        _presenceState.SetConnectionFocus("user-android", "sub-android", false, sequence: 4);
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-android"));
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-android"));
        Assert.False(_presenceState.SetConnectionFocusBySubscription("sub-android", false, sequence: 4));
    }

    [Fact]
    public void StaleIdleStateAndOfflineBeacon_WithLowerSequence_AreRejected()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-seq",
            SubscriptionId = "sub-seq",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            PresenceSequence = 5
        });

        // Stale 'away' state from an overdue background timer (seq=4 < 5) is rejected
        Assert.False(_presenceState.SetConnectionPresenceState("sub-seq", ConnectionPresenceState.Idle, sequence: 4));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-seq"));

        // Stale offline beacon (seq=4 < 5) is rejected and does NOT remove or tombstone the connection
        _presenceState.MarkConnectionOffline("sub-seq", sequence: 4);
        Assert.True(_presenceState.HasConnection("sub-seq"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-seq"));
    }

    [Fact]
    public void SequencedPagehideTombstone_BlocksOlderSeq_AllowsStrictlyNewerSeqOnLiveCircuit()
    {
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-bfcache",
            SubscriptionId = "sub-bfcache",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            PresenceSequence = 2,
            IsTransportAlive = () => true
        });

        // pagehide fires at seq=3
        _presenceState.MarkConnectionOffline("sub-bfcache", sequence: 3);
        Assert.False(_presenceState.HasConnection("sub-bfcache"));
        Assert.Equal(UserPresenceBadge.Offline, _presenceState.GetUserPresenceBadge("user-bfcache"));

        // Stale pre-pagehide interop (seq=2 <= 3) cannot resurrect
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-bfcache",
            SubscriptionId = "sub-bfcache",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            IsReconnect = false,
            PresenceSequence = 2,
            IsTransportAlive = () => true
        });
        Assert.False(_presenceState.HasConnection("sub-bfcache"));

        // Strictly newer foreground focus (seq=4 > 3) on a live circuit clears tombstone and restores OnlineMobile
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-bfcache",
            SubscriptionId = "sub-bfcache",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            IsReconnect = false,
            PresenceSequence = 4,
            IsTransportAlive = () => true
        });
        Assert.True(_presenceState.HasConnection("sub-bfcache"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-bfcache"));
    }

    [Fact]
    public void SeededOldLastSeen_DoesNotMarkFreshFocusedMobileConnectionIdle()
    {
        _presenceState.InitializeFromSessions([
            new Spokes_Server.Core.Models.Core.DeviceSession
            {
                Id = "old-mob-session",
                EmployeeId = "user-fresh-mob",
                DeviceType = "Mobile",
                LastSeenAt = System.DateTime.UtcNow.AddHours(-4)
            }
        ]);

        // Initial registration as Desktop, then upgraded to Mobile with PreserveExistingActivity
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-fresh-mob",
            SubscriptionId = "sub-fresh-mob",
            DeviceType = "Desktop",
            IsFocused = true
        });
        _presenceState.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "user-fresh-mob",
            SubscriptionId = "sub-fresh-mob",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            PreserveExistingActivity = true
        });

        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-fresh-mob"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presenceState.GetUserPresenceBadge("user-fresh-mob"));
    }
}
