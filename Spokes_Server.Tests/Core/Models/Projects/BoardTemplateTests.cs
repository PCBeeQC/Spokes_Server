namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class BoardTemplateTests
{
        [Fact]
        public void BoardTemplate_Initialization_SetsDefaultsCorrectly()
        {
            var template = new BoardTemplate();

            Assert.False(string.IsNullOrWhiteSpace(template.Id));
            Assert.True(Guid.TryParse(template.Id, out _));
            Assert.Equal(string.Empty, template.Name);
            Assert.Equal(string.Empty, template.Description);
            Assert.Null(template.Icon);
            Assert.NotNull(template.Properties);
            Assert.Empty(template.Properties);
            Assert.NotNull(template.Views);
            Assert.Empty(template.Views);
        }

        [Fact]
        public void BoardTemplate_Properties_CanSetAndGet()
        {
            List<BoardProperty> properties =
            [
                new BoardProperty { Id = "prop-1", Name = "Status" }
            ];
            List<BoardView> views =
            [
                new BoardView { Id = "view-1", Name = "Kanban" }
            ];

            var template = new BoardTemplate
            {
                Id = "template-123",
                Name = "Project Template",
                Description = "A default template for projects",
                Icon = "mdi-folder",
                Properties = properties,
                Views = views
            };

            Assert.Equal("template-123", template.Id);
            Assert.Equal("Project Template", template.Name);
            Assert.Equal("A default template for projects", template.Description);
            Assert.Equal("mdi-folder", template.Icon);
            Assert.Same(properties, template.Properties);
            Assert.Single(template.Properties);
            Assert.Same(views, template.Views);
            Assert.Single(template.Views);
        }

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
