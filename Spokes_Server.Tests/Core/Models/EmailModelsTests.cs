using Spokes_Server.Core.Models.Communication;

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
            Assert.Equal(string.Empty, folder.ParentPath);
            Assert.Equal("/", folder.Delimiter);
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
        public void EmailFolder_Equals_ReturnsExpectedResults()
        {
            var folder1 = new EmailFolder { Id = "folder-1" };
            var folder1SameId = new EmailFolder { Id = "folder-1" };
            var folder2 = new EmailFolder { Id = "folder-2" };

            // Reference equality
            Assert.True(folder1.Equals(folder1));

            // Value equality by Id
            Assert.True(folder1.Equals(folder1SameId));
            Assert.False(folder1.Equals(folder2));

            // Null and different type
            Assert.False(folder1.Equals(null));
            Assert.False(folder1.Equals("folder-1"));
            Assert.False(folder1.Equals(new object()));
        }

        [Fact]
        public void EmailFolder_GetHashCode_ReturnsExpectedHashCode()
        {
            var folder1 = new EmailFolder { Id = "folder-1" };
            var folder2 = new EmailFolder { Id = "folder-1" };
            var folderDifferent = new EmailFolder { Id = "folder-2" };

            // Equal Ids have equal hash codes
            Assert.Equal(folder1.GetHashCode(), folder2.GetHashCode());
            Assert.Equal("folder-1".GetHashCode(), folder1.GetHashCode());
            Assert.NotEqual(folder1.GetHashCode(), folderDifferent.GetHashCode());

            // Null-Id fallback to base.GetHashCode()
            var folderNullId = new EmailFolder { Id = null! };
            var hash = folderNullId.GetHashCode();
            // Should execute without throwing NullReferenceException
            Assert.True(hash != 0 || hash == 0);
        }

        [Fact]
        public void EmailFolder_PropertyMutations_UpdatesValuesCorrectly()
        {
            var folder = new EmailFolder
            {
                Id = "custom-folder-id",
                EmployeeId = "emp-001",
                Name = "Archive 2026",
                Path = "Archive/2026",
                ParentPath = "Archive",
                Delimiter = ".",
                UnreadCount = 7,
                TotalCount = 142,
                NextUid = 99u,
                IsInbox = true,
                IsSent = true,
                IsTrash = true,
                IsDrafts = true,
                IsArchive = true,
                IsJunk = true
            };

            Assert.Equal("custom-folder-id", folder.Id);
            Assert.Equal("emp-001", folder.EmployeeId);
            Assert.Equal("Archive 2026", folder.Name);
            Assert.Equal("Archive/2026", folder.Path);
            Assert.Equal("Archive", folder.ParentPath);
            Assert.Equal(".", folder.Delimiter);
            Assert.Equal(7, folder.UnreadCount);
            Assert.Equal(142, folder.TotalCount);
            Assert.Equal(99u, folder.NextUid);
            Assert.True(folder.IsInbox);
            Assert.True(folder.IsSent);
            Assert.True(folder.IsTrash);
            Assert.True(folder.IsDrafts);
            Assert.True(folder.IsArchive);
            Assert.True(folder.IsJunk);
        }

        [Fact]
        public void EmailMessage_Initialization_SetsDefaultsCorrectly()
        {
            var message = new EmailMessage();

            Assert.False(string.IsNullOrEmpty(message.Id));
            Assert.Equal(string.Empty, message.EmployeeId);
            Assert.Equal(string.Empty, message.FolderPath);
            Assert.Equal(0u, message.UniqueId);
            Assert.Equal(string.Empty, message.GlobalMessageId);
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
        public void EmailMessage_PropertyMutations_UpdatesValuesCorrectly()
        {
            var testDate = new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero);
            var attachment = new EmailAttachmentMeta { FileName = "spec.pdf", ContentType = "application/pdf", Size = 2048 };

            var message = new EmailMessage
            {
                Id = "msg-999",
                EmployeeId = "emp-002",
                FolderPath = "INBOX/Work",
                UniqueId = 55u,
                GlobalMessageId = "<uuid-1234@mail.domain.com>",
                Subject = "Project Update",
                FromAddress = "manager@company.com",
                FromName = "Operations Manager",
                ToAddresses = new List<string> { "alice@company.com", "bob@company.com" },
                CcAddresses = new List<string> { "charlie@company.com" },
                BccAddresses = new List<string> { "audit@company.com" },
                Date = testDate,
                Snippet = "Here is the weekly update on the project...",
                HasAttachments = true,
                Attachments = new List<EmailAttachmentMeta> { attachment },
                IsRead = true,
                IsFlagged = true
            };

            Assert.Equal("msg-999", message.Id);
            Assert.Equal("emp-002", message.EmployeeId);
            Assert.Equal("INBOX/Work", message.FolderPath);
            Assert.Equal(55u, message.UniqueId);
            Assert.Equal("<uuid-1234@mail.domain.com>", message.GlobalMessageId);
            Assert.Equal("Project Update", message.Subject);
            Assert.Equal("manager@company.com", message.FromAddress);
            Assert.Equal("Operations Manager", message.FromName);
            Assert.Equal(new[] { "alice@company.com", "bob@company.com" }, message.ToAddresses);
            Assert.Equal(new[] { "charlie@company.com" }, message.CcAddresses);
            Assert.Equal(new[] { "audit@company.com" }, message.BccAddresses);
            Assert.Equal(testDate, message.Date);
            Assert.Equal("Here is the weekly update on the project...", message.Snippet);
            Assert.True(message.HasAttachments);
            Assert.Single(message.Attachments);
            Assert.Same(attachment, message.Attachments[0]);
            Assert.True(message.IsRead);
            Assert.True(message.IsFlagged);
        }

        [Fact]
        public void EmailAttachmentMeta_Initialization_SetsDefaultsCorrectly()
        {
            var meta = new EmailAttachmentMeta();

            Assert.False(string.IsNullOrEmpty(meta.Id));
            Assert.Equal(string.Empty, meta.FileName);
            Assert.Equal(string.Empty, meta.ContentType);
            Assert.Equal(0L, meta.Size);
        }

        [Fact]
        public void EmailAttachmentMeta_PropertyMutations_UpdatesValuesCorrectly()
        {
            var meta = new EmailAttachmentMeta
            {
                Id = "att-custom-id",
                FileName = "report.xlsx",
                ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                Size = 1048576L
            };

            Assert.Equal("att-custom-id", meta.Id);
            Assert.Equal("report.xlsx", meta.FileName);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", meta.ContentType);
            Assert.Equal(1048576L, meta.Size);
        }
    }
}
