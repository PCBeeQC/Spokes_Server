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
            Assert.True(doc.CreatedAt <= DateTime.UtcNow);
            Assert.True(doc.UpdatedAt <= DateTime.UtcNow);
        }

        [Fact]
        public void StandardDocument_SetProperties_PersistsValues()
        {
            var doc = new StandardDocument
            {
                Name = "NDA - English",
                Description = "Standard NDA template",
                Body = "This Non-Disclosure Agreement is between @Company.Name and @Client.BusinessName."
            };

            Assert.Equal("NDA - English", doc.Name);
            Assert.Equal("Standard NDA template", doc.Description);
            Assert.Contains("@Company.Name", doc.Body);
            Assert.Contains("@Client.BusinessName", doc.Body);
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
