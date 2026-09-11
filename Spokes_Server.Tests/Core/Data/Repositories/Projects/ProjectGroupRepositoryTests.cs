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

namespace Spokes_Server.Tests.Core.Data.Repositories.Projects
{
    public class ProjectGroupRepositoryTests : IDisposable
    {
        private readonly string _testDataDir;
        private readonly DiskPersistenceService _writer;
        private readonly ProjectGroupRepository _repo;

        public ProjectGroupRepositoryTests()
        {
            _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_ProjectGroups_" + Guid.NewGuid().ToString());

            var mockConfig = new Mock<IConfiguration>();
            mockConfig.Setup(c => c["DataPath"]).Returns(_testDataDir);

            var mockLogger = new Mock<ILogger<DiskPersistenceService>>();
            _writer = new DiskPersistenceService(mockLogger.Object);

            _repo = new ProjectGroupRepository(_writer, mockConfig.Object);
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
        public void GetByClient_ReturnsGroupsForClientCaseInsensitive()
        {
            _repo.Save(new ProjectGroup { Id = "g1", Name = "Group 1", Client = new ClientInfo { BusinessName = "Acme Corp" } });
            _repo.Save(new ProjectGroup { Id = "g2", Name = "Group 2", Client = new ClientInfo { BusinessName = "ACME corp" } });
            _repo.Save(new ProjectGroup { Id = "g3", Name = "Group 3", Client = new ClientInfo { BusinessName = "Other Corp" } });

            var groups = _repo.GetByClient("acme CORP");

            Assert.Equal(2, groups.Count);
            // Verify ordering by name
            Assert.Equal("Group 1", groups.First().Name);
            Assert.Equal("Group 2", groups.Last().Name);
        }

        [Fact]
        public void GetGroupsContainingProject_ReturnsMatchingGroups()
        {
            var g1 = new ProjectGroup { Id = "g1" };
            g1.ProjectIds.Add("p1");
            g1.ProjectIds.Add("p2");
            _repo.Save(g1);

            var g2 = new ProjectGroup { Id = "g2" };
            g2.ProjectIds.Add("p2");
            g2.ProjectIds.Add("p3");
            _repo.Save(g2);

            var groupsForP2 = _repo.GetGroupsContainingProject("p2");
            Assert.Equal(2, groupsForP2.Count);
            Assert.Contains(groupsForP2, g => g.Id == "g1");
            Assert.Contains(groupsForP2, g => g.Id == "g2");

            var groupsForP1 = _repo.GetGroupsContainingProject("p1");
            Assert.Single(groupsForP1);
            Assert.Equal("g1", groupsForP1.First().Id);
        }

        [Fact]
        public void GetGroupForProject_ReturnsFirstMatchingGroup()
        {
            var g1 = new ProjectGroup { Id = "g1" };
            g1.ProjectIds.Add("p1");
            _repo.Save(g1);

            var group = _repo.GetGroupForProject("p1");
            Assert.NotNull(group);
            Assert.Equal("g1", group.Id);

            var missing = _repo.GetGroupForProject("p99");
            Assert.Null(missing);
        }
    }
}




