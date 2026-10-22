namespace Spokes_Server.Core.Services.Reports.Helpers;

using Spokes_Server.Core.Models.Projects;

/// <summary>
/// Layout modes for <see cref="Spokes_Server.Components.Shared.WorkTypeSubTaskSelector"/>.
/// </summary>
public enum WorkTypeSelectorLayout
{
    Grid,
    Stack,
    TableCells
}

/// <summary>
/// Represents a searchable Task or "Task - SubTask" option for unified work type autocomplete selectors.
/// </summary>
public class WorkTypeSearchItem
{
    public string WorkTypeId { get; set; } = string.Empty;
    public string WorkTypeName { get; set; } = string.Empty;
    public string? SubTaskId { get; set; }
    public string? SubTaskName { get; set; }
    public string DisplayText { get; set; } = string.Empty;

    public override bool Equals(object? obj)
    {
        if (obj is WorkTypeSearchItem other)
        {
            return WorkTypeId == other.WorkTypeId &&
                   string.Equals(SubTaskId ?? string.Empty, other.SubTaskId ?? string.Empty, StringComparison.Ordinal);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(WorkTypeId, SubTaskId ?? string.Empty);
    }
}

/// <summary>
/// Shared search and display helpers for Project / Task / SubTask autocomplete fields and quote/invoice task formatting.
/// </summary>
public static class TimesheetSearchHelper
{
    public static IEnumerable<string> SearchProjects(string? searchText, List<Project> projects)
    {
        var matching = string.IsNullOrEmpty(searchText)
            ? projects
            : projects.Where(p => p.DisplayName.Contains(searchText, StringComparison.InvariantCultureIgnoreCase));
        return matching.Select(p => p.Id);
    }

    public static string GetProjectName(string id, List<Project> projects)
    {
        return projects.FirstOrDefault(x => x.Id == id)?.DisplayName ?? "";
    }

    public static List<WorkType> GetAllowedWorkTypes(Project? project, IEnumerable<WorkType> workTypes)
    {
        IEnumerable<WorkType> allowed = workTypes;
        if (project != null && project.ApprovedWorkTypeIds.Count > 0)
        {
            allowed = workTypes.Where(t => project.ApprovedWorkTypeIds.Contains(t.Id));
        }

        return allowed.OrderBy(t => t.Name).ToList();
    }

    public static List<WorkType> GetAllowedWorkTypes(string? projectId, IEnumerable<Project>? projects, IEnumerable<WorkType> workTypes, bool requireProject = false)
    {
        if (requireProject && string.IsNullOrEmpty(projectId))
        {
            return [];
        }

        var project = !string.IsNullOrEmpty(projectId)
            ? projects?.FirstOrDefault(p => p.Id == projectId)
            : null;

        if (requireProject && project == null)
        {
            return [];
        }

        return GetAllowedWorkTypes(project, workTypes);
    }

    public static List<WorkTypeSearchItem> BuildWorkTypeSearchItems(IEnumerable<WorkType> workTypes)
    {
        var items = new List<WorkTypeSearchItem>();
        foreach (var task in workTypes.OrderBy(t => t.Name))
        {
            items.Add(new WorkTypeSearchItem
            {
                WorkTypeId = task.Id,
                WorkTypeName = task.Name,
                SubTaskId = null,
                SubTaskName = null,
                DisplayText = task.Name
            });

            foreach (var st in task.SubTasks)
            {
                items.Add(new WorkTypeSearchItem
                {
                    WorkTypeId = task.Id,
                    WorkTypeName = task.Name,
                    SubTaskId = st.Id,
                    SubTaskName = st.Name,
                    DisplayText = $"{task.Name} - {st.Name}"
                });
            }
        }

        return items;
    }

    public static IEnumerable<WorkTypeSearchItem> SearchWorkTypeItems(
        string? searchText,
        string? currentWorkTypeId,
        IEnumerable<WorkTypeSearchItem> searchItems)
    {
        var itemsList = searchItems as IReadOnlyList<WorkTypeSearchItem> ?? searchItems.ToList();
        var trimmed = searchText?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return itemsList.Where(x => string.IsNullOrEmpty(x.SubTaskId));
        }

