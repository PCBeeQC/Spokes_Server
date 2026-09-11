using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Tests.Core.Models
{
    public class EmailModelsTests
    {
        [Fact]
        public void EmailFolder_Initialization_SetsDefaultsCorrectly()
        {
            var folder = new EmailFolder();

            Assert.False(string.IsNullOrEmpty(folder.Id));
            Assert.Equal(string.Empty, folder.EmployeeId);
            Assert.Equal(string.Empty, folder.Name);
            Assert.Equal(string.Empty, folder.Path);
            Assert.Equal(0, folder.UnreadCount);
            Assert.Equal(0, folder.TotalCount);
            Assert.Equal(1u, folder.NextUid);
            Assert.False(folder.IsInbox);
            Assert.False(folder.IsSent);
            Assert.False(folder.IsTrash);
            Assert.False(folder.IsDrafts);
            Assert.False(folder.IsArchive);
            Assert.False(folder.IsJunk);
        }

        [Fact]
        public void EmailMessage_Initialization_SetsDefaultsCorrectly()
        {
            var message = new EmailMessage();

            Assert.False(string.IsNullOrEmpty(message.Id));
            Assert.Equal(string.Empty, message.EmployeeId);
            Assert.Equal(string.Empty, message.FolderPath);
            Assert.Equal(0u, message.UniqueId);
            Assert.Equal(string.Empty, message.Subject);
            Assert.Equal(string.Empty, message.FromAddress);
            Assert.Equal(string.Empty, message.FromName);
            Assert.Empty(message.ToAddresses);
            Assert.Empty(message.CcAddresses);
            Assert.Empty(message.BccAddresses);
            // Default DateTimeOffset is DateTimeOffset.MinValue
            Assert.Equal(DateTimeOffset.MinValue, message.Date);
            Assert.Equal(string.Empty, message.Snippet);
            Assert.False(message.HasAttachments);
            Assert.Empty(message.Attachments);
            Assert.False(message.IsRead);
            Assert.False(message.IsFlagged);
        }

        [Fact]
        public void EmailAttachmentMeta_Initialization_SetsDefaultsCorrectly()
        {
            var meta = new EmailAttachmentMeta();

            Assert.False(string.IsNullOrEmpty(meta.Id));
            Assert.Equal(string.Empty, meta.FileName);
            Assert.Equal(string.Empty, meta.ContentType);
            Assert.Equal(0, meta.Size);
        }
    }
}


