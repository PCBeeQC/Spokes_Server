namespace Spokes_Server.Core.Models.Accounting;

using System.Text.Json.Serialization;
using Spokes_Server.Core.Models.Core;
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

    // Party contact details for the supplier
    public PartyContactInfo ContactInfo { get; set; } = new();

    // Backward compatibility for legacy JSON files and existing call sites
    [JsonPropertyName("Info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PartyContactInfo? LegacyInfo
    {
        get => null;
        set { if (value != null) ContactInfo = value; }
    }

    [Obsolete("Prefer ContactInfo for consistency across accounting models.")]
    [JsonIgnore]
    public PartyContactInfo Info
    {
        get => ContactInfo;
        set => ContactInfo = value;
    }

    // Helper for search
    public string Name => ContactInfo.BusinessName;

    public List<ContactPerson> Contacts { get; set; } = [];
}
