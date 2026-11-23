namespace Spokes_Server.Tests.Core.Services.Communication.Voice;

using Spokes_Server.Core.Services.Communication.Voice;

public class VoiceStateServiceTests
{
    private readonly VoiceStateService _service;

    public VoiceStateServiceTests()
    {
        _service = new VoiceStateService();
    }

    [Fact]
    public void NotifyVoiceMemberJoined_TriggersEventAndAddsToParticipants()
    {
        string chan = null, user = null;
        _service.VoiceMemberJoined += (c, u) => { chan = c; user = u; };

        _service.NotifyVoiceMemberJoined("vc1", "u1");

        Assert.Equal("vc1", chan);
        Assert.Equal("u1", user);
        var participants = _service.GetVoiceParticipants("vc1");
        Assert.Single(participants);
        Assert.Contains("u1", participants);
    }

    [Fact]
    public void NotifyVoiceMemberJoined_SameMemberTwice_DoesNotDuplicate()
    {
        bool first = _service.NotifyVoiceMemberJoined("vc1", "u1");
        bool second = _service.NotifyVoiceMemberJoined("vc1", "u1");

        Assert.True(first);
        Assert.False(second);
        var participants = _service.GetVoiceParticipants("vc1");
        Assert.Single(participants);
    }

    [Fact]
    public void NotifyVoiceMemberLeft_WhenPresent_ReturnsTrue_WhenNotPresent_ReturnsFalse()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        bool first = _service.NotifyVoiceMemberLeft("vc1", "u1");
        bool second = _service.NotifyVoiceMemberLeft("vc1", "u1");

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void NotifyVoiceMemberLeft_TriggersEventAndRemovesFromParticipants()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        _service.NotifyVoiceMemberJoined("vc1", "u2");

        string chan = null, user = null;
        _service.VoiceMemberLeft += (c, u) => { chan = c; user = u; };

        _service.NotifyVoiceMemberLeft("vc1", "u1");

