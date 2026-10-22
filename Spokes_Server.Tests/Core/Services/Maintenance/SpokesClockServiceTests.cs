using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Maintenance.Time;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class SpokesClockServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CompanyProfileRepository _companyRepo;
    private readonly Mock<ILogger<SpokesClockService>> _mockLogger;

    public SpokesClockServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SpokesClockTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var persistence = new DiskPersistenceService(NullLogger<DiskPersistenceService>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = _tempDir })
            .Build();
        _companyRepo = new CompanyProfileRepository(persistence, config);
        _mockLogger = new Mock<ILogger<SpokesClockService>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UtcNow_WhenHostTimeSource_ReturnsSystemUtcWithZeroOffset()
    {
        var profile = new CompanyProfile
        {
            Time = new TimeConfig
            {
                TimeSource = TimeConfig.SourceHost,
                LastDriftOffsetSeconds = 50.0 // should be ignored when Source is Host
            }
        };
        _companyRepo.Save(profile);

        var clock = new SpokesClockService(_companyRepo, _mockLogger.Object);

        Assert.Equal(TimeSpan.Zero, clock.ClockOffset);
        var diff = Math.Abs((clock.UtcNow - DateTime.UtcNow).TotalSeconds);
        Assert.True(diff < 2);
    }

    [Fact]
    public void UtcNow_WhenNtpTimeSource_AppliesClockOffset()
    {
        var profile = new CompanyProfile
        {
            Time = new TimeConfig
            {
                TimeSource = TimeConfig.SourceNtp,
                LastDriftOffsetSeconds = 120.0
            }
        };
        _companyRepo.Save(profile);

        var clock = new SpokesClockService(_companyRepo, _mockLogger.Object);

        Assert.Equal(TimeSpan.FromSeconds(120.0), clock.ClockOffset);
        var expected = DateTime.UtcNow.AddSeconds(120.0);
        var diff = Math.Abs((clock.UtcNow - expected).TotalSeconds);
        Assert.True(diff < 2);
    }

    [Fact]
    public void TimeZoneHelper_UsesSpokesClock_ForCurrentTime()
    {
        var profile = new CompanyProfile
        {
            TimeZoneId = "UTC",
            Time = new TimeConfig
            {
                TimeSource = TimeConfig.SourceNtp,
                LastDriftOffsetSeconds = 60.0
            }
        };
        _companyRepo.Save(profile);

        var clock = new SpokesClockService(_companyRepo, _mockLogger.Object);

        var companyTime = TimeZoneHelper.GetCompanyTime(_companyRepo);
        var expected = clock.UtcNow;
        var diff = Math.Abs((companyTime - expected).TotalSeconds);
        Assert.True(diff < 2);
    }

    [Fact]
    public async Task NtpClient_EmptyHost_ReturnsErrorResultGracefully()
    {
        var client = new NtpClient();
        var result = await client.QueryAsync("");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task NtpClient_InvalidHost_ReturnsErrorResultGracefully()
    {
        var client = new NtpClient();
        var result = await client.QueryAsync("invalid.hostname.that.does.not.exist.test", timeoutMs: 1000);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
