namespace Spokes_Server.Tests.Core.Services.Communication;

using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services.Communication;

public class ChatStateServiceTests
{
        private readonly ChatStateService _service;

        public ChatStateServiceTests()
        {
            _service = new ChatStateService();
        }

        [Fact]
        public void NotifyMessageReceived_TriggersEvent()
        {
            ChatMessage received = null;
            _service.MessageReceived += (msg) => received = msg;
            var expected = new ChatMessage { Id = "msg-1" };

            _service.NotifyMessageReceived(expected);

            Assert.Same(expected, received);
        }

        [Fact]
        public void NotifyUserTyping_TriggersEvent()
        {
            string chan = null, user = null, name = null;
            _service.UserTyping += (c, u, n) => { chan = c; user = u; name = n; };

            _service.NotifyUserTyping("c1", "u1", "n1");

            Assert.Equal("c1", chan);
            Assert.Equal("u1", user);
            Assert.Equal("n1", name);
        }

        [Fact]
        public void NotifyUserStoppedTyping_TriggersEvent()
        {
            string chan = null, user = null;
            _service.UserStoppedTyping += (c, u) => { chan = c; user = u; };

            _service.NotifyUserStoppedTyping("c1", "u1");

            Assert.Equal("c1", chan);
            Assert.Equal("u1", user);
        }

        [Fact]
        public void NotifyMessageEdited_TriggersEvent()
        {
            ChatMessage edited = null;
            _service.MessageEdited += (msg) => edited = msg;
            var expected = new ChatMessage { Id = "msg-1" };

            _service.NotifyMessageEdited(expected);

            Assert.Same(expected, edited);
        }

        [Fact]
        public void NotifyMessageDeleted_TriggersEvent()
        {
            string chan = null, msg = null;
            _service.MessageDeleted += (c, m) => { chan = c; msg = m; };

            _service.NotifyMessageDeleted("c1", "m1");

            Assert.Equal("c1", chan);
            Assert.Equal("m1", msg);
        }

        [Fact]
        public void NotifyReactionAdded_TriggersEvent()
        {
            string chan = null, msg = null, emoji = null, user = null, name = null;
            _service.ReactionAdded += (c, m, e, u, n) => { chan = c; msg = m; emoji = e; user = u; name = n; };

            _service.NotifyReactionAdded("c1", "m1", "😊", "u1", "n1");

            Assert.Equal("c1", chan);
            Assert.Equal("m1", msg);
            Assert.Equal("😊", emoji);
            Assert.Equal("u1", user);
            Assert.Equal("n1", name);
        }

        [Fact]
        public void NotifyReactionRemoved_TriggersEvent()
        {
            string chan = null, msg = null, emoji = null, user = null;
            _service.ReactionRemoved += (c, m, e, u) => { chan = c; msg = m; emoji = e; user = u; };

            _service.NotifyReactionRemoved("c1", "m1", "😊", "u1");

            Assert.Equal("c1", chan);
            Assert.Equal("m1", msg);
            Assert.Equal("😊", emoji);
            Assert.Equal("u1", user);
        }

        [Fact]
        public void NotifyChannelCreated_TriggersEvent()
        {
            ChatChannel created = null;
            _service.ChannelCreated += (chan) => created = chan;
            var expected = new ChatChannel { Id = "c1" };

            _service.NotifyChannelCreated(expected);

            Assert.Same(expected, created);
        }

        [Fact]
        public void NotifyCalendarEventUpdated_TriggersEvent()
        {
            CalendarEvent updated = null;
            _service.CalendarEventUpdated += (evt) => updated = evt;
            var expected = new CalendarEvent { Id = "evt-1", Title = "Company Meetup" };

            _service.NotifyCalendarEventUpdated(expected);

            Assert.Same(expected, updated);
        }

        [Fact]
        public void NotifyModerationAlert_TriggersEvent()
        {
            ReportedMessage report = null;
            _service.ModerationAlertReceived += (r) => report = r;
            var expected = new ReportedMessage { Id = "rep-1" };

            _service.NotifyModerationAlert(expected);

            Assert.Same(expected, report);
        }

        [Fact]
        public void NotifyModerationAlert_NullInput_TriggersEventWithNull()
        {
            ReportedMessage report = new ReportedMessage();
            _service.ModerationAlertReceived += (r) => report = r;

            _service.NotifyModerationAlert(null);

            Assert.Null(report);
        }

        [Fact]
        public void NotifyEmployeeProfileUpdated_TriggersEvent()
        {
            Employee profile = null;
            _service.EmployeeProfileUpdated += (e) => profile = e;
            var expected = new Employee { Id = "emp-1" };

            _service.NotifyEmployeeProfileUpdated(expected);

            Assert.Same(expected, profile);
        }

        [Fact]
        public void NotifyEmployeeProfileUpdated_NullInput_TriggersEventWithNull()
        {
            Employee profile = new Employee();
            _service.EmployeeProfileUpdated += (e) => profile = e;

            _service.NotifyEmployeeProfileUpdated(null);

            Assert.Null(profile);
        }

        [Fact]
        public void NotifyMessageRead_TriggersEvent()
        {
            string chan = null, msg = null;
            List<string> read = null;
            _service.MessageRead += (c, m, r) => { chan = c; msg = m; read = r; };
            var expectedRead = new List<string> { "u1", "u2" };

            _service.NotifyMessageRead("c1", "m1", expectedRead);

            Assert.Equal("c1", chan);
            Assert.Equal("m1", msg);
            Assert.Same(expectedRead, read);
        }

        [Fact]
        public void NotifyMessageRead_NullList_TriggersEvent()
        {
            List<string> read = new List<string>();
            _service.MessageRead += (c, m, r) => { read = r; };

            _service.NotifyMessageRead("c1", "m1", null);

            Assert.Null(read);
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
            _service.NotifyVoiceMemberJoined("vc1", "u1");
            _service.NotifyVoiceMemberJoined("vc1", "u1");

            var participants = _service.GetVoiceParticipants("vc1");
            Assert.Single(participants);
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
            _service.NotifyVoiceActiveSpeakersChanged("vc1", new[] { "u1", "u2" });

            _service.NotifyVoiceMemberLeft("vc1", "u1");

            var speakers = _service.GetActiveSpeakers("vc1");
            Assert.Single(speakers);
            Assert.Contains("u2", speakers);
        }
        
        [Fact]
        public void NotifyVoiceMemberLeft_NotInChannel_DoesNotThrow()
        {
            var ex = Record.Exception(() => _service.NotifyVoiceMemberLeft("vc1", "u1"));
            Assert.Null(ex);
        }

        [Fact]
        public void NotifyVoiceActiveSpeakersChanged_TriggersEventAndUpdatesSpeakers()
        {
            string chan = null;
            string[] speakers = null;
            _service.VoiceActiveSpeakersChanged += (c, s) => { chan = c; speakers = s; };
            var expectedSpeakers = new[] { "u1", "u2" };

            _service.NotifyVoiceActiveSpeakersChanged("vc1", expectedSpeakers);

            Assert.Equal("vc1", chan);
            Assert.Same(expectedSpeakers, speakers);
            Assert.Same(expectedSpeakers, _service.GetActiveSpeakers("vc1"));
        }

        [Fact]
        public void NotifyVoiceActiveSpeakersChanged_NullSpeakers_UpdatesToNull()
        {
            _service.NotifyVoiceActiveSpeakersChanged("vc1", null);

            var speakers = _service.GetActiveSpeakers("vc1");
            Assert.Null(speakers);
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
    }
