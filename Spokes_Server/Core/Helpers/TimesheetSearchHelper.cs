namespace Spokes_Server.Core.Helpers;

using Spokes_Server.Core.Models.Projects;

/// <summary>
/// Shared search and display helpers for Project / Task / SubTask autocomplete fields.
/// Used by MyTimesheet.razor and TimesheetRowDialog.razor to avoid duplicated logic.
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

    public static IEnumerable<string> SearchTasks(string? searchText, string projectId, List<Project> projects, List<WorkType> workTypes)
    {
        var p = projects.FirstOrDefault(x => x.Id == projectId);

        IEnumerable<WorkType> allowedTasks = Enumerable.Empty<WorkType>();

        if (p != null && p.ApprovedWorkTypeIds.Any())
        {
            allowedTasks = workTypes.Where(t => p.ApprovedWorkTypeIds.Contains(t.Id));
        }
        else if (p != null)
        {
            allowedTasks = workTypes; // Fallback: show all when no restrictions configured
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            allowedTasks = allowedTasks.Where(t => t.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase));
        }

        return allowedTasks.OrderBy(t => t.Name).Select(t => t.Id);
    }

    public static string GetTaskName(string id, List<WorkType> workTypes)
    {
        return workTypes.FirstOrDefault(x => x.Id == id)?.Name ?? "";
    }

    public static IEnumerable<string> SearchSubTasks(string? searchText, string workTypeId, List<WorkType> workTypes)
    {
        if (string.IsNullOrEmpty(workTypeId)) return Enumerable.Empty<string>();

        var task = workTypes.FirstOrDefault(t => t.Id == workTypeId);
        if (task == null) return Enumerable.Empty<string>();

        var subTasks = task.SubTasks.AsEnumerable();

        if (!string.IsNullOrEmpty(searchText))
        {
            subTasks = subTasks.Where(s => s.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase));
        }

        return subTasks.OrderBy(s => s.Name).Select(s => s.Id);
    }

    public static string GetSubTaskName(string subTaskId, string workTypeId, List<WorkType> workTypes)
    {
        var t = workTypes.FirstOrDefault(x => x.Id == workTypeId);
        if (t == null) return "";
        var st = t.SubTasks.FirstOrDefault(s => s.Id == subTaskId);
        return st?.Name ?? "";
    }

    public static string FormatHours(decimal hours)
    {
        if (hours == 0) return "";
        var ts = TimeSpan.FromHours((double)hours);
        return $"{(int)ts.TotalHours}:{ts.Minutes:D2}";
    }

    public static decimal ParseTimeInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return 0;

        input = input.Trim();

        // 1. Colon format (8:30)
        if (input.Contains(':'))
        {
            var parts = input.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
            {
                return h + (m / 60m);
            }
        }

        // 2. Decimal format (8.5 or 8,5)
        if (decimal.TryParse(input, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal d) && input.Contains('.'))
        {
            return d;
        }
        if (decimal.TryParse(input, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out decimal d2) && input.Contains(','))
        {
            return d2;
        }

        // 3. Integer Quick Format
        if (int.TryParse(input, out int val))
        {
            // 830 -> 8:30
            if (input.Length >= 3)
            {
                int hours = val / 100;
                int minutes = val % 100;
                return hours + (minutes / 60m);
            }
            // 8 -> 8:00
            if (val <= 24)
            {
                return val; // Treat "8" as 8 hours
            }
            // 85 -> 1:25 (85 minutes)
            if (val > 24)
            {
                return val / 60m;
            }
        }

        return 0;
    }
}
