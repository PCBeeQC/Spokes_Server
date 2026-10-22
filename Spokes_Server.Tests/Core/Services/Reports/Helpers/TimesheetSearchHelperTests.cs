using Spokes_Server.Core.Helpers;
using Spokes_Server.Core.Services.Reports.Helpers;
using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Tests.Core.Services.Reports.Helpers;

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



    [Fact]
    public void GetTaskName_ExistingId_ReturnsTaskName()
    {
        var result = TimesheetSearchHelper.GetTaskName("wt1", _workTypes);
        Assert.Equal("Development", result);
    }

    [Fact]
    public void GetTaskName_NonExistingId_ReturnsEmptyString()
    {
        var result = TimesheetSearchHelper.GetTaskName("nonexistent", _workTypes);
        Assert.Equal("", result);
    }

    [Fact]
    public void SearchTasks_WithSearchText_FiltersCaseInsensitive()
    {
        var result = TimesheetSearchHelper.SearchTasks("QUAL", "p2", _projects, _workTypes).ToList();
        Assert.Single(result);
        Assert.Equal("wt2", result[0]);
    }

    [Fact]
    public void SearchTasks_ProjectNotFound_ReturnsEmpty()
    {
        var result = TimesheetSearchHelper.SearchTasks(null, "nonexistent", _projects, _workTypes).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void SearchSubTasks_WorkTypeNotFound_ReturnsEmpty()
    {
        var result = TimesheetSearchHelper.SearchSubTasks(null, "nonexistent", _workTypes).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void SearchSubTasks_SubTasksEmpty_ReturnsEmpty()
    {
        var result = TimesheetSearchHelper.SearchSubTasks(null, "wt2", _workTypes).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void GetSubTaskName_WorkTypeNotFound_ReturnsEmptyString()
    {
        var result = TimesheetSearchHelper.GetSubTaskName("st1", "nonexistent", _workTypes);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildWorkTypeSearchItems_IncludesTaskAndTaskSubTaskEntries()
    {
        var items = TimesheetSearchHelper.BuildWorkTypeSearchItems(_workTypes);

        Assert.Equal(4, items.Count);
        Assert.Contains(items, x => x.WorkTypeId == "wt1" && x.SubTaskId == null && x.DisplayText == "Development");
        Assert.Contains(items, x => x.WorkTypeId == "wt1" && x.SubTaskId == "st1" && x.DisplayText == "Development - Frontend");
        Assert.Contains(items, x => x.WorkTypeId == "wt1" && x.SubTaskId == "st2" && x.DisplayText == "Development - Backend");
        Assert.Contains(items, x => x.WorkTypeId == "wt2" && x.SubTaskId == null && x.DisplayText == "Quality Assurance");
    }

    [Fact]
    public void SearchWorkTypeItems_ReturnsAllTopLevelTasks_WhenSearchIsEmptyOrMatchesSelectedTask()
    {
        var searchItems = TimesheetSearchHelper.BuildWorkTypeSearchItems(_workTypes);

        var emptyQuery = TimesheetSearchHelper.SearchWorkTypeItems("", null, searchItems).ToList();
        Assert.Equal(2, emptyQuery.Count);
        Assert.All(emptyQuery, x => Assert.Null(x.SubTaskId));

        var selectedTaskQuery = TimesheetSearchHelper.SearchWorkTypeItems("Development", "wt1", searchItems).ToList();
        Assert.Equal(2, selectedTaskQuery.Count);
        Assert.All(selectedTaskQuery, x => Assert.Null(x.SubTaskId));
    }

    [Fact]
    public void SearchWorkTypeItems_NarrowsToTasksAndSubTasks_WhenTypingFilter()
    {
        var searchItems = TimesheetSearchHelper.BuildWorkTypeSearchItems(_workTypes);

        var filtered = TimesheetSearchHelper.SearchWorkTypeItems("Backend", "wt1", searchItems).ToList();
        Assert.Single(filtered);
        Assert.Equal("wt1", filtered[0].WorkTypeId);
        Assert.Equal("st2", filtered[0].SubTaskId);
        Assert.Equal("Development - Backend", filtered[0].DisplayText);
    }

    [Fact]
    public void FormatTaskAndSubTask_AlwaysIncludesSubTask_IncludingGeneral()
    {
        Assert.Equal("Development : General", TimesheetSearchHelper.FormatTaskAndSubTask("Development", "General"));
        Assert.Equal("Development : Backend", TimesheetSearchHelper.FormatTaskAndSubTask("Development", "Backend"));
        Assert.Equal("Development", TimesheetSearchHelper.FormatTaskAndSubTask("Development", ""));
    }

    [Fact]
    public void FormatTranslatedTaskAndSubTask_TranslatesAndAlwaysDisplaysGeneralSubTask()
    {
        var workType = new WorkType
        {
            Id = "wt-prog",
            Name = "Programming",
            NameTranslations = new Dictionary<string, string> { ["fr"] = "Programmation" },
            SubTasks = new List<SubTask>
            {
                new SubTask
                {
                    Id = "st-gen",
                    Name = "General",
                    NameTranslations = new Dictionary<string, string> { ["fr"] = "Général" }
                }
            }
        };

        var enFormatted = TimesheetSearchHelper.FormatTranslatedTaskAndSubTask(
            "Programming", "General", "st-gen", workType, null);
        Assert.Equal("Programming : General", enFormatted);

        var frFormatted = TimesheetSearchHelper.FormatTranslatedTaskAndSubTask(
            "Programming", "General", "st-gen", workType, "fr");
        Assert.Equal("Programmation : Général", frFormatted);
    }
}