        Assert.Equal("vc1", chan);
        Assert.Equal("u1", user);
        var participants = _service.GetVoiceParticipants("vc1");
        Assert.Single(participants);
        Assert.Contains("u2", participants);
    }

    [Fact]
    public void NotifyVoiceMemberLeft_LastMember_RemovesChannel()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        _service.NotifyVoiceMemberLeft("vc1", "u1");

        var participants = _service.GetVoiceParticipants("vc1");
        Assert.Empty(participants);
        Assert.Empty(_service.GetAllActiveVoiceChannels());
    }

    [Fact]
    public void NotifyVoiceMemberLeft_RemovesFromActiveSpeakers()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        _service.NotifyVoiceMemberJoined("vc1", "u2");
        _service.SetUserSpeaking("vc1", "u1", true);
        _service.SetUserSpeaking("vc1", "u2", true);

        string[] eventSpeakers = null;
        _service.VoiceActiveSpeakersChanged += (channelId, speakers) =>
        {
            eventSpeakers = speakers;
            // Verify subscriber can safely inspect state without deadlock
            Assert.Equal(speakers, _service.GetActiveSpeakers(channelId));
        };

        _service.NotifyVoiceMemberLeft("vc1", "u1");

        var activeSpeakers = _service.GetActiveSpeakers("vc1");
        Assert.Single(activeSpeakers);
        Assert.Contains("u2", activeSpeakers);
        Assert.NotNull(eventSpeakers);
        Assert.Equal(new[] { "u2" }, eventSpeakers);
    }

    [Fact]
    public void SetPhysicalRoomDspManaged_EnforcesSingleAuthority_SuppressesRawBleedAndHandsOffCleanly()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        _service.NotifyVoiceMemberJoined("vc1", "u2");

        int broadcastCount = 0;
        string[] lastBroadcast = [];
        _service.VoiceActiveSpeakersChanged += (_, speakers) =>
        {
            broadcastCount++;
            lastBroadcast = speakers;
        };

        // Outside DSP management, client self-report drives speaking normally
        _service.SetUserSpeaking("vc1", "u1", true);
        Assert.Contains("u1", _service.GetActiveSpeakers("vc1"));
        Assert.Equal(1, broadcastCount);

        // Claiming DSP authority switches u1 exclusively to post-isolation DSP state (raw client report ignored)
        _service.SetPhysicalRoomDspManaged("vc1", "u1", true);
        Assert.True(_service.IsPhysicalRoomDspManaged("vc1", "u1"));
        Assert.Empty(_service.GetActiveSpeakers("vc1"));
        Assert.Equal(2, broadcastCount);

        // Raw desk bleed self-reports while DSP-managed must NOT light up u1 or fire redundant events
        _service.SetUserSpeaking("vc1", "u1", false);
        _service.SetUserSpeaking("vc1", "u1", true);
        Assert.Empty(_service.GetActiveSpeakers("vc1"));
        Assert.Equal(2, broadcastCount);

        // Post-isolation DSP confirming speech lights up u1 cleanly
        _service.SetPhysicalRoomDspSpeaking("vc1", "u1", true);
        Assert.Contains("u1", _service.GetActiveSpeakers("vc1"));
        Assert.Equal(["u1"], lastBroadcast);
        Assert.Equal(3, broadcastCount);

        // Client self-report turning false does not cancel active DSP speech
        _service.SetUserSpeaking("vc1", "u1", false);
        Assert.Contains("u1", _service.GetActiveSpeakers("vc1"));
        Assert.Equal(3, broadcastCount);

        // Muting immediately clears DSP speech and broadcasts
        _service.SetUserMuted("vc1", "u1", true);
        Assert.Empty(_service.GetActiveSpeakers("vc1"));
        Assert.Equal(4, broadcastCount);

        // Releasing DSP authority restores client self-report ownership seamlessly
        _service.SetUserMuted("vc1", "u1", false);
        _service.SetUserSpeaking("vc1", "u1", true);
        Assert.Empty(_service.GetActiveSpeakers("vc1")); // still DSP-managed

        _service.SetPhysicalRoomDspManaged("vc1", "u1", false);
        Assert.False(_service.IsPhysicalRoomDspManaged("vc1", "u1"));
        Assert.Contains("u1", _service.GetActiveSpeakers("vc1")); // handed back to client self-report

        // Leaving cleans DSP managed & speaking flags
        _service.SetPhysicalRoomDspManaged("vc1", "u2", true);
        _service.SetPhysicalRoomDspSpeaking("vc1", "u2", true);
        _service.NotifyVoiceMemberLeft("vc1", "u2");
        Assert.False(_service.IsPhysicalRoomDspManaged("vc1", "u2"));
        Assert.DoesNotContain("u2", _service.GetActiveSpeakers("vc1"));
    }

    [Fact]
    public void NotifyVoiceMemberLeft_NotInChannel_DoesNotThrow()
    {
        var ex = Record.Exception(() => _service.NotifyVoiceMemberLeft("vc1", "u1"));
        Assert.Null(ex);
    }

    [Fact]
    public void GetVoiceParticipants_ChannelDoesNotExist_ReturnsEmptyList()
    {
        var participants = _service.GetVoiceParticipants("unknown");

        Assert.NotNull(participants);
        Assert.Empty(participants);
    }

    [Fact]
    public void GetAllActiveVoiceChannels_NoChannels_ReturnsEmptyList()
    {
        var channels = _service.GetAllActiveVoiceChannels();

        Assert.NotNull(channels);
        Assert.Empty(channels);
    }

    [Fact]
    public void GetAllActiveVoiceChannels_ReturnsAllChannelsWithParticipants()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");
        _service.NotifyVoiceMemberJoined("vc2", "u2");

        var channels = _service.GetAllActiveVoiceChannels();

        Assert.Equal(2, channels.Count);
        Assert.Contains("vc1", channels);
        Assert.Contains("vc2", channels);
    }

    [Fact]
    public void IsUserInAnyVoiceChannel_UserInChannel_ReturnsTrue()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");

        Assert.True(_service.IsUserInAnyVoiceChannel("u1"));
    }

    [Fact]
    public void IsUserInAnyVoiceChannel_UserNotInChannel_ReturnsFalse()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1");

        Assert.False(_service.IsUserInAnyVoiceChannel("u2"));
    }

    [Fact]
    public void GetActiveSpeakers_ChannelNoSpeakers_ReturnsEmptyArray()
    {
        var speakers = _service.GetActiveSpeakers("vc1");

        Assert.NotNull(speakers);
        Assert.Empty(speakers);
    }

    [Fact]
    public void SetUserMuted_AddsAndRemovesMutedUser_AndTriggersEvent()
    {
        string? changedChannel = null;
        string? changedUser = null;
        bool? changedMuted = null;

        _service.VoiceUserMutedChanged += (c, u, m) =>
        {
            changedChannel = c;
            changedUser = u;
            changedMuted = m;
        };

        // Mute user
        _service.SetUserMuted("ch1", "u1", true);

        var muted = _service.GetMutedUsers("ch1");
        Assert.Contains("u1", muted);
        Assert.Equal("ch1", changedChannel);
        Assert.Equal("u1", changedUser);
        Assert.True(changedMuted);

        // Unmute user
        _service.SetUserMuted("ch1", "u1", false);

        muted = _service.GetMutedUsers("ch1");
        Assert.DoesNotContain("u1", muted);
        Assert.False(changedMuted);
    }

    [Fact]
    public void NotifyVoiceMemberLeft_RemovesMutedUser_AndTriggersEvent()
    {
        string? changedChannel = null;
        string? changedUser = null;
        bool? changedMuted = null;

        _service.SetUserMuted("ch1", "u1", true);
        Assert.Contains("u1", _service.GetMutedUsers("ch1"));

        _service.VoiceUserMutedChanged += (c, u, m) =>
        {
            changedChannel = c;
            changedUser = u;
            changedMuted = m;
        };

        _service.NotifyVoiceMemberLeft("ch1", "u1");

        Assert.DoesNotContain("u1", _service.GetMutedUsers("ch1"));
        Assert.Equal("ch1", changedChannel);
        Assert.Equal("u1", changedUser);
        Assert.False(changedMuted);
    }

    [Fact]
    public void IsVoiceDeviceConnected_ReturnsTrueOnlyWhileDeviceIsActiveInVoiceChannel()
    {
        Assert.False(_service.IsVoiceDeviceConnected("u1", "dev-1"));

        _service.NotifyVoiceMemberJoined("vc1", "u1", deviceId: "dev-1", deviceName: "Laptop");

        Assert.True(_service.IsVoiceDeviceConnected("u1", "dev-1"));
        Assert.False(_service.IsVoiceDeviceConnected("u1", "dev-2"));
        Assert.False(_service.IsVoiceDeviceConnected("u2", "dev-1"));

        _service.NotifyVoiceMemberLeft("vc1", "u1");

        Assert.False(_service.IsVoiceDeviceConnected("u1", "dev-1"));
    }

    [Fact]
    public void IsVoiceDeviceConnected_NullOrWhitespaceArgs_ReturnsFalse()
    {
        _service.NotifyVoiceMemberJoined("vc1", "u1", deviceId: "dev-1", deviceName: "Laptop");

        Assert.False(_service.IsVoiceDeviceConnected(null, "dev-1"));
        Assert.False(_service.IsVoiceDeviceConnected("u1", null));
        Assert.False(_service.IsVoiceDeviceConnected(" ", "dev-1"));
        Assert.False(_service.IsVoiceDeviceConnected("u1", " "));
    }
}
