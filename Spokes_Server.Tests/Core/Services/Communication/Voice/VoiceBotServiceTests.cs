using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Notifications.Routing;
using Spokes_Server.Core.Services.Communication.Voice;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceBotServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigs;

    public VoiceBotServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_VoiceBot_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?>
        {
            ["DataPath"] = _testDataDir,
            ["Spokes_Voice_Port"] = "7880"
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);
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
    public void ParseAndResampleWav_WithEmptyOrShortBytes_ReturnsEmptyList()
    {
        var frames = VoiceBotService.ParseAndResampleWav([]);
        Assert.Empty(frames);

        var shortFrames = VoiceBotService.ParseAndResampleWav(new byte[20]);
        Assert.Empty(shortFrames);
    }

    [Fact]
    public void ParseAndResampleWav_WithValidWavFile_Returns20ms48kHzFrames()
    {
        // Generate a 100ms synthetic 44.1kHz stereo 16-bit WAV in memory
        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int numSamples = sampleRate / 10; // 100ms
        int pcmBytesLength = numSamples * channels * (bitsPerSample / 8);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmBytesLength);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
        bw.Write((short)(channels * (bitsPerSample / 8)));     // block align
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmBytesLength);
        bw.Write(new byte[pcmBytesLength]); // silence samples

        var wavBytes = ms.ToArray();
        var frames = VoiceBotService.ParseAndResampleWav(wavBytes);

        // At 48kHz, 100ms is ~5 20ms frames
        Assert.NotEmpty(frames);
        Assert.All(frames, f =>
        {
            Assert.Equal(48000, f.SampleRate);
            Assert.Equal(2, f.NumChannels);
            Assert.Equal(960, f.SamplesPerChannel);
        });
    }

    [Fact]
    public async Task EnsureBotJoinedAsync_WhenApiKeysMissing_LogsWarningGracefully()
    {
        var loggerMock = new Mock<ILogger<VoiceBotService>>();
        var config = _systemConfigs.Get();
        config.LiveKitApiKey = "";
        config.LiveKitApiSecret = "";
        _systemConfigs.Save(config);

        var botService = new VoiceBotService(_systemConfigs, loggerMock.Object, null, _config);

        await botService.EnsureBotJoinedAsync("test-channel");

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("LiveKit API key or secret missing")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public async Task EnsureBotJoinedAsync_WhenDaemonUnreachable_LogsWarningGracefully()
    {
        var loggerMock = new Mock<ILogger<VoiceBotService>>();
        var config = _systemConfigs.Get();
        config.LiveKitApiKey = "test-key";
        config.LiveKitApiSecret = "test-secret";
        _systemConfigs.Save(config);

        var botService = new VoiceBotService(_systemConfigs, loggerMock.Object, null, _config);

        await botService.EnsureBotJoinedAsync("test-channel");

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to connect bot")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    [Fact]
    public async Task PlaySoundAsync_SoundNotInCache_ReturnsGracefullyWithoutThrowing()
    {
        var loggerMock = new Mock<ILogger<VoiceBotService>>();
        var botService = new VoiceBotService(_systemConfigs, loggerMock.Object, null, _config);

        var ex = await Record.ExceptionAsync(() => botService.PlaySoundAsync("test-channel", "non_existent_sound"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task VoiceChannelService_JoinVoiceAsync_FirstUser_JoinsSilentlyAndPrewarmsBot()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        await voiceChannelService.JoinVoiceAsync("ch100", "user1");

        // First user pre-warms the bot presence silently without a join chime
        botMock.Verify(b => b.EnsureBotJoinedAsync("ch100", It.IsAny<CancellationToken>()), Times.Once);
        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_join", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VoiceChannelService_JoinVoiceAsync_SubsequentUser_PlaysJoinChime()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("ch100", "user1");

        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        await voiceChannelService.JoinVoiceAsync("ch100", "user2");

        // Subsequent user triggers the chime for existing members
        botMock.Verify(b => b.EnsureBotJoinedAsync("ch100", It.IsAny<CancellationToken>()), Times.Once);
        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_join", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VoiceChannelService_LeaveVoiceAsync_WithRemainingParticipants_PlaysLeaveChime()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("ch100", "user1");
        chatState.NotifyVoiceMemberJoined("ch100", "user2");

        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        await voiceChannelService.LeaveVoiceAsync("ch100", "user1");

        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_leave", It.IsAny<CancellationToken>()), Times.Once);
        botMock.Verify(b => b.ScheduleBotDeparture("ch100", It.IsAny<TimeSpan>()), Times.Never);
    }

    [Fact]
    public async Task VoiceChannelService_LeaveVoiceAsync_WhenLastParticipantLeaves_SchedulesBotDeparture()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("ch100", "user1");

        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        await voiceChannelService.LeaveVoiceAsync("ch100", "user1");

        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_leave", It.IsAny<CancellationToken>()), Times.Never);
        botMock.Verify(b => b.ScheduleBotDeparture("ch100", TimeSpan.FromSeconds(30)), Times.Once);
    }

    [Fact]
    public async Task VoiceChannelService_JoinVoiceAsync_CalledTwice_PlaysJoinChimeOnlyOnce()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("ch100", "user1");

        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        // Calling JoinVoiceAsync twice for the same user (e.g. from UI reconnect/cascade)
        await voiceChannelService.JoinVoiceAsync("ch100", "user2");
        await voiceChannelService.JoinVoiceAsync("ch100", "user2");

        // Chime must only play ONCE
        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_join", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VoiceChannelService_LeaveVoiceAsync_CalledTwice_PlaysLeaveChimeOnlyOnce()
    {
        var botMock = new Mock<IVoiceBotService>();
        var chatState = new ChatStateService();
        chatState.NotifyVoiceMemberJoined("ch100", "user1");
        chatState.NotifyVoiceMemberJoined("ch100", "user2");

        var realtimeGatewayMock = new Mock<IChatRealtimeGateway>();
        var companyRepo = new CompanyProfileRepository(_persistence, _config);
        var messagesRepo = new ChatMessageRepository(_persistence, _config, companyRepo);
        var notifRoutingMock = new Mock<INotificationRoutingService>();
        var livekitService = new LiveKitService(new Mock<ILogger<LiveKitService>>().Object, _config);
        var employees = new EmployeeRepository(_persistence, _config);
        var channels = new ChatChannelRepository(_persistence, _config);
        var readStates = new ChatReadStateRepository(_persistence, _config, companyRepo);
        var logger = new Mock<ILogger<VoiceChannelService>>().Object;

        var voiceChannelService = new VoiceChannelService(
            chatState,
            realtimeGatewayMock.Object,
            messagesRepo,
            notifRoutingMock.Object,
            _systemConfigs,
            livekitService,
            logger,
            employees,
            channels,
            readStates,
            chatAuth: null,
            voiceBotService: botMock.Object);

        // Calling LeaveVoiceAsync twice for the same user (e.g. from disconnect button + OnRoomDisconnected event)
        await voiceChannelService.LeaveVoiceAsync("ch100", "user1");
        await voiceChannelService.LeaveVoiceAsync("ch100", "user1");

        // Leave chime must only play ONCE
        botMock.Verify(b => b.PlaySoundAsync("ch100", "channel_leave", It.IsAny<CancellationToken>()), Times.Once);
    }
}
