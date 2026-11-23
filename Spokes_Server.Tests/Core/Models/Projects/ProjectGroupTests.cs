namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

public class ProjectGroupTests
{
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
