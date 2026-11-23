using System;
using System.Collections.Generic;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Controllers;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class PresenceControllerTests
{
    private readonly PresenceStateService _presence;
    private readonly Mock<IUserClientStateService> _mockClientState;
    private readonly Mock<ILogger<PresenceController>> _mockLogger;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly PresenceController _controller;

    public PresenceControllerTests()
    {
        _presence = new PresenceStateService(new VoiceStateService());
        _mockClientState = new Mock<IUserClientStateService>();
        _mockLogger = new Mock<ILogger<PresenceController>>();

        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _dataProtection = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();

        _controller = new PresenceController(_presence, _mockClientState.Object, _mockLogger.Object, _dataProtection)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateSameOriginHttpContext("emp1")
            }
        };
    }

    private static DefaultHttpContext CreateSameOriginHttpContext(string? authenticatedEmployeeId = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("spokes.example.com");
        ctx.Request.Headers["Origin"] = "https://spokes.example.com";

        if (!string.IsNullOrEmpty(authenticatedEmployeeId))
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("EmployeeId", authenticatedEmployeeId)
            ], "Cookies"));
        }

        return ctx;
    }

    [Fact]
    public void MarkOffline_RemovesConnection_WhenAuthenticatedOwnerCallsEndpoint()
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
    public void Unfocus_SetsConnectionFocusFalse_AndFlushesClientState_WhenAuthenticatedOwnerCallsEndpoint()
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
        _mockClientState.Verify(c => c.StopActiveTyping("emp1"), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unfocus_ReturnsOk_WhenSubscriptionIdIsNullOrEmpty(string? subscriptionId)
    {
        var result = _controller.Unfocus(subscriptionId!);
        Assert.IsType<OkResult>(result);
        _mockClientState.Verify(c => c.StopActiveTyping(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Unfocus_WhenUserNotFound_AuthenticatedUserSucceedsWithoutFlushing()
    {
        var result = _controller.Unfocus("unknown_conn");
        Assert.IsType<OkResult>(result);
        _mockClientState.Verify(c => c.StopActiveTyping(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Controller_WorksWithoutOptionalDependencies_WhenAuthenticatedOwner()
    {
        var minimalController = new PresenceController(_presence)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateSameOriginHttpContext("emp2")
            }
        };

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
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext("emp-state");
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
        _mockClientState.Verify(c => c.StopActiveTyping("emp-state"), Times.Once);
    }

    [Fact]
    public void SetState_Online_RestoresActivePresenceState()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext("emp-online");
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

    [Fact]
    public void AnonymousRequest_WithoutPresenceToken_Returns403AndPreservesState()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext(authenticatedEmployeeId: null);
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-victim",
            SubscriptionId = "conn-victim",
            DeviceType = "Desktop",
            HasPushEnabled = true,
            IsFocused = true
        });

        var unfocus = Assert.IsType<ObjectResult>(_controller.Unfocus("conn-victim"));
        Assert.Equal(StatusCodes.Status403Forbidden, unfocus.StatusCode);
        Assert.True(_presence.IsUserFocused("emp-victim"));

        var setState = Assert.IsType<ObjectResult>(_controller.SetState("conn-victim", "offline"));
        Assert.Equal(StatusCodes.Status403Forbidden, setState.StatusCode);
        Assert.True(_presence.HasActiveConnection("emp-victim"));

        var offline = Assert.IsType<ObjectResult>(_controller.MarkOffline("conn-victim"));
        Assert.Equal(StatusCodes.Status403Forbidden, offline.StatusCode);
        Assert.True(_presence.HasActiveConnection("emp-victim"));
    }

    [Fact]
    public void AnonymousRequest_WithValidPresenceToken_SucceedsForBeaconTeardown()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext(authenticatedEmployeeId: null);
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-ios",
            SubscriptionId = "conn-ios",
            DeviceType = "Mobile",
            HasPushEnabled = true,
            IsFocused = true
        });

        var token = PresenceController.CreatePresenceToken(_dataProtection, "conn-ios", "emp-ios");

        var unfocusResult = _controller.Unfocus(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-ios",
            PresenceToken = token
        });
        Assert.IsType<OkResult>(unfocusResult);
        Assert.False(_presence.IsUserFocused("emp-ios"));

        var offlineResult = _controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-ios",
            PresenceToken = token
        });
        Assert.IsType<OkResult>(offlineResult);
        Assert.False(_presence.HasActiveConnection("emp-ios"));
    }

    [Fact]
    public void AnonymousRequest_WithMismatchedOrExpiredPresenceToken_Returns403()
    {
        var baseNow = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var mockTime = new Mock<TimeProvider>();
        mockTime.Setup(t => t.GetUtcNow()).Returns(baseNow);

        var controller = new PresenceController(_presence, _mockClientState.Object, _mockLogger.Object, _dataProtection, timeProvider: mockTime.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = CreateSameOriginHttpContext(authenticatedEmployeeId: null)
            }
        };

        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-1",
            SubscriptionId = "conn-1",
            DeviceType = "Desktop",
            IsFocused = true
        });

        // Wrong subscriptionId
        var wrongSubToken = PresenceController.CreatePresenceToken(_dataProtection, "conn-other", "emp-1", TimeSpan.FromMinutes(10), mockTime.Object);
        var wrongSubRes = Assert.IsType<ObjectResult>(controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-1",
            PresenceToken = wrongSubToken
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, wrongSubRes.StatusCode);

        // Expired token
        var shortToken = PresenceController.CreatePresenceToken(_dataProtection, "conn-1", "emp-1", TimeSpan.FromMinutes(5), mockTime.Object);
        mockTime.Setup(t => t.GetUtcNow()).Returns(baseNow.AddMinutes(6));
        var expiredRes = Assert.IsType<ObjectResult>(controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-1",
            PresenceToken = shortToken
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, expiredRes.StatusCode);
        Assert.True(_presence.HasActiveConnection("emp-1"));
    }

    [Fact]
    public void AuthenticatedUser_TargetingOtherUsersSubscription_Returns403()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext("emp-attacker");
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-owner",
            SubscriptionId = "conn-owner",
            DeviceType = "Desktop",
            IsFocused = true
        });

        var res = Assert.IsType<ObjectResult>(_controller.MarkOffline("conn-owner"));
        Assert.Equal(StatusCodes.Status403Forbidden, res.StatusCode);
        Assert.True(_presence.HasActiveConnection("emp-owner"));
    }

    [Fact]
    public void BannedUser_EvenWithValidPresenceToken_Returns403()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Spokes_PresenceTest_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = tempDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"}).Build();
            using var writer = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var employees = new EmployeeRepository(writer, cfg);
            employees.Save(new Employee { Id = "emp-banned", IsActive = true, IsBanned = true });

            var controller = new PresenceController(_presence, _mockClientState.Object, _mockLogger.Object, _dataProtection, employees: employees)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = CreateSameOriginHttpContext(authenticatedEmployeeId: null)
                }
            };

            _presence.RegisterConnection(new ConnectionRegistrationOptions
            {
                UserId = "emp-banned",
                SubscriptionId = "conn-banned",
                DeviceType = "Desktop",
                IsFocused = true
            });

            var token = PresenceController.CreatePresenceToken(_dataProtection, "conn-banned", "emp-banned");
            var res = Assert.IsType<ObjectResult>(controller.SetState(body: new PresenceController.PresenceUpdateRequest
            {
                SubscriptionId = "conn-banned",
                State = "dnd",
                PresenceToken = token
            }));
            Assert.Equal(StatusCodes.Status403Forbidden, res.StatusCode);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void CrossOriginOrMissingOriginHeaders_Returns403()
    {
        var noOriginCtx = new DefaultHttpContext();
        noOriginCtx.Request.Scheme = "https";
        noOriginCtx.Request.Host = new HostString("spokes.example.com");
        _controller.ControllerContext.HttpContext = noOriginCtx;

        var res = Assert.IsType<ObjectResult>(_controller.MarkOffline("conn1"));
        Assert.Equal(StatusCodes.Status403Forbidden, res.StatusCode);
    }

    [Fact]
    public void WarmServerSwitch_UnfocusesInsteadOfRemovingConnection_PreservingMobileBadgeOnReturn()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext("emp-mobile");
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-mobile",
            SubscriptionId = "conn-mobile",
            SessionId = "sess-mobile",
            DeviceType = Spokes_Server.Core.Models.Core.DeviceType.Mobile,
            IsCapacitorApp = true,
            HasPushEnabled = true,
            IsFocused = true
        });

        Assert.Equal(UserPresenceBadge.OnlineMobile, _presence.GetUserPresenceBadge("emp-mobile"));
        Assert.Equal(PresenceTier.None, _presence.DetermineNotificationTier("emp-mobile"));

        // Switching from Server 1 to Server 2 in the warm native pool calls Unfocus (not MarkOffline)
        var unfocusRes = _controller.Unfocus("conn-mobile");
        Assert.IsType<OkResult>(unfocusRes);

        Assert.True(_presence.HasConnection("conn-mobile"));
        Assert.False(_presence.IsUserFocused("emp-mobile"));
        Assert.Equal(PresenceTier.All, _presence.DetermineNotificationTier("emp-mobile"));

        // Switching back to Server 1 restores focus and OnlineMobile badge immediately
        _presence.SetConnectionFocus("emp-mobile", "conn-mobile", true);
        Assert.True(_presence.IsUserFocused("emp-mobile"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presence.GetUserPresenceBadge("emp-mobile"));
        Assert.Equal(PresenceTier.None, _presence.DetermineNotificationTier("emp-mobile"));
    }

    [Fact]
    public void RegisterConnection_WhenUpgradingPrematureDesktopConnectionToMobile_RemovesStaleDesktopStateAndShowsOnlineMobile()
    {
        // 1. Initial pre-JS registration misclassified as Desktop
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-upgrade",
            SubscriptionId = "sub-upgrade",
            SessionId = "sess-upgrade",
            DeviceType = Spokes_Server.Core.Models.Core.DeviceType.Desktop,
            IsCapacitorApp = false,
            HasPushEnabled = true,
            IsFocused = true
        });

        // 2. Authoritative JS layout initialization upgrades the connection to Mobile/Capacitor
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-upgrade",
            SubscriptionId = "sub-upgrade",
            SessionId = "sess-upgrade",
            DeviceType = Spokes_Server.Core.Models.Core.DeviceType.Mobile,
            IsCapacitorApp = true,
            HasPushEnabled = true,
            IsFocused = true
        });

        Assert.False(_presence.HasActiveDesktopConnection("emp-upgrade"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presence.GetUserPresenceBadge("emp-upgrade"));
        Assert.Equal(PresenceTier.None, _presence.DetermineNotificationTier("emp-upgrade"));
    }

    [Fact]
    public void MarkOffline_WithValidPresenceToken_SucceedsBehindTlsProxyAndLocalhostWhenPublicUrlDiffers()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Spokes_PresenceProxyTest_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = tempDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"}).Build();
            using var writer = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
            var systemConfigs = new Spokes_Server.Core.Data.Repositories.Core.SystemConfigRepository(writer, cfg);
            var sysCfg = systemConfigs.Get();
            sysCfg.ServerPublicUrl = "https://spokes.example.com";
            systemConfigs.Save(sysCfg);

            // Case 1: Accessing via http://localhost:5000 while ServerPublicUrl is https://spokes.example.com
            var localhostCtx = new DefaultHttpContext();
            localhostCtx.Request.Scheme = "http";
            localhostCtx.Request.Host = new HostString("localhost", 5000);
            localhostCtx.Request.Headers["Origin"] = "http://localhost:5000";
            localhostCtx.Request.Headers["Sec-Fetch-Site"] = "same-origin";

            var controller = new PresenceController(_presence, _mockClientState.Object, _mockLogger.Object, _dataProtection, systemConfigs: systemConfigs)
            {
                ControllerContext = new ControllerContext { HttpContext = localhostCtx }
            };

            _presence.RegisterConnection(new ConnectionRegistrationOptions
            {
                UserId = "emp-local",
                SubscriptionId = "sub-local",
                DeviceType = "Desktop",
                IsFocused = false
            });
            _presence.SetConnectionPresenceState("sub-local", ConnectionPresenceState.Idle);
            _presence.SetConnectionLastInteraction("sub-local", DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(UserPresenceBadge.Away, _presence.GetUserPresenceBadge("emp-local"));

            var localToken = PresenceController.CreatePresenceToken(_dataProtection, "sub-local", "emp-local");
            var localRes = controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
            {
                SubscriptionId = "sub-local",
                PresenceToken = localToken
            });

            Assert.IsType<OkResult>(localRes);
            Assert.Equal(UserPresenceBadge.Offline, _presence.GetUserPresenceBadge("emp-local"));

            // Case 2: TLS-terminating reverse proxy forwarding https://spokes.example.com -> http://spokes.example.com without X-Forwarded-Proto
            var proxyCtx = new DefaultHttpContext();
            proxyCtx.Request.Scheme = "http";
            proxyCtx.Request.Host = new HostString("spokes.example.com");
            proxyCtx.Request.Headers["Origin"] = "https://spokes.example.com";
            proxyCtx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
            controller.ControllerContext = new ControllerContext { HttpContext = proxyCtx };

            _presence.RegisterConnection(new ConnectionRegistrationOptions
            {
                UserId = "emp-proxy",
                SubscriptionId = "sub-proxy",
                DeviceType = "Desktop",
                IsFocused = false
            });
            _presence.SetConnectionPresenceState("sub-proxy", ConnectionPresenceState.Idle);
            _presence.SetConnectionLastInteraction("sub-proxy", DateTime.UtcNow.AddMinutes(-10));
            Assert.Equal(UserPresenceBadge.Away, _presence.GetUserPresenceBadge("emp-proxy"));

            var proxyToken = PresenceController.CreatePresenceToken(_dataProtection, "sub-proxy", "emp-proxy");
            var proxyRes = controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
            {
                SubscriptionId = "sub-proxy",
                PresenceToken = proxyToken
            });

            Assert.IsType<OkResult>(proxyRes);
            Assert.Equal(UserPresenceBadge.Offline, _presence.GetUserPresenceBadge("emp-proxy"));
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void MarkOffline_EvenWithValidPresenceToken_RejectsCrossSiteAndForeignOrigins()
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-sec",
            SubscriptionId = "sub-sec",
            DeviceType = "Desktop",
            IsFocused = true
        });
        var token = PresenceController.CreatePresenceToken(_dataProtection, "sub-sec", "emp-sec");

        // Foreign Origin
        var evilCtx = new DefaultHttpContext();
        evilCtx.Request.Scheme = "https";
        evilCtx.Request.Host = new HostString("spokes.example.com");
        evilCtx.Request.Headers["Origin"] = "https://evil.example.net";
        _controller.ControllerContext = new ControllerContext { HttpContext = evilCtx };

        var evilRes = Assert.IsType<ObjectResult>(_controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "sub-sec",
            PresenceToken = token
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, evilRes.StatusCode);

        // Sec-Fetch-Site: cross-site
        var crossSiteCtx = new DefaultHttpContext();
        crossSiteCtx.Request.Scheme = "https";
        crossSiteCtx.Request.Host = new HostString("spokes.example.com");
        crossSiteCtx.Request.Headers["Origin"] = "https://spokes.example.com";
        crossSiteCtx.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        _controller.ControllerContext = new ControllerContext { HttpContext = crossSiteCtx };

        var crossRes = Assert.IsType<ObjectResult>(_controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "sub-sec",
            PresenceToken = token
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, crossRes.StatusCode);
        Assert.True(_presence.HasActiveConnection("emp-sec"));
    }

    [Fact]
    public void DelayedUnfocusAndAwayState_WithOlderSequence_AreIgnoredAndDoNotStopTyping()
    {
        _controller.ControllerContext.HttpContext = CreateSameOriginHttpContext("emp-race");
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = "emp-race",
            SubscriptionId = "conn-race",
            DeviceType = "Mobile",
            IsCapacitorApp = true,
            IsFocused = true,
            PresenceSequence = 10
        });

        // Stale HTTP /unfocus (seq=9 < 10) must be ignored without clearing focus or typing
        var unfocusRes = _controller.Unfocus(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-race",
            Sequence = 9
        });
        Assert.IsType<OkResult>(unfocusRes);
        Assert.True(_presence.IsConnectionActivelyViewed("conn-race"));
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presence.GetUserPresenceBadge("emp-race"));

        // Stale HTTP /state=away (seq=9 < 10) must also be ignored
        var stateRes = _controller.SetState(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-race",
            State = "away",
            Sequence = 9
        });
        Assert.IsType<OkResult>(stateRes);
        Assert.Equal(UserPresenceBadge.OnlineMobile, _presence.GetUserPresenceBadge("emp-race"));

        // Stale HTTP /offline (seq=9 < 10) must also be ignored
        var offlineRes = _controller.MarkOffline(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-race",
            Sequence = 9
        });
        Assert.IsType<OkResult>(offlineRes);
        Assert.True(_presence.HasConnection("conn-race"));
        _mockClientState.Verify(c => c.StopActiveTyping("emp-race"), Times.Never);

        // Newer HTTP /unfocus (seq=11 > 10) applies normally and stops typing
        var newerUnfocusRes = _controller.Unfocus(body: new PresenceController.PresenceUpdateRequest
        {
            SubscriptionId = "conn-race",
            Sequence = 11
        });
        Assert.IsType<OkResult>(newerUnfocusRes);
        Assert.False(_presence.IsConnectionActivelyViewed("conn-race"));
        Assert.Equal(UserPresenceBadge.Offline, _presence.GetUserPresenceBadge("emp-race"));
        _mockClientState.Verify(c => c.StopActiveTyping("emp-race"), Times.Once);
    }
}
