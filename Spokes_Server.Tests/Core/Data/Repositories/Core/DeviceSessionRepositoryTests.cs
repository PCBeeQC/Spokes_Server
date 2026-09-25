using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Data.Repositories.Core;

public class DeviceSessionRepositoryTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _writer;
    private readonly DeviceSessionRepository _repo;

    public DeviceSessionRepositoryTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_DeviceSessions_{Guid.NewGuid()}");

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

        var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
        _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

        _repo = new DeviceSessionRepository(_writer, mockConfig.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { Console.WriteLine($"Cleanup failed: {ex.Message}"); }
        }
        _writer.Dispose();
    }

    [Fact]
    public void Constructor_WithNullDataPath_UsesDefaultPath()
    {
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns((string?)null);

        var repo = new DeviceSessionRepository(_writer, mockConfig.Object);

        Assert.NotNull(repo);
    }

    [Fact]
    public void GetByEmployeeId_ReturnsAllSessionsForEmployee()
    {
        _repo.Save(new DeviceSession { Id = "s1", EmployeeId = "emp1" });
        _repo.Save(new DeviceSession { Id = "s2", EmployeeId = "emp1" });
        _repo.Save(new DeviceSession { Id = "s3", EmployeeId = "emp2" });

        var sessions = _repo.GetByEmployeeId("emp1").ToList();
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.Id == "s1");
        Assert.Contains(sessions, s => s.Id == "s2");
    }

    [Fact]
    public void GetActiveByEmployeeId_FiltersOutRevokedAndExpired()
    {
        var now = DateTime.UtcNow;
        _repo.Save(new DeviceSession { Id = "active", EmployeeId = "emp-act", ExpiresAt = now.AddHours(2) });
        _repo.Save(new DeviceSession { Id = "revoked", EmployeeId = "emp-act", ExpiresAt = now.AddHours(2), RevokedAt = now.AddMinutes(-5) });
        _repo.Save(new DeviceSession { Id = "expired", EmployeeId = "emp-act", ExpiresAt = now.AddMinutes(-10) });

        var active = _repo.GetActiveByEmployeeId("emp-act");
        Assert.Single(active);
        Assert.Equal("active", active[0].Id);
    }

    [Fact]
    public void GetPushEnabledByEmployeeId_FiltersCorrectly()
    {
        var now = DateTime.UtcNow;
        _repo.Save(new DeviceSession
        {
            Id = "p-ios",
            EmployeeId = "emp-push",
            ExpiresAt = now.AddHours(2),
            PushEndpoint = "https://push.apple.com/1",
            PushEnabled = true,
            DeviceType = "iOS"
        });
        _repo.Save(new DeviceSession
        {
            Id = "p-web",
            EmployeeId = "emp-push",
            ExpiresAt = now.AddHours(2),
            PushEndpoint = "https://push.google.com/2",
            PushEnabled = true,
            DeviceType = "Web"
        });
        _repo.Save(new DeviceSession
        {
            Id = "p-disabled",
            EmployeeId = "emp-push",
            ExpiresAt = now.AddHours(2),
            PushEndpoint = "https://push.google.com/3",
            PushEnabled = false,
            DeviceType = "Web"
        });

        var allPush = _repo.GetPushEnabledByEmployeeId("emp-push");
        Assert.Equal(2, allPush.Count);

        var iosOnly = _repo.GetPushEnabledByEmployeeId("emp-push", "ios");
        Assert.Single(iosOnly);
        Assert.Equal("p-ios", iosOnly[0].Id);
    }

    [Fact]
    public void GetByPushEndpoint_ReturnsMatchingActiveSession()
    {
        var now = DateTime.UtcNow;
        _repo.Save(new DeviceSession
        {
            Id = "push-s1",
            PushEndpoint = "https://push.example.com/unique-ep",
            ExpiresAt = now.AddHours(1)
        });

        var found = _repo.GetByPushEndpoint("https://push.example.com/unique-ep");
        Assert.NotNull(found);
        Assert.Equal("push-s1", found.Id);

        var notFound = _repo.GetByPushEndpoint("https://push.example.com/nonexistent");
        Assert.Null(notFound);
    }

    [Fact]
    public void GetByTokenHash_And_GetActiveByTokenHash_ReturnMatchingSession()
    {
        var now = DateTime.UtcNow;
        _repo.Save(new DeviceSession
        {
            Id = "token-s1",
            TokenHash = "hash123",
            ExpiresAt = now.AddHours(1)
        });
        _repo.Save(new DeviceSession
        {
            Id = "token-s2",
            TokenHash = "hash-revoked",
            ExpiresAt = now.AddHours(1),
            RevokedAt = now.AddMinutes(-1)
        });

        Assert.NotNull(_repo.GetByTokenHash("hash123"));
        Assert.NotNull(_repo.GetActiveByTokenHash("hash123"));

        Assert.NotNull(_repo.GetByTokenHash("hash-revoked"));
        Assert.Null(_repo.GetActiveByTokenHash("hash-revoked"));
    }

    [Fact]
    public void ClearPushFields_ResetsAllPushProperties()
    {
        var session = new DeviceSession
        {
            Id = "push-clear",
            PushEndpoint = "https://endpoint.com",
            PushP256dh = "key1",
            PushAuth = "auth1",
            PushPublicKey = "pub1",
            PushEnabled = true,
            PushSubscribedAt = DateTime.UtcNow,
            PushUserAgent = "Mozilla"
        };
        _repo.Save(session);

        _repo.ClearPushFields(session);

        var retrieved = _repo.GetById("push-clear");
        Assert.NotNull(retrieved);
        Assert.Null(retrieved.PushEndpoint);
        Assert.False(retrieved.PushEnabled);
        Assert.Empty(retrieved.PushP256dh);
        Assert.Empty(retrieved.PushAuth);
        Assert.Null(retrieved.PushSubscribedAt);
    }

    [Fact]
    public void GetFilePath_SavesInDeviceSessionsFolder()
    {
        var session = new DeviceSession { Id = "sess-disk" };
        _repo.Save(session);
        _writer.FlushAll();

        var expectedPath = Path.Combine(_testDataDir, "DeviceSessions", "sess-disk.json");
        Assert.True(File.Exists(expectedPath));
    }
}
