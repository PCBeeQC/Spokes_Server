namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class DeviceSessionTests
{
    [Fact]
    public void DeviceSession_Defaults_AreSetCorrectly()
    {
        var before = DateTime.UtcNow;
        var session = new DeviceSession();
        var after = DateTime.UtcNow;

        // Id
        Assert.False(string.IsNullOrWhiteSpace(session.Id));
        Assert.True(Guid.TryParse(session.Id, out _));

        // Core fields
        Assert.Null(session.DeviceId);
        Assert.Equal(string.Empty, session.EmployeeId);
        Assert.Equal(string.Empty, session.TokenHash);
        Assert.Equal(string.Empty, session.DeviceInfo);

        // Timestamps
        Assert.InRange(session.CreatedAt, before, after);
        Assert.True(session.ExpiresAt > DateTime.UtcNow);
        Assert.InRange(session.ExpiresAt, before.AddDays(89), after.AddDays(91));
        Assert.InRange(session.LastSeenAt, before, after);
        Assert.Null(session.RevokedAt);

        // Security / Auth
        Assert.False(session.HasVaultCookie);
        Assert.Null(session.IdToken);
        Assert.Null(session.TicketData);

        // Push notifications
        Assert.Null(session.PushEndpoint);
        Assert.Equal(string.Empty, session.PushP256dh);
        Assert.Equal(string.Empty, session.PushAuth);
        Assert.Equal("WebPush", session.PushSubscriptionType);
        Assert.Equal(string.Empty, session.PushPublicKey);
        Assert.False(session.PushEnabled);
        Assert.False(session.IsIdleDetectionEnabled);
        Assert.Null(session.PushSubscribedAt);
        Assert.Equal("Desktop", session.DeviceType);
        Assert.Equal(string.Empty, session.DeviceName);
        Assert.Equal(string.Empty, session.PushUserAgent);
        Assert.False(session.IsCapacitor);

        // Computed helpers
        Assert.False(session.HasPush);
        Assert.False(session.IsCapacitorApp);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://push.example.com/v1/send/abc", true)]
    public void DeviceSession_HasPush_ReturnsExpectedValue(string? pushEndpoint, bool expected)
    {
        var session = new DeviceSession
        {
            PushEndpoint = pushEndpoint
        };

        Assert.Equal(expected, session.HasPush);
    }

    [Fact]
    public void DeviceSession_Properties_CanBeSetAndRead()
    {
        var created = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var expires = new DateTime(2025, 4, 1, 10, 0, 0, DateTimeKind.Utc);
        var lastSeen = new DateTime(2025, 1, 2, 11, 0, 0, DateTimeKind.Utc);
        var revoked = new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        var pushSubscribed = new DateTime(2025, 1, 1, 10, 30, 0, DateTimeKind.Utc);

        var session = new DeviceSession
        {
            Id = "session-custom-id",
            DeviceId = "device-uuid-123",
            EmployeeId = "emp-uuid-456",
            TokenHash = "sha256-hash-val",
            DeviceInfo = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            CreatedAt = created,
            ExpiresAt = expires,
            LastSeenAt = lastSeen,
            RevokedAt = revoked,
            HasVaultCookie = true,
            IdToken = "jwt-id-token",
            TicketData = "base64-ticket-data",
            PushEndpoint = "https://fcm.googleapis.com/fcm/send/device-key",
            PushP256dh = "p256dh-crypto-key",
            PushAuth = "auth-secret-string",
            PushSubscriptionType = "NativeRelay",
            PushPublicKey = "rsa-public-key-blob",
            PushEnabled = true,
            IsIdleDetectionEnabled = true,
            PushSubscribedAt = pushSubscribed,
            DeviceType = "Mobile",
            DeviceName = "Personal iPhone",
            PushUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)",
            IsCapacitor = true
        };

        Assert.Equal("session-custom-id", session.Id);
        Assert.Equal("device-uuid-123", session.DeviceId);
        Assert.Equal("emp-uuid-456", session.EmployeeId);
        Assert.Equal("sha256-hash-val", session.TokenHash);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", session.DeviceInfo);
        Assert.Equal(created, session.CreatedAt);
        Assert.Equal(expires, session.ExpiresAt);
        Assert.Equal(lastSeen, session.LastSeenAt);
        Assert.Equal(revoked, session.RevokedAt);
        Assert.True(session.HasVaultCookie);
        Assert.Equal("jwt-id-token", session.IdToken);
        Assert.Equal("base64-ticket-data", session.TicketData);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/device-key", session.PushEndpoint);
        Assert.Equal("p256dh-crypto-key", session.PushP256dh);
        Assert.Equal("auth-secret-string", session.PushAuth);
        Assert.Equal("NativeRelay", session.PushSubscriptionType);
        Assert.Equal("rsa-public-key-blob", session.PushPublicKey);
        Assert.True(session.PushEnabled);
        Assert.True(session.IsIdleDetectionEnabled);
        Assert.Equal(pushSubscribed, session.PushSubscribedAt);
        Assert.Equal("Mobile", session.DeviceType);
        Assert.Equal("Personal iPhone", session.DeviceName);
        Assert.Equal("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", session.PushUserAgent);
        Assert.True(session.IsCapacitor);
        Assert.True(session.HasPush);
        Assert.True(session.IsCapacitorApp);
    }

    [Theory]
    [InlineData(true, "WebPush", "Chrome on Windows", "", true)]
    [InlineData(false, "NativeRelay", "Unknown Device", "", true)]
    [InlineData(false, "WebPush", "iOS (App)", "", true)]
    [InlineData(false, "WebPush", "Android (App)", "", true)]
    [InlineData(false, "WebPush", "Unknown Device", "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0) Capacitor/6.0", true)]
    [InlineData(false, "WebPush", "Windows", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", false)]
    public void DeviceSession_IsCapacitorApp_HeuristicsMatchCorrectly(
        bool isCapacitor,
        string subscriptionType,
        string deviceInfo,
        string pushUserAgent,
        bool expected)
    {
        var session = new DeviceSession
        {
            IsCapacitor = isCapacitor,
            PushSubscriptionType = subscriptionType,
            DeviceInfo = deviceInfo,
            PushUserAgent = pushUserAgent
        };

        Assert.Equal(expected, session.IsCapacitorApp);
    }

    [Fact]
    public void DeviceSession_Equals_ReturnsTrueForReferenceEquality()
    {
        var session = new DeviceSession();
        Assert.True(session.Equals(session));
    }

    [Fact]
    public void DeviceSession_Equals_ReturnsTrueForSameId()
    {
        var id = Guid.NewGuid().ToString();
        var session1 = new DeviceSession { Id = id, DeviceName = "Session 1" };
        var session2 = new DeviceSession { Id = id, DeviceName = "Session 2" };

        Assert.True(session1.Equals(session2));
        Assert.Equal(session1.GetHashCode(), session2.GetHashCode());
    }

    [Fact]
    public void DeviceSession_Equals_ReturnsFalseForDifferentId()
    {
        var session1 = new DeviceSession { Id = "id-1" };
        var session2 = new DeviceSession { Id = "id-2" };

        Assert.False(session1.Equals(session2));
    }

    [Fact]
    public void DeviceSession_Equals_ReturnsFalseForNull()
    {
        var session = new DeviceSession();
        Assert.False(session.Equals(null));
    }

    [Fact]
    public void DeviceSession_Equals_ReturnsFalseForDifferentType()
    {
        var session = new DeviceSession();
        var otherObject = new object();

        Assert.False(session.Equals(otherObject));
        Assert.False(session.Equals("some-string"));
    }

    [Fact]
    public void DeviceSession_GetHashCode_WhenIdIsNull_DoesNotThrow()
    {
        var session = new DeviceSession { Id = null! };
        var hashCode = session.GetHashCode();
        Assert.IsType<int>(hashCode);
    }

    [Fact]
    public void DeviceSession_GetHashCode_ReturnsSameHashCodeForSameId()
    {
        var id = "unique-device-session-id";
        var session1 = new DeviceSession { Id = id };
        var session2 = new DeviceSession { Id = id };

        Assert.Equal(session1.GetHashCode(), session2.GetHashCode());
    }
}
