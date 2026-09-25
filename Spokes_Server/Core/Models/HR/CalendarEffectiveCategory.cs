namespace Spokes_Server.Core.Models.HR;

using MudBlazor;
using Spokes_Server.Core.Models.Core;

public class CalendarEffectiveCategory
{
    public string FilterKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public Color Color { get; set; } = Color.Info;
    public bool IsShared { get; set; }
    public string? Attribution { get; set; }
    public int EventCount { get; set; }

    public static CalendarEffectiveCategory Resolve(
        CalendarEvent evt,
        string currentUserId,
        IReadOnlySet<string> currentUserTeamIds,
        IReadOnlyDictionary<string, string> employeeNames,
        IReadOnlyDictionary<string, string> teamNames,
        IReadOnlyDictionary<string, CalendarCategory>? userCategories = null)
    {
        var categoryName = string.IsNullOrWhiteSpace(evt.Category) ? "General" : evt.Category;
        var color = ResolveColor(evt, userCategories);

        // 1. Is this the current user's own event?
        if (string.Equals(evt.EmployeeId, currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            return new CalendarEffectiveCategory
            {
                FilterKey = $"own:{categoryName.ToLowerInvariant()}",
                DisplayName = categoryName,
                CategoryName = categoryName,
                Color = color,
                IsShared = false
            };
        }

        // 2. Is this a company-wide event?
        if (evt.IsCompanyWide)
        {
            return new CalendarEffectiveCategory
            {
                FilterKey = $"company:{categoryName.ToLowerInvariant()}",
                DisplayName = $"{categoryName} - Company",
                CategoryName = categoryName,
                Color = color,
                IsShared = true,
                Attribution = "Company"
            };
        }

        // 3. Is this event shared via a Team that the current user belongs to?
        if (evt.InvitedTeamIds != null && evt.InvitedTeamIds.Count > 0)
        {
            var matchingTeamId = evt.InvitedTeamIds.FirstOrDefault(t => currentUserTeamIds.Contains(t));
            if (!string.IsNullOrEmpty(matchingTeamId))
            {
                var teamName = teamNames.GetValueOrDefault(matchingTeamId, "Team");
                return new CalendarEffectiveCategory
                {
                    FilterKey = $"team:{matchingTeamId}:{categoryName.ToLowerInvariant()}",
                    DisplayName = $"{categoryName} - {teamName}",
                    CategoryName = categoryName,
                    Color = color,
                    IsShared = true,
                    Attribution = teamName
                };
            }
        }

        // 4. Otherwise, it is shared directly with the current user by the creator (attendee)
        var creatorName = employeeNames.GetValueOrDefault(evt.EmployeeId, "User");
        return new CalendarEffectiveCategory
        {
            FilterKey = $"user:{evt.EmployeeId}:{categoryName.ToLowerInvariant()}",
            DisplayName = $"{categoryName} - Shared by {creatorName}",
            CategoryName = categoryName,
            Color = color,
            IsShared = true,
            Attribution = creatorName
        };
    }

    public static Color ResolveColor(CalendarEvent evt, IReadOnlyDictionary<string, CalendarCategory>? userCategories)
    {
        // 1. If the event has a snapshot CategoryColor, use it (creator's intended color)
        if (!string.IsNullOrWhiteSpace(evt.CategoryColor) && Enum.TryParse<Color>(evt.CategoryColor, true, out var parsedSnapshotColor))
        {
            return parsedSnapshotColor;
        }

        // 2. If userCategories provided, try to match by CategoryId or Category Name
        if (userCategories != null)
        {
            var matchedCat = userCategories.Values.FirstOrDefault(c =>
                (!string.IsNullOrEmpty(evt.CategoryId) && string.Equals(c.Id, evt.CategoryId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(c.Name, evt.Category, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Id, evt.Category, StringComparison.OrdinalIgnoreCase));

            if (matchedCat != null && Enum.TryParse<Color>(matchedCat.Color, true, out var matchedColor))
            {
                return matchedColor;
            }
        }

        return Color.Info;
    }
}
