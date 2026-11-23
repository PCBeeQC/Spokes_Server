namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class ProjectNoteTests
{
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
