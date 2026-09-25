using Spokes_Server.Core.Models.Core;

namespace Spokes_Server.Core.Data.Repositories.Core;

public class DocumentTemplateRepository : JsonRepository<DocumentTemplate>
{
    public DocumentTemplateRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "Templates"), "*.json")
    {
    }

    protected override string GetFilePath(DocumentTemplate item) => Path.Combine(_basePath, $"{item.Id}.json");

    public DocumentTemplate GetDefault() =>
        _cache.Values.FirstOrDefault(t => t.IsDefault) ?? _cache.Values.FirstOrDefault() ?? new DocumentTemplate();
}
