using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class BoardRepository : JsonRepository<Board>
{
    public BoardRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Boards"), "*.json")
    {
    }

    protected override string GetFilePath(Board item) =>
        Path.Combine(_basePath, $"{item.Id}.json");
}
