using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Models.Projects;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers
{
    public class TimesheetSearchHelperTests
    {
        private readonly List<Project> _projects = new()
        {
            new Project { Id = "p1", Name = "Alpha Project", ApprovedWorkTypeIds = new List<string> { "wt1" } },
            new Project { Id = "p2", Name = "Beta Project", ApprovedWorkTypeIds = new List<string>() }
        };

        private readonly List<WorkType> _workTypes = new()
        {
            new WorkType
            {
                Id = "wt1",
                Name = "Development",
                SubTasks = new List<SubTask>
                {
                    new SubTask { Id = "st1", Name = "Frontend" },
                    new SubTask { Id = "st2", Name = "Backend" }
                }
            },
            new WorkType
            {
                Id = "wt2",
                Name = "Quality Assurance",
                SubTasks = new List<SubTask>()
            }
        };

        [Fact]
        public void SearchProjects_ReturnsAll_WhenSearchIsEmpty()
        {
            var result = TimesheetSearchHelper.SearchProjects(null, _projects).ToList();
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void SearchProjects_FiltersBySubstring_CaseInsensitive()
        {
            var result = TimesheetSearchHelper.SearchProjects("alpha", _projects).ToList();
            Assert.Single(result);
            Assert.Equal("p1", result[0]);
        }

        [Fact]
        public void GetProjectName_ReturnsDisplayNameOrEmpty()
        {
            Assert.Equal("Alpha Project", TimesheetSearchHelper.GetProjectName("p1", _projects));
            Assert.Equal("", TimesheetSearchHelper.GetProjectName("nonexistent", _projects));
        }

        [Fact]
        public void SearchTasks_FiltersByApprovedWorkTypeIds_WhenConfigured()
        {
            var result = TimesheetSearchHelper.SearchTasks(null, "p1", _projects, _workTypes).ToList();
            Assert.Single(result);
            Assert.Equal("wt1", result[0]);
        }

        [Fact]
        public void SearchTasks_ReturnsAllTasks_WhenNoApprovedWorkTypeIdsConfigured()
        {
            var result = TimesheetSearchHelper.SearchTasks(null, "p2", _projects, _workTypes).ToList();
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void SearchSubTasks_ReturnsSubTasksForGivenWorkType()
        {
            var result = TimesheetSearchHelper.SearchSubTasks(null, "wt1", _workTypes).ToList();
            Assert.Equal(2, result.Count);
            Assert.Contains("st1", result);
            Assert.Contains("st2", result);
        }

        [Fact]
        public void SearchSubTasks_FiltersByName_CaseInsensitive()
        {
            var result = TimesheetSearchHelper.SearchSubTasks("front", "wt1", _workTypes).ToList();
            Assert.Single(result);
            Assert.Equal("st1", result[0]);
        }

        [Fact]
        public void SearchSubTasks_ReturnsEmpty_WhenWorkTypeIdIsEmpty()
        {
            var result = TimesheetSearchHelper.SearchSubTasks(null, "", _workTypes).ToList();
            Assert.Empty(result);
        }

        [Fact]
        public void GetSubTaskName_ReturnsCorrectName()
        {
            Assert.Equal("Frontend", TimesheetSearchHelper.GetSubTaskName("st1", "wt1", _workTypes));
            Assert.Equal("", TimesheetSearchHelper.GetSubTaskName("nonexistent", "wt1", _workTypes));
        }

        [Theory]
        [InlineData(0, "")]
        [InlineData(8.0, "8:00")]
        [InlineData(7.5, "7:30")]
        [InlineData(1.25, "1:15")]
        [InlineData(40.0, "40:00")]
        public void FormatHours_ReturnsExpectedFormat(decimal input, string expected)
        {
            Assert.Equal(expected, TimesheetSearchHelper.FormatHours(input));
        }

        [Theory]
        [InlineData("", 0)]
        [InlineData("   ", 0)]
        [InlineData("8", 8)]
        [InlineData("8:30", 8.5)]
        [InlineData("7.5", 7.5)]
        [InlineData("830", 8.5)]
        [InlineData("120", 1.3333)] // 1 hour + 20/60m
        public void ParseTimeInput_ParsesVariousFormats(string input, double expected)
        {
            var parsed = TimesheetSearchHelper.ParseTimeInput(input);
            Assert.Equal((decimal)expected, Math.Round(parsed, 4));
        }
    }
}
