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

    public TermsAndConditions EnsureSeeded(string? fallbackContent = null)
    {
        EnsureLoaded();
        if (_cache.Count > 0)
        {
            return GetDefault() ?? _cache.Values.First();
        }

        var defaultTerms = new TermsAndConditions
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Default Terms & Conditions",
            Content = !string.IsNullOrWhiteSpace(fallbackContent)
                ? fallbackContent
                : "1. Validity: This quote is valid for 30 days.\n2. Payment: 30% deposit required.",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        Save(defaultTerms);
        return defaultTerms;
    }
}
