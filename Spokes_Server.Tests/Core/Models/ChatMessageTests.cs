using System.Linq;
using Spokes_Server.Core.Models.Communication;
using Xunit;

namespace Spokes_Server.Tests.Core.Models
{
    public class ChatMessageTests
    {
        [Fact]
        public void IsReadBy_Sender_AlwaysReturnsTrue()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() // Empty ReadBy
            };

            Assert.True(msg.IsReadBy("user-alice"));
        }

        [Fact]
        public void IsReadBy_OtherUserInReadBy_ReturnsTrue()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() { "user-bob" }
            };

            Assert.True(msg.IsReadBy("user-bob"));
        }

        [Fact]
        public void IsReadBy_OtherUserNotInReadBy_ReturnsFalse()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() { "user-bob" }
            };

            Assert.False(msg.IsReadBy("user-charlie"));
        }

        [Fact]
        public void IsReadBy_NullOrEmptyUser_ReturnsFalse()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice"
            };

            Assert.False(msg.IsReadBy(""));
            Assert.False(msg.IsReadBy(null!));
        }

        [Fact]
        public void GetAllReaders_IncludesSenderEvenIfMissingInReadBy()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() { "user-bob" }
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Contains("user-alice", readers);
            Assert.Contains("user-bob", readers);
            Assert.Equal(2, readers.Count);
        }

        [Fact]
        public void GetAllReaders_DoesNotDuplicateSenderIfAlreadyInReadBy()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() { "user-alice", "user-bob" }
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Equal(2, readers.Count);
            Assert.Equal(1, readers.Count(r => r == "user-alice"));
        }

        [Fact]
        public void GetAllReaders_EmptyReadBy_ReturnsSender()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new()
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Single(readers);
            Assert.Equal("user-alice", readers[0]);
        }
    }
}
