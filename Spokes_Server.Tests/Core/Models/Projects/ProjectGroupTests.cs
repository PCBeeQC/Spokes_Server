namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class ProjectGroupTests
{
        [Fact]
        public void ProjectGroup_Initialization_SetsDefaultsCorrectly()
        {
            var before = DateTime.UtcNow;
            var group = new ProjectGroup();
            var after = DateTime.UtcNow;

            Assert.False(string.IsNullOrWhiteSpace(group.Id));
            Assert.True(Guid.TryParse(group.Id, out _));
            Assert.Equal(string.Empty, group.Name);
            Assert.Equal(string.Empty, group.Description);
            Assert.NotNull(group.Client);
            Assert.NotNull(group.ProjectIds);
            Assert.Empty(group.ProjectIds);
            Assert.True(group.CreatedAt >= before.AddSeconds(-1) && group.CreatedAt <= after.AddSeconds(1));
        }

        [Fact]
        public void ProjectGroup_Properties_CanSetAndGet()
        {
            var now = DateTime.UtcNow;
            var client = new ClientInfo
            {
                BusinessName = "Acme Corp",
                ContactPersonName = "Jane Doe"
            };
            List<string> projectIds = ["proj-1", "proj-2"];

            var group = new ProjectGroup
            {
                Id = "custom-id",
                Name = "Alpha Group",
                Description = "Alpha Description",
                Client = client,
                ProjectIds = projectIds,
                CreatedAt = now
            };

            Assert.Equal("custom-id", group.Id);
            Assert.Equal("Alpha Group", group.Name);
            Assert.Equal("Alpha Description", group.Description);
            Assert.Same(client, group.Client);
            Assert.Same(projectIds, group.ProjectIds);
            Assert.Equal(2, group.ProjectIds.Count);
            Assert.Equal(now, group.CreatedAt);
        }

        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var group = new ProjectGroup { Id = "group-1" };

            Assert.True(group.Equals(group));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var group1 = new ProjectGroup { Id = "group-1", Name = "Name 1" };
            var group2 = new ProjectGroup { Id = "group-1", Name = "Name 2" };

            Assert.True(group1.Equals(group2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var group1 = new ProjectGroup { Id = "group-1" };
            var group2 = new ProjectGroup { Id = "group-2" };

            Assert.False(group1.Equals(group2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var group = new ProjectGroup { Id = "group-1" };

            Assert.False(group.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var group = new ProjectGroup { Id = "group-1" };

            Assert.False(group.Equals("group-1"));
            Assert.False(group.Equals(new object()));
            Assert.False(group.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var group1 = new ProjectGroup { Id = "group-1" };
            var group2 = new ProjectGroup { Id = "group-1" };

            Assert.Equal(group1.GetHashCode(), group2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrow()
        {
            var group = new ProjectGroup { Id = null! };

            var exception = Record.Exception(() => group.GetHashCode());
            Assert.Null(exception);
        }
    }
