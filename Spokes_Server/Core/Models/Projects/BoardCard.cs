namespace Spokes_Server.Core.Models.Projects;

using Spokes_Server.Core.Data;

public class BoardCard : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is BoardCard other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();
    public string BoardId { get; set; } = string.Empty; // Parent Board

    public string Title { get; set; } = string.Empty;

    // Optional description / content (could be markdown)
    public string? Content { get; set; }

    public string? Icon { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // --- DYNAMIC VALUES ---
    // Key: PropertyId (from Board.Properties)
    // Value: The value as a string. 
    //   - Text: "Hello"
    //   - Select: OptionId
    //   - MultiSelect: JSON Array of OptionIds
    //   - Person: UserId
    //   - Checkbox: "true" or "false"
    //   - Date: ISO String
    public Dictionary<string, string> PropertyValues { get; set; } = [];

    // --- PHASE 3 FEATURES ---
    public List<BoardCardComment> Comments { get; set; } = [];
    public List<BoardCardActivity> ActivityLog { get; set; } = [];
}



