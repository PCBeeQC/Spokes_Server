using Spokes_Server.Core.Models.Documents;

namespace Spokes_Server.Core.Data.Repositories.Documents;

public class StandardDocumentRepository : JsonRepository<StandardDocument>
{
    public StandardDocumentRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Settings", "StandardDocuments"), "*.json")
    {
    }

    protected override string GetFilePath(StandardDocument item) => Path.Combine(_basePath, $"{item.Id}.json");
}