        if (!string.IsNullOrEmpty(currentWorkTypeId))
        {
            var currentTaskItem = itemsList.FirstOrDefault(x => x.WorkTypeId == currentWorkTypeId && string.IsNullOrEmpty(x.SubTaskId));
            if (currentTaskItem != null && string.Equals(trimmed, currentTaskItem.WorkTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return itemsList.Where(x => string.IsNullOrEmpty(x.SubTaskId));
            }
        }

        return itemsList.Where(x => x.DisplayText.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> SearchTasks(string? searchText, string projectId, List<Project> projects, List<WorkType> workTypes)
    {
        var allowedTasks = GetAllowedWorkTypes(projectId, projects, workTypes, requireProject: true).AsEnumerable();

        if (!string.IsNullOrEmpty(searchText))
        {
            allowedTasks = allowedTasks.Where(t => t.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase));
        }

        return allowedTasks.Select(t => t.Id);
    }

    public static string GetTaskName(string id, List<WorkType> workTypes)
    {
        return workTypes.FirstOrDefault(x => x.Id == id)?.Name ?? "";
    }

    public static IEnumerable<string> SearchSubTasks(
        string? searchText,
        string? workTypeId,
        IEnumerable<WorkType> workTypes,
        string? currentSubTaskId = null)
    {
        if (string.IsNullOrEmpty(workTypeId)) return [];

        var task = workTypes.FirstOrDefault(t => t.Id == workTypeId);
        if (task == null) return [];

        var subTasks = task.SubTasks.AsEnumerable();
        var trimmed = searchText?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            var currentSubTaskName = !string.IsNullOrEmpty(currentSubTaskId)
                ? task.SubTasks.FirstOrDefault(s => s.Id == currentSubTaskId)?.Name
                : null;

            if (!string.Equals(trimmed, currentSubTaskName, StringComparison.OrdinalIgnoreCase))
            {
                subTasks = subTasks.Where(s => s.Name.Contains(trimmed, StringComparison.InvariantCultureIgnoreCase));
            }
        }

        return subTasks.OrderBy(s => s.Name).Select(s => s.Id);
    }

    public static string GetSubTaskName(string? subTaskId, string? workTypeId, IEnumerable<WorkType> workTypes)
    {
        if (string.IsNullOrEmpty(subTaskId) || string.IsNullOrEmpty(workTypeId)) return "";
        var t = workTypes.FirstOrDefault(x => x.Id == workTypeId);
        if (t == null) return "";
        var st = t.SubTasks.FirstOrDefault(s => s.Id == subTaskId);
        return st?.Name ?? "";
    }

    public static SubTask? GetDefaultSubTask(WorkType? task)
    {
        if (task == null || task.SubTasks.Count == 0) return null;
        return task.SubTasks.FirstOrDefault(s => string.Equals(s.Name, "General", StringComparison.OrdinalIgnoreCase))
               ?? task.SubTasks.First();
    }

    public static string FormatTaskAndSubTask(string? taskName, string? subTaskName, string separator = " : ")
    {
        if (string.IsNullOrWhiteSpace(taskName)) return string.Empty;
        if (string.IsNullOrWhiteSpace(subTaskName)) return taskName;
        return $"{taskName}{separator}{subTaskName}";
    }

    public static string FormatTranslatedTaskAndSubTask(
        string? fallbackTaskName,
        string? fallbackSubTaskName,
        string? subTaskId,
        WorkType? workType,
        string? languageCode,
        string separator = " : ")
    {
        var taskName = !string.IsNullOrWhiteSpace(fallbackTaskName)
            ? fallbackTaskName
            : (workType?.Name ?? string.Empty);

        SubTask? subTask = null;
        if (workType != null)
        {
            if (!string.IsNullOrEmpty(subTaskId))
            {
                subTask = workType.SubTasks.FirstOrDefault(s => s.Id == subTaskId);
            }
            else if (!string.IsNullOrWhiteSpace(fallbackSubTaskName))
            {
                subTask = workType.SubTasks.FirstOrDefault(s =>
                    string.Equals(s.Name, fallbackSubTaskName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                subTask = GetDefaultSubTask(workType);
            }
        }

        var subTaskName = !string.IsNullOrWhiteSpace(fallbackSubTaskName)
            ? fallbackSubTaskName
            : (subTask?.Name ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(languageCode) && workType != null)
        {
            if (workType.NameTranslations != null &&
                workType.NameTranslations.TryGetValue(languageCode, out var taskTrans) &&
                !string.IsNullOrWhiteSpace(taskTrans))
            {
                taskName = taskTrans;
            }

            if (subTask != null &&
                subTask.NameTranslations != null &&
                subTask.NameTranslations.TryGetValue(languageCode, out var subTaskTrans) &&
                !string.IsNullOrWhiteSpace(subTaskTrans))
            {
                subTaskName = subTaskTrans;
            }
        }

        return FormatTaskAndSubTask(taskName, subTaskName, separator);
    }
}
