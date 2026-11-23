using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Maintenance;

public class StorageHealthServiceTests
{
    private readonly Mock<ILogger<StorageHealthService>> _mockLogger = new();
    private readonly IConfiguration _config;

    public StorageHealthServiceTests()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "DataPath", Path.GetTempPath() }, { "SPOKES_MASTER_PASSWORD", "UnitTest-Master-Password-2026!" }
        };
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [Fact]
    public void StorageHealthService_Initializes_WithValidDriveInfo()
    {
        var service = new StorageHealthService(_config, _mockLogger.Object);

        Assert.True(service.TotalBytes > 0);
        Assert.True(service.AvailableFreeBytes > 0);
        Assert.False(service.HasPersistenceFailure);
    }

    [Fact]
    public void RecordPersistenceFailure_SwitchesToCriticalReadOnly()
    {
        var service = new StorageHealthService(_config, _mockLogger.Object);
        Assert.False(service.HasPersistenceFailure);

        service.RecordPersistenceFailure();

        Assert.True(service.HasPersistenceFailure);
        Assert.Equal(StorageStatus.CriticalReadOnly, service.Status);
        Assert.False(service.IsUploadAllowed);
    }

    [Fact]
    public void RecordPersistenceSuccess_RecoversFromFailure()
    {
        var service = new StorageHealthService(_config, _mockLogger.Object);
        service.RecordPersistenceFailure();
        Assert.True(service.HasPersistenceFailure);

        service.RecordPersistenceSuccess();

        Assert.False(service.HasPersistenceFailure);
    }
}
