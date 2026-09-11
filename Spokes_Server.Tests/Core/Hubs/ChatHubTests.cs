using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Hubs;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Hubs
{
    public class ChatHubTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly ChatChannelRepository _channels;
        private readonly ChatMessageRepository _messages;
        private readonly EmployeeRepository _employees;
        private readonly ProjectRepository _projects;
        private readonly OpenIdAccountRepository _openIdAccounts;
        private readonly Mock<IHubCallerClients> _mockClients;
        private readonly Mock<IClientProxy> _mockClientProxy;
        private readonly Mock<IGroupManager> _mockGroups;
        private readonly Mock<HubCallerContext> _mockContext;

        public ChatHubTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ChatHub_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockPersistenceLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockPersistenceLogger.Object);

            _channels = new ChatChannelRepository(_writer, mockConfig.Object);
            var companyProfiles = new CompanyProfileRepository(_writer, mockConfig.Object);
            _messages = new ChatMessageRepository(_writer, mockConfig.Object, companyProfiles);
            _employees = new EmployeeRepository(_writer, mockConfig.Object);
            _projects = new ProjectRepository(_writer, mockConfig.Object);
            _openIdAccounts = new OpenIdAccountRepository(_writer, mockConfig.Object);

            // Setup SignalR Mocks
            _mockClients = new Mock<IHubCallerClients>();
            _mockClientProxy = new Mock<IClientProxy>();
            _mockGroups = new Mock<IGroupManager>();
            _mockContext = new Mock<HubCallerContext>();

            _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
            _mockClients.Setup(c => c.OthersInGroup(It.IsAny<string>())).Returns(_mockClientProxy.Object);
            _mockContext.Setup(c => c.ConnectionId).Returns("test-connection");
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
            _writer.Dispose();
        }

        private ChatHub CreateHub(string? userId = "u1")
        {
            if (userId != null)
            {
                var claims = new[] { new Claim("sub", userId) };
                var identity = new ClaimsIdentity(claims, "TestAuthType");
                var principal = new ClaimsPrincipal(identity);
                _mockContext.Setup(c => c.User).Returns(principal);
            }
            else
            {
                _mockContext.Setup(c => c.User).Returns((ClaimsPrincipal?)null);
            }

            var mockAuthState = new Mock<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
            var userService = new UserService(mockAuthState.Object, _employees, _openIdAccounts);
            var hub = new ChatHub(_channels, _messages, _projects, userService, null!, new Mock<ILogger<ChatHub>>().Object)
            {
                Context = _mockContext.Object,
                Clients = _mockClients.Object,
                Groups = _mockGroups.Object
            };
            return hub;
        }

        [Fact]
        public async Task OnConnectedAsync_AddsUserToGroup()
        {
            _employees.Save(new Employee { Id = "e123", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "user123", LinkedEmployeeId = "e123" });
            var hub = CreateHub("user123");

            await hub.OnConnectedAsync();

            _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", "user_e123", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task JoinChannel_AddsToChannelGroup()
        {
            _employees.Save(new Employee { Id = "e_join", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "u_join", LinkedEmployeeId = "e_join" });
            _channels.Save(new ChatChannel { Id = "c1" });
            var hub = CreateHub("u_join");

            await hub.JoinChannel("c1");

            _mockGroups.Verify(g => g.AddToGroupAsync("test-connection", "channel_c1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task LeaveChannel_RemovesFromChannelGroup()
        {
            var hub = CreateHub();

            await hub.LeaveChannel("c1");

            _mockGroups.Verify(g => g.RemoveFromGroupAsync("test-connection", "channel_c1", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SendMessage_SavesAndBroadcasts()
        {
            _employees.Save(new Employee { Id = "e1", FirstName = "Test", LastName = "User", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "user123", LinkedEmployeeId = "e1" });
            _channels.Save(new ChatChannel { Id = "c1", ChannelType = "Public" });

            var hub = CreateHub("user123");

            await hub.SendMessage("c1", "Hello world!");

            Assert.Single(_messages.GetAll());
            var savedMsg = _messages.GetAll()[0];
            Assert.Equal("Hello world!", savedMsg.Content);
            Assert.Equal("e1", savedMsg.SenderId);

            _mockClientProxy.Verify(
                c => c.SendCoreAsync("ReceiveMessage", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task EditMessage_UpdatesAndBroadcasts()
        {
            _employees.Save(new Employee { Id = "e2", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "user456", LinkedEmployeeId = "e2" });
            _channels.Save(new ChatChannel { Id = "c2", ChannelType = "Public" });
            var initialMsg = new ChatMessage { Id = "m1", ChannelId = "c2", SenderId = "e2", Content = "Old" };
            _messages.Save(initialMsg);

            var hub = CreateHub("user456");

            await hub.EditMessage("m1", "New");

            var updated = _messages.GetById("m1");
            Assert.NotNull(updated);
            Assert.Equal("New", updated.Content);
            Assert.NotNull(updated.EditedAt);

            _mockClientProxy.Verify(
                c => c.SendCoreAsync("MessageEdited", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task DeleteMessage_SoftDeletesAndBroadcasts()
        {
            _employees.Save(new Employee { Id = "e3", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "user789", LinkedEmployeeId = "e3" });
            var initialMsg = new ChatMessage { Id = "m2", ChannelId = "c3", SenderId = "e3", Content = "To del" };
            _messages.Save(initialMsg);

            var hub = CreateHub("user789");

            await hub.DeleteMessage("m2");

            var deleted = _messages.GetById("m2");
            Assert.NotNull(deleted);
            Assert.True(deleted.IsDeleted);

            _mockClientProxy.Verify(
                c => c.SendCoreAsync("MessageDeleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task AddReaction_AddsAndBroadcasts()
        {
            _employees.Save(new Employee { Id = "e4", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "u_rea", LinkedEmployeeId = "e4" });
            _channels.Save(new ChatChannel { Id = "c4" });
            var msg = new ChatMessage { Id = "m3", ChannelId = "c4" };
            _messages.Save(msg);

            var hub = CreateHub("u_rea");

            await hub.AddReaction("m3", "👍");

            var updated = _messages.GetById("m3");
            Assert.NotNull(updated);
            Assert.Contains("👍:e4", updated.Reactions);

            _mockClientProxy.Verify(
                c => c.SendCoreAsync("ReactionAdded", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task RemoveReaction_RemovesAndBroadcasts()
        {
            _employees.Save(new Employee { Id = "e5", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "u_rem", LinkedEmployeeId = "e5" });
            _channels.Save(new ChatChannel { Id = "c5" });
            var msg = new ChatMessage { Id = "m4", ChannelId = "c5" };
            msg.Reactions.Add("❤️:e5");
            _messages.Save(msg);

            var hub = CreateHub("u_rem");

            await hub.RemoveReaction("m4", "❤️");

            var updated = _messages.GetById("m4");
            Assert.NotNull(updated);
            Assert.Empty(updated.Reactions);

            _mockClientProxy.Verify(
                c => c.SendCoreAsync("ReactionRemoved", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task TypingIndicators_BroadcastToOthers()
        {
            _employees.Save(new Employee { Id = "e6", IsAdmin = true });
            _openIdAccounts.Save(new OpenIdAccount { Sub = "u_type", LinkedEmployeeId = "e6" });
            _channels.Save(new ChatChannel { Id = "c6" });

            var hub = CreateHub("u_type");

            await hub.StartTyping("c6");
            _mockClientProxy.Verify(c => c.SendCoreAsync("UserTyping", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);

            await hub.StopTyping("c6");
            _mockClientProxy.Verify(c => c.SendCoreAsync("UserStoppedTyping", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}



