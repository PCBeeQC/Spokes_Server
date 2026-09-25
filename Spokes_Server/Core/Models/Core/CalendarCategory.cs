namespace Spokes_Server.Core.Models.Core;

public class CalendarCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "Default"; // MudBlazor Color Enum as string
}

public static class CalendarCategoryDefaults
{
    public static List<CalendarCategory> GetSystemDefaults() =>
    [
        new CalendarCategory { Id = "work", Name = "Work", Color = "Info" },
        new CalendarCategory { Id = "meeting", Name = "Meeting", Color = "Primary" },
        new CalendarCategory { Id = "holiday", Name = "Holiday", Color = "Success" },
        new CalendarCategory { Id = "personal", Name = "Personal", Color = "Warning" },
        new CalendarCategory { Id = "important", Name = "Important", Color = "Error" }
    ];

    public static List<CalendarCategory> CloneList(IEnumerable<CalendarCategory>? categories)
    {
        if (categories == null || !categories.Any())
            return GetSystemDefaults();

        return categories.Select(c => new CalendarCategory
        {
            Id = Guid.NewGuid().ToString(),
            Name = c.Name,
            Color = string.IsNullOrWhiteSpace(c.Color) ? "Info" : c.Color
        }).ToList();
    }
}
