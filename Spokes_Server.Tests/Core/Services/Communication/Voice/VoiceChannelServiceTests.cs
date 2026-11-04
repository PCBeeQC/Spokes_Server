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
    private readonly ChatStateService _chatState;
    private readonly Mock<IChatRealtimeGateway> _mockRealtimeGateway;
    private readonly Mock<ILogger<VoiceChannelService>> _mockLogger;
    private readonly VoiceChannelService _service;

    public VoiceChannelServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"Spokes_Test_VoiceChannelService_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDataDir);

        var configDict = new Dictionary<string, string?> { ["DataPath"] = _testDataDir };
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
        _chatState = new ChatStateService();

        _mockRealtimeGateway = new Mock<IChatRealtimeGateway>();
        _mockLogger = new Mock<ILogger<VoiceChannelService>>();

        _service = new VoiceChannelService(
            _chatState,
            _mockRealtimeGateway.Object,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            new LiveKitService(Mock.Of<ILogger<LiveKitService>>()),
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

    [Fact]
    public void GenerateJoinToken_ValidInputs_ReturnsJwtString()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";
        string userAvatarUrl = "http://example.com/avatar.png";

        // Act
        var result = _service.GenerateJoinToken(channelId, userId, userName, userAvatarUrl);

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("eyJ", result);
    }

    [Fact]
    public void GenerateJoinToken_EmptyAvatar_ReturnsJwtString()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";

        // Act
        var result = _service.GenerateJoinToken(channelId, userId, userName, "");

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("eyJ", result);
    }

    [Fact]
    public void GenerateJoinToken_NullAvatar_ThrowsNullReferenceException()
    {
        // Arrange
        string channelId = "123";
        string userId = "user-1";
        string userName = "Alice";

        // Act & Assert
        Assert.Throws<NullReferenceException>(() =>
            _service.GenerateJoinToken(channelId, userId, userName, null!));
    }

    [Fact]
    public async Task JoinVoiceAsync_AddsParticipantAndSendsSignal()
    {
        // Act
        await _service.JoinVoiceAsync("chan-1", "user-1");

        // Assert
        var participants = _chatState.GetVoiceParticipants("chan-1");
        Assert.Contains("user-1", participants);
        _mockRealtimeGateway.Verify(g => g.SendToChannelAsync("chan-1", "VoiceMemberJoined", "chan-1", "user-1", default), Times.Once);
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
        var participants = _chatState.GetVoiceParticipants("chan-2");
        Assert.DoesNotContain("user-1", participants);

        var updatedMsg = _messages.GetById("msg-invite");
        Assert.NotNull(updatedMsg);
        Assert.Equal(CallStatus.Ended, updatedMsg.CallStatus);
    }

    [Fact]
    public async Task UpdateActiveSpeakersAsync_UpdatesStateAndSendsSignal()
    {
        // Act
        await _service.UpdateActiveSpeakersAsync("chan-3", new[] { "user-1", "user-2" });

        // Assert
        var speakers = _chatState.GetActiveSpeakers("chan-3");
        Assert.Contains("user-1", speakers);
        Assert.Contains("user-2", speakers);
        _mockRealtimeGateway.Verify(g => g.SendToChannelAsync("chan-3", "VoiceActiveSpeakersChanged", "chan-3", It.IsAny<string[]>(), default), Times.Once);
    }

    [Fact]
    public async Task SetUserSpeakingAsync_TogglesSpeakerState()
    {
        // Act
        await _service.SetUserSpeakingAsync("chan-4", "user-1", true);

        // Assert
        var speakers = _chatState.GetActiveSpeakers("chan-4");
        Assert.Contains("user-1", speakers);

        // Act 2: Stop speaking
        await _service.SetUserSpeakingAsync("chan-4", "user-1", false);
        speakers = _chatState.GetActiveSpeakers("chan-4");
        Assert.DoesNotContain("user-1", speakers);
    }

    [Fact]
    public async Task SetUserMutedAsync_UpdatesStateAndSendsSignal()
    {
        // Act
        await _service.SetUserMutedAsync("chan-5", "user-1", true);

        // Assert
        Assert.Contains("user-1", _chatState.GetMutedUsers("chan-5"));
        _mockRealtimeGateway.Verify(g => g.SendToChannelAsync("chan-5", "VoiceUserMutedChanged", "chan-5", "user-1", true, default), Times.Once);
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

    [Fact]
    public async Task LeaveVoiceAsync_WhenActiveInviteIsJoined_TransitionsToEndedWhenLastUserLeaves()
    {
        var channelId = "call_channel_1";
        var invite = new ChatMessage
        {
            ChannelId = channelId,
            SenderId = "user-1",
            MessageType = "CallInvite",
            CallStatus = CallStatus.Joined,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(invite);

        _chatState.NotifyVoiceMemberJoined(channelId, "user-1");

        await _service.LeaveVoiceAsync(channelId, "user-1");

        var updatedInvite = _messages.GetById(invite.Id);
        Assert.NotNull(updatedInvite);
        Assert.Equal(CallStatus.Ended, updatedInvite.CallStatus);
    }

    [Fact]
    public async Task LeaveVoiceAsync_WhenActiveInviteIsActive_TransitionsToEndedWhenLastUserLeaves()
    {
        var channelId = "call_channel_active";
        var invite = new ChatMessage
        {
            ChannelId = channelId,
            SenderId = "user-1",
            MessageType = "CallInvite",
            CallStatus = CallStatus.Active,
            SentAt = DateTime.UtcNow
        };
        _messages.Save(invite);

        _chatState.NotifyVoiceMemberJoined(channelId, "user-1");

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
            _chatState,
            _mockRealtimeGateway.Object,
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
            _mockRealtimeGateway.Object,
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
            _chatState,
            _mockRealtimeGateway.Object,
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
        Assert.DoesNotContain("user-1", _chatState.GetVoiceParticipants("chan-evict-true"));
    }

    [Fact]
    public async Task LeaveVoiceAsync_WhenEvictFromLiveKitIsFalse_SkipsRemoveParticipantAsyncAndCleansUpState()
    {
        var mockLiveKit = new Mock<LiveKitService>(Mock.Of<ILogger<LiveKitService>>(), null);
        var service = new VoiceChannelService(
            _chatState,
            _mockRealtimeGateway.Object,
            _messages,
            new Mock<INotificationRoutingService>().Object,
            _systemConfigs,
            mockLiveKit.Object,
            _mockLogger.Object,
            _employees,
            _channels,
            _readStates);

        await service.JoinVoiceAsync("chan-evict-false", "user-1");
        await service.LeaveVoiceAsync("chan-evict-false", "user-1", evictFromLiveKit: false);

        mockLiveKit.Verify(
            x => x.RemoveParticipantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.DoesNotContain("user-1", _chatState.GetVoiceParticipants("chan-evict-false"));
        _mockRealtimeGateway.Verify(g => g.SendToChannelAsync("chan-evict-false", "VoiceMemberLeft", "chan-evict-false", "user-1", default), Times.Once);
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
            _chatState,
            _mockRealtimeGateway.Object,
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
        Assert.DoesNotContain("user-outsider", _chatState.GetVoiceParticipants("chan-priv"));

        // Authorized channel member (even without a vault password) can generate a join token and join
        var token = service.GenerateJoinToken("chan-priv", "user-member", "Member", "");
        Assert.False(string.IsNullOrWhiteSpace(token));
        await service.JoinVoiceAsync("chan-priv", "user-member", "dev-2", "PC");
        Assert.Contains("user-member", _chatState.GetVoiceParticipants("chan-priv"));
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
            _chatState,
            _mockRealtimeGateway.Object,
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
        Assert.Empty(_chatState.GetVoiceParticipants("non-existent-channel"));

        // Missing employee -> fail closed!
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, "non-existent-emp", "Ghost", ""));
        await service.JoinVoiceAsync(channel.Id, "non-existent-emp");
        Assert.DoesNotContain("non-existent-emp", _chatState.GetVoiceParticipants(channel.Id));

        // Inactive or banned employee -> fail closed!
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, inactiveUser.Id, "Inactive", ""));
        Assert.Throws<UnauthorizedAccessException>(() =>
            service.GenerateJoinToken(channel.Id, bannedUser.Id, "Banned", ""));
        await service.JoinVoiceAsync(channel.Id, inactiveUser.Id, "dev-inact", "PC");
        await service.JoinVoiceAsync(channel.Id, bannedUser.Id, "dev-ban", "PC");
        Assert.DoesNotContain(inactiveUser.Id, _chatState.GetVoiceParticipants(channel.Id));
        Assert.DoesNotContain(bannedUser.Id, _chatState.GetVoiceParticipants(channel.Id));

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
        Assert.Contains(activeMember.Id, _chatState.GetVoiceParticipants(channel.Id));

        await service.UpdateCallInviteStatusAsync(invite.Id, CallStatus.Joined, actorUserId: activeMember.Id);
        Assert.Equal(CallStatus.Joined, _messages.GetById(invite.Id)!.CallStatus);
    }

    [Fact]
    public void GenerateCameraCompanionToken_ValidInputs_MintsJwtWithCamIdentity()
    {
        var token = _service.GenerateCameraCompanionToken("123", "user-1", "Alice", "http://example.com/avatar.png");

        Assert.False(string.IsNullOrWhiteSpace(token));
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payloadBase64.Length % 4)
        {
            case 2: payloadBase64 += "=="; break;
            case 3: payloadBase64 += "="; break;
        }
        var payloadJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
        Assert.Contains("\"sub\":\"user-1:cam\"", payloadJson);
        Assert.Contains("\"room\":\"channel-123\"", payloadJson);
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
            _chatState,
            _mockRealtimeGateway.Object,
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
}
