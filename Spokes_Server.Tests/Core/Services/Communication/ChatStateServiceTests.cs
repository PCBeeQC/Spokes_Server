using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using Spokes_Server.Core.Services;

namespace Spokes_Server.Tests.Core.Services.Communication
{
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
    }
}


