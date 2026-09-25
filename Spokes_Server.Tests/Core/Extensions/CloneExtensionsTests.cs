using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Tests.Core.Extensions
{
    public class CloneExtensionsTests
    {
        private class TestChild
        {
            public string Name { get; set; } = string.Empty;
        }

        private class TestComplexEntity
        {
            public int Id { get; set; }
            public string Title { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public double Amount { get; set; }
            public bool IsActive { get; set; }
            public TestChild Child { get; set; } = new();
            public List<string> Tags { get; set; } = new();
            public List<TestChild> Children { get; set; } = new();
        }

        [Fact]
        public void DeepClone_WhenSourceIsNull_ReturnsNull()
        {
            // Arrange
            TestComplexEntity? source = null;

            // Act
            var clone = source.DeepClone();

            // Assert
            Assert.Null(clone);
        }

        [Fact]
        public void DeepClone_ClonesObjectWithAllProperties()
        {
            // Arrange
            var source = new TestComplexEntity
            {
                Id = 42,
                Title = "Test Entity",
                CreatedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                Amount = 99.95,
                IsActive = true
            };

            // Act
            var clone = source.DeepClone();

            // Assert
            Assert.NotNull(clone);
            Assert.NotSame(source, clone);
            Assert.Equal(source.Id, clone.Id);
            Assert.Equal(source.Title, clone.Title);
            Assert.Equal(source.CreatedAt, clone.CreatedAt);
            Assert.Equal(source.Amount, clone.Amount);
            Assert.Equal(source.IsActive, clone.IsActive);
        }

        [Fact]
        public void DeepClone_DetachesNestedObjectsAndCollections()
        {
            // Arrange
            var source = new TestComplexEntity
            {
                Title = "Original Title",
                Child = new TestChild { Name = "Original Child" },
                Tags = new List<string> { "tag1", "tag2" },
                Children = new List<TestChild>
                {
                    new() { Name = "Child 1" }
                }
            };

            // Act
            var clone = source.DeepClone();

            Assert.NotNull(clone);
            Assert.NotSame(source, clone);
            Assert.NotSame(source.Child, clone.Child);
            Assert.NotSame(source.Tags, clone.Tags);
            Assert.NotSame(source.Children, clone.Children);

            // Mutate clone
            clone.Title = "Modified Title";
            clone.Child.Name = "Modified Child";
            clone.Tags.Add("tag3");
            clone.Children[0].Name = "Modified Child 1";

            // Assert original remains unchanged
            Assert.Equal("Original Title", source.Title);
            Assert.Equal("Original Child", source.Child.Name);
            Assert.Equal(2, source.Tags.Count);
            Assert.DoesNotContain("tag3", source.Tags);
            Assert.Equal("Child 1", source.Children[0].Name);
        }

        [Fact]
        public void DeepClone_ClonesComplexEntity()
        {
            // Arrange
            var source = new TestComplexEntity
            {
                Id = 100,
                Title = "Complex Entity",
                CreatedAt = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc),
                Amount = 1234.56,
                IsActive = true,
                Child = new TestChild { Name = "Sub Item" },
                Tags = new List<string> { "urgent", "internal" },
                Children = new List<TestChild>
                {
                    new() { Name = "Child A" },
                    new() { Name = "Child B" }
                }
            };

            // Act
            var clone = source.DeepClone();

            // Assert
            Assert.NotNull(clone);
            Assert.NotSame(source, clone);
            Assert.Equal(source.Id, clone.Id);
            Assert.Equal(source.Title, clone.Title);
            Assert.Equal(source.CreatedAt, clone.CreatedAt);
            Assert.Equal(source.Amount, clone.Amount);
            Assert.Equal(source.IsActive, clone.IsActive);
            Assert.Equal(source.Child.Name, clone.Child.Name);
            Assert.Equal(source.Tags, clone.Tags);
            Assert.Equal(source.Children.Count, clone.Children.Count);
            Assert.Equal(source.Children[0].Name, clone.Children[0].Name);
            Assert.Equal(source.Children[1].Name, clone.Children[1].Name);
        }
    }
}
