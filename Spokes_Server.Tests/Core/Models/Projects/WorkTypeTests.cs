namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Collections.Generic;
using Spokes_Server.Core.Models.Projects;

public class WorkTypeTests
{
        [Fact]
        public void WorkType_Initialization_SetsDefaultsCorrectly()
        {
            var workType = new WorkType();

            Assert.False(string.IsNullOrWhiteSpace(workType.Id));
            Assert.True(Guid.TryParse(workType.Id, out _));
            Assert.Equal(string.Empty, workType.Name);
            Assert.NotNull(workType.NameTranslations);
            Assert.Empty(workType.NameTranslations);
            Assert.Equal(0m, workType.DefaultRate);
            Assert.Equal(0m, workType.CostRate);
            Assert.NotNull(workType.AssignedTeamIds);
            Assert.Empty(workType.AssignedTeamIds);
            Assert.NotNull(workType.SubTasks);
            Assert.Empty(workType.SubTasks);
            Assert.True(workType.IsActive);
        }

        [Fact]
        public void WorkType_Properties_CanSetAndGet()
        {
            var translations = new Dictionary<string, string>
            {
                { "en", "Engineering" },
                { "fr", "Ingénierie" }
            };
            List<string> teamIds = ["team-1", "team-2"];
            List<SubTask> subTasks =
            [
                new SubTask { Id = "st-1", Name = "Initial Review" }
            ];

            var workType = new WorkType
            {
                Id = "custom-id-123",
                Name = "Engineering",
                NameTranslations = translations,
                DefaultRate = 125.50m,
                CostRate = 75.00m,
                AssignedTeamIds = teamIds,
                SubTasks = subTasks,
                IsActive = false
            };

            Assert.Equal("custom-id-123", workType.Id);
            Assert.Equal("Engineering", workType.Name);
            Assert.Same(translations, workType.NameTranslations);
            Assert.Equal(2, workType.NameTranslations.Count);
            Assert.Equal("Engineering", workType.NameTranslations["en"]);
            Assert.Equal("Ingénierie", workType.NameTranslations["fr"]);
            Assert.Equal(125.50m, workType.DefaultRate);
            Assert.Equal(75.00m, workType.CostRate);
            Assert.Same(teamIds, workType.AssignedTeamIds);
            Assert.Equal(2, workType.AssignedTeamIds.Count);
            Assert.Same(subTasks, workType.SubTasks);
            Assert.Single(workType.SubTasks);
            Assert.False(workType.IsActive);
        }

        [Fact]
        public void Equals_WithSameReference_ReturnsTrue()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.True(workType.Equals(workType));
        }

        [Fact]
        public void Equals_WithSameId_ReturnsTrue()
        {
            var workType1 = new WorkType { Id = "wt-1", Name = "Name 1" };
            var workType2 = new WorkType { Id = "wt-1", Name = "Name 2" };

            Assert.True(workType1.Equals(workType2));
        }

        [Fact]
        public void Equals_WithDifferentId_ReturnsFalse()
        {
            var workType1 = new WorkType { Id = "wt-1" };
            var workType2 = new WorkType { Id = "wt-2" };

            Assert.False(workType1.Equals(workType2));
        }

        [Fact]
        public void Equals_WithNull_ReturnsFalse()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.False(workType.Equals(null));
        }

        [Fact]
        public void Equals_WithDifferentType_ReturnsFalse()
        {
            var workType = new WorkType { Id = "wt-1" };

            Assert.False(workType.Equals("wt-1"));
            Assert.False(workType.Equals(new object()));
            Assert.False(workType.Equals(123));
        }

        [Fact]
        public void GetHashCode_WithSameId_ReturnsSameHashCode()
        {
            var workType1 = new WorkType { Id = "wt-1" };
            var workType2 = new WorkType { Id = "wt-1" };

            Assert.Equal(workType1.GetHashCode(), workType2.GetHashCode());
        }

        [Fact]
        public void GetHashCode_WithNullId_DoesNotThrowAndReturnsBaseHashCode()
        {
            var workType = new WorkType { Id = null! };

            var exception = Record.Exception(() => workType.GetHashCode());
            Assert.Null(exception);
        }
    }
