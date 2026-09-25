namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

public class ProjectDocumentTests
{
        [Fact]
        public void ProjectDocument_Initialization_SetsDefaultsCorrectly()
        {
            var doc = new ProjectDocument();

            Assert.False(string.IsNullOrWhiteSpace(doc.Id));
            Assert.True(Guid.TryParse(doc.Id, out _));
            Assert.Equal(string.Empty, doc.ProjectId);
            Assert.Equal(string.Empty, doc.StandardDocumentId);
            Assert.Equal(string.Empty, doc.Name);
            Assert.Equal(string.Empty, doc.ResolvedBody);
            Assert.Equal(ProjectDocumentStatus.Draft, doc.Status);
            Assert.Equal("Draft", doc.Status);
            Assert.False(doc.IsManualEdit);
            Assert.False(doc.IsConfidential);
            Assert.False(doc.IsLongFooter);
            Assert.NotNull(doc.Blocks);
            Assert.Empty(doc.Blocks);
            Assert.Equal(string.Empty, doc.PdfPath);
            Assert.True(doc.CreatedAt <= DateTime.UtcNow);
            Assert.True(doc.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
            Assert.True(doc.UpdatedAt <= DateTime.UtcNow);
            Assert.True(doc.UpdatedAt > DateTime.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void ProjectDocumentStatus_Constants_HaveExpectedValues()
        {
            Assert.Equal("Draft", ProjectDocumentStatus.Draft);
            Assert.Equal("Final", ProjectDocumentStatus.Final);
        }

        [Fact]
        public void ProjectDocument_Properties_CanSetAndGet()
        {
            var createdAt = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var updatedAt = new DateTime(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc);
            List<DocumentBlockInstance> blocks =
            [
                new()
                {
                    Id = "block-1",
                    Type = "Signature",
                    Name = "Client Signature",
                    Properties = new Dictionary<string, string> { { "SignerName", "Jane Doe" } }
                }
            ];

            var doc = new ProjectDocument
            {
                Id = "custom-doc-id",
                ProjectId = "project-100",
                StandardDocumentId = "std-doc-200",
                Name = "Master Agreement",
                ResolvedBody = "<h1>Agreement Body</h1>",
                Status = ProjectDocumentStatus.Final,
                IsManualEdit = true,
                IsConfidential = true,
                IsLongFooter = true,
                Blocks = blocks,
                PdfPath = "/documents/master-agreement.pdf",
                CreatedAt = createdAt,
                UpdatedAt = updatedAt
            };

            Assert.Equal("custom-doc-id", doc.Id);
            Assert.Equal("project-100", doc.ProjectId);
            Assert.Equal("std-doc-200", doc.StandardDocumentId);
            Assert.Equal("Master Agreement", doc.Name);
            Assert.Equal("<h1>Agreement Body</h1>", doc.ResolvedBody);
            Assert.Equal(ProjectDocumentStatus.Final, doc.Status);
            Assert.True(doc.IsManualEdit);
            Assert.True(doc.IsConfidential);
            Assert.True(doc.IsLongFooter);
            Assert.Same(blocks, doc.Blocks);
            Assert.Single(doc.Blocks);
            Assert.Equal("/documents/master-agreement.pdf", doc.PdfPath);
            Assert.Equal(createdAt, doc.CreatedAt);
            Assert.Equal(updatedAt, doc.UpdatedAt);
        }

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
