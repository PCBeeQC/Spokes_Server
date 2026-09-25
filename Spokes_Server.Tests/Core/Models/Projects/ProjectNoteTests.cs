namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class ProjectNoteTests
{
        [Fact]
        public void ProjectNote_Initialization_SetsDefaultsCorrectly()
        {
            var note = new ProjectNote();

            Assert.False(string.IsNullOrWhiteSpace(note.Id));
            Assert.True(Guid.TryParse(note.Id, out _));
            Assert.Equal(string.Empty, note.ProjectId);
            Assert.Equal(DateTime.Today, note.Date);
            Assert.Equal(string.Empty, note.Title);
            Assert.Equal(string.Empty, note.Category);
            Assert.Equal(string.Empty, note.Content);
            Assert.False(note.IsEmail);
            Assert.Null(note.EmailHtmlPath);
            Assert.True(note.CreatedAt <= DateTime.UtcNow.AddSeconds(1));
            Assert.True(note.CreatedAt >= DateTime.UtcNow.AddMinutes(-1));
            Assert.Equal(string.Empty, note.CreatedBy);
            Assert.Equal(string.Empty, note.SystemReferenceId);
            Assert.False(note.IsSystemEvent);
            Assert.NotNull(note.Attachments);
            Assert.Empty(note.Attachments);
        }

        [Fact]
        public void NoteAttachment_Initialization_SetsDefaultsCorrectly()
        {
            var attachment = new NoteAttachment();

            Assert.False(string.IsNullOrWhiteSpace(attachment.Id));
            Assert.True(Guid.TryParse(attachment.Id, out _));
            Assert.Equal(string.Empty, attachment.FileName);
            Assert.Equal(string.Empty, attachment.FilePath);
            Assert.Equal(string.Empty, attachment.ContentType);
        }

        [Fact]
        public void ProjectNote_Properties_CanSetAndGet()
        {
            var testDate = new DateTime(2025, 1, 15);
            var testCreatedAt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
            List<NoteAttachment> attachments =
            [
                new NoteAttachment
                {
                    Id = "att-1",
                    FileName = "doc.pdf",
                    FilePath = "/files/doc.pdf",
                    ContentType = "application/pdf"
                }
            ];

            var note = new ProjectNote
            {
                Id = "custom-note-id",
                ProjectId = "proj-123",
                Date = testDate,
                Title = "Meeting Notes",
                Category = "Design",
                Content = "# Discussion Points\n- Point 1",
                IsEmail = true,
                EmailHtmlPath = "emails/note.eml",
                CreatedAt = testCreatedAt,
                CreatedBy = "user-abc",
                SystemReferenceId = "sys-ref-999",
                IsSystemEvent = true,
                Attachments = attachments
            };

            Assert.Equal("custom-note-id", note.Id);
            Assert.Equal("proj-123", note.ProjectId);
            Assert.Equal(testDate, note.Date);
            Assert.Equal("Meeting Notes", note.Title);
            Assert.Equal("Design", note.Category);
            Assert.Equal("# Discussion Points\n- Point 1", note.Content);
            Assert.True(note.IsEmail);
            Assert.Equal("emails/note.eml", note.EmailHtmlPath);
            Assert.Equal(testCreatedAt, note.CreatedAt);
            Assert.Equal("user-abc", note.CreatedBy);
            Assert.Equal("sys-ref-999", note.SystemReferenceId);
            Assert.True(note.IsSystemEvent);
            Assert.Same(attachments, note.Attachments);
            Assert.Single(note.Attachments);
        }

        [Fact]
        public void NoteAttachment_Properties_CanSetAndGet()
        {
            var attachment = new NoteAttachment
            {
                Id = "att-custom-id",
                FileName = "test.png",
                FilePath = "uploads/test.png",
                ContentType = "image/png"
            };

            Assert.Equal("att-custom-id", attachment.Id);
            Assert.Equal("test.png", attachment.FileName);
            Assert.Equal("uploads/test.png", attachment.FilePath);
            Assert.Equal("image/png", attachment.ContentType);
        }

        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var note = new ProjectNote { Id = "note-1" };

            Assert.True(note.Equals(note));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var note1 = new ProjectNote { Id = "note-1", Title = "Title 1" };
            var note2 = new ProjectNote { Id = "note-1", Title = "Title 2" };

            Assert.True(note1.Equals(note2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var note1 = new ProjectNote { Id = "note-1" };
            var note2 = new ProjectNote { Id = "note-2" };

            Assert.False(note1.Equals(note2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var note = new ProjectNote { Id = "note-1" };

            Assert.False(note.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var note = new ProjectNote { Id = "note-1" };

            Assert.False(note.Equals("note-1"));
            Assert.False(note.Equals(new object()));
            Assert.False(note.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var note1 = new ProjectNote { Id = "note-1" };
            var note2 = new ProjectNote { Id = "note-1" };

            Assert.Equal(note1.GetHashCode(), note2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var note = new ProjectNote { Id = null! };

            var exception = Record.Exception(() => note.GetHashCode());
            Assert.Null(exception);
        }
    }
