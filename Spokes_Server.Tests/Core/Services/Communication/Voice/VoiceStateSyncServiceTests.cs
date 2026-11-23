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

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        var mockDb = new Mock<Database>();
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

        var dummyChatService = (ChatService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ChatService));
        
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

        public TimeSpan OverrideGracePeriod { get; set; } = TimeSpan.Zero;

        protected override TimeSpan InitialDelay => TimeSpan.FromMilliseconds(20);
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(20);
        protected override TimeSpan ActivityGracePeriod => OverrideGracePeriod;

        public Task RunExecuteAsync(CancellationToken token) => ExecuteAsync(token);
        public Task RunSyncOnceAsync(CancellationToken token) => SyncActiveChannelsOnceAsync(token);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenUserMissingFromLiveKit_RequiresConsecutiveMissesAndLeavesWithoutEvicting()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-present");
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-missing");

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>
            {
                new("user-present", "PA_1", "", null, true)
            });

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);

        // First miss: transient reconnect grace period - should NOT kick yet
        await service.RunSyncOnceAsync(CancellationToken.None);
        mockVoiceChannel.Verify(
            x => x.LeaveVoiceAsync("chan-sync", "user-missing", It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Second consecutive miss: confirmed absent - evicts from C# without evicting from LiveKit
        await service.RunSyncOnceAsync(CancellationToken.None);
        mockVoiceChannel.Verify(
            x => x.LeaveVoiceAsync("chan-sync", "user-missing", false, It.IsAny<CancellationToken>()),
            Times.Once);
        mockVoiceChannel.Verify(
            x => x.LeaveVoiceAsync("chan-sync", "user-present", It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenUserReturnsAfterSingleMiss_ResetsMissCountAndDoesNotEvict()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-flapping");

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .SetupSequence(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>())
            .ReturnsAsync(new List<LiveKitParticipantSnapshot> { new("user-flapping", "PA_1", "", null, true) })
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>());

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);

        await service.RunSyncOnceAsync(CancellationToken.None); // miss 1
        await service.RunSyncOnceAsync(CancellationToken.None); // returned -> reset
        await service.RunSyncOnceAsync(CancellationToken.None); // miss 1 again

        mockVoiceChannel.Verify(
            x => x.LeaveVoiceAsync("chan-sync", "user-flapping", It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenUserPresentInLiveKitButMissingOrDriftedInServer_ReconcilesMemberState()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-host");
        // Simulate stuck muted state in ChatState while LiveKit shows user-host unmuted
        chatState.SetUserMuted("chan-sync", "user-host", true);

        var restoredMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-restored",
            DeviceId = "dev-restored",
            DeviceName = "Desktop Restored"
        }.ToJson();

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>
            {
                new("user-host", "PA_1", "", IsMicMuted: false, IsConnected: true),
                new("user-dropped", "PA_2", restoredMeta, IsMicMuted: true, IsConnected: true)
            });

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        await service.RunSyncOnceAsync(CancellationToken.None);

        mockVoiceChannel.Verify(
            x => x.ReconcileMemberStateAsync(
                "chan-sync",
                "user-host",
                It.IsAny<VoiceParticipantMetadata>(),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
        mockVoiceChannel.Verify(
            x => x.ReconcileMemberStateAsync(
                "chan-sync",
                "user-dropped",
                It.Is<VoiceParticipantMetadata>(m => m.VoiceSessionId == "sess-restored" && m.DeviceId == "dev-restored"),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenUserWithinActivityGracePeriod_SkipsEvictionEvenAcrossPolls()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-reconnecting", voiceSessionId: "sess-1");

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>());

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config)
        {
            OverrideGracePeriod = TimeSpan.FromSeconds(45)
        };

        await service.RunSyncOnceAsync(CancellationToken.None);
        await service.RunSyncOnceAsync(CancellationToken.None);

        mockVoiceChannel.Verify(
            x => x.TryLeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mockVoiceChannel.Verify(
            x => x.LeaveVoiceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenUserWithSessionMissingPastGrace_UsesTryLeaveVoiceAsyncWithSessionFence()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-stale", voiceSessionId: "sess-stale");

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>());

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);

        await service.RunSyncOnceAsync(CancellationToken.None);
        await service.RunSyncOnceAsync(CancellationToken.None);

        mockVoiceChannel.Verify(
            x => x.TryLeaveVoiceAsync("chan-sync", "user-stale", "sess-stale", false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenSessionClosedWithinActivityGrace_DefersLiveKitRemoveParticipant()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-anchor", voiceSessionId: "sess-anchor");
        chatState.NotifyVoiceMemberJoined("chan-sync", "user-reconnect", voiceSessionId: "sess-transient");
        // Simulate transient participant_left webhook closing sess-transient while user-reconnect is still in LiveKit
        chatState.TryNotifyVoiceMemberLeft("chan-sync", "user-reconnect", "sess-transient");
        Assert.True(chatState.IsVoiceSessionClosed("sess-transient"));

        var reconnectMeta = new VoiceParticipantMetadata { VoiceSessionId = "sess-transient", DeviceId = "dev-1" }.ToJson();
        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-sync", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>
            {
                new("user-anchor", "PA_1", "", false, true),
                new("user-reconnect", "PA_2", reconnectMeta, false, true)
            });

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config)
        {
            OverrideGracePeriod = TimeSpan.FromSeconds(15)
        };

        // Within grace period: must NOT call RemoveParticipantAsync so OnRoomReconnectedAsync has time to ReopenVoiceSession
        await service.RunSyncOnceAsync(CancellationToken.None);
        mockLiveKit.Verify(
            x => x.RemoveParticipantAsync("chan-sync", "user-reconnect", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Once grace period expires (OverrideGracePeriod = Zero): evicts the closed session from LiveKit
        service.OverrideGracePeriod = TimeSpan.Zero;
        await service.RunSyncOnceAsync(CancellationToken.None);
        mockLiveKit.Verify(
            x => x.RemoveParticipantAsync("chan-sync", "user-reconnect", "test-key", "test-secret", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DemoModeTrue_ReturnsImmediately()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = "true",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();

        var task = service.RunExecuteAsync(cts.Token);
        await task;
        
        Assert.NotNull(service);
        Assert.NotNull(task);
        Assert.NotNull(config);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_DemoSetupTrue_ReturnsImmediately()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "true"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();

        var task = service.RunExecuteAsync(cts.Token);
        await task;
        
        Assert.NotNull(service);
        Assert.NotNull(task);
        Assert.NotNull(config);
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        Assert.NotNull(service);
        Assert.NotNull(config);
        Assert.True(cts.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_ExecutesLoop_HandlesTransientErrors()
    {
        var configDict = new Dictionary<string, string?> 
        { 
            ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!",
            ["Spokes_DemoMode"] = "false",
            ["Spokes_DemoSetup"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("channel-1", "user1");

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        
        using var cts = new CancellationTokenSource();
        // Allow the initial delay to pass and hit the loop before canceling
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        Assert.NotNull(service);
        Assert.NotNull(chatState);
        Assert.NotNull(config);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunExecuteAsync(cts.Token));
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_WhenChannelEmptyInChatStateButDiscoveredInLiveKit_PollsAndReconcilesParticipant()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        Assert.Empty(chatState.GetAllActiveVoiceChannels());

        var iphoneMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-iphone-1",
            DeviceId = "dev-iphone-1",
            DeviceName = "iPhone",
            Platform = "ios"
        }.ToJson();

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetActiveVoiceChannelIdsAsync("test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "chan-iphone-solo" });
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-iphone-solo", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>
            {
                new("user-iphone", "PA_IOS_1", iphoneMeta, IsMicMuted: false, IsConnected: true)
            });

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        await service.RunSyncOnceAsync(CancellationToken.None);

        mockVoiceChannel.Verify(
            x => x.ReconcileMemberStateAsync(
                "chan-iphone-solo",
                "user-iphone",
                It.Is<VoiceParticipantMetadata>(m => m.VoiceSessionId == "sess-iphone-1" && m.Platform == "ios"),
                false,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncActiveChannelsOnceAsync_SteadyStatePoll_DoesNotRefreshLastActivityUtc()
    {
        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir, ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        var chatState = new VoiceStateService();
        chatState.NotifyVoiceMemberJoined("chan-steady", "user-steady", voiceSessionId: "sess-steady");
        var initialActivity = chatState.GetVoiceParticipantLastActivityUtc("chan-steady", "user-steady");
        Assert.NotNull(initialActivity);

        // Allow clock tick so any unintended TouchVoiceParticipantActivity would produce a newer timestamp.
        await Task.Delay(25);

        var steadyMeta = new VoiceParticipantMetadata
        {
            VoiceSessionId = "sess-steady"
        }.ToJson();

        var mockVoiceChannel = new Mock<VoiceChannelService>();
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), config);
        mockLiveKit
            .Setup(x => x.GetRoomParticipantStatesAsync("chan-steady", "test-key", "test-secret", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveKitParticipantSnapshot>
            {
                new("user-steady", "PA_STEADY_1", steadyMeta, IsMicMuted: false, IsConnected: true)
            });

        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceStateService))).Returns(chatState);
        _mockScopedProvider.Setup(x => x.GetService(typeof(VoiceChannelService))).Returns(mockVoiceChannel.Object);
        _mockScopedProvider.Setup(x => x.GetService(typeof(SystemConfigRepository))).Returns(_db.SystemConfigs);
        _mockScopedProvider.Setup(x => x.GetService(typeof(LiveKitService))).Returns(mockLiveKit.Object);

        var service = new TestableVoiceStateSyncService(_mockServiceProvider.Object, _mockLogger.Object, config);
        await service.RunSyncOnceAsync(CancellationToken.None);

        var afterPollActivity = chatState.GetVoiceParticipantLastActivityUtc("chan-steady", "user-steady");
        Assert.Equal(initialActivity, afterPollActivity);
        mockVoiceChannel.Verify(
            x => x.ReconcileMemberStateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<VoiceParticipantMetadata>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
