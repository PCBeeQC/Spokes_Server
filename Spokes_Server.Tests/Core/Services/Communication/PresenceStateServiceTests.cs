using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication;

public class PresenceStateServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly ChatStateService _chatState;
    private readonly PresenceStateService _service;

    public PresenceStateServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "Spokes_PresenceTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);

        _db = new Database(_sessionRepo, _employeeRepo);
        _chatState = new ChatStateService();
        _service = new PresenceStateService(_chatState, _db);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void InitializeFromSessions_PopulatesLastSeen_ResolvesMaxTimestamp()
    {
        var now = DateTime.UtcNow;
        var sessions = new List<DeviceSession>
        {
            // User 1 has two Desktop sessions and one Mobile session
            new() { Id = "s1", EmployeeId = "user-1", DeviceType = "Desktop", LastSeenAt = now.AddHours(-2) },
            new() { Id = "s2", EmployeeId = "user-1", DeviceType = "Desktop", LastSeenAt = now.AddHours(-1) },
            new() { Id = "s3", EmployeeId = "user-1", DeviceType = "Mobile", LastSeenAt = now.AddMinutes(-30) },

            // User 2 has one Desktop session
            new() { Id = "s4", EmployeeId = "user-2", DeviceType = "Desktop", LastSeenAt = now.AddDays(-1) }
        };

        _service.InitializeFromSessions(sessions);

        // User 1 overall last seen should be the latest across all devices (Mobile: 30 mins ago)
        Assert.Equal(now.AddMinutes(-30), _service.GetLastSeen("user-1"));

        // User 1 Desktop last seen should be the latest desktop session (1 hour ago)
        Assert.Equal(now.AddHours(-1), _service.GetLastSeenOnDesktop("user-1"));

        // User 2 overall last seen should be 1 day ago
        Assert.Equal(now.AddDays(-1), _service.GetLastSeen("user-2"));

        // User 3 has no sessions -> null
        Assert.Null(_service.GetLastSeen("user-3"));
    }

    [Fact]
    public void InitializeFromSessions_HandlesPushEnabled()
    {
        var now = DateTime.UtcNow;
        var sessions = new List<DeviceSession>
        {
            new() { Id = "s1", EmployeeId = "user-1", DeviceType = "Desktop", LastSeenAt = now.AddMinutes(-10), PushEndpoint = "https://push.example.com", PushEnabled = true },
            new() { Id = "s2", EmployeeId = "user-2", DeviceType = "Desktop", LastSeenAt = now.AddMinutes(-10), PushEndpoint = null, PushEnabled = false }
        };

        _service.InitializeFromSessions(sessions);

        Assert.Equal(now.AddMinutes(-10), _service.GetLastSeenOnDesktopForPush("user-1"));
        Assert.Null(_service.GetLastSeenOnDesktopForPush("user-2"));
    }

    [Fact]
    public void FormatLastSeen_ReturnsCleanAndAccurateStrings()
    {
        var now = DateTime.UtcNow;

        Assert.Equal("Offline", PresenceStateService.FormatLastSeen(null));
        Assert.Equal("Offline", PresenceStateService.FormatLastSeen(default(DateTime)));

        Assert.Equal("Last seen just now", PresenceStateService.FormatLastSeen(now.AddSeconds(-15)));
        Assert.Equal("Last seen 1 minute ago", PresenceStateService.FormatLastSeen(now.AddMinutes(-1)));
        Assert.Equal("Last seen 25 minutes ago", PresenceStateService.FormatLastSeen(now.AddMinutes(-25)));
        Assert.Equal("Last seen 1 hour ago", PresenceStateService.FormatLastSeen(now.AddHours(-1)));
        Assert.Equal("Last seen 4 hours ago", PresenceStateService.FormatLastSeen(now.AddHours(-4)));
        Assert.Equal("Last seen yesterday", PresenceStateService.FormatLastSeen(now.AddHours(-30)));
        Assert.Equal("Last seen 3 days ago", PresenceStateService.FormatLastSeen(now.AddDays(-3)));

        var currentYearDate = new DateTime(DateTime.Now.Year, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        if (DateTime.UtcNow - currentYearDate > TimeSpan.FromDays(7))
        {
            Assert.Equal($"Last seen {currentYearDate.ToLocalTime():MMM d}", PresenceStateService.FormatLastSeen(currentYearDate));
        }

        var pastYearDate = new DateTime(2020, 5, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal($"Last seen {pastYearDate.ToLocalTime():MMM d, yyyy}", PresenceStateService.FormatLastSeen(pastYearDate));
    }

    [Fact]
    public void RemoveConnection_TouchesSessionInDatabase()
    {
        var session = new DeviceSession
        {
            Id = "session-test-1",
            EmployeeId = "user-1",
            DeviceType = "Desktop",
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10)
        };
        _sessionRepo.Save(session);

        _service.RegisterConnection("user-1", "sub-1", "Desktop", false, "session-test-1", true);
        _service.RemoveConnection("sub-1");

        var updated = _sessionRepo.GetById("session-test-1");
        Assert.NotNull(updated);
        Assert.True(updated.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void RemoveConnection_ThrottlesSessionTouchWithinOneMinute()
    {
        var recentTime = DateTime.UtcNow.AddSeconds(-20);
        var session = new DeviceSession
        {
            Id = "session-test-2",
            EmployeeId = "user-1",
            DeviceType = "Desktop",
            LastSeenAt = recentTime
        };
        _sessionRepo.Save(session);

        _service.RegisterConnection("user-1", "sub-2", "Desktop", false, "session-test-2", true);
        _service.RemoveConnection("sub-2");

        var updated = _sessionRepo.GetById("session-test-2");
        Assert.NotNull(updated);
        Assert.Equal(recentTime, updated.LastSeenAt);
    }

    [Fact]
    public void FlushActiveSessionsToDatabase_SavesAllActiveSessions()
    {
        var session1 = new DeviceSession
        {
            Id = "s-active-1",
            EmployeeId = "user-1",
            LastSeenAt = DateTime.UtcNow.AddHours(-1)
        };
        var session2 = new DeviceSession
        {
            Id = "s-active-2",
            EmployeeId = "user-2",
            LastSeenAt = DateTime.UtcNow.AddHours(-2)
        };
        _sessionRepo.Save(session1);
        _sessionRepo.Save(session2);

        _service.RegisterConnection("user-1", "sub-1", "Desktop", false, "s-active-1", true);
        _service.RegisterConnection("user-2", "sub-2", "Desktop", false, "s-active-2", true);

        _service.FlushActiveSessionsToDatabase();

        var updated1 = _sessionRepo.GetById("s-active-1");
        var updated2 = _sessionRepo.GetById("s-active-2");

        Assert.True(updated1.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.True(updated2.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void PingDeviceActive_UpdatesBothGeneralAndPushLastSeen()
    {
        var now = DateTime.UtcNow;
        var session = new DeviceSession
        {
            Id = "session-idle-1",
            EmployeeId = "user-idle",
            DeviceType = "Desktop",
            LastSeenAt = now.AddMinutes(-10),
            PushEnabled = true,
            PushEndpoint = "https://push.example.com/endpoint"
        };
        _sessionRepo.Save(session);

        _service.RegisterConnection("user-idle", "sub-idle-1", "Desktop", hasPushEnabled: true, sessionId: "session-idle-1", isFocused: false);

        // Call PingDeviceActive (simulating idle detector active heartbeat)
        _service.PingDeviceActive("sub-idle-1");

        var desktopLastSeen = _service.GetLastSeenOnDesktop("user-idle");
        var pushLastSeen = _service.GetLastSeenOnDesktopForPush("user-idle");

        Assert.NotNull(desktopLastSeen);
        Assert.NotNull(pushLastSeen);
        Assert.True(desktopLastSeen.Value > now.AddSeconds(-2));
        Assert.True(pushLastSeen.Value > now.AddSeconds(-2));

        // When recently pinged within grace period (even while unfocused), user is NOT away from desktop
        Assert.False(_service.IsUserAwayFromDesktopForPush("user-idle"));
    }

    [Fact]
    public void IsUserAwayFromDesktopForPush_RespectsNinetySecondGracePeriod()
    {
        var session = new DeviceSession
        {
            Id = "session-grace-1",
            EmployeeId = "user-grace",
            DeviceType = "Desktop",
            LastSeenAt = DateTime.UtcNow,
            PushEnabled = true,
            PushEndpoint = "https://push.example.com/endpoint"
        };
        _sessionRepo.Save(session);

        _service.RegisterConnection("user-grace", "sub-grace-1", "Desktop", hasPushEnabled: true, sessionId: "session-grace-1", isFocused: false);

        // Case 1: Just pinged (0s ago) -> NOT away
        _service.PingDeviceActive("sub-grace-1");
        Assert.False(_service.IsUserAwayFromDesktopForPush("user-grace"));

        // Case 2: Connection is focused -> Never away for push
        _service.SetConnectionFocus("user-grace", "sub-grace-1", true);
        Assert.False(_service.IsUserAwayFromDesktopForPush("user-grace"));

        // Case 3: When connection is removed -> Not away for push (HasActiveDesktopConnectionForPush is false)
        _service.RemoveConnection("sub-grace-1");
        Assert.False(_service.IsUserAwayFromDesktopForPush("user-grace"));
    }
}
