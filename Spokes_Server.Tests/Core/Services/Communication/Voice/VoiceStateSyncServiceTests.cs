using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Aggregate;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceStateSyncServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly DiskPersistenceService _persistence;
    private readonly Database _db;
    private readonly Mock<ILogger<VoiceStateSyncService>> _mockLogger;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockScopedProvider;

    public VoiceStateSyncServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_VoiceStateSync_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        var mockDb = new Mock<Database>(
            (DeviceSessionRepository)null!, 
            (EmployeeRepository)null!
        );
        var systemConfigs = new SystemConfigRepository(_persistence, config);
        mockDb.Setup(x => x.SystemConfigs).Returns(systemConfigs);
        _db = mockDb.Object;
        
        var sysConfig = _db.SystemConfigs.Get();
        sysConfig.LiveKitApiKey = "test-key";
        sysConfig.LiveKitApiSecret = "test-secret";
        _db.SystemConfigs.Save(sysConfig);

        _mockLogger = new Mock<ILogger<VoiceStateSyncService>>();

        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopedProvider = new Mock<IServiceProvider>();

        _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockScopedProvider.Object);

        var dummyChatService = (ChatService)FormatterServices.GetUninitializedObject(typeof(ChatService));
        
        _mockScopedProvider.Setup(x => x.GetService(typeof(ChatService))).Returns(dummyChatService);
        _mockScopedProvider.Setup(x => x.GetService(typeof(Database))).Returns(_db);
        _mockScopedProvider.Setup(x => x.GetService(typeof(System.Net.Http.IHttpClientFactory)))
            .Returns(new Mock<System.Net.Http.IHttpClientFactory>().Object);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        if (Directory.Exists(_testDataDir))
        {
            try { Directory.Delete(_testDataDir, true); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
        }
    }

    private class TestableVoiceStateSyncService : VoiceStateSyncService
    {
        public TestableVoiceStateSyncService(IServiceProvider serviceProvider, ILogger<VoiceStateSyncService> logger, IConfiguration config) 
            : base(serviceProvider, logger, config)
        {
        }

        public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
    }

    [Fact]
    public async Task ExecuteAsync_DemoModeTrue_ReturnsImmediately()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir,
            ["Spokes_DemoMode"] = "true",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();

        var task = service.RunExecuteAsync(cts.Token);
        await task;
        
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_DemoSetupTrue_ReturnsImmediately()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir,
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "true"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();

        var task = service.RunExecuteAsync(cts.Token);
        await task;
        
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir,
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_ExecutesLoop_HandlesTransientErrors()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir,
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("channel-1", "user1");

        _mockScopedProvider.Setup(x => x.GetService(typeof(ChatStateService))).Returns(chatState);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        
        using var cts = new CancellationTokenSource();
        // Wait 6s to allow the initial 5s delay to pass and hit the loop before canceling
        cts.CancelAfter(TimeSpan.FromSeconds(6.0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
    }
}
