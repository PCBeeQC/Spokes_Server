using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class BoardCardRepository : JsonRepository<BoardCard>
{
    public BoardCardRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "BoardCards"), "*.json")
    {
    }

    protected override string GetFilePath(BoardCard item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }

    public List<BoardCard> GetByBoardId(string boardId)
    {
        // Simple linear scan - fast enough for <100k items in memory
        return _cache.Values.Where(c => c.BoardId == boardId).ToList();
    }
}


