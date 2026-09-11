using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Core.Models.Communication;

namespace Spokes_Server.Tests.Components.Pages.Chat
{
    public class PendingFileTests
    {
        [Fact]
        public void PendingFile_Initialization_SetsDefaults()
        {
            var pendingFile = new PendingFile();

            Assert.False(string.IsNullOrEmpty(pendingFile.Id));
            Assert.Equal("", pendingFile.FileName);
            Assert.False(pendingFile.IsUploading);
            Assert.Null(pendingFile.Attachment);
        }

        [Fact]
        public void PendingFile_Properties_CanBeSet()
        {
            var id = Guid.NewGuid().ToString();
            var fileName = "test.png";
            var attachment = new ChatAttachment { FileName = fileName };

            var pendingFile = new PendingFile
            {
                Id = id,
                FileName = fileName,
                IsUploading = true,
                Attachment = attachment
            };

            Assert.Equal(id, pendingFile.Id);
            Assert.Equal(fileName, pendingFile.FileName);
            Assert.True(pendingFile.IsUploading);
            Assert.Equal(attachment, pendingFile.Attachment);
        }
    }
}

