using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class PresenceControllerTests
{
    private readonly PresenceStateService _presence;
    private readonly Mock<IUserClientStateService> _mockClientState;
    private readonly Mock<ILogger<PresenceController>> _mockLogger;
    private readonly PresenceController _controller;

    public PresenceControllerTests()
    {
        _presence = new PresenceStateService(new ChatStateService());
        _mockClientState = new Mock<IUserClientStateService>();
        _mockLogger = new Mock<ILogger<PresenceController>>();

        _controller = new PresenceController(_presence, _mockClientState.Object, _mockLogger.Object);
    }

    [Fact]
    public void MarkOffline_RemovesConnection_WhenSubscriptionIdProvided()
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp1",
            SubscriptionId = "conn1",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            SessionId = "sess1",
            IsFocused = true
        });
        Assert.True(_presence.HasActiveConnection("emp1"));

        var result = _controller.MarkOffline("conn1");
        Assert.IsType<OkResult>(result);
        Assert.False(_presence.HasActiveConnection("emp1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MarkOffline_ReturnsOk_WhenSubscriptionIdIsNullOrEmpty(string? subscriptionId)
    {
        var result = _controller.MarkOffline(subscriptionId!);
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void Unfocus_SetsConnectionFocusFalse_AndFlushesClientState_WhenSubscriptionIdProvided()
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp1",
            SubscriptionId = "conn1",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            SessionId = "sess1",
            IsFocused = true
        });
        Assert.True(_presence.IsUserFocused("emp1"));

        var result = _controller.Unfocus("conn1");
        Assert.IsType<OkResult>(result);
        Assert.False(_presence.IsUserFocused("emp1"));
        _mockClientState.Verify(c => c.FlushToDiskIfUnsent("emp1"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unfocus_ReturnsOk_WhenSubscriptionIdIsNullOrEmpty(string? subscriptionId)
    {
        var result = _controller.Unfocus(subscriptionId!);
        Assert.IsType<OkResult>(result);
        _mockClientState.Verify(c => c.FlushToDiskIfUnsent(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Unfocus_WhenUserNotFound_SetsFocusFalseWithoutFlushing()
    {
        var result = _controller.Unfocus("unknown_conn");
        Assert.IsType<OkResult>(result);
        _mockClientState.Verify(c => c.FlushToDiskIfUnsent(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Controller_WorksWithoutOptionalDependencies()
    {
        var minimalController = new PresenceController(_presence);

        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp2",
            SubscriptionId = "conn2",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            SessionId = "sess2",
            IsFocused = true
        });

        var unfocusResult = minimalController.Unfocus("conn2");
        Assert.IsType<OkResult>(unfocusResult);
        Assert.False(_presence.IsUserFocused("emp2"));

        var offlineResult = minimalController.MarkOffline("conn2");
        Assert.IsType<OkResult>(offlineResult);
        Assert.False(_presence.HasActiveConnection("emp2"));
    }

    [Theory]
    [InlineData("away", true)]
    [InlineData("idle", true)]
    [InlineData("offline", false)]
    [InlineData("invisible", false)]
    public void SetState_AwayOrOffline_UpdatesConnectionStateAndFlushesClientState(string state, bool expectedHasActiveConnection)
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-state",
            SubscriptionId = "conn-state",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            IsFocused = true
        });

        var result = _controller.SetState("conn-state", state);

        Assert.IsType<OkResult>(result);
        Assert.Equal(expectedHasActiveConnection, _presence.HasActiveConnection("emp-state"));
        _mockClientState.Verify(c => c.FlushToDiskIfUnsent("emp-state"), Times.Once);
    }

    [Fact]
    public void SetState_Online_RestoresActivePresenceState()
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-online",
            SubscriptionId = "conn-online",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            IsFocused = true
        });

        _controller.SetState("conn-online", "away");
        Assert.True(_presence.IsGloballyAway("emp-online"));

        _controller.SetState("conn-online", "online");
        Assert.False(_presence.IsGloballyAway("emp-online"));
    }
}
