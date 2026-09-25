using Spokes_Server.Core.Models.Projects;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class BoardCardRepository : JsonRepository<BoardCard>
{
    public BoardCardRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "BoardCards"), "*.json")
    {
    }

    protected override string GetFilePath(BoardCard item) =>
        Path.Combine(_basePath, $"{item.Id}.json");

    public List<BoardCard> GetByBoardId(string boardId) =>
        _cache.Values.Where(c => c.BoardId == boardId).ToList();
}
