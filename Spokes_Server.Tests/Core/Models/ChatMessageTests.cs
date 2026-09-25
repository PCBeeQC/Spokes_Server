using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Models
{
    public class ChatMessageTests
    {
        #region ChatMessage Defaults & Property Mutation Tests

        [Fact]
        public void ChatMessage_Defaults_AreSetCorrectly()
        {
            var msg = new ChatMessage();

            Assert.False(string.IsNullOrEmpty(msg.Id));
            Assert.True(Guid.TryParse(msg.Id, out _));
            Assert.Equal(string.Empty, msg.ChannelId);
            Assert.Equal(ChatChannelType.General, msg.ChannelType);
            Assert.Equal(string.Empty, msg.SenderId);
            Assert.Equal(string.Empty, msg.Content);
            Assert.Equal("Text", msg.MessageType);
            Assert.Equal(string.Empty, msg.CallStatus);
            Assert.True(msg.SentAt <= DateTime.UtcNow);
            Assert.Null(msg.EditedAt);
            Assert.False(msg.IsDeleted);
            Assert.False(msg.IsEncrypted);
            Assert.NotNull(msg.Reactions);
            Assert.Empty(msg.Reactions);
            Assert.Null(msg.ReplyToId);
            Assert.NotNull(msg.Attachments);
            Assert.Empty(msg.Attachments);
            Assert.NotNull(msg.MentionedUserIds);
            Assert.Empty(msg.MentionedUserIds);
            Assert.NotNull(msg.MentionedEventIds);
            Assert.Empty(msg.MentionedEventIds);
            Assert.NotNull(msg.MentionedAlbumIds);
            Assert.Empty(msg.MentionedAlbumIds);
            Assert.False(msg.MentionsEveryone);
            Assert.False(msg.MentionsTeam);
            Assert.NotNull(msg.ReadBy);
            Assert.Empty(msg.ReadBy);
        }

        [Fact]
        public void ChatMessage_Properties_CanBeMutated()
        {
            var sentAt = DateTime.UtcNow.AddHours(-2);
            var editedAt = DateTime.UtcNow.AddHours(-1);
            var reactions = new List<string> { "👍:user-1", "❤️:user-2" };
            var attachments = new List<ChatAttachment>
            {
                new() { FileName = "doc.pdf", FilePath = "/files/doc.pdf" }
            };
            var mentionedUsers = new List<string> { "user-2", "user-3" };
            var mentionedEvents = new List<string> { "event-1" };
            var mentionedAlbums = new List<string> { "album-1" };
            var readBy = new List<string> { "user-2", "user-3" };

            var msg = new ChatMessage
            {
                Id = "msg-100",
                ChannelId = "chan-200",
                ChannelType = ChatChannelType.Project,
                SenderId = "user-1",
                Content = "Hello @user-2 #album-1",
                MessageType = "CallInvite",
                CallStatus = "Active",
                SentAt = sentAt,
                EditedAt = editedAt,
                IsDeleted = true,
                IsEncrypted = true,
                Reactions = reactions,
                ReplyToId = "msg-parent",
                Attachments = attachments,
                MentionedUserIds = mentionedUsers,
                MentionedEventIds = mentionedEvents,
                MentionedAlbumIds = mentionedAlbums,
                MentionsEveryone = true,
                MentionsTeam = true,
                ReadBy = readBy
            };

            Assert.Equal("msg-100", msg.Id);
            Assert.Equal("chan-200", msg.ChannelId);
            Assert.Equal(ChatChannelType.Project, msg.ChannelType);
            Assert.Equal("user-1", msg.SenderId);
            Assert.Equal("Hello @user-2 #album-1", msg.Content);
            Assert.Equal("CallInvite", msg.MessageType);
            Assert.Equal("Active", msg.CallStatus);
            Assert.Equal(sentAt, msg.SentAt);
            Assert.Equal(editedAt, msg.EditedAt);
            Assert.True(msg.IsDeleted);
            Assert.True(msg.IsEncrypted);
            Assert.Equal(reactions, msg.Reactions);
            Assert.Equal("msg-parent", msg.ReplyToId);
            Assert.Equal(attachments, msg.Attachments);
            Assert.Equal(mentionedUsers, msg.MentionedUserIds);
            Assert.Equal(mentionedEvents, msg.MentionedEventIds);
            Assert.Equal(mentionedAlbums, msg.MentionedAlbumIds);
            Assert.True(msg.MentionsEveryone);
            Assert.True(msg.MentionsTeam);
            Assert.Equal(readBy, msg.ReadBy);
        }

        #endregion

        #region Equals & GetHashCode Tests

        [Fact]
        public void Equals_SameReference_ReturnsTrue()
        {
            var msg = new ChatMessage();

            Assert.True(msg.Equals(msg));
        }

        [Fact]
        public void Equals_SameId_ReturnsTrue()
        {
            var sharedId = Guid.NewGuid().ToString();
            var msg1 = new ChatMessage { Id = sharedId, Content = "Content 1" };
            var msg2 = new ChatMessage { Id = sharedId, Content = "Content 2" };

            Assert.True(msg1.Equals(msg2));
            Assert.True(msg2.Equals(msg1));
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var msg1 = new ChatMessage { Id = "id-1" };
            var msg2 = new ChatMessage { Id = "id-2" };

            Assert.False(msg1.Equals(msg2));
            Assert.False(msg2.Equals(msg1));
        }

        [Fact]
        public void Equals_NullOrDifferentType_ReturnsFalse()
        {
            var msg = new ChatMessage();

            Assert.False(msg.Equals(null));
            Assert.False(msg.Equals("a string"));
            Assert.False(msg.Equals(new object()));
        }

        [Fact]
        public void GetHashCode_SameId_ReturnsSameHashCode()
        {
            var sharedId = "msg-hash-test-id";
            var msg1 = new ChatMessage { Id = sharedId };
            var msg2 = new ChatMessage { Id = sharedId };

            Assert.Equal(msg1.GetHashCode(), msg2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_NullId_FallsBackToBaseHashCodeWithoutThrowing()
        {
            var msg = new ChatMessage { Id = null! };

            var exception = Record.Exception(() => msg.GetHashCode());

            Assert.Null(exception);
        }

        #endregion

        #region IsReadBy Tests

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
        public void IsReadBy_NullReadBy_ReturnsFalseForOtherUser_AndTrueForSender()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = null!
            };

            Assert.True(msg.IsReadBy("user-alice"));
            Assert.False(msg.IsReadBy("user-bob"));
        }

        #endregion

        #region GetAllReaders Tests

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

        [Fact]
        public void GetAllReaders_WhenReadByIsNull_AndSenderIdProvided_ReturnsSender()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = null!
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Single(readers);
            Assert.Equal("user-alice", readers[0]);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void GetAllReaders_WhenReadByIsEmptyOrNull_AndSenderIdNullOrEmpty_ReturnsEmpty(string? emptySenderId)
        {
            var msgNullReadBy = new ChatMessage
            {
                SenderId = emptySenderId!,
                ReadBy = null!
            };
            var msgEmptyReadBy = new ChatMessage
            {
                SenderId = emptySenderId!,
                ReadBy = new()
            };

            Assert.Empty(msgNullReadBy.GetAllReaders());
            Assert.Empty(msgEmptyReadBy.GetAllReaders());
        }

        [Fact]
        public void GetAllReaders_WhenReadByHasMultipleReadersWithoutSender_AppendsSender()
        {
            var msg = new ChatMessage
            {
                SenderId = "user-alice",
                ReadBy = new() { "user-bob", "user-charlie" }
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Equal(3, readers.Count);
            Assert.Equal("user-bob", readers[0]);
            Assert.Equal("user-charlie", readers[1]);
            Assert.Equal("user-alice", readers[2]);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void GetAllReaders_WhenReadByHasMultipleReaders_AndSenderIdIsEmptyOrNull_YieldsReadByOnly(string? emptySenderId)
        {
            var msg = new ChatMessage
            {
                SenderId = emptySenderId!,
                ReadBy = new() { "user-bob", "user-charlie" }
            };

            var readers = msg.GetAllReaders().ToList();

            Assert.Equal(2, readers.Count);
            Assert.Equal(new[] { "user-bob", "user-charlie" }, readers);
        }

        #endregion

        #region ChatAttachment Tests

        [Fact]
        public void ChatAttachment_Defaults_AreSetCorrectly()
        {
            var attachment = new ChatAttachment();

            Assert.False(string.IsNullOrEmpty(attachment.Id));
            Assert.True(Guid.TryParse(attachment.Id, out _));
            Assert.Equal(string.Empty, attachment.FileName);
            Assert.Equal(string.Empty, attachment.FilePath);
            Assert.Equal(string.Empty, attachment.ContentType);
            Assert.Equal(0L, attachment.FileSizeBytes);
            Assert.False(attachment.HasServerThumbnail);
            Assert.Null(attachment.ImageWidth);
            Assert.Null(attachment.ImageHeight);
            Assert.Equal(string.Empty, attachment.ThumbnailUrl);
        }

        [Fact]
        public void ChatAttachment_Properties_CanBeMutated()
        {
            var attachment = new ChatAttachment
            {
                Id = "att-123",
                FileName = "photo.png",
                FilePath = "/spokesapi/files/photo.png",
                ContentType = "image/png",
                FileSizeBytes = 2048,
                HasServerThumbnail = true,
                ImageWidth = 1920,
                ImageHeight = 1080
            };

            Assert.Equal("att-123", attachment.Id);
            Assert.Equal("photo.png", attachment.FileName);
            Assert.Equal("/spokesapi/files/photo.png", attachment.FilePath);
            Assert.Equal("image/png", attachment.ContentType);
            Assert.Equal(2048, attachment.FileSizeBytes);
            Assert.True(attachment.HasServerThumbnail);
            Assert.Equal(1920, attachment.ImageWidth);
            Assert.Equal(1080, attachment.ImageHeight);
            Assert.Equal("/spokesapi/files/thumb/photo.png", attachment.ThumbnailUrl);
        }

        [Fact]
        public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsFalse_ReturnsFilePath()
        {
            var attachment = new ChatAttachment
            {
                HasServerThumbnail = false,
                FilePath = "/spokesapi/files/photo.png"
            };

            Assert.Equal("/spokesapi/files/photo.png", attachment.ThumbnailUrl);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ChatAttachment_ThumbnailUrl_WhenFilePathIsNullOrWhitespace_ReturnsFilePath(string? filePath)
        {
            var attachment = new ChatAttachment
            {
                HasServerThumbnail = true,
                FilePath = filePath!
            };

            Assert.Equal(filePath, attachment.ThumbnailUrl);
        }

        [Fact]
        public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_SpokesApiFiles_ReplacesPrefix()
        {
            var attachment = new ChatAttachment
            {
                HasServerThumbnail = true,
                FilePath = "/spokesapi/files/images/doc.png"
            };

            Assert.Equal("/spokesapi/files/thumb/images/doc.png", attachment.ThumbnailUrl);
        }

        [Fact]
        public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_InternalAttachments_ReplacesPrefix()
        {
            var attachment = new ChatAttachment
            {
                HasServerThumbnail = true,
                FilePath = "/internal/attachments/avatar.jpg"
            };

            Assert.Equal("/internal/attachments/thumb/avatar.jpg", attachment.ThumbnailUrl);
        }

        [Fact]
        public void ChatAttachment_ThumbnailUrl_WhenHasServerThumbnailIsTrue_OtherPrefix_ReturnsFilePath()
        {
            var attachment = new ChatAttachment
            {
                HasServerThumbnail = true,
                FilePath = "/external/cdn/photo.png"
            };

            Assert.Equal("/external/cdn/photo.png", attachment.ThumbnailUrl);
        }

        #endregion

        #region ChatChannelType Tests

        [Fact]
        public void ChatChannelType_Constants_HaveExpectedValues()
        {
            Assert.Equal("Project", ChatChannelType.Project);
            Assert.Equal("Team", ChatChannelType.Team);
            Assert.Equal("Direct", ChatChannelType.Direct);
            Assert.Equal("General", ChatChannelType.General);
            Assert.Equal("Group", ChatChannelType.Group);
        }

        #endregion
    }
}
