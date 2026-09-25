namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class BoardCardTests
{
        [Fact]
        public void BoardCard_Initialization_SetsDefaultsCorrectly()
        {
            var card = new BoardCard();

            Assert.False(string.IsNullOrWhiteSpace(card.Id));
            Assert.True(Guid.TryParse(card.Id, out _));
            Assert.Equal(string.Empty, card.BoardId);
            Assert.Equal(string.Empty, card.Title);
            Assert.Null(card.Content);
            Assert.Null(card.Icon);
            Assert.True(card.CreatedAt <= DateTime.Now);
            Assert.True(card.UpdatedAt <= DateTime.Now);
            Assert.NotNull(card.PropertyValues);
            Assert.Empty(card.PropertyValues);
            Assert.NotNull(card.Comments);
            Assert.Empty(card.Comments);
            Assert.NotNull(card.ActivityLog);
            Assert.Empty(card.ActivityLog);
        }

        [Fact]
        public void BoardCard_Properties_CanSetAndGet()
        {
            var now = DateTime.Now;
            List<BoardCardComment> comments =
            [
                new BoardCardComment { Id = "c1", CardId = "card-1", Text = "Test Comment", UserId = "user-1" }
            ];
            List<BoardCardActivity> activityLog =
            [
                new BoardCardActivity { Id = "a1", CardId = "card-1", Action = "created", UserId = "user-1" }
            ];

            var card = new BoardCard
            {
                Id = "custom-id",
                BoardId = "board-123",
                Title = "Task 1",
                Content = "Detailed description",
                Icon = "mdi-checkbox",
                CreatedAt = now,
                UpdatedAt = now,
                PropertyValues = new Dictionary<string, string>
                {
                    { "prop-status", "Done" },
                    { "prop-priority", "High" }
                },
                Comments = comments,
                ActivityLog = activityLog
            };

            Assert.Equal("custom-id", card.Id);
            Assert.Equal("board-123", card.BoardId);
            Assert.Equal("Task 1", card.Title);
            Assert.Equal("Detailed description", card.Content);
            Assert.Equal("mdi-checkbox", card.Icon);
            Assert.Equal(now, card.CreatedAt);
            Assert.Equal(now, card.UpdatedAt);
            Assert.Equal(2, card.PropertyValues.Count);
            Assert.Equal("Done", card.PropertyValues["prop-status"]);
            Assert.Equal("High", card.PropertyValues["prop-priority"]);
            Assert.Same(comments, card.Comments);
            Assert.Single(card.Comments);
            Assert.Same(activityLog, card.ActivityLog);
            Assert.Single(card.ActivityLog);
        }

        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var card = new BoardCard { Id = "card-1" };

            Assert.True(card.Equals(card));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var card1 = new BoardCard { Id = "card-1", Title = "Title 1" };
            var card2 = new BoardCard { Id = "card-1", Title = "Title 2" };

            Assert.True(card1.Equals(card2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var card1 = new BoardCard { Id = "card-1" };
            var card2 = new BoardCard { Id = "card-2" };

            Assert.False(card1.Equals(card2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var card = new BoardCard { Id = "card-1" };

            Assert.False(card.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var card = new BoardCard { Id = "card-1" };

            Assert.False(card.Equals("card-1"));
            Assert.False(card.Equals(new object()));
            Assert.False(card.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var card1 = new BoardCard { Id = "card-1" };
            var card2 = new BoardCard { Id = "card-1" };

            Assert.Equal(card1.GetHashCode(), card2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var card = new BoardCard { Id = null! };

            var exception = Record.Exception(() => card.GetHashCode());
            Assert.Null(exception);
        }
    }
