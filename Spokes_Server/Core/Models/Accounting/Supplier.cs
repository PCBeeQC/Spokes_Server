namespace Spokes_Server.Core.Models.Accounting;

using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Data;

public class Supplier : IDataEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj)) return true;
        return obj is Supplier other && Id == other.Id;
    }

    public override int GetHashCode() => Id?.GetHashCode() ?? base.GetHashCode();

    // We reuse your existing ClientInfo class to keep things compatible
    public ClientInfo Info { get; set; } = new();

    // Helper for search
    public string Name => Info.BusinessName;

    public List<ContactPerson> Contacts { get; set; } = [];
}
