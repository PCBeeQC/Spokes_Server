using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Configuration;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Services.Identity;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Identity;

public class CasdoorBrandingBackgroundServiceTests : TestDataTestBase
{
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly CompanyProfileRepository _companyProfiles;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly Mock<ILogger<CasdoorBrandingBackgroundService>> _loggerMock;

    public CasdoorBrandingBackgroundServiceTests()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataPath };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
        _loggerMock = new Mock<ILogger<CasdoorBrandingBackgroundService>>();
    }

    private class MockableCasdoorProvisioningService : CasdoorProvisioningService
    {
        public int SyncCount { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public MockableCasdoorProvisioningService(
            CompanyProfileRepository companyProfiles,
            SystemConfigRepository systemConfigs,
            IConfiguration configuration)
            : base(companyProfiles, systemConfigs, configuration)
        {
        }

        public override Task SyncBrandingAsync()
        {
            SyncCount++;
            if (ExceptionToThrow != null)
            {
                throw ExceptionToThrow;
            }
            return Task.CompletedTask;
        }
    }

    private class TestableCasdoorBrandingBackgroundService : CasdoorBrandingBackgroundService
    {
        public TestableCasdoorBrandingBackgroundService(
            IServiceProvider serviceProvider,
            SystemConfigRepository systemConfigs,
            ILogger<CasdoorBrandingBackgroundService> logger,
            TimeSpan? startupDelay = null)
            : base(serviceProvider, systemConfigs, logger, startupDelay)
        {
        }

        public Task RunExecuteAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSetupIncomplete_ReturnsImmediatelyWithoutSyncing()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = false,
            ProviderType = IdpType.BuiltInCasdoor
        });

        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config);
        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        var service = new TestableCasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.Zero);

        await service.RunExecuteAsync(CancellationToken.None);

        Assert.Equal(0, casdoorMock.SyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProviderIsNotBuiltInCasdoor_ReturnsImmediatelyWithoutSyncing()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = true,
            ProviderType = IdpType.External
        });

        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config);
        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        var service = new TestableCasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.Zero);

        await service.RunExecuteAsync(CancellationToken.None);

        Assert.Equal(0, casdoorMock.SyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBuiltInCasdoorAndSetupComplete_SyncsBrandingAndLogs()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = true,
            ProviderType = IdpType.BuiltInCasdoor
        });

        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config);
        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        var service = new TestableCasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.Zero);

        await service.RunExecuteAsync(CancellationToken.None);

        Assert.Equal(1, casdoorMock.SyncCount);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Casdoor branding synced automatically on boot")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelledDuringDelay_ExitsGracefullyWithoutThrowing()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = true,
            ProviderType = IdpType.BuiltInCasdoor
        });

        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config);
        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var service = new TestableCasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.FromSeconds(30));

        var task = service.RunExecuteAsync(cts.Token);
        await task;

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(0, casdoorMock.SyncCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSyncBrandingThrows_LogsErrorAndDoesNotBubbleException()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = true,
            ProviderType = IdpType.BuiltInCasdoor
        });

        var expectedException = new HttpRequestException("Casdoor host unreachable");
        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config)
        {
            ExceptionToThrow = expectedException
        };

        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        var service = new TestableCasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.Zero);

        await service.RunExecuteAsync(CancellationToken.None);

        Assert.Equal(1, casdoorMock.SyncCount);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to sync Casdoor branding on boot")),
                expectedException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Lifecycle_StartAsyncAndStopAsync_ExecutesAndShutsDownCleanly()
    {
        _systemConfigs.Save(new SystemConfig
        {
            IsSetupComplete = true,
            ProviderType = IdpType.BuiltInCasdoor
        });

        var casdoorMock = new MockableCasdoorProvisioningService(_companyProfiles, _systemConfigs, _config);
        var services = new ServiceCollection();
        services.AddScoped<CasdoorProvisioningService>(_ => casdoorMock);
        var provider = services.BuildServiceProvider();

        var service = new CasdoorBrandingBackgroundService(
            provider,
            _systemConfigs,
            _loggerMock.Object,
            TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, casdoorMock.SyncCount);
    }
}
