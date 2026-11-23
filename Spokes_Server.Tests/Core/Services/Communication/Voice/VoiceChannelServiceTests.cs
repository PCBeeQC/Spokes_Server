using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Hubs;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Notifications;
using Spokes_Server.Core.Services.Communication.Voice;

namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

public class VoiceChannelServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly IConfiguration _config;
    private readonly DiskPersistenceService _persistence;
    private readonly SystemConfigRepository _systemConfigs;
    private readonly ChatMessageRepository _messages;
    private readonly EmployeeRepository _employees;
    private readonly ChatChannelRepository _channels;
    private readonly ChatReadStateRepository _readStates;
    private readonly VoiceStateService _voiceState;
    private readonly ChatStateService _chatState;
    private readonly Mock<ILogger<VoiceChannelService>> _mockLogger;
    private readonly VoiceChannelService _service;

    public VoiceChannelServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_VoiceChannelService_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir , ["SPOKES_MASTER_PASSWORD"] = "UnitTest-Master-Password-2026!"};
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();

        _persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        _systemConfigs = new SystemConfigRepository(_persistence, _config);

        var sysConfig = _systemConfigs.Get();
        sysConfig.LiveKitApiKey = "test-api-key";
        sysConfig.LiveKitApiSecret = "test-api-secret-which-must-be-long-enough";
        _systemConfigs.Save(sysConfig);

        var companyProfiles = new CompanyProfileRepository(_persistence, _config);
        _messages = new ChatMessageRepository(_persistence, _config, companyProfiles);
        _employees = new EmployeeRepository(_persistence, _config);
        _channels = new ChatChannelRepository(_persistence, _config);
        _readStates = new ChatReadStateRepository(_persistence, _config, companyProfiles);
        _voiceState = new VoiceStateService();
        _chatState = new ChatStateService();

        _mockLogger = new Mock<ILogger<VoiceChannelService>>();

        _service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), null).Object,
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates);
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

    [Theory]
    [InlineData("http://example.com/avatar.png", null, null, null, null)]
    [InlineData("", null, null, null, null)]
    [InlineData(null, "sess-123", "dev-1", "PC", "web")]
    public void GenerateJoinToken_ValidInputs_ReturnsJwtString(string? userAvatarUrl, string? sessionId, string? deviceId, string? deviceName, string? clientType)
    {
        var result = sessionId == null
            ? _service.GenerateJoinToken("123", "user-1", "Alice", userAvatarUrl!)
            : _service.GenerateJoinToken("123", "user-1", "Alice", userAvatarUrl!, sessionId, deviceId, deviceName, clientType);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("eyJ", result);
    }

    [Fact]
    public async Task JoinVoiceAsync_AddsParticipantAndSendsSignal()
    {
        (string ChannelId, string UserId)? joinedEvent = null;
        _voiceState.VoiceMemberJoined += (channelId, userId) => joinedEvent = (channelId, userId);

        // Act
        await _service.JoinVoiceAsync("chan-1", "user-1");

        // Assert
        var participants = _voiceState.GetVoiceParticipants("chan-1");
        Assert.Contains("user-1", participants);
        Assert.Equal(("chan-1", "user-1"), joinedEvent);
    }

    [Fact]
    public async Task LeaveVoiceAsync_RemovesParticipantAndUpdatesInviteStatusIfEmpty()
    {
        // Arrange
        var inviteMsg = new ChatMessage
        {
            Id = "msg-invite",
            ChannelId = "chan-2",
            SenderId = "user-1",
            MessageType = "CallInvite",
            CallStatus = "Active",
            Content = "Call started"
        };
        _messages.Save(inviteMsg);

        await _service.JoinVoiceAsync("chan-2", "user-1");

        // Act
        await _service.LeaveVoiceAsync("chan-2", "user-1");

        // Assert
        var participants = _voiceState.GetVoiceParticipants("chan-2");
        Assert.DoesNotContain("user-1", participants);

        var updatedMsg = _messages.GetById("msg-invite");
        Assert.NotNull(updatedMsg);
        Assert.Equal(CallStatus.Ended, updatedMsg.CallStatus);
    }

    [Fact]
    public async Task SetUserSpeakingAsync_TogglesSpeakerState()
    {
        // Act
        await _service.SetUserSpeakingAsync("chan-4", "user-1", true);

        // Assert
        var speakers = _voiceState.GetActiveSpeakers("chan-4");
        Assert.Contains("user-1", speakers);

        // Act 2: Stop speaking
        await _service.SetUserSpeakingAsync("chan-4", "user-1", false);
        speakers = _voiceState.GetActiveSpeakers("chan-4");
        Assert.DoesNotContain("user-1", speakers);
    }

    [Fact]
    public async Task SetUserMutedAsync_UpdatesStateAndSendsSignal()
    {
        (string ChannelId, string UserId, bool IsMuted)? mutedEvent = null;
        _voiceState.VoiceUserMutedChanged += (channelId, userId, isMuted) => mutedEvent = (channelId, userId, isMuted);

        // Act
        await _service.SetUserMutedAsync("chan-5", "user-1", true);

        // Assert
        Assert.Contains("user-1", _voiceState.GetMutedUsers("chan-5"));
        Assert.Equal(("chan-5", "user-1", true), mutedEvent);
    }

    [Fact]
    public async Task UpdateCallInviteStatusAsync_UpdatesMessageStatus()
    {
        // Arrange
        var invite = new ChatMessage
        {
            Id = "invite-99",
            ChannelId = "chan-99",
            SenderId = "user-1",
            MessageType = "CallInvite",
            CallStatus = CallStatus.Active,
            Content = "Call"
        };
        _messages.Save(invite);

        // Act
        await _service.UpdateCallInviteStatusAsync("invite-99", CallStatus.Declined);

        // Assert
        var updated = _messages.GetById("invite-99");
        Assert.NotNull(updated);
        Assert.Equal(CallStatus.Declined, updated.CallStatus);
    }

    [Theory]
    [InlineData(CallStatus.Joined, "call_channel_1")]
    [InlineData(CallStatus.Active, "call_channel_active")]
    public async Task LeaveVoiceAsync_WhenActiveInvite_TransitionsToEndedWhenLastUserLeaves(string initialStatus, string channelId)
    {
        var invite = new ChatMessage
        {
            ChannelId = channelId,
            SenderId = "user-1",
            MessageType = "CallInvite",
            CallStatus = initialStatus,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(invite);

        _voiceState.NotifyVoiceMemberJoined(channelId, "user-1");

        await _service.LeaveVoiceAsync(channelId, "user-1");

        var updatedInvite = _messages.GetById(invite.Id);
        Assert.NotNull(updatedInvite);
        Assert.Equal(CallStatus.Ended, updatedInvite.CallStatus);
    }

    [Fact]
    public async Task SendCallInviteAsync_MarksSenderAsRead()
    {
        var caller = new Employee { Id = "user-caller", FirstName = "Caller", LastName = "User" };
        _employees.Save(caller);
        var channel = new ChatChannel { Id = "chan-call", Name = "General" };
        _channels.Save(channel);

        var result = await _service.SendCallInviteAsync("user-caller", "chan-call");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Contains("user-caller", result.Value.ReadBy);
        var readState = _readStates.GetReadState("user-caller", "chan-call");
        Assert.NotNull(readState);
    }

    [Fact]
    public async Task SendCallInviteAsync_WhenEmployeeNotFound_ReturnsNotFound()
    {
        var channel = new ChatChannel { Id = "chan-missing-user", Name = "General" };
        _channels.Save(channel);

        var result = await _service.SendCallInviteAsync("non-existent-user", "chan-missing-user");

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.Code);
        Assert.Contains("non-existent-user", result.ErrorMessage);
    }

    [Fact]
    public async Task SendCallInviteAsync_WhenChannelNotFound_ReturnsNotFound()
    {
        var caller = new Employee { Id = "user-missing-chan", FirstName = "Caller", LastName = "User" };
        _employees.Save(caller);

        var result = await _service.SendCallInviteAsync("user-missing-chan", "non-existent-channel");

        Assert.False(result.IsSuccess);
        Assert.Equal("NOT_FOUND", result.Code);
        Assert.Contains("non-existent-channel", result.ErrorMessage);
    }

    [Fact]
    public async Task SendCallInviteAsync_WhenUnauthorized_ReturnsForbidden()
    {
        var caller = new Employee { Id = "user-forbidden", FirstName = "Forbidden", LastName = "User" };
        _employees.Save(caller);
        var channel = new ChatChannel { Id = "chan-forbidden", Name = "Restricted" };
        _channels.Save(channel);

        var mockChatAuth = new Mock<IChatAuthorizationService>();
        mockChatAuth.Setup(a => a.CanUserAccessChannel(channel, caller)).Returns(false);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates,
            mockChatAuth.Object);

        var result = await service.SendCallInviteAsync("user-forbidden", "chan-forbidden");

        Assert.False(result.IsSuccess);
        Assert.Equal("FORBIDDEN", result.Code);
    }

    [Fact]
    public void Constructor_WhenRequiredDependencyNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new VoiceChannelService(
            null!,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates));
    }

    [Fact]
    public async Task SafeDispatchNotificationAsync_WhenActionThrows_LogsErrorAndDoesNotCrash()
    {
        var task = _service.SafeDispatchNotificationAsync(() => throw new InvalidOperationException("Simulated notification failure"), "test");
        await task;

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to dispatch test notification in background")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LeaveVoiceAsync_WhenEvictFromLiveKitIsTrue_CallsRemoveParticipantAsync()
    {
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), null);
        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            mockLiveKit.Object,
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates);

        await service.JoinVoiceAsync("chan-evict-true", "user-1");
        await service.LeaveVoiceAsync("chan-evict-true", "user-1");

        mockLiveKit.Verify(
            x => x.RemoveParticipantAsync("chan-evict-true", "user-1", "test-api-key", "test-api-secret-which-must-be-long-enough", It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("chan-evict-true"));
    }

    [Fact]
    public async Task LeaveVoiceAsync_WhenEvictFromLiveKitIsFalse_SkipsRemoveParticipantAsyncAndCleansUpState()
    {
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), null);
        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            mockLiveKit.Object,
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates);

        (string ChannelId, string UserId)? leftEvent = null;
        _voiceState.VoiceMemberLeft += (channelId, userId) => leftEvent = (channelId, userId);

        await service.JoinVoiceAsync("chan-evict-false", "user-1");
        await service.LeaveVoiceAsync("chan-evict-false", "user-1", evictFromLiveKit: false);

        mockLiveKit.Verify(
            x => x.RemoveParticipantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("chan-evict-false"));
        Assert.Equal(("chan-evict-false", "user-1"), leftEvent);
    }

    [Fact]
    public async Task GenerateJoinToken_AndJoinVoiceAsync_WhenUnauthorized_RejectsAccess()
    {
        var channel = new ChatChannel { Id = "chan-priv", Name = "Private Voice", ChannelType = ChatChannelType.Direct };
        var outsider = new Employee { Id = "user-outsider", FirstName = "Outsider" };
        var member = new Employee { Id = "user-member", FirstName = "Member" };
        _channels.Save(channel);
        _employees.Save(outsider);
        _employees.Save(member);

        var mockChatAuth = new Mock<IChatAuthorizationService>();
        mockChatAuth.Setup(a => a.CanUserAccessChannel(It.Is<ChatChannel>(c => c.Id == "chan-priv"), It.Is<Employee>(e => e.Id == "user-outsider"))).Returns(false);
        mockChatAuth.Setup(a => a.CanUserAccessChannel(It.Is<ChatChannel>(c => c.Id == "chan-priv"), It.Is<Employee>(e => e.Id == "user-member"))).Returns(true);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates,
            mockChatAuth.Object);

        // Unauthorized user cannot generate a LiveKit join token or join the voice channel
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken("chan-priv", "user-outsider", "Outsider", ""));

        await service.JoinVoiceAsync("chan-priv", "user-outsider", "dev-1", "PC");
        Assert.DoesNotContain("user-outsider", _voiceState.GetVoiceParticipants("chan-priv"));

        // Authorized channel member (even without a vault password) can generate a join token and join
        var token = service.GenerateJoinToken("chan-priv", "user-member", "Member", "");
        Assert.False(string.IsNullOrWhiteSpace(token));
        await service.JoinVoiceAsync("chan-priv", "user-member", "dev-2", "PC");
        Assert.Contains("user-member", _voiceState.GetVoiceParticipants("chan-priv"));
    }

    [Fact]
    public async Task GenerateJoinToken_JoinVoice_And_UpdateCallInvite_FailClosedWhenChannelOrEmployeeMissingInactiveOrBanned()
    {
        var activeMember = new Employee { Id = "voice-active", FirstName = "Active", IsActive = true };
        var inactiveUser = new Employee { Id = "voice-inactive", FirstName = "Inactive", IsActive = false };
        var bannedUser = new Employee { Id = "voice-banned", FirstName = "Banned", IsActive = true, IsBanned = true };
        var suspendedUser = new Employee { Id = "voice-suspended", FirstName = "Suspended", IsActive = true, IsSuspended = true };
        var outsider = new Employee { Id = "voice-outsider", FirstName = "Outsider", IsActive = true };
        _employees.Save(activeMember);
        _employees.Save(inactiveUser);
        _employees.Save(bannedUser);
        _employees.Save(suspendedUser);
        _employees.Save(outsider);

        var channel = new ChatChannel
        {
            Id = "chan-voice-closed",
            Name = "Secure Voice",
            ChannelType = ChatChannelType.Group,
            IsEncrypted = true, // Encrypted channel must NOT require vault unlock — only CanUserAccessChannel!
            ParticipantIds = [activeMember.Id, suspendedUser.Id]
        };
        _channels.Save(channel);

        var invite = new ChatMessage
        {
            Id = "invite-closed-1",
            ChannelId = channel.Id,
            SenderId = activeMember.Id,
            MessageType = "CallInvite",
            CallStatus = CallStatus.Active,
            Content = "Call"
        };
        _messages.Save(invite);

        var mockChatAuth = new Mock<IChatAuthorizationService>();
        mockChatAuth
            .Setup(a => a.CanUserAccessChannel(
                It.Is<ChatChannel>(c => c.Id == channel.Id),
                It.Is<Employee>(e => e.Id == activeMember.Id || e.Id == suspendedUser.Id)))
            .Returns(true);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates,
            mockChatAuth.Object);

        // Missing channel -> fail closed!
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken("non-existent-channel", activeMember.Id, "Active", ""));
        await service.JoinVoiceAsync("non-existent-channel", activeMember.Id);
        Assert.Empty(_voiceState.GetVoiceParticipants("non-existent-channel"));

        // Missing employee -> fail closed!
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, "non-existent-emp", "Ghost", ""));
        await service.JoinVoiceAsync(channel.Id, "non-existent-emp");
        Assert.DoesNotContain("non-existent-emp", _voiceState.GetVoiceParticipants(channel.Id));

        // Inactive or banned employee -> fail closed!
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, inactiveUser.Id, "Inactive", ""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, bannedUser.Id, "Banned", ""));
        await service.JoinVoiceAsync(channel.Id, inactiveUser.Id, "dev-inact", "PC");
        await service.JoinVoiceAsync(channel.Id, bannedUser.Id, "dev-ban", "PC");
        Assert.DoesNotContain(inactiveUser.Id, _voiceState.GetVoiceParticipants(channel.Id));
        Assert.DoesNotContain(bannedUser.Id, _voiceState.GetVoiceParticipants(channel.Id));

        // SendCallInviteAsync rejects suspended and banned users
        var suspendedInviteRes = await service.SendCallInviteAsync(suspendedUser.Id, channel.Id);
        Assert.False(suspendedInviteRes.IsSuccess);
        Assert.Equal("FORBIDDEN", suspendedInviteRes.Code);

        // UpdateCallInviteStatusAsync fails closed for null/empty actorUserId, outsider, banned, inactive, or suspended users
        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Declined, actorUserId: null);
        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Declined, actorUserId: "");
        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Declined, actorUserId: outsider.Id);
        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Declined, actorUserId: bannedUser.Id);
        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Declined, actorUserId: suspendedUser.Id);
        Assert.Equal(CallStatus.Active, _messages.GetById(invite.Id)!.CallStatus);

        // Authorized active member succeeds on encrypted channel with simple CanUserAccessChannel (no vault check)
        activeMember.EncryptedPrivateKey = "personal-vault-locked";
        _employees.Save(activeMember);
        var encVoiceToken = service.GenerateJoinToken(channel.Id, activeMember.Id, "Active", "");
        Assert.False(string.IsNullOrWhiteSpace(encVoiceToken));
        await service.JoinVoiceAsync(channel.Id, activeMember.Id, "dev-ok", "PC");
        Assert.Contains(activeMember.Id, _voiceState.GetVoiceParticipants(channel.Id));

        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Joined, actorUserId: activeMember.Id);
        Assert.Equal(CallStatus.Joined, _messages.GetById(invite.Id)!.CallStatus);
    }

    [Fact]
    public async Task JoinVoiceAsync_WhenSecondParticipantJoins_TransitionsActiveCallInviteToJoinedAndClearsCallNotifications()
    {
        var channelId = "chan-call-answer";
        var caller = new Employee { Id = "user-caller", FirstName = "Caller", IsActive = true };
        var callee = new Employee { Id = "user-callee", FirstName = "Callee", IsActive = true };
        _employees.Save(caller);
        _employees.Save(callee);

        var channel = new ChatChannel
        {
            Id = channelId,
            Name = "Direct Call",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = [caller.Id, callee.Id]
        };
        _channels.Save(channel);

        var invite = new ChatMessage
        {
            Id = "invite-answer-1",
            ChannelId = channelId,
            SenderId = caller.Id,
            MessageType = ChatMessageType.CallInvite,
            CallStatus = CallStatus.Active,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(invite);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockRouting = new Mock<INotificationRoutingService>();
        mockRouting
            .Setup(r => r.ClearChannelNotificationsAsync(channelId))
            .Callback<string>(id => tcs.TrySetResult(id))
            .Returns(Task.CompletedTask);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            mockRouting.Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates);

        // First participant (caller) joins -> invite remains Active so recipients keep ringing
        await service.JoinVoiceAsync(channelId, caller.Id, "dev-1", "PC");
        Assert.Equal(CallStatus.Active, _messages.GetById(invite.Id)!.CallStatus);

        // Second participant (callee) joins -> invite transitions to Joined and clears call notifications
        await service.JoinVoiceAsync(channelId, callee.Id, "dev-2", "Android");
        Assert.Equal(CallStatus.Joined, _messages.GetById(invite.Id)!.CallStatus);

        var clearedChannelId = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(channelId, clearedChannelId);
    }

    [Fact]
    public async Task TryLeaveVoiceAsync_And_TrySetUserMutedAsync_RespectVoiceSessionFence()
    {
        await _service.JoinVoiceAsync("chan-fence", "user-1", "dev-1", "PC", "sess-new");
        Assert.Contains("user-1", _voiceState.GetVoiceParticipants("chan-fence"));

        // Stale session leave must be rejected without evicting user-1
        var staleLeave = await _service.TryLeaveVoiceAsync("chan-fence", "user-1", "sess-old");
        Assert.False(staleLeave);
        Assert.Contains("user-1", _voiceState.GetVoiceParticipants("chan-fence"));

        // Stale session mute must be rejected
        var staleMute = await _service.TrySetUserMutedAsync("chan-fence", "user-1", true, "sess-old");
        Assert.False(staleMute);
        Assert.False(_voiceState.IsUserMuted("chan-fence", "user-1"));

        // Matching session mute succeeds
        var validMute = await _service.TrySetUserMutedAsync("chan-fence", "user-1", true, "sess-new");
        Assert.True(validMute);
        Assert.True(_voiceState.IsUserMuted("chan-fence", "user-1"));

        // Matching session leave succeeds
        var validLeave = await _service.TryLeaveVoiceAsync("chan-fence", "user-1", "sess-new", evictFromLiveKit: false);
        Assert.True(validLeave);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("chan-fence"));

        // Closed session tombstone prevents ghost resurrection via ReconcileMemberStateAsync or late JoinVoiceAsync
        Assert.True(_voiceState.IsVoiceSessionClosed("sess-new"));
        await _service.ReconcileMemberStateAsync(
            "chan-fence",
            "user-1",
            new VoiceParticipantMetadata { VoiceSessionId = "sess-new", DeviceId = "dev-1", DeviceName = "PC" },
            false);
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("chan-fence"));

        await _service.JoinVoiceAsync("chan-fence", "user-1", "dev-1", "PC", "sess-new");
        Assert.DoesNotContain("user-1", _voiceState.GetVoiceParticipants("chan-fence"));
    }

    [Fact]
    public void SetVoiceParticipantSessionContext_PreservesHighSpecificityPlatformTagWithinSession()
    {
        _voiceState.NotifyVoiceMemberJoined("chan-plat", "user-1", "dev-1", "PC", "sess-plat");
        _voiceState.SetVoiceParticipantSessionContext("chan-plat", "user-1", "dev-1", "PC", "rnnoise", 20.0, "desktop", false);

        var initial = _voiceState.GetVoiceParticipantAudioTelemetry("chan-plat", "user-1");
        Assert.NotNull(initial);
        Assert.Equal("desktop", initial.Value.Platform);

        // Subsequent update with generic "web" fallback must not downgrade "desktop"
        _voiceState.SetVoiceParticipantSessionContext("chan-plat", "user-1", "dev-1", "PC", "rnnoise", 20.0, "web", false);
        var afterGeneric = _voiceState.GetVoiceParticipantAudioTelemetry("chan-plat", "user-1");
        Assert.NotNull(afterGeneric);
        Assert.Equal("desktop", afterGeneric.Value.Platform);
    }

    [Fact]
    public async Task SendCallInviteAsync_InDirectChannelWhenAlone_StartsRingingWithCallerSelectedRingtone()
    {
        var caller = new Employee
        {
            Id = "user-caller-ring",
            FirstName = "Alice",
            LastName = "Caller",
            IsActive = true
        };
        caller.SetNotificationSound(Spokes_Server.Core.Models.Communication.Notifications.NotificationCategories.Call, "mixkit_vintage_telephone_ringtone_1356");
        _employees.Save(caller);

        var dmChannel = new ChatChannel
        {
            Id = "dm-ring-1",
            Name = "Alice & Bob",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = ["user-caller-ring", "user-callee-ring"]
        };
        _channels.Save(dmChannel);

        var startTcs = new TaskCompletionSource<(string ChannelId, string SoundId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockBot = new Mock<IVoiceBotService>();
        mockBot.Setup(b => b.StartRingingAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, TimeSpan?, CancellationToken>((cid, sid, _, _) => startTcs.TrySetResult((cid, sid)))
            .Returns(Task.CompletedTask);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates,
            voiceBotService: mockBot.Object);

        _voiceState.NotifyVoiceMemberJoined(dmChannel.Id, caller.Id);

        var result = await service.SendCallInviteAsync(caller.Id, dmChannel.Id);
        Assert.True(result.IsSuccess);

        var started = await startTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(dmChannel.Id, started.ChannelId);
        Assert.Equal("mixkit_vintage_telephone_ringtone_1356.wav", started.SoundId);
    }

    [Fact]
    public async Task JoinVoiceAsync_And_DeclineCallInvite_StopRingingOnVoiceBot()
    {
        var caller = new Employee { Id = "u-caller-stop", FirstName = "Alice", LastName = "Caller", IsActive = true };
        var callee = new Employee { Id = "u-callee-stop", FirstName = "Bob", LastName = "Callee", IsActive = true };
        _employees.Save(caller);
        _employees.Save(callee);

        var dmChannel = new ChatChannel
        {
            Id = "dm-stop-ring",
            Name = "Alice & Bob",
            ChannelType = ChatChannelType.Direct,
            ParticipantIds = [caller.Id, callee.Id]
        };
        _channels.Save(dmChannel);

        var mockBot = new Mock<IVoiceBotService>();
        mockBot.Setup(b => b.EnsureBotJoinedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockBot.Setup(b => b.StopRingingAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var service = new VoiceChannelService(
            _voiceState,
            _chatState,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates,
            voiceBotService: mockBot.Object);

        await service.JoinVoiceAsync(dmChannel.Id, caller.Id);
        var inviteResult = await service.SendCallInviteAsync(caller.Id, dmChannel.Id);
        Assert.True(inviteResult.IsSuccess);

        // Declining the call invite must stop ringing immediately
        await service.UpdateCallInviteStatusAsync(inviteResult.Value!.Id, CallStatus.Declined, actorUserId: callee.Id);
        mockBot.Verify(b => b.StopRingingAsync(dmChannel.Id), Times.AtLeastOnce);

        mockBot.Invocations.Clear();

        // Second participant joining must also stop ringing before playing join chime
        await service.JoinVoiceAsync(dmChannel.Id, callee.Id);
        mockBot.Verify(b => b.StopRingingAsync(dmChannel.Id), Times.AtLeastOnce);
    }
}
