namespace Spokes_Server.Tests.Core.Models.Core;

using Spokes_Server.Core.Models.Core;

public class PushSubscriptionTests
{
    [Fact]
    public void PushSubscription_Defaults_AreSetCorrectly()
    {
        var before = DateTime.UtcNow;
        var subscription = new PushSubscription();
        var after = DateTime.UtcNow;

        // Id
        Assert.False(string.IsNullOrWhiteSpace(subscription.Id));
        Assert.True(Guid.TryParse(subscription.Id, out _));

        // Nullable identifiers
        Assert.Null(subscription.DeviceId);
        Assert.Null(subscription.SessionId);

        // Core fields
        Assert.Equal(string.Empty, subscription.UserId);
        Assert.Equal(string.Empty, subscription.Endpoint);
        Assert.Equal(string.Empty, subscription.P256dh);
        Assert.Equal(string.Empty, subscription.Auth);
        Assert.Equal(string.Empty, subscription.UserAgent);
        Assert.Equal(string.Empty, subscription.DeviceName);
        Assert.Equal(string.Empty, subscription.PublicKey);

        // Default configurations
        Assert.Equal("Desktop", subscription.DeviceType);
        Assert.True(subscription.IsEnabled);
        Assert.True(subscription.IsIdleDetectionEnabled);
        Assert.Equal("WebPush", subscription.SubscriptionType);

        // Timestamp
        Assert.InRange(subscription.CreatedAt, before, after);
    }

    [Fact]
    public void PushSubscription_Properties_CanBeSetAndRead()
    {
        var created = new DateTime(2025, 6, 15, 8, 30, 0, DateTimeKind.Utc);

        var subscription = new PushSubscription
        {
            Id = "sub-custom-id-123",
            DeviceId = "device-abc-456",
            SessionId = "session-xyz-789",
            UserId = "user-101",
            Endpoint = "https://fcm.googleapis.com/fcm/send/test-token",
            P256dh = "test-p256dh-key",
            Auth = "test-auth-secret",
            CreatedAt = created,
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            DeviceType = "Mobile",
            DeviceName = "Pixel 8 Pro",
            IsEnabled = false,
            IsIdleDetectionEnabled = false,
            SubscriptionType = "NativeRelay",
            PublicKey = "test-public-key-material"
        };

        Assert.Equal("sub-custom-id-123", subscription.Id);
        Assert.Equal("device-abc-456", subscription.DeviceId);
        Assert.Equal("session-xyz-789", subscription.SessionId);
        Assert.Equal("user-101", subscription.UserId);
        Assert.Equal("https://fcm.googleapis.com/fcm/send/test-token", subscription.Endpoint);
        Assert.Equal("test-p256dh-key", subscription.P256dh);
        Assert.Equal("test-auth-secret", subscription.Auth);
        Assert.Equal(created, subscription.CreatedAt);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", subscription.UserAgent);
        Assert.Equal("Mobile", subscription.DeviceType);
        Assert.Equal("Pixel 8 Pro", subscription.DeviceName);
        Assert.False(subscription.IsEnabled);
        Assert.False(subscription.IsIdleDetectionEnabled);
        Assert.Equal("NativeRelay", subscription.SubscriptionType);
        Assert.Equal("test-public-key-material", subscription.PublicKey);
    }

    [Fact]
    public void PushSubscription_Equals_ReturnsTrueForReferenceEquality()
    {
        var subscription = new PushSubscription();
        Assert.True(subscription.Equals(subscription));
    }

    [Fact]
    public void PushSubscription_Equals_ReturnsTrueForSameId()
    {
        var id = Guid.NewGuid().ToString();
        var sub1 = new PushSubscription { Id = id, DeviceName = "Desktop 1" };
        var sub2 = new PushSubscription { Id = id, DeviceName = "Desktop 2" };

        Assert.True(sub1.Equals(sub2));
        Assert.Equal(sub1.GetHashCode(), sub2.GetHashCode());
    }

    [Fact]
    public void PushSubscription_Equals_ReturnsFalseForDifferentId()
    {
        var sub1 = new PushSubscription { Id = "sub-1" };
        var sub2 = new PushSubscription { Id = "sub-2" };

        Assert.False(sub1.Equals(sub2));
    }

    [Fact]
    public void PushSubscription_Equals_ReturnsFalseForNull()
    {
        var subscription = new PushSubscription();
        Assert.False(subscription.Equals(null));
    }

    [Fact]
    public void PushSubscription_Equals_ReturnsFalseForDifferentType()
    {
        var subscription = new PushSubscription();
        var other = new object();

        Assert.False(subscription.Equals(other));
        Assert.False(subscription.Equals("some-string"));
    }

    [Fact]
    public void PushSubscription_GetHashCode_WhenIdIsNull_DoesNotThrow()
    {
        var subscription = new PushSubscription { Id = null! };
        var hashCode = subscription.GetHashCode();
        Assert.IsType<int>(hashCode);
    }

    [Fact]
    public void PushSubscription_GetHashCode_ReturnsSameHashCodeForSameId()
    {
        var id = "unique-push-sub-id";
        var sub1 = new PushSubscription { Id = id };
        var sub2 = new PushSubscription { Id = id };

        Assert.Equal(sub1.GetHashCode(), sub2.GetHashCode());
    }
}
