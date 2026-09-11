namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

using Spokes_Server.Core.Data;
using System.Text.Json.Serialization;




public class Board : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (Board)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string Title { get; set; } = "Untitled Board";
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // --- PERMISSIONS ---
    public string OwnerId { get; set; } = string.Empty; // EmployeeId of creator
    public List<string> MemberIds { get; set; } = new(); // List of EmployeeIds who have access
    public bool IsPublic { get; set; } = false; // If true, visible to everyone (read-only or edit? let's assume edit for now or just visibility)

    // --- SCHEMA DEFINTION ---
    public List<BoardProperty> Properties { get; set; } = new();

    // --- VIEW DEFINITIONS ---
    public List<BoardView> Views { get; set; } = new();
}

public class BoardProperty
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Property";
    public BoardPropertyType Type { get; set; } = BoardPropertyType.Text;

    // For Select/MultiSelect options
    public List<BoardPropertyOption> Options { get; set; } = new();
}

public class BoardPropertyOption
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Option";
    public string Color { get; set; } = "gray"; // simple color name or hex
}

public class BoardView
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New View";
    public BoardViewType Type { get; set; } = BoardViewType.Kanban;

    // For Kanban: which property to group by?
    // Must be a Select or Person property
    public string? GroupByPropertyId { get; set; }

    // For Calendar: which property to use for date?
    public string? DatePropertyId { get; set; }

    // Ordered list of visible columns (PropertyIds)
    public List<string> VisiblePropertyIds { get; set; } = new();

    // --- NEW PHASE 2 FEATURES ---
    public List<BoardViewFilter> Filters { get; set; } = new();
    public List<BoardViewSortOption> SortOptions { get; set; } = new();

    // Key: PropertyId, Value: CalculationType (e.g. "count", "sum", "average")
    public Dictionary<string, string> ColumnCalculations { get; set; } = new();
}

public class BoardViewFilter
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PropertyId { get; set; } = string.Empty;
    public BoardFilterOperator Operator { get; set; } = BoardFilterOperator.Contains;
    public string Value { get; set; } = "";
}

public class BoardViewSortOption
{
    public string PropertyId { get; set; } = string.Empty;
    public bool IsDescending { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoardFilterOperator
{
    Contains,
    DoesNotContain,
    Is,
    IsNot,
    IsEmpty,
    IsNotEmpty
    // Add numerical/date operators later
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoardPropertyType
{
    Text,
    Select,
    MultiSelect,
    Person,
    Date,
    Checkbox,
    URL,
    Email,
    Phone,
    Number,
    CreatedBy,
    UpdatedBy,
    CreatedAt,
    UpdatedAt,
    Button,
    Project
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BoardViewType
{
    Kanban,
    Table,
    Gallery,
    Calendar
}



