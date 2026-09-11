using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Security;
using Xunit;

namespace Spokes_Server.Tests.Core.Security;

public class DeviceSessionTicketStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskPersistenceService _persistence;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly EmployeeRepository _employeeRepo;
    private readonly Database _db;
    private readonly ServiceProvider _serviceProvider;
    private readonly DeviceSessionTicketStore _store;

    public DeviceSessionTicketStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TicketStoreTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c["DataPath"]).Returns(_tempDir);

        var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
        _persistence = new DiskPersistenceService(mockLogger.Object);

        _sessionRepo = new DeviceSessionRepository(_persistence, mockConfig.Object);
        _employeeRepo = new EmployeeRepository(_persistence, mockConfig.Object);
        _db = new Database(_sessionRepo, _employeeRepo);

        var services = new ServiceCollection();
        services.AddScoped(_ => _db);
        _serviceProvider = services.BuildServiceProvider();

        _store = new DeviceSessionTicketStore(_serviceProvider);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _persistence.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private AuthenticationTicket CreateSampleTicket(string sessionId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Alice"),
            new Claim("SessionId", sessionId),
            new Claim("EmployeeId", "emp_1")
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        var principal = new ClaimsPrincipal(identity);
        var props = new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24)
        };

        return new AuthenticationTicket(principal, props, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task StoreAsync_WithValidTicket_StoresTicketDataAndReturnsKey()
    {
        var sessionId = "sess_store_1";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        Assert.Equal("session-" + sessionId, key);

        var saved = _sessionRepo.GetById(sessionId);
        Assert.NotNull(saved);
        Assert.NotNull(saved.TicketData);
    }

    [Fact]
    public async Task StoreAsync_MissingSessionIdClaim_ThrowsInvalidOperationException()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "Alice")
        }, CookieAuthenticationDefaults.AuthenticationScheme);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), CookieAuthenticationDefaults.AuthenticationScheme);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.StoreAsync(ticket));
    }

    [Fact]
    public async Task RetrieveAsync_ActiveSession_ReturnsTicket()
    {
        var sessionId = "sess_retrieve_active";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        var retrieved = await _store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal("Alice", retrieved.Principal.Identity?.Name);
        Assert.Equal(sessionId, retrieved.Principal.FindFirst("SessionId")?.Value);
    }

    [Fact]
    public async Task RetrieveAsync_RevokedSession_ReturnsTicketSoPrincipalCanFlagRevocation()
    {
        var sessionId = "sess_retrieve_revoked";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            RevokedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        // Crucial behavior: Revoked sessions MUST return the ticket so OnValidatePrincipal
        // can set IsRevoked: true and post-auth revocation middleware can trigger OIDC signout
        var retrieved = await _store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal(sessionId, retrieved.Principal.FindFirst("SessionId")?.Value);
    }

    [Fact]
    public async Task RetrieveAsync_ExpiredSession_ReturnsNull()
    {
        var sessionId = "sess_retrieve_expired";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        var retrieved = await _store.RetrieveAsync(key);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task RetrieveAsync_CorruptedTicketData_ReturnsNullWithoutThrowing()
    {
        var sessionId = "sess_retrieve_corrupted";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            TicketData = "not_valid_base64_ticket_data!!!",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var retrieved = await _store.RetrieveAsync("session-" + sessionId);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task RemoveAsync_ClearsTicketDataWithoutDeletingSession()
    {
        var sessionId = "sess_remove";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        await _store.RemoveAsync(key);

        var updatedSession = _sessionRepo.GetById(sessionId);
        Assert.NotNull(updatedSession);
        Assert.Null(updatedSession.TicketData);

        var retrieved = await _store.RetrieveAsync(key);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task RenewAsync_UpdatesTicketDataAndIsRetrievable()
    {
        var sessionId = "sess_renew_1";
        var session = new DeviceSession
        {
            Id = sessionId,
            EmployeeId = "emp_1",
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        _sessionRepo.Save(session);

        var ticket = CreateSampleTicket(sessionId);
        var key = await _store.StoreAsync(ticket);

        var newExpires = DateTimeOffset.UtcNow.AddDays(2);
        ticket.Properties.ExpiresUtc = newExpires;

        await _store.RenewAsync(key, ticket);

        var retrieved = await _store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal(newExpires.ToUnixTimeSeconds(), retrieved.Properties.ExpiresUtc?.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RetrieveAsync_WithNullOrEmptyKey_ReturnsNull(string? key)
    {
        var result = await _store.RetrieveAsync(key!);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveAsync_WithNullOrEmptyKey_DoesNotThrow(string? key)
    {
        await _store.RemoveAsync(key!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenewAsync_WithNullOrEmptyKey_DoesNotThrow(string? key)
    {
        var ticket = CreateSampleTicket("dummy");
        await _store.RenewAsync(key!, ticket);
    }
}
