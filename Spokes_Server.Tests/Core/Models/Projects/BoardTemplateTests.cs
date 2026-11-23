namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class BoardTemplateTests
{
        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var template = new BoardTemplate { Id = "template-1" };

            Assert.True(template.Equals(template));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var template1 = new BoardTemplate { Id = "template-1", Name = "Template 1" };
            var template2 = new BoardTemplate { Id = "template-1", Name = "Template 2" };

            Assert.True(template1.Equals(template2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var template1 = new BoardTemplate { Id = "template-1" };
            var template2 = new BoardTemplate { Id = "template-2" };

            Assert.False(template1.Equals(template2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var template = new BoardTemplate { Id = "template-1" };

            Assert.False(template.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var template = new BoardTemplate { Id = "template-1" };

            Assert.False(template.Equals("template-1"));
            Assert.False(template.Equals(new object()));
            Assert.False(template.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var template1 = new BoardTemplate { Id = "template-1" };
            var template2 = new BoardTemplate { Id = "template-1" };

            Assert.Equal(template1.GetHashCode(), template2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var template = new BoardTemplate { Id = null! };

            var exception = Record.Exception(() => template.GetHashCode());
            Assert.Null(exception);
        }
    }
