using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Models
{
    public class StandardDocumentTests
    {
        [Fact]
        public void StandardDocument_Initialization_SetsDefaultsCorrectly()
        {
            var doc = new StandardDocument();

            Assert.False(string.IsNullOrEmpty(doc.Id));
            Assert.Equal(string.Empty, doc.Name);
            Assert.Equal(string.Empty, doc.Description);
            Assert.Equal(string.Empty, doc.Body);
            Assert.False(doc.IsConfidential);
            Assert.NotNull(doc.Blocks);
            Assert.Empty(doc.Blocks);
            Assert.True(doc.CreatedAt <= DateTime.UtcNow);
            Assert.True(doc.UpdatedAt <= DateTime.UtcNow);
        }

        [Fact]
        public void StandardDocument_SetProperties_PersistsValues()
        {
            var now = DateTime.UtcNow;
            var doc = new StandardDocument
            {
                Id = "custom-id",
                Name = "NDA - English",
                Description = "Standard NDA template",
                Body = "This Non-Disclosure Agreement is between @Company.Name and @Client.BusinessName.",
                IsConfidential = true,
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now
            };

            Assert.Equal("custom-id", doc.Id);
            Assert.Equal("NDA - English", doc.Name);
            Assert.Equal("Standard NDA template", doc.Description);
            Assert.Contains("@Company.Name", doc.Body);
            Assert.Contains("@Client.BusinessName", doc.Body);
            Assert.True(doc.IsConfidential);
            Assert.Equal(now.AddDays(-1), doc.CreatedAt);
            Assert.Equal(now, doc.UpdatedAt);
        }

        [Fact]
        public void StandardDocument_Blocks_CanAddAndMutate()
        {
            var doc = new StandardDocument();
            var block = new DocumentBlockInstance
            {
                Id = "block-1",
                Type = "Signature",
                Name = "Client Signature",
                Properties = new Dictionary<string, string> { { "Signer", "John Doe" } }
            };

            doc.Blocks.Add(block);

            Assert.Single(doc.Blocks);
            Assert.Equal("block-1", doc.Blocks[0].Id);
            Assert.Equal("Signature", doc.Blocks[0].Type);
            Assert.Equal("Client Signature", doc.Blocks[0].Name);
            Assert.Equal("John Doe", doc.Blocks[0].Properties["Signer"]);
        }

        [Fact]
        public void StandardDocument_Equals_ReferenceEquality_ReturnsTrue()
        {
            var doc = new StandardDocument { Id = "doc-1" };
            Assert.True(doc.Equals(doc));
        }

        [Fact]
        public void StandardDocument_Equals_SameId_ReturnsTrue()
        {
            var doc1 = new StandardDocument { Id = "doc-1", Name = "Doc A" };
            var doc2 = new StandardDocument { Id = "doc-1", Name = "Doc B" };

            Assert.True(doc1.Equals(doc2));
            Assert.True(doc1.Equals((object)doc2));
        }

        [Fact]
        public void StandardDocument_Equals_DifferentId_ReturnsFalse()
        {
            var doc1 = new StandardDocument { Id = "doc-1" };
            var doc2 = new StandardDocument { Id = "doc-2" };

            Assert.False(doc1.Equals(doc2));
        }

        [Fact]
        public void StandardDocument_Equals_Null_ReturnsFalse()
        {
            var doc = new StandardDocument { Id = "doc-1" };

            Assert.False(doc.Equals(null));
        }

        [Fact]
        public void StandardDocument_Equals_DifferentType_ReturnsFalse()
        {
            var doc = new StandardDocument { Id = "doc-1" };

            Assert.False(doc.Equals("not a document"));
            Assert.False(doc.Equals(new object()));
        }

        [Fact]
        public void StandardDocument_GetHashCode_SameId_ReturnsSameHashCode()
        {
            var doc1 = new StandardDocument { Id = "doc-1" };
            var doc2 = new StandardDocument { Id = "doc-1" };

            Assert.Equal(doc1.GetHashCode(), doc2.GetHashCode());
        }

        [Fact]
        public void StandardDocument_GetHashCode_NullId_DoesNotThrow()
        {
            var doc = new StandardDocument { Id = null! };

            var exception = Record.Exception(() => doc.GetHashCode());
            Assert.Null(exception);
        }

        [Fact]
        public void ProjectDocument_Initialization_SetsDefaultsCorrectly()
        {
            var doc = new ProjectDocument();

            Assert.False(string.IsNullOrEmpty(doc.Id));
            Assert.Equal(string.Empty, doc.ProjectId);
            Assert.Equal(string.Empty, doc.StandardDocumentId);
            Assert.Equal(string.Empty, doc.Name);
            Assert.Equal(string.Empty, doc.ResolvedBody);
            Assert.Equal(ProjectDocumentStatus.Draft, doc.Status);
            Assert.True(doc.CreatedAt <= DateTime.UtcNow);
            Assert.True(doc.UpdatedAt <= DateTime.UtcNow);
        }

        [Fact]
        public void ProjectDocument_StatusConstants_HaveExpectedValues()
        {
            Assert.Equal("Draft", ProjectDocumentStatus.Draft);
            Assert.Equal("Final", ProjectDocumentStatus.Final);
        }
    }
}
