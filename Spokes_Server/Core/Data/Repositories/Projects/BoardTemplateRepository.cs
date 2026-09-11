using Spokes_Server.Core.Models.Core;
using Spokes_Server.Core.Models.Projects;
using Spokes_Server.Core.Models.Accounting;
using Spokes_Server.Core.Models.Communication;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.Projects;

public class BoardTemplateRepository : JsonRepository<BoardTemplate>
{
    public BoardTemplateRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Templates"), "*.json")
    {
    }

    protected override string GetFilePath(BoardTemplate item)
    {
        return Path.Combine(_basePath, $"{item.Id}.json");
    }
}


