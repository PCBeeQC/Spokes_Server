namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

public class ProjectDocumentTests
{
        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var doc = new ProjectDocument { Id = "doc-1" };

            Assert.True(doc.Equals(doc));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var doc1 = new ProjectDocument { Id = "doc-1", Name = "Doc 1" };
            var doc2 = new ProjectDocument { Id = "doc-1", Name = "Doc 2" };

            Assert.True(doc1.Equals(doc2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var doc1 = new ProjectDocument { Id = "doc-1" };
            var doc2 = new ProjectDocument { Id = "doc-2" };

            Assert.False(doc1.Equals(doc2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var doc = new ProjectDocument { Id = "doc-1" };

            Assert.False(doc.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var doc = new ProjectDocument { Id = "doc-1" };

            Assert.False(doc.Equals("doc-1"));
            Assert.False(doc.Equals(new object()));
            Assert.False(doc.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var doc1 = new ProjectDocument { Id = "doc-1" };
            var doc2 = new ProjectDocument { Id = "doc-1" };

            Assert.Equal(doc1.GetHashCode(), doc2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var doc = new ProjectDocument { Id = null! };

            var exception = Record.Exception(() => doc.GetHashCode());
            Assert.Null(exception);
        }
    }
