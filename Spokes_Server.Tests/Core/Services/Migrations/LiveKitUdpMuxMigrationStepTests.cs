using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Migrations;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Migrations;

public class LiveKitUdpMuxMigrationStepTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigs;

    public LiveKitUdpMuxMigrationStepTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_LiveKitMuxMig_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = _testDataDir,
            ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"
        }).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _systemConfigs = new SystemConfigRepository(_persistence, config);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); } catch { }
        }
    }

    [Theory]
    [InlineData(30000, 30499, 30000, 30007)]
    [InlineData(50000, 50499, 50000, 50007)]
    [InlineData(41200, 41299, 41200, 41207)]
    [InlineData(40000, 40003, 40000, 40003)]
    [InlineData(40000, 40000, 40000, 40000)]
    [InlineData(65532, 65535, 65532, 65535)]
    public async Task ExecuteAsync_MigratesLegacyWideRangesWhilePreservingCustomStartPortsAndSmallRanges(
        int initialStart,
        int initialEnd,
        int expectedStart,
        int expectedEnd)
    {
        var config = _systemConfigs.Get();
        config.IsSetupComplete = true;
        config.LiveKitUdpStartPort = initialStart;
        config.LiveKitUdpEndPort = initialEnd;
        _systemConfigs.Save(config);

        var step = new LiveKitUdpMuxMigrationStep(_systemConfigs, new Mock<ILogger<LiveKitUdpMuxMigrationStep>>().Object);
        await step.ExecuteAsync();

        var reloaded = _systemConfigs.Get();
        Assert.Equal(expectedStart, reloaded.LiveKitUdpStartPort);
        Assert.Equal(expectedEnd, reloaded.LiveKitUdpEndPort);
    }
}
