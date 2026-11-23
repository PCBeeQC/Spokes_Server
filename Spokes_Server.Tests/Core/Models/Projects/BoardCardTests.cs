namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class BoardCardTests
{
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
