namespace Spokes_Server.Core.Models.Communication;

using Spokes_Server.Core.Data;
using System;

public class ChatCategory : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        if (obj == null || GetType() != obj.GetType())
            return false;
        var other = (ChatCategory)obj;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    /// <summary>
    /// Display name for the category.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Global display order for organizing custom categories in the sidebar.
    /// </summary>
    public int DisplayOrder { get; set; } = 0;

    /// <summary>
    /// Determines if this is an undeletable system category.
    /// </summary>
    public bool IsSystem { get; set; } = false;
}
