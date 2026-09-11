using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models
{
    public class ChatChannelTests
    {
        [Fact]
        public void ChatChannel_Initialization_SetsDefaultsCorrectly()
        {
            var channel = new ChatChannel();

            Assert.False(string.IsNullOrEmpty(channel.Id));
            Assert.Equal(string.Empty, channel.Name);
            Assert.Null(channel.Icon);
            Assert.Equal(ChatChannelType.General, channel.ChannelType);
            Assert.Null(channel.LinkedEntityId);
            Assert.Equal(string.Empty, channel.Description);
            Assert.Empty(channel.ParticipantIds);
            Assert.False(channel.IsDefaultGeneral);
            Assert.Empty(channel.PinnedMessageIds);
            Assert.Empty(channel.AllowedTeamIds);
            Assert.False(channel.IsArchived);
            Assert.True(channel.CreatedAt <= DateTime.UtcNow);
            Assert.Equal(string.Empty, channel.CreatedById);
            Assert.True(channel.LastActivityAt <= DateTime.UtcNow);
        }
    }
}


