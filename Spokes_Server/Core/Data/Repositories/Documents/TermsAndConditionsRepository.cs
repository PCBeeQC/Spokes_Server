using Spokes_Server.Core.Models.Documents;

namespace Spokes_Server.Core.Data.Repositories.Documents;

public class TermsAndConditionsRepository : JsonRepository<TermsAndConditions>
{
    public TermsAndConditionsRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "TermsAndConditions"), "*.json")
    {
    }

    protected override string GetFilePath(TermsAndConditions item) => Path.Combine(_basePath, $"{item.Id}.json");

    public TermsAndConditions? GetDefault()
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault(t => t.IsDefault) ?? _cache.Values.FirstOrDefault();
    }
}
