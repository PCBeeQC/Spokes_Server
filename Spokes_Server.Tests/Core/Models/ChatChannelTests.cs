using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Core.Models
{
    public class ChatChannelTests
    {
        [Fact]
        public void ChatChannel_Initialization_SetsDefaultsCorrectly()
        {
            var channel = new ChatChannel();

            Assert.False(string.IsNullOrEmpty(channel.Id));
            Assert.True(Guid.TryParse(channel.Id, out _));
            Assert.Equal(string.Empty, channel.Name);
            Assert.Null(channel.Icon);
            Assert.Null(channel.CategoryId);
            Assert.Equal(ChatChannelType.General, channel.ChannelType);
            Assert.Null(channel.LinkedEntityId);
            Assert.Equal(string.Empty, channel.Description);
            Assert.Empty(channel.ParticipantIds);
            Assert.False(channel.IsDefaultGeneral);
            Assert.False(channel.IsVoiceChannel);
            Assert.False(channel.IsAnnouncementOnly);
            Assert.True(channel.SupportsAnnouncements);
            Assert.Empty(channel.AllowedPostUserIds);
            Assert.Empty(channel.AllowedPostTeamIds);
            Assert.Empty(channel.PinnedMessageIds);
            Assert.Equal(0, channel.DisplayOrder);
            Assert.Empty(channel.AllowedTeamIds);
            Assert.False(channel.IsArchived);
            Assert.False(channel.IsEncrypted);
            Assert.NotNull(channel.EncryptedChannelKeys);
            Assert.Empty(channel.EncryptedChannelKeys);
            Assert.Equal(64, channel.AudioBitrateKbps);
            Assert.True(channel.CreatedAt <= DateTime.UtcNow);
            Assert.Equal(string.Empty, channel.CreatedById);
            Assert.True(channel.LastActivityAt <= DateTime.UtcNow);
        }

        [Theory]
        [InlineData(ChatChannelType.Direct, false)]
        [InlineData(ChatChannelType.General, true)]
        [InlineData(ChatChannelType.Team, true)]
        [InlineData(ChatChannelType.Project, true)]
        [InlineData(ChatChannelType.Group, true)]
        [InlineData("CustomChannelType", true)]
        public void SupportsAnnouncements_ReturnsExpectedValue_BasedOnChannelType(string channelType, bool expected)
        {
            var channel = new ChatChannel { ChannelType = channelType };

            Assert.Equal(expected, channel.SupportsAnnouncements);
        }

        [Fact]
        public void Normalize_DirectChannel_ResetsAnnouncementSettings()
        {
            var channel = new ChatChannel
            {
                ChannelType = ChatChannelType.Direct,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "user-1", "user-2" },
                AllowedPostTeamIds = new List<string> { "team-1" }
            };

            channel.Normalize();

            Assert.False(channel.IsAnnouncementOnly);
            Assert.Empty(channel.AllowedPostUserIds);
            Assert.Empty(channel.AllowedPostTeamIds);
        }

        [Theory]
        [InlineData(ChatChannelType.General)]
        [InlineData(ChatChannelType.Team)]
        [InlineData(ChatChannelType.Project)]
        [InlineData(ChatChannelType.Group)]
        [InlineData("CustomType")]
        public void Normalize_NonDirectChannel_RetainsAnnouncementSettings(string channelType)
        {
            var channel = new ChatChannel
            {
                ChannelType = channelType,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "user-1", "user-2" },
                AllowedPostTeamIds = new List<string> { "team-1" }
            };

            channel.Normalize();

            Assert.True(channel.IsAnnouncementOnly);
            Assert.Equal(new[] { "user-1", "user-2" }, channel.AllowedPostUserIds);
            Assert.Equal(new[] { "team-1" }, channel.AllowedPostTeamIds);
        }

        [Fact]
        public void Normalize_DirectChannelWithNullLists_DoesNotThrow()
        {
            var channel = new ChatChannel
            {
                ChannelType = ChatChannelType.Direct,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = null!,
                AllowedPostTeamIds = null!
            };

            var exception = Record.Exception(() => channel.Normalize());

            Assert.Null(exception);
            Assert.False(channel.IsAnnouncementOnly);
        }

        [Fact]
        public void Equals_SameReference_ReturnsTrue()
        {
            var channel = new ChatChannel();

            Assert.True(channel.Equals(channel));
        }

        [Fact]
        public void Equals_SameId_ReturnsTrue()
        {
            var channelId = Guid.NewGuid().ToString();
            var channel1 = new ChatChannel { Id = channelId, Name = "Channel A" };
            var channel2 = new ChatChannel { Id = channelId, Name = "Channel B" };

            Assert.True(channel1.Equals(channel2));
            Assert.True(channel2.Equals(channel1));
        }

        [Fact]
        public void Equals_DifferentId_ReturnsFalse()
        {
            var channel1 = new ChatChannel { Id = "id-1" };
            var channel2 = new ChatChannel { Id = "id-2" };

            Assert.False(channel1.Equals(channel2));
        }

        [Fact]
        public void Equals_NullOrDifferentType_ReturnsFalse()
        {
            var channel = new ChatChannel();

            Assert.False(channel.Equals(null));
            Assert.False(channel.Equals("string object"));
            Assert.False(channel.Equals(new object()));
        }

        [Fact]
        public void GetHashCode_SameId_ReturnsSameHashCode()
        {
            var channelId = "same-id-12345";
            var channel1 = new ChatChannel { Id = channelId };
            var channel2 = new ChatChannel { Id = channelId };

            Assert.Equal(channel1.GetHashCode(), channel2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_NullId_DoesNotThrow()
        {
            var channel = new ChatChannel { Id = null! };

            var exception = Record.Exception(() => channel.GetHashCode());

            Assert.Null(exception);
        }

        [Fact]
        public void Properties_CanBeMutatedAndRead()
        {
            var now = DateTime.UtcNow;
            var keys = new Dictionary<string, string> { ["device1"] = "key123" };
            var channel = new ChatChannel
            {
                Id = "custom-id",
                Name = "General Discussion",
                Icon = "icon-chat",
                CategoryId = "cat-1",
                ChannelType = ChatChannelType.Team,
                LinkedEntityId = "team-123",
                Description = "General team discussion",
                ParticipantIds = new List<string> { "emp-1" },
                IsDefaultGeneral = true,
                IsVoiceChannel = true,
                IsAnnouncementOnly = true,
                AllowedPostUserIds = new List<string> { "emp-1" },
                AllowedPostTeamIds = new List<string> { "team-1" },
                PinnedMessageIds = new List<string> { "msg-1" },
                DisplayOrder = 5,
                AllowedTeamIds = new List<string> { "team-2" },
                IsArchived = true,
                IsEncrypted = true,
                EncryptedChannelKeys = keys,
                AudioBitrateKbps = 128,
                CreatedAt = now,
                CreatedById = "emp-creator",
                LastActivityAt = now
            };

            Assert.Equal("custom-id", channel.Id);
            Assert.Equal("General Discussion", channel.Name);
            Assert.Equal("icon-chat", channel.Icon);
            Assert.Equal("cat-1", channel.CategoryId);
            Assert.Equal(ChatChannelType.Team, channel.ChannelType);
            Assert.Equal("team-123", channel.LinkedEntityId);
            Assert.Equal("General team discussion", channel.Description);
            Assert.Equal(new[] { "emp-1" }, channel.ParticipantIds);
            Assert.True(channel.IsDefaultGeneral);
            Assert.True(channel.IsVoiceChannel);
            Assert.True(channel.IsAnnouncementOnly);
            Assert.Equal(new[] { "emp-1" }, channel.AllowedPostUserIds);
            Assert.Equal(new[] { "team-1" }, channel.AllowedPostTeamIds);
            Assert.Equal(new[] { "msg-1" }, channel.PinnedMessageIds);
            Assert.Equal(5, channel.DisplayOrder);
            Assert.Equal(new[] { "team-2" }, channel.AllowedTeamIds);
            Assert.True(channel.IsArchived);
            Assert.True(channel.IsEncrypted);
            Assert.Equal(keys, channel.EncryptedChannelKeys);
            Assert.Equal(128, channel.AudioBitrateKbps);
            Assert.Equal(now, channel.CreatedAt);
            Assert.Equal("emp-creator", channel.CreatedById);
            Assert.Equal(now, channel.LastActivityAt);
        }
    }
}
