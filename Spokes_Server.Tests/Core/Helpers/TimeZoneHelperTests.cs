using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class TimeZoneHelperTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyRepo;

    public TimeZoneHelperTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_TimeZoneHelper_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyRepo = new CompanyProfileRepository(_persistence, _config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Fact]
    public void GetCompanyTime_WhenProfileHasValidUtcTimeZone_ReturnsUtcEquivalent()
    {
        var profile = new CompanyProfile { TimeZoneId = "UTC" };
        _companyRepo.Save(profile);

        var fixedUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = TimeZoneHelper.GetCompanyTime(_companyRepo, fixedUtc);

        Assert.Equal(fixedUtc, result);
    }

    [Fact]
    public void GetCompanyTime_WhenProfileIsNull_FallsBackToLocalTime()
    {
        var fixedUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = TimeZoneHelper.GetCompanyTime(null, fixedUtc);

        Assert.Equal(fixedUtc.ToLocalTime(), result);
    }

    [Fact]
    public void GetCompanyTime_WhenProfileHasInvalidTimeZone_FallsBackToLocalTime()
    {
        var profile = new CompanyProfile { TimeZoneId = "Invalid/Nonexistent/Zone_12345" };
        _companyRepo.Save(profile);

        var fixedUtc = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = TimeZoneHelper.GetCompanyTime(_companyRepo, fixedUtc);

        Assert.Equal(fixedUtc.ToLocalTime(), result);
    }

    [Fact]
    public void ConvertUtcToCompanyTime_WhenUtcOmitted_ReturnsCurrentTimeCloseToNow()
    {
        var result = TimeZoneHelper.ConvertUtcToCompanyTime("UTC");
        var diff = Math.Abs((DateTime.UtcNow - result).TotalSeconds);

        Assert.True(diff < 5);
    }

    [Fact]
    public void ConvertUtcToCompanyTime_WhenValidSystemTimeZone_ConvertsCorrectly()
    {
        // Find any available system timezone
        var localZone = TimeZoneInfo.Local;
        var fixedUtc = new DateTime(2026, 1, 15, 15, 30, 0, DateTimeKind.Utc);

        var result = TimeZoneHelper.ConvertUtcToCompanyTime(localZone.Id, fixedUtc);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(fixedUtc, localZone);

        Assert.Equal(expected, result);
    }
}
