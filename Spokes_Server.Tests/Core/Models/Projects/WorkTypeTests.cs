namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class WorkTypeTests
{
        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.True(workType.Equals(workType));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var workType1 = new WorkType { Id = "wt-1", Name = "Name 1" };
            var workType2 = new WorkType { Id = "wt-1", Name = "Name 2" };

            Assert.True(workType1.Equals(workType2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var workType1 = new WorkType { Id = "wt-1" };
            var workType2 = new WorkType { Id = "wt-2" };

            Assert.False(workType1.Equals(workType2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.False(workType.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.False(workType.Equals("wt-1"));
            Assert.False(workType.Equals(new object()));
            Assert.False(workType.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var workType1 = new WorkType { Id = "wt-1" };
            var workType2 = new WorkType { Id = "wt-1" };

            Assert.Equal(workType1.GetHashCode(), workType2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var workType = new WorkType { Id = null! };

            var exception = Record.Exception(() => workType.GetHashCode());
            Assert.Null(exception);
        }
    }
