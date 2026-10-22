using Spokes_Server.Core.Models.Documents;

namespace Spokes_Server.Core.Data.Repositories.Documents;

public class DocumentTemplateRepository : JsonRepository<DocumentTemplate>
{
    public DocumentTemplateRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "Templates"), "*.json")
    {
    }

    protected override string GetFilePath(DocumentTemplate item) => Path.Combine(_basePath, $"{item.Id}.json");

    public DocumentTemplate GetDefault()
    {
        EnsureLoaded();
        return _cache.Values.FirstOrDefault(t => t.IsDefault) ?? _cache.Values.FirstOrDefault() ?? new DocumentTemplate();
    }
}
