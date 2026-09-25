using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class BoardTemplateRepository : JsonRepository<BoardTemplate>
{
    public BoardTemplateRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Templates"), "*.json")
    {
    }

    protected override string GetFilePath(BoardTemplate item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
