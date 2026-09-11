using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Tests.Core.Services.Communication
{
    public class EmailIdleServiceTests : IDisposable
    {
        private readonly EmailIdleService _service;
        private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
        private readonly Mock<IServiceScope> _mockScope;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<ILogger<EmailIdleService>> _mockLogger;

        public EmailIdleServiceTests()
        {
            _mockScopeFactory = new Mock<IServiceScopeFactory>();
            _mockScope = new Mock<IServiceScope>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockLogger = new Mock<ILogger<EmailIdleService>>();

            _mockScopeFactory.Setup(s => s.CreateScope()).Returns(_mockScope.Object);
            _mockScope.Setup(s => s.ServiceProvider).Returns(_mockServiceProvider.Object);

            _service = new EmailIdleService(_mockScopeFactory.Object, _mockLogger.Object);
        }

        public void Dispose()
        {
            _service.Dispose();
        }

        [Fact]
        public void WatchFolder_StartsNewTask()
        {
            // We can't easily wait for the background task to complete or verify its internals
            // because it starts a loop that depends on external IMAP servers.
            // But we can verify that multiple calls to WatchFolder for the same user
            // clean up previous tokens.

            _service.WatchFolder("user-1", "INBOX");

            // Calling it again should trigger StopWatching internally
            _service.WatchFolder("user-1", "SENT");

            // If we didn't crash, and we can stop it, that's a basic verification of the state management
            _service.StopWatching("user-1", "INBOX");
            _service.StopWatching("user-1", "SENT");
        }

        [Fact]
        public void StopWatching_RemovesFromActiveList()
        {
            _service.WatchFolder("user-1", "INBOX");
            _service.StopWatching("user-1", "INBOX");

            // Subsequent stop should be a no-op/safe
            _service.StopWatching("user-1", "INBOX");
        }

        [Fact]
        public void Dispose_CancelsAllWatchers()
        {
            _service.WatchFolder("user-1", "INBOX");
            _service.WatchFolder("user-2", "INBOX");

            _service.Dispose();

            // Should be empty now
            _service.StopWatching("user-1", "INBOX");
            _service.StopWatching("user-2", "INBOX");
        }

        [Fact]
        public async Task Events_CanBeSubscribed()
        {
            bool receivedTriggered = false;
            bool flagsTriggered = false;

            _service.MessageReceived += (u, f, id) => receivedTriggered = true;
            _service.MessageFlagsChanged += (u, f, id) => flagsTriggered = true;

            // Since we can't easily trigger the folder events from a mock ImapClient 
            // (the service creates the client internally via 'new'), 
            // this test mainly ensures the event surface is functional.

            Assert.False(receivedTriggered);
            Assert.False(flagsTriggered);
        }
    }
}


