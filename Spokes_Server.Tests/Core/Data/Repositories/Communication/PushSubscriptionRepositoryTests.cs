using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.Projects;
using Spokes_Server.Core.Data.Repositories.Accounting;
using Spokes_Server.Core.Data.Repositories.Communication;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;
using System.IO;
using System.Linq;

namespace Spokes_Server.Tests.Core.Data.Repositories.Communication
{
    public class PushSubscriptionRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly PushSubscriptionRepository _repo;

        public PushSubscriptionRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_PushSubs_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _repo = new PushSubscriptionRepository(_writer, mockConfig.Object);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testDataDir))
            {
                try { Directory.Delete(_testDataDir, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
            _writer.Dispose();
        }

        [Fact]
        public void GetByUserId_ReturnsSubscriptionsForUser()
        {
            _repo.Save(new PushSubscription { Id = "s1", UserId = "u1", Endpoint = "e1" });
            _repo.Save(new PushSubscription { Id = "s2", UserId = "u1", Endpoint = "e2" });
            _repo.Save(new PushSubscription { Id = "s3", UserId = "u2", Endpoint = "e3" });

            var subs = _repo.GetByUserId("u1");
            Assert.Equal(2, subs.Count);
            Assert.All(subs, s => Assert.Equal("u1", s.UserId));
        }

        [Fact]
        public void GetByEndpoint_ReturnsMatchingSubscription()
        {
            _repo.Save(new PushSubscription { Id = "s4", UserId = "u3", Endpoint = "e4" });
            _repo.Save(new PushSubscription { Id = "s5", UserId = "u4", Endpoint = "e5" });

            var sub = _repo.GetByEndpoint("e4");
            Assert.NotNull(sub);
            Assert.Equal("s4", sub.Id);

            var missing = _repo.GetByEndpoint("e99");
            Assert.Null(missing);
        }

        [Fact]
        public void DeleteByEndpoint_RemovesSubscriptionAndReturnsTrueWhenFound()
        {
            _repo.Save(new PushSubscription { Id = "s6", Endpoint = "e6" });

            var removed = _repo.DeleteByEndpoint("e6");
            Assert.True(removed);
            Assert.Null(_repo.GetByEndpoint("e6"));

            var removedAgain = _repo.DeleteByEndpoint("e6");
            Assert.False(removedAgain);
        }

        [Fact]
        public void DeleteByUserId_RemovesAllUsersSubscriptions()
        {
            _repo.Save(new PushSubscription { Id = "s7", UserId = "u5", Endpoint = "e7" });
            _repo.Save(new PushSubscription { Id = "s8", UserId = "u5", Endpoint = "e8" });
            _repo.Save(new PushSubscription { Id = "s9", UserId = "u6", Endpoint = "e9" });

            _repo.DeleteByUserId("u5");

            Assert.Empty(_repo.GetByUserId("u5"));

            // Should not affect other users
            Assert.Single(_repo.GetByUserId("u6"));
        }
    }
}




