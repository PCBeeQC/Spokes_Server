using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;
using Spokes_Server.Tests.Helpers;

namespace Spokes_Server.Tests.Core.Services.Communication.Notifications.Routing;

public class PushDecisionServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly ChatStateService _chatState;
    private readonly PresenceStateService _presence;
    private readonly IPushDecisionService _service;

    public PushDecisionServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_PushDecisionTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);

        _db = TestDatabaseFactory.Create(_sessionRepo, _employeeRepo);
        _chatState = new ChatStateService();
        _presence = new PresenceStateService(_chatState, _db.CoreContext, _db.HrContext);
        _service = _presence;
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }

    private void Register(string userId, string? subscriptionId, string deviceType, bool hasPushEnabled = false, string? sessionId = null, bool isFocused = false)
    {
        _presence.RegisterConnection(new ConnectionRegistrationOptions
        {
            UserId = userId,
            SubscriptionId = subscriptionId,
            DeviceType = deviceType,
            HasPushEnabled = hasPushEnabled,
            SessionId = sessionId,
            IsFocused = isFocused
        });
    }

    [Fact]
    public void IsUserFocusedForPush_FocusedWithPush_ReturnsTrue()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: true);
        Assert.True(_service.IsUserFocusedForPush("user1"));
    }

    [Fact]
    public void IsUserFocusedForPush_FocusedWithoutPush_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: false, sessionId: null, isFocused: true);
        Assert.False(_service.IsUserFocusedForPush("user1"));
    }

    [Fact]
    public void IsUserFocusedForPush_UnfocusedWithPush_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: false);
        Assert.False(_service.IsUserFocusedForPush("user1"));
    }

    [Fact]
    public void HasActiveDesktopConnectionForPush_DesktopWithPush_ReturnsTrue()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true);
        Assert.True(_service.HasActiveDesktopConnectionForPush("user1"));
    }

    [Fact]
    public void HasActiveDesktopConnectionForPush_DesktopWithoutPush_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: false);
        Assert.False(_service.HasActiveDesktopConnectionForPush("user1"));
    }

    [Fact]
    public void HasActiveDesktopConnectionForPush_MobileWithPush_ReturnsFalse()
    {
        Register("user1", "sub1", "Mobile", hasPushEnabled: true);
        Assert.False(_service.HasActiveDesktopConnectionForPush("user1"));
    }

    [Fact]
    public void IsUserAwayFromDesktopForPush_NoDesktopConnection_ReturnsFalse()
    {
        Assert.False(_service.IsUserAwayFromDesktopForPush("user1"));
    }

    [Fact]
    public void IsUserAwayFromDesktopForPush_Focused_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: true);
        Assert.False(_service.IsUserAwayFromDesktopForPush("user1"));
    }

    [Fact]
    public void IsUserAwayFromDesktopForPush_RecentUnfocused_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: false);
        Assert.False(_service.IsUserAwayFromDesktopForPush("user1"));
    }

    [Fact]
    public void ShouldSuppressMobilePush_FocusedDesktop_ReturnsTrue()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: true);
        Assert.True(_service.ShouldSuppressMobilePush("user1"));
    }

    [Fact]
    public void ShouldSuppressMobilePush_UnfocusedDesktop_ReturnsFalse()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: false);
        Assert.False(_service.ShouldSuppressMobilePush("user1"));
    }

    [Fact]
    public void ShouldSuppressMobilePush_NoDesktop_ReturnsFalse()
    {
        Assert.False(_service.ShouldSuppressMobilePush("user1"));
    }

    [Fact]
    public void DetermineNotificationTier_FocusedWithPush_ReturnsNone()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: true);
        Assert.Equal(PresenceTier.None, _service.DetermineNotificationTier("user1"));
    }

    [Fact]
    public void DetermineNotificationTier_FocusedWithoutPush_ReturnsNone()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: false, sessionId: null, isFocused: true);
        Assert.Equal(PresenceTier.None, _service.DetermineNotificationTier("user1"));
    }

    [Fact]
    public void DetermineNotificationTier_ActiveDesktopUnfocused_ReturnsDesktopOnly()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true, sessionId: null, isFocused: false);
        Assert.Equal(PresenceTier.DesktopOnly, _service.DetermineNotificationTier("user1"));
    }

    [Fact]
    public void DetermineNotificationTier_ActiveDesktopWithoutPush_ReturnsDesktopOnly()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: false, sessionId: null, isFocused: false);
        Assert.Equal(PresenceTier.DesktopOnly, _service.DetermineNotificationTier("user1"));
    }

    [Fact]
    public void DetermineNotificationTier_Offline_ReturnsAll()
    {
        Assert.Equal(PresenceTier.All, _service.DetermineNotificationTier("user1"));
    }

    [Fact]
    public void WasRecentlySeenOnDesktopForPush_ActiveDesktop_ReturnsTrue()
    {
        Register("user1", "sub1", "Desktop", hasPushEnabled: true);
        Assert.True(_service.WasRecentlySeenOnDesktopForPush("user1"));
    }

    [Fact]
    public void WasRecentlySeenOnDesktopForPush_NoRecord_ReturnsFalse()
    {
        Assert.False(_service.WasRecentlySeenOnDesktopForPush("user1"));
    }

    [Fact]
    public void PresenceStateService_Implements_IPushDecisionService()
    {
        Assert.IsAssignableFrom<IPushDecisionService>(_presence);
    }
}
