using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Presence;

public class PresenceStateConnectionTests
{
    private readonly ChatStateService _chatState;
    private readonly PresenceStateService _presenceState;

    public PresenceStateConnectionTests()
    {
        _chatState = new ChatStateService();
        _presenceState = new PresenceStateService(_chatState);
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
        _presenceState.RegisterConnection("user1", "sub-1", "Mobile", isFocused: false);

        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_RegisteredFocused_ReturnsTrue()
    {
        _presenceState.RegisterConnection("user1", "sub-1", "Mobile", isFocused: true);

        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_FocusChangedToFalse_ReturnsFalse()
    {
        _presenceState.RegisterConnection("user1", "sub-1", "Mobile", isFocused: true);
        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));

        _presenceState.SetConnectionFocus("user1", "sub-1", false);
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Fact]
    public void IsConnectionActivelyViewed_RemovedConnection_ReturnsFalse()
    {
        _presenceState.RegisterConnection("user1", "sub-1", "Mobile", isFocused: true);
        Assert.True(_presenceState.IsConnectionActivelyViewed("sub-1"));

        _presenceState.RemoveConnection("sub-1");
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-1"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("unknown", false)]
    public void IsConnectionMobile_MissingOrNull_ReturnsFalse(string? subId, bool expected)
    {
        Assert.Equal(expected, _presenceState.IsConnectionMobile(subId));
    }

    [Fact]
    public void IsConnectionMobile_DesktopConnection_ReturnsFalse()
    {
        _presenceState.RegisterConnection("user1", "sub-desk", "Desktop");

        Assert.False(_presenceState.IsConnectionMobile("sub-desk"));
    }

    [Theory]
    [InlineData("Mobile")]
    [InlineData("mobile")]
    [InlineData("MOBILE")]
    public void IsConnectionMobile_MobileConnection_ReturnsTrue(string deviceType)
    {
        _presenceState.RegisterConnection("user1", "sub-mob", deviceType);

        Assert.True(_presenceState.IsConnectionMobile("sub-mob"));
    }
}
