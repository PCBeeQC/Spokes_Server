using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Services.Core;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Core;

public class StorageHealthServiceTests
{
    private readonly Mock<ILogger<StorageHealthService>> _mockLogger = new();
    private readonly IConfiguration _config;

    public StorageHealthServiceTests()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "DataPath", Path.GetTempPath() }
        };
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    [Fact]
    public void StorageHealthService_Initializes_WithValidDriveInfo()
    {
        var service = new StorageHealthService(_config, _mockLogger.Object);
        var info = service.GetHealthInfo();

        Assert.NotNull(info);
        Assert.True(info.TotalBytes > 0);
        Assert.True(info.AvailableFreeBytes > 0);
        Assert.False(info.HasPersistenceFailure);
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

    [Fact]
    public void StorageHealthInfo_Properties_AreConsistent()
    {
        var service = new StorageHealthService(_config, _mockLogger.Object);
        var info = service.GetHealthInfo();

        Assert.Equal(service.Status, info.Status);
        Assert.Equal(service.AvailableFreeBytes, info.AvailableFreeBytes);
        Assert.Equal(service.TotalBytes, info.TotalBytes);
        Assert.Equal(service.IsUploadAllowed, info.IsUploadAllowed);
    }
}
